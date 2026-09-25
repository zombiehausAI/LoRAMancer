using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class AiToolkitSetupService {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService _settingsService;
    private readonly AmdVenvProvisioner? _venvProvisioner;

    public AiToolkitSetupService(
        ProcessRunner processRunner,
        SettingsService settingsService,
        AmdVenvProvisioner? venvProvisioner = null
    ) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _venvProvisioner = venvProvisioner;
    }

    public static string GetApplicationRoot() {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (baseDir.EndsWith("bin", StringComparison.OrdinalIgnoreCase)) {
            string? parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrEmpty(parent)) {
                return parent;
            }
        }
        return baseDir;
    }

    public static string GetDefaultVenvPath() {
        return Path.Combine(GetApplicationRoot(), ".venv");
    }

    public Dictionary<string, string> GetIsolatedEnvironmentVariables() {
        string appRoot = GetApplicationRoot();
        string cacheRoot = Path.Combine(appRoot, "cache");
        string tempDir = Path.Combine(cacheRoot, "temp");
        string pipCacheDir = Path.Combine(cacheRoot, "pip");
        string hfCacheDir = !string.IsNullOrWhiteSpace(_settingsService.Current.HfHomeCachePath)
            ? _settingsService.Current.HfHomeCachePath
            : Path.Combine(cacheRoot, "huggingface");
        string torchCacheDir = Path.Combine(cacheRoot, "torch");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(pipCacheDir);
        Directory.CreateDirectory(hfCacheDir);
        Directory.CreateDirectory(torchCacheDir);

        var env = new Dictionary<string, string> {
            ["TEMP"] = tempDir,
            ["TMP"] = tempDir,
            ["TMPDIR"] = tempDir,
            ["PIP_CACHE_DIR"] = pipCacheDir,
            ["HF_HOME"] = hfCacheDir,
            ["TORCH_HOME"] = torchCacheDir,
            ["PYTHONNOUSERSITE"] = "1",
            ["PIP_NO_WARN_SCRIPT_LOCATION"] = "0"
        };

        string hfToken = _settingsService.Current.HuggingFaceToken?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(hfToken)) {
            env["HF_TOKEN"] = hfToken;
            env["HUGGING_FACE_HUB_TOKEN"] = hfToken;
        }

        return env;
    }

    public string GetInstallDirectory() {
        string customPath = _settingsService.Current.AiToolkitPath;
        if (!string.IsNullOrWhiteSpace(customPath)) {
            return customPath;
        }

        string defaultPath = Path.Combine(GetApplicationRoot(), "tools", "ai-toolkit");
        return defaultPath;
    }

    public string GetRunScriptPath() {
        return Path.Combine(GetInstallDirectory(), "run.py");
    }

    public bool IsInstalled() {
        string runScript = GetRunScriptPath();
        return File.Exists(runScript);
    }

    public bool IsGitRepository() {
        string installDir = GetInstallDirectory();
        return Directory.Exists(Path.Combine(installDir, ".git"));
    }

    public async Task<string> GetCurrentCommitAsync(CancellationToken cancellationToken = default) {
        string installDir = GetInstallDirectory();
        if (!IsGitRepository()) {
            return "Not a Git repository";
        }

        string commit = string.Empty;
        int exit = await _processRunner.RunAsync(
            "git.exe",
            "log -1 --format=\"%h (%cd) - %s\" --date=short",
            installDir,
            GetIsolatedEnvironmentVariables(),
            line => {
                if (string.IsNullOrEmpty(commit)) {
                    commit = line.Trim();
                }
            },
            _ => { },
            cancellationToken
        );

        return exit == 0 && !string.IsNullOrWhiteSpace(commit) ? commit : "Git repository detected";
    }

    public async Task UpdateAiToolkitAsync(
        string venvPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        string installDir = GetInstallDirectory();
        var envVars = GetIsolatedEnvironmentVariables();

        if (!Directory.Exists(installDir) || !IsGitRepository()) {
            onProgress?.Invoke("[AI-Toolkit] Repository not found. Performing full setup instead...");
            await SetupAiToolkitAsync(venvPath, onProgress, cancellationToken);
            return;
        }

        onProgress?.Invoke($"[AI-Toolkit] Pulling latest changes in {installDir}...");
        int pullExit = await _processRunner.RunAsync(
            "git.exe",
            "pull --recurse-submodules",
            installDir,
            envVars,
            line => onProgress?.Invoke($"[git] {line}"),
            line => onProgress?.Invoke($"[git err] {line}"),
            cancellationToken
        );

        if (pullExit != 0) {
            throw new InvalidOperationException("Failed to pull latest AI-Toolkit updates via Git.");
        }

        onProgress?.Invoke("[AI-Toolkit] Synchronizing submodules...");
        await _processRunner.RunAsync(
            "git.exe",
            "submodule update --init --recursive",
            installDir,
            envVars,
            line => onProgress?.Invoke($"[git] {line}"),
            _ => { },
            cancellationToken
        );

        string effectiveVenv = !string.IsNullOrWhiteSpace(venvPath) && Directory.Exists(venvPath)
            ? venvPath
            : GetDefaultVenvPath();

        string pythonExe = Path.Combine(effectiveVenv, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            if (_venvProvisioner != null) {
                onProgress?.Invoke($"[AI-Toolkit] Dedicated .venv not found at {effectiveVenv}. Provisioning compute environment first...");
                await _venvProvisioner.ProvisionVenvAsync(GetApplicationRoot(), onProgress, cancellationToken: cancellationToken);
                pythonExe = Path.Combine(effectiveVenv, "Scripts", "python.exe");
            }
        }

        if (!File.Exists(pythonExe)) {
            throw new InvalidOperationException(
                $"Cannot update AI-Toolkit: Dedicated virtual environment was not found at '{effectiveVenv}'. " +
                "Please provision the compute environment from the Compute Environment page first to prevent polluting your home directory.");
        }

        string reqFile = Path.Combine(installDir, "requirements.txt");
        if (File.Exists(reqFile)) {
            onProgress?.Invoke("[AI-Toolkit] Updating dependencies in .venv (protecting PyTorch wheels)...");
            await _processRunner.RunAsync(
                pythonExe,
                $"-m pip install --no-cache-dir --no-user -r \"{reqFile}\" sympy networkx jinja2",
                installDir,
                envVars,
                line => onProgress?.Invoke($"[pip] {line}"),
                line => onProgress?.Invoke($"[pip err] {line}"),
                cancellationToken
            );

            // Patch torchao distributed_utils for Windows ROCm (so both torchao and diffusers work)
            AmdVenvProvisioner.PatchTorchaoDistributedUtils(effectiveVenv, onProgress);
        }

        onProgress?.Invoke("[AI-Toolkit] AI-Toolkit successfully updated to latest version!");
    }

    public async Task SetupAiToolkitAsync(
        string venvPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        string installDir = GetInstallDirectory();
        var envVars = GetIsolatedEnvironmentVariables();
        string repoUrl = string.IsNullOrWhiteSpace(_settingsService.Current.AiToolkitRepoUrl)
            ? "https://github.com/ostris/ai-toolkit.git"
            : _settingsService.Current.AiToolkitRepoUrl;

        onProgress?.Invoke($"[AI-Toolkit] Target installation directory: {installDir}");

        string parentDir = Path.GetDirectoryName(installDir) ?? GetApplicationRoot();
        if (!Directory.Exists(parentDir)) {
            Directory.CreateDirectory(parentDir);
        }

        if (!Directory.Exists(installDir) || !Directory.Exists(Path.Combine(installDir, ".git"))) {
            onProgress?.Invoke($"[AI-Toolkit] Cloning repository from {repoUrl} (including submodules)...");
            int cloneExit = await _processRunner.RunAsync(
                "git.exe",
                $"clone --recurse-submodules \"{repoUrl}\" \"{installDir}\"",
                parentDir,
                envVars,
                line => onProgress?.Invoke($"[git] {line}"),
                line => onProgress?.Invoke($"[git err] {line}"),
                cancellationToken
            );

            if (cloneExit != 0) {
                throw new InvalidOperationException($"Failed to clone AI-Toolkit repository from {repoUrl}");
            }
        } else {
            onProgress?.Invoke("[AI-Toolkit] Existing repository detected. Updating submodules...");
            await _processRunner.RunAsync(
                "git.exe",
                "submodule update --init --recursive",
                installDir,
                envVars,
                line => onProgress?.Invoke($"[git] {line}"),
                _ => { },
                cancellationToken
            );
        }

        string effectiveVenv = !string.IsNullOrWhiteSpace(venvPath) && Directory.Exists(venvPath)
            ? venvPath
            : GetDefaultVenvPath();

        string pythonExe = Path.Combine(effectiveVenv, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            if (_venvProvisioner != null) {
                onProgress?.Invoke($"[AI-Toolkit] Dedicated .venv not found at {effectiveVenv}. Provisioning compute environment first...");
                await _venvProvisioner.ProvisionVenvAsync(GetApplicationRoot(), onProgress, cancellationToken: cancellationToken);
                pythonExe = Path.Combine(effectiveVenv, "Scripts", "python.exe");
            }
        }

        if (!File.Exists(pythonExe)) {
            throw new InvalidOperationException(
                $"Cannot setup AI-Toolkit: Dedicated virtual environment was not found at '{effectiveVenv}'. " +
                "Please provision the compute environment from the Compute Environment page first to prevent polluting your home directory.");
        }

        string reqFile = Path.Combine(installDir, "requirements.txt");
        if (File.Exists(reqFile)) {
            onProgress?.Invoke("[AI-Toolkit] Installing requirements into compute .venv (protecting PyTorch wheels)...");

            // Install dependencies protecting torch binaries (without --upgrade to preserve ROCm torch)
            int pipExit = await _processRunner.RunAsync(
                pythonExe,
                $"-m pip install --no-cache-dir --no-user -r \"{reqFile}\" sympy networkx jinja2",
                installDir,
                envVars,
                line => onProgress?.Invoke($"[pip] {line}"),
                line => onProgress?.Invoke($"[pip err] {line}"),
                cancellationToken
            );

            if (pipExit != 0) {
                onProgress?.Invoke("[AI-Toolkit] Note: Pip reported warnings during dependency resolution, validating run.py...");
            }

            // Patch torchao distributed_utils for Windows ROCm (so both torchao and diffusers work)
            AmdVenvProvisioner.PatchTorchaoDistributedUtils(effectiveVenv, onProgress);
        }

        if (IsInstalled()) {
            onProgress?.Invoke($"[AI-Toolkit] Auto-setup complete! run.py verified at {GetRunScriptPath()}");
        } else {
            throw new FileNotFoundException("AI-Toolkit run.py was not found after setup.", GetRunScriptPath());
        }
    }
}
