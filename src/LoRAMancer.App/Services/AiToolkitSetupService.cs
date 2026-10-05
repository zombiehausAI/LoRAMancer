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
            ["PIP_NO_WARN_SCRIPT_LOCATION"] = "0",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_PAGER"] = "cat",
            ["GIT_OPTIONAL_LOCKS"] = "0"
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

        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

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
                cts.Token
            );

            return exit == 0 && !string.IsNullOrWhiteSpace(commit) ? commit : "Git repository detected";
        } catch {
            return "Git repository detected";
        }
    }

    public async Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) {
        string installDir = GetInstallDirectory();
        if (!IsGitRepository()) {
            return string.Empty;
        }

        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            string branch = string.Empty;
            int exit = await _processRunner.RunAsync(
                "git.exe",
                "branch --show-current",
                installDir,
                GetIsolatedEnvironmentVariables(),
                line => {
                    if (string.IsNullOrEmpty(branch)) {
                        branch = line.Trim();
                    }
                },
                _ => { },
                cts.Token
            );

            return exit == 0 && !string.IsNullOrWhiteSpace(branch) ? branch : "main";
        } catch {
            return "main";
        }
    }

    public async Task<bool> IsGitInstalledAsync(CancellationToken cancellationToken = default) {
        try {
            int exit = await _processRunner.RunAsync(
                "git.exe",
                "--version",
                GetApplicationRoot(),
                GetIsolatedEnvironmentVariables(),
                _ => { },
                _ => { },
                cancellationToken
            );
            return exit == 0;
        } catch {
            return false;
        }
    }

    public AiToolkitUpdateCheckResult? LatestUpdateCheckResult { get; private set; }
    public event Action<AiToolkitUpdateCheckResult>? OnUpdateChecked;

    public async Task<AiToolkitUpdateCheckResult> CheckForUpdatesAsync(
        string? targetBranch = null,
        CancellationToken cancellationToken = default
    ) {
        string branch = !string.IsNullOrWhiteSpace(targetBranch)
            ? targetBranch.Trim()
            : (!string.IsNullOrWhiteSpace(_settingsService.Current.AiToolkitBranch) ? _settingsService.Current.AiToolkitBranch.Trim() : "main");

        string installDir = GetInstallDirectory();
        if (!await IsGitInstalledAsync(cancellationToken)) {
            var noGitResult = new AiToolkitUpdateCheckResult {
                IsInstalled = IsInstalled(),
                IsGitRepository = IsGitRepository(),
                HasUpdate = false,
                CommitsBehind = 0,
                Branch = branch,
                ErrorMessage = "Git is not installed or not found on PATH. Git for Windows (https://git-scm.com/) is a required dependency.",
                StatusMessage = "Git prerequisite missing"
            };
            LatestUpdateCheckResult = noGitResult;
            OnUpdateChecked?.Invoke(noGitResult);
            return noGitResult;
        }

        if (!IsInstalled() || !IsGitRepository()) {
            var notInstalledResult = new AiToolkitUpdateCheckResult {
                IsInstalled = IsInstalled(),
                IsGitRepository = IsGitRepository(),
                HasUpdate = false,
                CommitsBehind = 0,
                Branch = branch,
                StatusMessage = !IsInstalled() ? "AI-Toolkit is not installed" : "Installation is not a Git repository"
            };
            LatestUpdateCheckResult = notInstalledResult;
            OnUpdateChecked?.Invoke(notInstalledResult);
            return notInstalledResult;
        }

        var envVars = GetIsolatedEnvironmentVariables();
        string currentCommit = await GetCurrentCommitAsync(cancellationToken);

        string currentBranch = string.Empty;
        await _processRunner.RunAsync(
            "git.exe",
            "branch --show-current",
            installDir,
            envVars,
            line => {
                if (string.IsNullOrEmpty(currentBranch)) {
                    currentBranch = line.Trim();
                }
            },
            _ => { },
            cancellationToken
        );

        string fetchError = string.Empty;
        int fetchExit = await _processRunner.RunAsync(
            "git.exe",
            $"fetch origin \"{branch}\"",
            installDir,
            envVars,
            _ => { },
            errLine => {
                if (string.IsNullOrEmpty(fetchError) && !string.IsNullOrWhiteSpace(errLine)) {
                    fetchError = errLine.Trim();
                }
            },
            cancellationToken
        );

        if (fetchExit != 0) {
            var failResult = new AiToolkitUpdateCheckResult {
                IsInstalled = true,
                IsGitRepository = true,
                HasUpdate = false,
                CommitsBehind = 0,
                Branch = branch,
                CurrentBranch = currentBranch,
                CurrentCommit = currentCommit,
                ErrorMessage = !string.IsNullOrWhiteSpace(fetchError) ? fetchError : $"Failed to fetch branch '{branch}' from remote origin",
                StatusMessage = $"Unable to reach remote branch '{branch}'"
            };
            LatestUpdateCheckResult = failResult;
            OnUpdateChecked?.Invoke(failResult);
            return failResult;
        }

        int commitsBehind = 0;
        await _processRunner.RunAsync(
            "git.exe",
            $"rev-list --count HEAD..origin/\"{branch}\"",
            installDir,
            envVars,
            line => {
                if (int.TryParse(line.Trim(), out int count)) {
                    commitsBehind = count;
                }
            },
            _ => { },
            cancellationToken
        );

        string remoteCommit = string.Empty;
        if (commitsBehind > 0) {
            await _processRunner.RunAsync(
                "git.exe",
                $"log -1 --format=\"%h (%cd) - %s\" --date=short origin/\"{branch}\"",
                installDir,
                envVars,
                line => {
                    if (string.IsNullOrEmpty(remoteCommit)) {
                        remoteCommit = line.Trim();
                    }
                },
                _ => { },
                cancellationToken
            );
        }

        var result = new AiToolkitUpdateCheckResult {
            IsInstalled = true,
            IsGitRepository = true,
            HasUpdate = commitsBehind > 0,
            CommitsBehind = commitsBehind,
            Branch = branch,
            CurrentBranch = currentBranch,
            CurrentCommit = currentCommit,
            RemoteCommit = remoteCommit,
            StatusMessage = commitsBehind > 0
                ? $"{commitsBehind} commit(s) behind origin/{branch}"
                : $"Up to date with origin/{branch}"
        };

        LatestUpdateCheckResult = result;
        OnUpdateChecked?.Invoke(result);
        return result;
    }

    public Task UpdateAiToolkitAsync(
        string venvPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) => UpdateAiToolkitAsync(venvPath, targetBranch: null, onProgress, cancellationToken);

    public async Task UpdateAiToolkitAsync(
        string venvPath,
        string? targetBranch = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        string installDir = GetInstallDirectory();
        var envVars = GetIsolatedEnvironmentVariables();

        string branch = !string.IsNullOrWhiteSpace(targetBranch)
            ? targetBranch.Trim()
            : (!string.IsNullOrWhiteSpace(_settingsService.Current.AiToolkitBranch) ? _settingsService.Current.AiToolkitBranch.Trim() : "main");

        if (!await IsGitInstalledAsync(cancellationToken)) {
            throw new InvalidOperationException("Git is not installed or not found on PATH. Git for Windows is a required dependency to update AI-Toolkit. Please install Git from https://git-scm.com/ and restart LoRAMancer.");
        }

        if (!Directory.Exists(installDir) || !IsGitRepository()) {
            onProgress?.Invoke("[AI-Toolkit] Repository not found. Performing full setup instead...");
            await SetupAiToolkitAsync(venvPath, branch, onProgress, cancellationToken);
            return;
        }

        onProgress?.Invoke($"[AI-Toolkit] Fetching updates from origin/{branch} in {installDir}...");
        await _processRunner.RunAsync(
            "git.exe",
            $"fetch origin \"{branch}\"",
            installDir,
            envVars,
            line => onProgress?.Invoke($"[git] {line}"),
            line => onProgress?.Invoke($"[git err] {line}"),
            cancellationToken
        );

        string currentBranch = string.Empty;
        await _processRunner.RunAsync(
            "git.exe",
            "branch --show-current",
            installDir,
            envVars,
            line => {
                if (string.IsNullOrEmpty(currentBranch)) {
                    currentBranch = line.Trim();
                }
            },
            _ => { },
            cancellationToken
        );

        if (!string.Equals(currentBranch, branch, StringComparison.OrdinalIgnoreCase)) {
            onProgress?.Invoke($"[AI-Toolkit] Switching branch from '{currentBranch}' to '{branch}'...");
            int checkoutExit = await _processRunner.RunAsync(
                "git.exe",
                $"checkout \"{branch}\"",
                installDir,
                envVars,
                line => onProgress?.Invoke($"[git] {line}"),
                line => onProgress?.Invoke($"[git err] {line}"),
                cancellationToken
            );
            if (checkoutExit != 0) {
                onProgress?.Invoke($"[AI-Toolkit] Creating and tracking local branch '{branch}' from 'origin/{branch}'...");
                await _processRunner.RunAsync(
                    "git.exe",
                    $"checkout -B \"{branch}\" \"origin/{branch}\"",
                    installDir,
                    envVars,
                    line => onProgress?.Invoke($"[git] {line}"),
                    line => onProgress?.Invoke($"[git err] {line}"),
                    cancellationToken
                );
            }
        }

        onProgress?.Invoke($"[AI-Toolkit] Pulling latest changes for branch '{branch}' in {installDir}...");
        int pullExit = await _processRunner.RunAsync(
            "git.exe",
            $"pull origin \"{branch}\" --recurse-submodules",
            installDir,
            envVars,
            line => onProgress?.Invoke($"[git] {line}"),
            line => onProgress?.Invoke($"[git err] {line}"),
            cancellationToken
        );

        if (pullExit != 0) {
            throw new InvalidOperationException($"Failed to pull latest AI-Toolkit updates for branch '{branch}' via Git.");
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

        string updatedCommit = await GetCurrentCommitAsync(cancellationToken);
        var status = new AiToolkitUpdateCheckResult {
            IsInstalled = true,
            IsGitRepository = true,
            HasUpdate = false,
            CommitsBehind = 0,
            Branch = branch,
            CurrentBranch = branch,
            CurrentCommit = updatedCommit,
            StatusMessage = $"Updated to {updatedCommit}"
        };
        LatestUpdateCheckResult = status;
        OnUpdateChecked?.Invoke(status);

        onProgress?.Invoke("[AI-Toolkit] AI-Toolkit successfully updated to latest version!");
    }

    public Task SetupAiToolkitAsync(
        string venvPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) => SetupAiToolkitAsync(venvPath, targetBranch: null, onProgress, cancellationToken);

    public async Task SetupAiToolkitAsync(
        string venvPath,
        string? targetBranch = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        string installDir = GetInstallDirectory();
        var envVars = GetIsolatedEnvironmentVariables();
        string repoUrl = string.IsNullOrWhiteSpace(_settingsService.Current.AiToolkitRepoUrl)
            ? "https://github.com/ostris/ai-toolkit.git"
            : _settingsService.Current.AiToolkitRepoUrl;
        string branch = !string.IsNullOrWhiteSpace(targetBranch)
            ? targetBranch.Trim()
            : (!string.IsNullOrWhiteSpace(_settingsService.Current.AiToolkitBranch) ? _settingsService.Current.AiToolkitBranch.Trim() : "main");

        onProgress?.Invoke($"[AI-Toolkit] Target installation directory: {installDir}");

        if (!await IsGitInstalledAsync(cancellationToken)) {
            throw new InvalidOperationException("Git is not installed or not found on PATH. Git for Windows is a required dependency to clone and setup AI-Toolkit. Please install Git from https://git-scm.com/ and restart LoRAMancer.");
        }

        string parentDir = Path.GetDirectoryName(installDir) ?? GetApplicationRoot();
        if (!Directory.Exists(parentDir)) {
            Directory.CreateDirectory(parentDir);
        }

        if (!Directory.Exists(installDir) || !Directory.Exists(Path.Combine(installDir, ".git"))) {
            onProgress?.Invoke($"[AI-Toolkit] Cloning repository (branch: '{branch}') from {repoUrl} (including submodules)...");
            int cloneExit = await _processRunner.RunAsync(
                "git.exe",
                $"clone -b \"{branch}\" --recurse-submodules \"{repoUrl}\" \"{installDir}\"",
                parentDir,
                envVars,
                line => onProgress?.Invoke($"[git] {line}"),
                line => onProgress?.Invoke($"[git err] {line}"),
                cancellationToken
            );

            if (cloneExit != 0) {
                onProgress?.Invoke($"[AI-Toolkit] Clone on branch '{branch}' failed. Falling back to default branch clone...");
                cloneExit = await _processRunner.RunAsync(
                    "git.exe",
                    $"clone --recurse-submodules \"{repoUrl}\" \"{installDir}\"",
                    parentDir,
                    envVars,
                    line => onProgress?.Invoke($"[git] {line}"),
                    line => onProgress?.Invoke($"[git err] {line}"),
                    cancellationToken
                );
            }

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
            string setupCommit = await GetCurrentCommitAsync(cancellationToken);
            var status = new AiToolkitUpdateCheckResult {
                IsInstalled = true,
                IsGitRepository = true,
                HasUpdate = false,
                CommitsBehind = 0,
                Branch = branch,
                CurrentBranch = branch,
                CurrentCommit = setupCommit,
                StatusMessage = $"Installed {setupCommit}"
            };
            LatestUpdateCheckResult = status;
            OnUpdateChecked?.Invoke(status);

            onProgress?.Invoke($"[AI-Toolkit] Auto-setup complete! run.py verified at {GetRunScriptPath()}");
        } else {
            throw new FileNotFoundException("AI-Toolkit run.py was not found after setup.", GetRunScriptPath());
        }
    }
}

public sealed class AiToolkitUpdateCheckResult {
    public bool IsInstalled { get; set; }
    public bool IsGitRepository { get; set; }
    public bool HasUpdate { get; set; }
    public int CommitsBehind { get; set; }
    public string Branch { get; set; } = "main";
    public string CurrentBranch { get; set; } = string.Empty;
    public string CurrentCommit { get; set; } = string.Empty;
    public string RemoteCommit { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
