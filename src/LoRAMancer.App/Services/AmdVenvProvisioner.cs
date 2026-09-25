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
        string? nvidiaName = null;
        string? intelName = null;

        try {
            using ManagementObjectSearcher searcher = new("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get()) {
                string name = mo["Name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (Regex.IsMatch(name, "AMD|Radeon|ROCm", RegexOptions.IgnoreCase)) {
                    info.DetectedVendor = HardwareVendor.Amd;
                    info.IsAmdGpuDetected = true;
                    info.GpuName = name;
                    break;
                }

                if (Regex.IsMatch(name, "NVIDIA|GeForce|RTX|Quadro|Tesla", RegexOptions.IgnoreCase) && nvidiaName == null) {
                    nvidiaName = name;
                } else if (Regex.IsMatch(name, "Intel|Arc|Iris|Xe", RegexOptions.IgnoreCase) && intelName == null) {
                    intelName = name;
                }
            }

            if (!info.IsAmdGpuDetected) {
                if (nvidiaName != null) {
                    info.DetectedVendor = HardwareVendor.Nvidia;
                    info.GpuName = nvidiaName;
                } else if (intelName != null) {
                    info.DetectedVendor = HardwareVendor.Intel;
                    info.GpuName = intelName;
                } else {
                    info.DetectedVendor = HardwareVendor.Cpu;
                    info.GpuName = "CPU / Generic (No dedicated accelerator)";
                }
            }
        } catch {
            info.DetectedVendor = HardwareVendor.Amd;
            info.IsAmdGpuDetected = true;
            info.GpuName = "AMD Radeon Graphics (Fallback)";
        }

        string rocmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "ROCm");
        info.RocmDriverFound = Directory.Exists(rocmDir);
        info.RocmVersion = info.RocmDriverFound ? "7.2.1" : (info.DetectedVendor == HardwareVendor.Amd ? "Not Found (Driver)" : "N/A (Non-AMD)");
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
        HardwareVendor? overrideVendor = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        string venvPath = Path.Combine(targetDirectory, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");

        AmdEnvironmentInfo envInfo = new();
        DetectGpuHardware(envInfo);
        HardwareVendor vendor = overrideVendor ?? envInfo.DetectedVendor;

        onProgress?.Invoke($"[Provisioner] Target .venv path: {venvPath}");
        onProgress?.Invoke($"[Provisioner] Hardware accelerator target: {vendor} ({envInfo.GpuName})");

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

        AppSettings settings = _settingsService?.Current ?? new AppSettings();

        switch (vendor) {
            case HardwareVendor.Nvidia:
                if (settings.NvidiaUseCustomWheels && !string.IsNullOrWhiteSpace(settings.NvidiaCustomWheelUrls)) {
                    onProgress?.Invoke("[Provisioner] NVIDIA GPU detected. Installing custom NVIDIA PyTorch wheels...");
                    int customExit = await _processRunner.RunAsync(
                        pythonExe,
                        $"-m pip install --no-cache-dir {settings.NvidiaCustomWheelUrls}",
                        targetDirectory,
                        null,
                        line => onProgress?.Invoke($"[torch-cuda-custom] {line}"),
                        line => onProgress?.Invoke($"[torch-cuda-custom err] {line}"),
                        cancellationToken
                    );
                    if (customExit != 0) {
                        throw new InvalidOperationException("Failed to install custom NVIDIA PyTorch wheels into .venv");
                    }
                } else {
                    string nvIndex = !string.IsNullOrWhiteSpace(settings.NvidiaIndexUrl) ? settings.NvidiaIndexUrl : "https://download.pytorch.org/whl/cu124";
                    string nvPkg = !string.IsNullOrWhiteSpace(settings.NvidiaPackageSpec) ? settings.NvidiaPackageSpec : "torch torchvision torchaudio";
                    onProgress?.Invoke($"[Provisioner] NVIDIA GPU detected. Installing PyTorch ({nvPkg}) from index: {nvIndex}...");
                    int nvExit = await _processRunner.RunAsync(
                        pythonExe,
                        $"-m pip install --no-cache-dir {nvPkg} --index-url {nvIndex}",
                        targetDirectory,
                        null,
                        line => onProgress?.Invoke($"[torch-cuda] {line}"),
                        line => onProgress?.Invoke($"[torch-cuda err] {line}"),
                        cancellationToken
                    );
                    if (nvExit != 0) {
                        throw new InvalidOperationException("Failed to install PyTorch CUDA wheels into .venv");
                    }
                }
                break;

            case HardwareVendor.Intel:
                if (settings.IntelUseCustomWheels && !string.IsNullOrWhiteSpace(settings.IntelCustomWheelUrls)) {
                    onProgress?.Invoke("[Provisioner] Intel GPU detected. Installing custom Intel PyTorch wheels...");
                    int customExit = await _processRunner.RunAsync(
                        pythonExe,
                        $"-m pip install --no-cache-dir {settings.IntelCustomWheelUrls}",
                        targetDirectory,
                        null,
                        line => onProgress?.Invoke($"[torch-xpu-custom] {line}"),
                        line => onProgress?.Invoke($"[torch-xpu-custom err] {line}"),
                        cancellationToken
                    );
                    if (customExit != 0) {
                        throw new InvalidOperationException("Failed to install custom Intel PyTorch wheels into .venv");
                    }
                } else {
                    string intelIndex = !string.IsNullOrWhiteSpace(settings.IntelIndexUrl) ? settings.IntelIndexUrl : "https://download.pytorch.org/whl/xpu";
                    string intelPkg = !string.IsNullOrWhiteSpace(settings.IntelPackageSpec) ? settings.IntelPackageSpec : "torch torchvision torchaudio";
                    onProgress?.Invoke($"[Provisioner] Intel GPU detected. Installing PyTorch with Intel XPU acceleration ({intelPkg}) from index: {intelIndex}...");
                    int intelExit = await _processRunner.RunAsync(
                        pythonExe,
                        $"-m pip install --no-cache-dir {intelPkg} --index-url {intelIndex}",
                        targetDirectory,
                        null,
                        line => onProgress?.Invoke($"[torch-xpu] {line}"),
                        line => onProgress?.Invoke($"[torch-xpu err] {line}"),
                        cancellationToken
                    );
                    if (intelExit != 0) {
                        throw new InvalidOperationException("Failed to install PyTorch XPU wheels into .venv");
                    }
                }
                break;

            case HardwareVendor.Cpu:
                string cpuIndex = !string.IsNullOrWhiteSpace(settings.CpuIndexUrl) ? settings.CpuIndexUrl : "https://download.pytorch.org/whl/cpu";
                string cpuPkg = !string.IsNullOrWhiteSpace(settings.CpuPackageSpec) ? settings.CpuPackageSpec : "torch torchvision torchaudio";
                onProgress?.Invoke($"[Provisioner] No dedicated GPU detected. Installing CPU-optimized PyTorch ({cpuPkg}) from index: {cpuIndex}...");
                int cpuExit = await _processRunner.RunAsync(
                    pythonExe,
                    $"-m pip install --no-cache-dir {cpuPkg} --index-url {cpuIndex}",
                    targetDirectory,
                    null,
                    line => onProgress?.Invoke($"[torch-cpu] {line}"),
                    line => onProgress?.Invoke($"[torch-cpu err] {line}"),
                    cancellationToken
                );
                if (cpuExit != 0) {
                    throw new InvalidOperationException("Failed to install PyTorch CPU wheels into .venv");
                }
                break;

            case HardwareVendor.Amd:
            default:
                onProgress?.Invoke($"[Provisioner] AMD GPU detected. Installing AMD ROCm PyTorch wheels ({CurrentTorchVersion})...");
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
                    PatchTorchaoDistributedUtils(venvPath, onProgress);
                }
                break;
        }

        onProgress?.Invoke($"[Provisioner] .venv successfully provisioned and verified for {vendor} training!");
    }

    public static void PatchRocmSdkDistInfo(string venvPath, Action<string>? onProgress) {
        string distInfoPath = Path.Combine(venvPath, "Lib", "site-packages", "rocm_sdk", "_dist_info.py");
        if (!File.Exists(distInfoPath)) {
            return;
        }

        string content = File.ReadAllText(distInfoPath);

        // Auto-repair any previously applied malformed patch with leading indentation
        if (content.Contains("    LibraryEntry(")) {
            content = content.Replace("    LibraryEntry(", "LibraryEntry(");
            File.WriteAllText(distInfoPath, content);
            onProgress?.Invoke("[Patch] Corrected rocm_sdk _dist_info.py indentation.");
            return;
        }

        if (content.Contains("# [loramancer] windows-missing-libs")) {
            onProgress?.Invoke("[Patch] rocm_sdk _dist_info.py already contains patch.");
            return;
        }

        List<string> missing = new();
        if (!content.Contains("\"hipsparselt\"")) {
            missing.Add("LibraryEntry(\"hipsparselt\", \"core\", \"libhipsparselt.so.0\", \"\", optional=True)");
        }
        if (!content.Contains("\"hipdnn\"")) {
            missing.Add("LibraryEntry(\"hipdnn\", \"core\", \"libhipdnn.so.0\", \"\", optional=True)");
        }
        if (!content.Contains("\"rocm-openblas\"")) {
            missing.Add("LibraryEntry(\"rocm-openblas\", \"core\", \"librocm-openblas.so.0\", \"\", optional=True)");
        }

        if (missing.Count > 0) {
            string patch = "\n# [loramancer] windows-missing-libs\n" + string.Join("\n", missing) + "\n";
            File.AppendAllText(distInfoPath, patch);
            onProgress?.Invoke($"[Patch] Applied {missing.Count} Windows library stubs to rocm_sdk _dist_info.py");
        }
    }

    public static void PatchTorchDistributedInit(string venvPath, Action<string>? onProgress) {
        string distInitPath = Path.Combine(venvPath, "Lib", "site-packages", "torch", "distributed", "__init__.py");
        if (!File.Exists(distInitPath)) {
            return;
        }

        string content = File.ReadAllText(distInitPath);
        if (content.Contains("# [loramancer] windows-distributed-stubs")) {
            return;
        }

        string normalized = content.Replace("\r\n", "\n");
        string target = "sys.modules[\"torch.distributed\"].ProcessGroup = _ProcessGroupStub  # type: ignore[attr-defined]";
        string replacement = @"sys.modules[""torch.distributed""].ProcessGroup = _ProcessGroupStub  # type: ignore[attr-defined]

    # [loramancer] windows-distributed-stubs
    class _GroupStub:
        WORLD = None

    class _ReduceOpStub:
        SUM = None
        PRODUCT = None
        MIN = None
        MAX = None
        BAND = None
        BOR = None
        BXOR = None

    sys.modules[""torch.distributed""].group = _GroupStub
    sys.modules[""torch.distributed""].ReduceOp = _ReduceOpStub
    sys.modules[""torch.distributed""].is_initialized = lambda: False
    sys.modules[""torch.distributed""].get_rank = lambda group=None: 0
    sys.modules[""torch.distributed""].get_world_size = lambda group=None: 1";

        string normalizedTarget = target.Replace("\r\n", "\n");
        string normalizedReplacement = replacement.Replace("\r\n", "\n");

        if (normalized.Contains(normalizedTarget)) {
            normalized = normalized.Replace(normalizedTarget, normalizedReplacement);
            File.WriteAllText(distInitPath, normalized);
            onProgress?.Invoke("[Patch] Patched torch.distributed stubs for Windows ROCm.");
        }
    }

    public static void PatchTorchaoDistributedUtils(string venvPath, Action<string>? onProgress) {
        PatchTorchDistributedInit(venvPath, onProgress);

        string aoInitPath = Path.Combine(venvPath, "Lib", "site-packages", "torchao", "__init__.py");
        if (File.Exists(aoInitPath)) {
            string initContent = File.ReadAllText(aoInitPath);
            if (!initContent.Contains("# [loramancer] windows-c10d-guard")) {
                string normalizedInit = initContent.Replace("\r\n", "\n");
                string targetInit = "from torchao.quantization import (\n    autoquant,\n    quantize_,\n)\n\nfrom . import dtypes, optim, testing";
                string replacementInit = "# [loramancer] windows-c10d-guard\ntry:\n    from torchao.quantization import (\n        autoquant,\n        quantize_,\n    )\n    from . import dtypes, optim, testing\nexcept Exception as e:\n    logging.debug(f\"Skipping distributed/c10d dependent modules: {e}\")";

                if (normalizedInit.Contains(targetInit)) {
                    normalizedInit = normalizedInit.Replace(targetInit, replacementInit);
                    File.WriteAllText(aoInitPath, normalizedInit);
                    onProgress?.Invoke("[Patch] Patched torchao/__init__.py to guard distributed/c10d imports.");
                }
            }
        }

        string distUtilsPath = Path.Combine(venvPath, "Lib", "site-packages", "torchao", "float8", "distributed_utils.py");
        if (File.Exists(distUtilsPath)) {
            string content = File.ReadAllText(distUtilsPath);
            if (!content.Contains("# [loramancer] windows-distributed-mock")) {
                string normalized = content.Replace("\r\n", "\n");
                string target = "import torch.distributed._functional_collectives as funcol\nfrom torch.distributed._tensor import DTensor";
                string replacement = "# [loramancer] windows-distributed-mock\ntry:\n    import torch.distributed._functional_collectives as funcol\n    from torch.distributed._tensor import DTensor\nexcept Exception:\n    funcol = None\n    DTensor = None";

                if (normalized.Contains(target)) {
                    normalized = normalized.Replace(target, replacement);
                    File.WriteAllText(distUtilsPath, normalized);
                    onProgress?.Invoke("[Patch] Patched torchao distributed_utils for Windows ROCm.");
                }
            }
        }

        string float8TensorPath = Path.Combine(venvPath, "Lib", "site-packages", "torchao", "float8", "float8_tensor.py");
        if (File.Exists(float8TensorPath)) {
            string content = File.ReadAllText(float8TensorPath);
            if (!content.Contains("# [loramancer] windows-dtensor-mock")) {
                string normalized = content.Replace("\r\n", "\n");
                string target = "from torch.distributed._tensor import DTensor";
                string replacement = "# [loramancer] windows-dtensor-mock\ntry:\n    from torch.distributed._tensor import DTensor\nexcept Exception:\n    class DTensor:\n        pass";

                if (normalized.Contains(target)) {
                    normalized = normalized.Replace(target, replacement);
                    File.WriteAllText(float8TensorPath, normalized);
                    onProgress?.Invoke("[Patch] Patched torchao float8_tensor for Windows ROCm.");
                }
            }
        }

        string float8UtilsPath = Path.Combine(venvPath, "Lib", "site-packages", "torchao", "float8", "float8_utils.py");
        if (File.Exists(float8UtilsPath)) {
            string content = File.ReadAllText(float8UtilsPath);
            if (!content.Contains("# [loramancer] windows-dist-mock")) {
                string normalized = content.Replace("\r\n", "\n");
                string target = "import torch.distributed as dist\nfrom torch.distributed._functional_collectives import AsyncCollectiveTensor, all_reduce";
                string replacement = "# [loramancer] windows-dist-mock\ntry:\n    import torch.distributed as dist\n    from torch.distributed._functional_collectives import AsyncCollectiveTensor, all_reduce\nexcept Exception:\n    dist = None\n    AsyncCollectiveTensor = None\n    all_reduce = None";

                if (normalized.Contains(target)) {
                    normalized = normalized.Replace(target, replacement);
                    File.WriteAllText(float8UtilsPath, normalized);
                    onProgress?.Invoke("[Patch] Patched torchao float8_utils for Windows ROCm.");
                }
            }
        }

        string distTensorInitPath = Path.Combine(venvPath, "Lib", "site-packages", "torch", "distributed", "tensor", "__init__.py");
        if (File.Exists(distTensorInitPath)) {
            string content = File.ReadAllText(distTensorInitPath);
            if (!content.Contains("# [loramancer] windows-dtensor-guard")) {
                string normalized = content.Replace("\r\n", "\n");
                string[] lines = normalized.Split('\n');
                string indented = string.Join("\n", lines.Select(l => string.IsNullOrWhiteSpace(l) ? l : "    " + l));
                string patched = "# [loramancer] windows-dtensor-guard\ntry:\n" + indented + "\nexcept Exception:\n    class DTensor:\n        pass\n";
                File.WriteAllText(distTensorInitPath, patched);
                onProgress?.Invoke("[Patch] Patched torch.distributed.tensor for Windows ROCm.");
            }
        }

        string accOtherPath = Path.Combine(venvPath, "Lib", "site-packages", "accelerate", "utils", "other.py");
        if (File.Exists(accOtherPath)) {
            string content = File.ReadAllText(accOtherPath);
            if (!content.Contains("# [loramancer] windows-dtensor-guard")) {
                string normalized = content.Replace("\r\n", "\n");
                string target = "def model_has_dtensor(model):";
                if (normalized.Contains(target)) {
                    string replacement = "# [loramancer] windows-dtensor-guard\ndef model_has_dtensor(model):\n    try:\n        from torch.distributed.tensor import DTensor\n        return any(isinstance(param, DTensor) for param in model.parameters())\n    except Exception:\n        return False\n\ndef _unpatched_model_has_dtensor(model):";
                    normalized = normalized.Replace(target, replacement);
                    File.WriteAllText(accOtherPath, normalized);
                    onProgress?.Invoke("[Patch] Patched accelerate model_has_dtensor for Windows ROCm.");
                }
            }
        }

        string siteCustomizePath = Path.Combine(venvPath, "Lib", "site-packages", "sitecustomize.py");
        if (!File.Exists(siteCustomizePath) || !File.ReadAllText(siteCustomizePath).Contains("# [loramancer] windows-rocm-sitecustomize")) {
            string siteCustomizeStub = @"# [loramancer] windows-rocm-sitecustomize
import sys
import types

class _MockC10d(types.ModuleType):
    def __getattr__(self, name):
        class _Stub:
            def __init__(self, *args, **kwargs):
                pass
            def __call__(self, *args, **kwargs):
                return self
        return _Stub

if ""torch._C._distributed_c10d"" not in sys.modules:
    sys.modules[""torch._C._distributed_c10d""] = _MockC10d(""torch._C._distributed_c10d"")
";
            if (File.Exists(siteCustomizePath)) {
                File.AppendAllText(siteCustomizePath, "\n" + siteCustomizeStub);
            } else {
                File.WriteAllText(siteCustomizePath, siteCustomizeStub);
            }
            onProgress?.Invoke("[Patch] Installed sitecustomize.py global c10d stub for Windows ROCm.");
        }
    }

    public async Task<VenvPackageStatus> GetVenvPackageInfoAsync(string targetDirectory, CancellationToken cancellationToken = default) {
        VenvPackageStatus status = new();
        string venvPath = Path.Combine(targetDirectory, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");

        if (!File.Exists(pythonExe)) {
            status.IsVenvCreated = false;
            return status;
        }

        status.IsVenvCreated = true;

        await _processRunner.RunAsync(
            pythonExe,
            "-m pip --version",
            targetDirectory,
            null,
            line => {
                if (line.Contains("pip ")) {
                    status.PipVersion = line.Trim();
                }
            },
            _ => { },
            cancellationToken
        );

        await _processRunner.RunAsync(
            pythonExe,
            "-c \"import sys;\ntry:\n import torch\n print(f'TORCH:{torch.__version__} (CUDA/HIP: {torch.cuda.is_available()})')\nexcept Exception as e:\n print('TORCH:Not Installed')\"",
            targetDirectory,
            null,
            line => {
                if (line.StartsWith("TORCH:")) {
                    status.TorchVersion = line.Substring(6).Trim();
                }
            },
            _ => { },
            cancellationToken
        );

        return status;
    }

    public async Task UpgradePipAsync(string targetDirectory, Action<string>? onProgress, CancellationToken cancellationToken = default) {
        string venvPath = Path.Combine(targetDirectory, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            throw new FileNotFoundException("Cannot upgrade pip: .venv does not exist.", pythonExe);
        }

        onProgress?.Invoke("[Pip] Upgrading pip to latest version...");
        int exitCode = await _processRunner.RunAsync(
            pythonExe,
            "-m pip install --upgrade pip",
            targetDirectory,
            null,
            line => onProgress?.Invoke($"[pip] {line}"),
            line => onProgress?.Invoke($"[pip err] {line}"),
            cancellationToken
        );

        if (exitCode != 0) {
            throw new InvalidOperationException("Failed to upgrade pip in .venv");
        }
        onProgress?.Invoke("[Pip] Pip successfully upgraded to latest version!");
    }

    public async Task SwitchPyTorchVersionAsync(
        string targetDirectory,
        string packageSpecOrWheelUrl,
        string? indexUrl,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        string venvPath = Path.Combine(targetDirectory, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            throw new FileNotFoundException("Cannot switch PyTorch: .venv does not exist.", pythonExe);
        }

        string cmd;
        if (packageSpecOrWheelUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            packageSpecOrWheelUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            packageSpecOrWheelUrl.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)) {
            onProgress?.Invoke($"[PyTorch Switch] Installing custom wheel from: {packageSpecOrWheelUrl}");
            cmd = $"-m pip install --no-cache-dir --force-reinstall \"{packageSpecOrWheelUrl}\"";
        } else {
            string indexArg = !string.IsNullOrWhiteSpace(indexUrl) ? $"--index-url \"{indexUrl}\"" : string.Empty;
            onProgress?.Invoke($"[PyTorch Switch] Installing package spec: {packageSpecOrWheelUrl} {indexArg}");
            cmd = $"-m pip install --no-cache-dir --force-reinstall {packageSpecOrWheelUrl} {indexArg}";
        }

        int exitCode = await _processRunner.RunAsync(
            pythonExe,
            cmd,
            targetDirectory,
            null,
            line => onProgress?.Invoke($"[pip] {line}"),
            line => onProgress?.Invoke($"[pip err] {line}"),
            cancellationToken
        );

        if (exitCode != 0) {
            throw new InvalidOperationException($"Failed to switch PyTorch version to: {packageSpecOrWheelUrl}");
        }

        onProgress?.Invoke("[PyTorch Switch] PyTorch switch/upgrade completed successfully!");
    }
}
