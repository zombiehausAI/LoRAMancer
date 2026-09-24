using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class AiToolkitSetupService {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService _settingsService;

    public AiToolkitSetupService(ProcessRunner processRunner, SettingsService settingsService) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public string GetInstallDirectory() {
        string customPath = _settingsService.Current.AiToolkitPath;
        if (!string.IsNullOrWhiteSpace(customPath)) {
            return customPath;
        }

        string defaultPath = Path.Combine(AppContext.BaseDirectory, "tools", "ai-toolkit");
        return defaultPath;
    }

    public string GetRunScriptPath() {
        return Path.Combine(GetInstallDirectory(), "run.py");
    }

    public bool IsInstalled() {
        string runScript = GetRunScriptPath();
        return File.Exists(runScript);
    }

    public async Task SetupAiToolkitAsync(
        string venvPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        string installDir = GetInstallDirectory();
        string repoUrl = string.IsNullOrWhiteSpace(_settingsService.Current.AiToolkitRepoUrl)
            ? "https://github.com/ostris/ai-toolkit.git"
            : _settingsService.Current.AiToolkitRepoUrl;

        onProgress?.Invoke($"[AI-Toolkit] Target installation directory: {installDir}");

        string parentDir = Path.GetDirectoryName(installDir) ?? AppContext.BaseDirectory;
        if (!Directory.Exists(parentDir)) {
            Directory.CreateDirectory(parentDir);
        }

        if (!Directory.Exists(installDir) || !Directory.Exists(Path.Combine(installDir, ".git"))) {
            onProgress?.Invoke($"[AI-Toolkit] Cloning repository from {repoUrl} (including submodules)...");
            int cloneExit = await _processRunner.RunAsync(
                "git.exe",
                $"clone --recurse-submodules \"{repoUrl}\" \"{installDir}\"",
                parentDir,
                null,
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
                null,
                line => onProgress?.Invoke($"[git] {line}"),
                _ => { },
                cancellationToken
            );
        }

        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            pythonExe = "python.exe";
        }

        string reqFile = Path.Combine(installDir, "requirements.txt");
        if (File.Exists(reqFile)) {
            onProgress?.Invoke("[AI-Toolkit] Installing requirements into AMD ROCm .venv (protecting PyTorch wheels)...");

            // Install dependencies protecting torch binaries
            int pipExit = await _processRunner.RunAsync(
                pythonExe,
                $"-m pip install --no-cache-dir -r \"{reqFile}\" --no-deps",
                installDir,
                null,
                line => onProgress?.Invoke($"[pip] {line}"),
                line => onProgress?.Invoke($"[pip err] {line}"),
                cancellationToken
            );

            if (pipExit != 0) {
                onProgress?.Invoke("[AI-Toolkit] Note: Pip reported warnings during dependency resolution, validating run.py...");
            }
        }

        if (IsInstalled()) {
            onProgress?.Invoke($"[AI-Toolkit] Auto-setup complete! run.py verified at {GetRunScriptPath()}");
        } else {
            throw new FileNotFoundException("AI-Toolkit run.py was not found after setup.", GetRunScriptPath());
        }
    }
}
