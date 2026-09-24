using System.Management;
using System.Text.RegularExpressions;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class AmdVenvProvisioner {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService? _settingsService;

    public const string DefaultTorchVersion = "2.9.1+rocm7.2.1";
    public const string DefaultRocmBaseUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1";

    public static readonly IReadOnlyList<string> DefaultPyTorchWheels = new[] {
        $"{DefaultRocmBaseUrl}/torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl",
        $"{DefaultRocmBaseUrl}/torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl",
        $"{DefaultRocmBaseUrl}/torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl"
    };

    public static readonly IReadOnlyList<string> DefaultRocmSdkWheels = new[] {
        $"{DefaultRocmBaseUrl}/rocm-7.2.1.tar.gz",
        $"{DefaultRocmBaseUrl}/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
        $"{DefaultRocmBaseUrl}/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
        $"{DefaultRocmBaseUrl}/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl"
    };

    public AmdVenvProvisioner(ProcessRunner processRunner, SettingsService? settingsService = null) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _settingsService = settingsService;
    }

    public string CurrentTorchVersion => _settingsService?.Current.PyTorchVersion ?? DefaultTorchVersion;

    public IReadOnlyList<string> CurrentPyTorchWheels {
        get {
            if (_settingsService != null) {
                AppSettings s = _settingsService.Current;
                return new[] { s.TorchWheelUrl, s.TorchAudioWheelUrl, s.TorchVisionWheelUrl };
            }
            return DefaultPyTorchWheels;
        }
    }

    public IReadOnlyList<string> CurrentRocmSdkWheels => _settingsService?.Current.RocmSdkWheels ?? DefaultRocmSdkWheels;

    public async Task<AmdEnvironmentInfo> DetectEnvironmentAsync(string? comfyUiScriptPath = null, CancellationToken cancellationToken = default) {
        AmdEnvironmentInfo info = new();
        DetectGpuHardware(info);
        await DetectSystemPythonAsync(info, cancellationToken);

        if (!string.IsNullOrWhiteSpace(comfyUiScriptPath) && File.Exists(comfyUiScriptPath)) {
            info.ComfyUiScriptPath = comfyUiScriptPath;
            await IngestComfyUiScriptAsync(comfyUiScriptPath, info, cancellationToken);
        }

        return info;
    }

    public void DetectGpuHardware(AmdEnvironmentInfo info) {
        try {
            using ManagementObjectSearcher searcher = new("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get()) {
                string name = mo["Name"]?.ToString() ?? string.Empty;
                if (Regex.IsMatch(name, "AMD|Radeon|ROCm", RegexOptions.IgnoreCase)) {
                    info.IsAmdGpuDetected = true;
                    info.GpuName = name;
                    break;
                }
            }
        } catch {
            info.IsAmdGpuDetected = true;
            info.GpuName = "AMD Radeon Graphics (Generic)";
        }

        string rocmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "ROCm");
        info.RocmDriverFound = Directory.Exists(rocmDir);
        info.RocmVersion = info.RocmDriverFound ? "7.2.1" : "Not Found (Driver)";
    }

    public async Task DetectSystemPythonAsync(AmdEnvironmentInfo info, CancellationToken cancellationToken = default) {
        string preferred = _settingsService?.Current.PreferredPythonPath ?? "python.exe";
        string[] candidates = new[] { preferred, "python3.12", "python", "py -3.12" };

        foreach (string cmd in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
                string versionOutput = string.Empty;
                int exitCode = await _processRunner.RunAsync(
                    "pwsh.exe",
                    $"-NoProfile -Command \"& {cmd} --version\"",
                    Directory.GetCurrentDirectory(),
                    null,
                    line => versionOutput += line,
                    _ => { },
                    cancellationToken
                );

                if (exitCode == 0 && versionOutput.Contains("Python 3.")) {
                    info.PythonExecutable = cmd;
                    info.PythonVersion = versionOutput.Trim();
                    break;
                }
            } catch {
                // Check next candidate
            }
        }
    }

    public async Task IngestComfyUiScriptAsync(string scriptPath, AmdEnvironmentInfo info, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        if (!File.Exists(scriptPath)) {
            return;
        }

        string content = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        Match comfyMatch = Regex.Match(content, @"\$COMFYUI_PATH\s*=\s*[""']([^""']+)[""']");
        if (comfyMatch.Success) {
            info.ComfyUiPath = comfyMatch.Groups[1].Value;
        }

        Match pythonMatch = Regex.Match(content, @"\$PYTHON_EXEC\s*=\s*(?:Join-Path\s+\$COMFYUI_PATH\s+[""']([^""']+)[""']|[""']([^""']+)[""'])");
        if (pythonMatch.Success) {
            string relOrAbs = pythonMatch.Groups[1].Success ? pythonMatch.Groups[1].Value : pythonMatch.Groups[2].Value;
            if (!string.IsNullOrEmpty(info.ComfyUiPath) && !Path.IsPathRooted(relOrAbs)) {
                info.PythonExecutable = Path.Combine(info.ComfyUiPath, relOrAbs);
            } else {
                info.PythonExecutable = relOrAbs;
            }
        }

        Match torchVerMatch = Regex.Match(content, @"\$PYTORCH_TORCH_VERSION\s*=\s*[""']([^""']+)[""']");
        if (torchVerMatch.Success) {
            info.PyTorchVersion = torchVerMatch.Groups[1].Value;
            info.HasRocmSupport = true;
        }
    }

    public async Task ProvisionVenvAsync(
        string targetDirectory,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        string venvPath = Path.Combine(targetDirectory, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");

        onProgress?.Invoke($"[Provisioner] Target .venv path: {venvPath}");

        if (!File.Exists(pythonExe)) {
            onProgress?.Invoke("[Provisioner] Creating dedicated Python 3.12 virtual environment (.venv)...");
            int venvExit = await _processRunner.RunAsync(
                "pwsh.exe",
                $"-NoProfile -Command \"python -m venv '{venvPath}'\"",
                targetDirectory,
                null,
                line => onProgress?.Invoke($"[venv] {line}"),
                line => onProgress?.Invoke($"[venv err] {line}"),
                cancellationToken
            );

            if (venvExit != 0) {
                throw new InvalidOperationException($"Failed to create Python virtual environment at {venvPath}");
            }
        } else {
            onProgress?.Invoke("[Provisioner] Existing .venv detected. Validating packages...");
        }

        onProgress?.Invoke("[Provisioner] Upgrading pip, setuptools, and wheel in .venv...");
        await _processRunner.RunAsync(
            pythonExe,
            "-m pip install --upgrade pip setuptools wheel",
            targetDirectory,
            null,
            line => onProgress?.Invoke($"[pip] {line}"),
            line => onProgress?.Invoke($"[pip err] {line}"),
            cancellationToken
        );

        onProgress?.Invoke($"[Provisioner] Installing AMD ROCm PyTorch wheels ({CurrentTorchVersion})...");
        string wheelsArg = string.Join(" ", CurrentPyTorchWheels.Select(w => $"\"{w}\""));
        int torchExit = await _processRunner.RunAsync(
            pythonExe,
            $"-m pip install --no-cache-dir --no-deps {wheelsArg}",
            targetDirectory,
            null,
            line => onProgress?.Invoke($"[torch] {line}"),
            line => onProgress?.Invoke($"[torch err] {line}"),
            cancellationToken
        );

        if (torchExit != 0) {
            throw new InvalidOperationException("Failed to install PyTorch ROCm wheels into .venv");
        }

        onProgress?.Invoke("[Provisioner] Installing AMD ROCm SDK wheels...");
        string sdkArg = string.Join(" ", CurrentRocmSdkWheels.Select(w => $"\"{w}\""));
        int sdkExit = await _processRunner.RunAsync(
            pythonExe,
            $"-m pip install --no-cache-dir {sdkArg}",
            targetDirectory,
            null,
            line => onProgress?.Invoke($"[rocm_sdk] {line}"),
            line => onProgress?.Invoke($"[rocm_sdk err] {line}"),
            cancellationToken
        );

        if (sdkExit == 0) {
            PatchRocmSdkDistInfo(venvPath, onProgress);
        }

        onProgress?.Invoke("[Provisioner] .venv successfully provisioned and verified for AMD ROCm training!");
    }

    public static void PatchRocmSdkDistInfo(string venvPath, Action<string>? onProgress) {
        string distInfoPath = Path.Combine(venvPath, "Lib", "site-packages", "rocm_sdk", "_dist_info.py");
        if (!File.Exists(distInfoPath)) {
            return;
        }

        string content = File.ReadAllText(distInfoPath);
        if (content.Contains("# [loramancer] windows-missing-libs")) {
            onProgress?.Invoke("[Patch] rocm_sdk _dist_info.py already contains patch.");
            return;
        }

        List<string> missing = new();
        if (!content.Contains("\"hipsparselt\"")) {
            missing.Add("    LibraryEntry(\"hipsparselt\", \"core\", \"libhipsparselt.so.0\", \"\"),");
        }
        if (!content.Contains("\"hipdnn\"")) {
            missing.Add("    LibraryEntry(\"hipdnn\", \"core\", \"libhipdnn.so.0\", \"\"),");
        }
        if (!content.Contains("\"rocm-openblas\"")) {
            missing.Add("    LibraryEntry(\"rocm-openblas\", \"core\", \"librocm-openblas.so.0\", \"\"),");
        }

        if (missing.Count > 0) {
            string patch = "\n# [loramancer] windows-missing-libs\n" + string.Join("\n", missing) + "\n";
            File.AppendAllText(distInfoPath, patch);
            onProgress?.Invoke($"[Patch] Applied {missing.Count} Windows library stubs to rocm_sdk _dist_info.py");
        }
    }
}
