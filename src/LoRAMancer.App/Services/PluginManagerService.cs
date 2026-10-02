using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.PluginSdk;

namespace LoRAMancer.App.Services;

public sealed class PluginManagerService : IDisposable {
    private readonly ProcessRunner _processRunner;
    private readonly AmdVenvProvisioner? _provisioner;
    private readonly Dictionary<string, ILoRAMancerPlugin> _loadedCSharpPlugins = new();
    private readonly Dictionary<string, PluginManifest> _registeredPlugins = new();
    private readonly Dictionary<string, System.Diagnostics.Process> _runningWebServers = new();
    private readonly Dictionary<string, string> _runningWebServerUrls = new();
    private readonly object _processLock = new();
    private readonly object _pluginLock = new();

    public string PluginsDirectory { get; }

    public IReadOnlyCollection<PluginManifest> Plugins {
        get {
            lock (_pluginLock) {
                return _registeredPlugins.Values.ToList();
            }
        }
    }

    public IReadOnlyList<PluginManifest> GetPluginsForSection(string sectionName) {
        return Plugins
            .Where(p => p.IsEnabled && !IsScraperPlugin(p) && string.Equals(p.GetEffectiveSection(), sectionName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.MenuOrder)
            .ThenBy(p => p.NavLabel)
            .ToList();
    }

    public IReadOnlyList<string> GetCustomPluginSections() {
        HashSet<string> standardSections = new(StringComparer.OrdinalIgnoreCase) {
            "Studio Pipeline",
            "Studio Workshop",
            "Post-Forge Showcase",
            "Studio Modal Tools",
            "Subsystems"
        };

        return Plugins
            .Where(p => p.IsEnabled && !IsScraperPlugin(p))
            .Select(p => p.GetEffectiveSection())
            .Where(s => !string.IsNullOrWhiteSpace(s) && !standardSections.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();
    }

    public event Action? OnPluginsUpdated;

    public PluginManagerService(ProcessRunner processRunner, AmdVenvProvisioner? provisioner = null, string? pluginsDirectory = null) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _provisioner = provisioner;
        PluginsDirectory = !string.IsNullOrWhiteSpace(pluginsDirectory) && Directory.Exists(pluginsDirectory)
            ? pluginsDirectory
            : ResolvePluginsDirectory();

        _ = Task.Run(async () => {
            try {
                await DiscoverAndInitializePluginsAsync();
            } catch {
                // Background scan fallback
            }
        });
    }

    public static string ResolvePluginsDirectory() {
        string baseDir = AppContext.BaseDirectory;
        string appPlugins = Path.Combine(baseDir, "plugins");
        if (Directory.Exists(appPlugins) && Directory.GetDirectories(appPlugins).Length > 0) {
            return appPlugins;
        }

        // Search upward from AppContext.BaseDirectory for a repository/app root 'plugins' directory
        DirectoryInfo? current = new DirectoryInfo(baseDir);
        while (current != null) {
            string candidate = Path.Combine(current.FullName, "plugins");
            if (Directory.Exists(candidate) && Directory.GetDirectories(candidate).Length > 0) {
                return candidate;
            }
            current = current.Parent;
        }

        // Check current working directory
        string cwdPlugins = Path.Combine(Directory.GetCurrentDirectory(), "plugins");
        if (Directory.Exists(cwdPlugins) && Directory.GetDirectories(cwdPlugins).Length > 0) {
            return cwdPlugins;
        }

        return appPlugins;
    }

    public async Task DiscoverAndInitializePluginsAsync(CancellationToken cancellationToken = default) {
        var directoriesToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(PluginsDirectory)) {
            directoriesToScan.Add(PluginsDirectory);
        }

        string resolved = ResolvePluginsDirectory();
        if (Directory.Exists(resolved)) {
            directoriesToScan.Add(resolved);
        }

        string basePlugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(basePlugins)) {
            directoriesToScan.Add(basePlugins);
        }

        if (directoriesToScan.Count == 0) {
            Directory.CreateDirectory(PluginsDirectory);
            return;
        }

        foreach (string pluginDir in directoriesToScan) {
            string[] subDirs = Directory.GetDirectories(pluginDir);
            foreach (string dir in subDirs) {
                string folderName = Path.GetFileName(dir);
                bool isGit = Directory.Exists(Path.Combine(dir, ".git"));
                bool isDisabled = File.Exists(Path.Combine(dir, ".disabled"));

                // 1. Check for C# plugin (.dll matching folder or any .dll implementing ILoRAMancerPlugin)
                string[] dllFiles = Directory.GetFiles(dir, "*.dll");
                if (dllFiles.Length > 0) {
                    await LoadCSharpPluginFromDirectoryAsync(dir, dllFiles, isGit, isDisabled, cancellationToken);
                    continue;
                }

                // 2. Check for Python plugin (folder containing plugin.json, plugin.py, or requirements.txt)
                string pythonScript = Path.Combine(dir, "plugin.py");
                string manifestFile = Path.Combine(dir, "plugin.json");
                string reqFile = Path.Combine(dir, "requirements.txt");

                if (File.Exists(pythonScript) || File.Exists(manifestFile) || File.Exists(reqFile) || isGit) {
                    await RegisterPythonPluginAsync(dir, manifestFile, pythonScript, reqFile, folderName, isGit, isDisabled, cancellationToken);
                }
            }
        }

        OnPluginsUpdated?.Invoke();
    }

    private async Task LoadCSharpPluginFromDirectoryAsync(string dir, string[] dllFiles, bool isGit, bool isDisabled, CancellationToken cancellationToken) {
        foreach (string dllPath in dllFiles) {
            try {
                AssemblyLoadContext alc = new(Path.GetFileName(dllPath), isCollectible: true);
                Assembly assembly = alc.LoadFromAssemblyPath(dllPath);

                Type? pluginType = assembly.GetTypes().FirstOrDefault(t => typeof(ILoRAMancerPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                if (pluginType != null) {
                    if (Activator.CreateInstance(pluginType) is ILoRAMancerPlugin plugin) {
                        _loadedCSharpPlugins[plugin.Metadata.Id] = plugin;

                        PluginManifest manifest = new() {
                            Id = plugin.Metadata.Id,
                            Name = plugin.Metadata.Name,
                            Version = plugin.Metadata.Version,
                            Description = plugin.Metadata.Description,
                            Author = plugin.Metadata.Author,
                            PluginType = "CSharp",
                            DirectoryPath = dir,
                            EntryPoint = Path.GetFileName(dllPath),
                            IsEnabled = !isDisabled,
                            HasDedicatedVenv = false,
                            IsGitRepo = isGit,
                            UiSlot = string.IsNullOrWhiteSpace(plugin.Metadata.UiSlot) ? "None" : plugin.Metadata.UiSlot,
                            NavLabel = string.IsNullOrWhiteSpace(plugin.Metadata.NavLabel) ? plugin.Metadata.Name : plugin.Metadata.NavLabel,
                            Icon = plugin.Metadata.Icon,
                            UiType = string.IsNullOrWhiteSpace(plugin.Metadata.UiType) ? "Command" : plugin.Metadata.UiType,
                            WebPort = plugin.Metadata.WebPort,
                            WebUrl = plugin.Metadata.WebUrl,
                            MenuSection = plugin.Metadata.MenuSection,
                            MenuOrder = plugin.Metadata.MenuOrder,
                            IsModal = plugin.Metadata.IsModal
                        };
                        lock (_pluginLock) {
                            _registeredPlugins[manifest.Id] = manifest;
                        }

                        if (manifest.IsEnabled) {
                            PluginContext context = new(AppContext.BaseDirectory, dir, string.Empty);
                            await plugin.InitializeAsync(context, cancellationToken);
                        }
                        break;
                    }
                }
            } catch {
                // Skip invalid or non-plugin assemblies
            }
        }
    }

    private async Task RegisterPythonPluginAsync(
        string dir,
        string manifestFile,
        string pythonScript,
        string reqFile,
        string folderName,
        bool isGit,
        bool isDisabled,
        CancellationToken cancellationToken
    ) {
        PluginManifest manifest;
        if (File.Exists(manifestFile)) {
            try {
                string json = await File.ReadAllTextAsync(manifestFile, cancellationToken);
                manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PluginManifest();
            } catch {
                manifest = new PluginManifest();
            }
        } else {
            manifest = new PluginManifest();
        }

        if (string.IsNullOrWhiteSpace(manifest.Id)) {
            manifest.Id = folderName;
        }
        if (string.IsNullOrWhiteSpace(manifest.Name)) {
            manifest.Name = folderName;
        }
        if (string.IsNullOrWhiteSpace(manifest.NavLabel)) {
            manifest.NavLabel = manifest.Name;
        }
        if (string.IsNullOrWhiteSpace(manifest.UiSlot)) {
            manifest.UiSlot = "None";
        }
        if (string.IsNullOrWhiteSpace(manifest.UiType)) {
            manifest.UiType = "Command";
        }
        manifest.DirectoryPath = dir;
        manifest.PluginType = "Python";
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint)) {
            manifest.EntryPoint = File.Exists(pythonScript) ? Path.GetFileName(pythonScript) : (File.Exists(Path.Combine(dir, "app.py")) ? "app.py" : "plugin.py");
        }
        manifest.IsEnabled = !isDisabled && manifest.IsEnabled;
        manifest.IsGitRepo = isGit;

        if (File.Exists(reqFile)) {
            try {
                string reqText = await File.ReadAllTextAsync(reqFile, cancellationToken);
                manifest.RequiresPyTorch = reqText.Contains("torch", StringComparison.OrdinalIgnoreCase);
            } catch {
                // Ignore read errors
            }
        }

        string venvPath = Path.Combine(dir, ".venv");
        bool hasDedicated = Directory.Exists(venvPath);
        bool isScraper = IsScraperPlugin(manifest);
        string sharedScraperVenv = GetSharedScraperVenvPath();
        bool hasSharedScraper = isScraper && Directory.Exists(sharedScraperVenv);
        manifest.HasDedicatedVenv = hasDedicated || hasSharedScraper;

        lock (_pluginLock) {
            _registeredPlugins[manifest.Id] = manifest;
        }
    }

    public async Task InstallPluginFromGitAsync(
        string repoUrl,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);

        string cleanName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));
        if (cleanName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
            cleanName = cleanName.Substring(0, cleanName.Length - 4);
        }

        string targetDir = Path.Combine(PluginsDirectory, cleanName);
        if (Directory.Exists(targetDir)) {
            throw new InvalidOperationException($"A plugin with folder name '{cleanName}' already exists.");
        }

        onProgress?.Invoke($"[Git] Cloning repository from {repoUrl} into {cleanName}...");
        int cloneExit = await _processRunner.RunAsync(
            "git.exe",
            $"clone --recurse-submodules \"{repoUrl}\" \"{targetDir}\"",
            PluginsDirectory,
            null,
            line => onProgress?.Invoke($"[git] {line}"),
            line => onProgress?.Invoke($"[git err] {line}"),
            cancellationToken
        );

        if (cloneExit != 0) {
            throw new InvalidOperationException($"Failed to clone plugin repository from {repoUrl}");
        }

        onProgress?.Invoke("[Git] Repository cloned successfully. Inspecting and provisioning...");
        await DiscoverAndInitializePluginsAsync(cancellationToken);

        if (_registeredPlugins.TryGetValue(cleanName, out PluginManifest? newPlugin)) {
            newPlugin.GitRemoteUrl = repoUrl;
            if (newPlugin.PluginType == "Python") {
                await EnsurePythonPluginVenvAsync(newPlugin, onProgress, cancellationToken);
            }
        }

        onProgress?.Invoke($"[Git] Plugin '{cleanName}' successfully installed and ready!");
    }

    public async Task UpdatePluginAsync(
        PluginManifest plugin,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(plugin);

        if (!plugin.IsGitRepo) {
            onProgress?.Invoke($"[Update] Plugin '{plugin.Name}' is not a Git repository. Skipping git pull.");
            return;
        }

        onProgress?.Invoke($"[Update] Pulling latest changes for '{plugin.Name}'...");
        int pullExit = await _processRunner.RunAsync(
            "git.exe",
            "pull --recurse-submodules",
            plugin.DirectoryPath,
            null,
            line => onProgress?.Invoke($"[git] {line}"),
            line => onProgress?.Invoke($"[git err] {line}"),
            cancellationToken
        );

        if (pullExit != 0) {
            throw new InvalidOperationException($"git pull failed for plugin {plugin.Name}");
        }

        if (plugin.PluginType == "Python") {
            onProgress?.Invoke($"[Update] Updating Python dependencies for '{plugin.Name}'...");
            await EnsurePythonPluginVenvAsync(plugin, onProgress, cancellationToken);
        }

        plugin.LastUpdated = DateTime.UtcNow;
        onProgress?.Invoke($"[Update] Plugin '{plugin.Name}' is now up to date!");
    }

    public async Task UpdateAllGitPluginsAsync(
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        var gitPlugins = _registeredPlugins.Values.Where(p => p.IsGitRepo).ToList();
        if (gitPlugins.Count == 0) {
            onProgress?.Invoke("[Update All] No Git-based plugins found to update.");
            return;
        }

        onProgress?.Invoke($"[Update All] Updating {gitPlugins.Count} Git-based plugin(s)...");
        foreach (var p in gitPlugins) {
            try {
                await UpdatePluginAsync(p, onProgress, cancellationToken);
            } catch (Exception ex) {
                onProgress?.Invoke($"[ERROR] Failed to update {p.Name}: {ex.Message}");
            }
        }
        onProgress?.Invoke("[Update All] Finished updating all Git plugins!");
    }

    public async Task EnsurePythonPluginVenvAsync(
        PluginManifest plugin,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.PluginType != "Python") {
            return;
        }

        if (IsScraperPlugin(plugin)) {
            onProgress?.Invoke($"[Scraper: {plugin.Name}] Setting up shared scraper virtual environment (~/.loramancer/scraper_venv)...");
            await EnsureSharedScraperVenvAsync(onProgress, cancellationToken);
            plugin.HasDedicatedVenv = true;
            OnPluginsUpdated?.Invoke();
            return;
        }

        string venvPath = Path.Combine(plugin.DirectoryPath, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");

        if (!File.Exists(pythonExe)) {
            string shellExe = GetPowerShellExecutable();
            onProgress?.Invoke($"[Plugin: {plugin.Name}] Creating isolated virtual environment in {venvPath} using {Path.GetFileName(shellExe)}...");
            int venvExit = await _processRunner.RunAsync(
                shellExe,
                $"-NoProfile -Command \"python -m venv '{venvPath}'\"",
                plugin.DirectoryPath,
                null,
                line => onProgress?.Invoke(line),
                line => onProgress?.Invoke(line),
                cancellationToken
            );

            if (venvExit != 0) {
                throw new InvalidOperationException($"Failed to provision venv for plugin {plugin.Name}");
            }
            plugin.HasDedicatedVenv = true;
        }

        onProgress?.Invoke($"[Plugin: {plugin.Name}] Upgrading pip in plugin venv...");
        await _processRunner.RunAsync(
            pythonExe,
            "-m pip install --upgrade pip setuptools wheel",
            plugin.DirectoryPath,
            null,
            line => onProgress?.Invoke($"[pip] {line}"),
            _ => { },
            cancellationToken
        );

        string reqFile = Path.Combine(plugin.DirectoryPath, "requirements.txt");
        if (File.Exists(reqFile)) {
            string reqText = await File.ReadAllTextAsync(reqFile, cancellationToken);
            bool requiresTorch = reqText.Contains("torch", StringComparison.OrdinalIgnoreCase);

            if (requiresTorch && _provisioner != null) {
                AmdEnvironmentInfo envInfo = new();
                _provisioner.DetectGpuHardware(envInfo);
                HardwareVendor vendor = envInfo.DetectedVendor;

                onProgress?.Invoke($"[Plugin: {plugin.Name}] Plugin requires PyTorch. Installing hardware-appropriate {vendor} PyTorch distribution...");

                switch (vendor) {
                    case HardwareVendor.Nvidia:
                        await _processRunner.RunAsync(
                            pythonExe,
                            "-m pip install --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu124",
                            plugin.DirectoryPath,
                            null,
                            line => onProgress?.Invoke($"[torch-nv] {line}"),
                            _ => { },
                            cancellationToken
                        );
                        break;
                    case HardwareVendor.Intel:
                        await _processRunner.RunAsync(
                            pythonExe,
                            "-m pip install --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/xpu",
                            plugin.DirectoryPath,
                            null,
                            line => onProgress?.Invoke($"[torch-intel] {line}"),
                            _ => { },
                            cancellationToken
                        );
                        break;
                    case HardwareVendor.Cpu:
                        await _processRunner.RunAsync(
                            pythonExe,
                            "-m pip install --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu",
                            plugin.DirectoryPath,
                            null,
                            line => onProgress?.Invoke($"[torch-cpu] {line}"),
                            _ => { },
                            cancellationToken
                        );
                        break;
                    case HardwareVendor.Amd:
                    default:
                        string wheelsArg = string.Join(" ", _provisioner.CurrentPyTorchWheels.Select(w => $"\"{w}\""));
                        await _processRunner.RunAsync(
                            pythonExe,
                            $"-m pip install --no-cache-dir --no-deps {wheelsArg}",
                            plugin.DirectoryPath,
                            null,
                            line => onProgress?.Invoke($"[torch-amd] {line}"),
                            _ => { },
                            cancellationToken
                        );
                        break;
                }
            }

            onProgress?.Invoke($"[Plugin: {plugin.Name}] Installing plugin dependencies from requirements.txt...");
            await _processRunner.RunAsync(
                pythonExe,
                $"-m pip install --no-cache-dir -r \"{reqFile}\"",
                plugin.DirectoryPath,
                null,
                line => onProgress?.Invoke(line),
                line => onProgress?.Invoke(line),
                cancellationToken
            );
        }

        onProgress?.Invoke($"[Plugin: {plugin.Name}] Isolated .venv ready!");
    }

    public void RemovePythonPluginVenv(PluginManifest plugin) {
        ArgumentNullException.ThrowIfNull(plugin);
        if (IsScraperPlugin(plugin)) {
            string sharedVenv = GetSharedScraperVenvPath();
            if (Directory.Exists(sharedVenv)) {
                try {
                    foreach (string file in Directory.GetFiles(sharedVenv, "*", SearchOption.AllDirectories)) {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(sharedVenv, true);
                } catch (Exception ex) {
                    throw new InvalidOperationException($"Failed to remove shared scraper virtual environment: {ex.Message}", ex);
                }
            }
            plugin.HasDedicatedVenv = false;
            OnPluginsUpdated?.Invoke();
            return;
        }

        string venvPath = Path.Combine(plugin.DirectoryPath, ".venv");
        if (Directory.Exists(venvPath)) {
            try {
                foreach (string file in Directory.GetFiles(venvPath, "*", SearchOption.AllDirectories)) {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(venvPath, true);
            } catch (Exception ex) {
                throw new InvalidOperationException($"Failed to remove plugin virtual environment: {ex.Message}", ex);
            }
        }
        plugin.HasDedicatedVenv = false;
    }

    public async Task RebuildPythonPluginVenvAsync(
        PluginManifest plugin,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(plugin);
        onProgress?.Invoke($"[Plugin: {plugin.Name}] Removing existing .venv...");
        RemovePythonPluginVenv(plugin);
        onProgress?.Invoke($"[Plugin: {plugin.Name}] Rebuilding clean .venv and installing dependencies...");
        await EnsurePythonPluginVenvAsync(plugin, onProgress, cancellationToken);
    }

    public void TogglePluginState(string pluginId, bool isEnabled) {
        PluginManifest? manifest;
        lock (_pluginLock) {
            _registeredPlugins.TryGetValue(pluginId, out manifest);
        }
        if (manifest == null) {
            return;
        }

        manifest.IsEnabled = isEnabled;
        string disabledFlag = Path.Combine(manifest.DirectoryPath, ".disabled");

        if (!isEnabled) {
            File.WriteAllText(disabledFlag, $"Disabled at {DateTime.UtcNow:O}");
        } else if (File.Exists(disabledFlag)) {
            File.Delete(disabledFlag);
        }

        OnPluginsUpdated?.Invoke();
    }

    public void DeletePlugin(PluginManifest plugin) {
        ArgumentNullException.ThrowIfNull(plugin);

        StopPluginServer(plugin.Id);

        if (Directory.Exists(plugin.DirectoryPath)) {
            try {
                // Remove read-only attributes on .git files before deleting
                foreach (string file in Directory.GetFiles(plugin.DirectoryPath, "*", SearchOption.AllDirectories)) {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(plugin.DirectoryPath, true);
            } catch (Exception ex) {
                throw new InvalidOperationException($"Failed to delete plugin folder: {ex.Message}", ex);
            }
        }

        lock (_pluginLock) {
            _registeredPlugins.Remove(plugin.Id);
        }
        _loadedCSharpPlugins.Remove(plugin.Id);
        OnPluginsUpdated?.Invoke();
    }

    public async Task<PluginResult> ExecutePluginAsync(
        string pluginId,
        string command,
        IDictionary<string, object?> parameters,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default
    ) {
        PluginManifest? manifest;
        lock (_pluginLock) {
            _registeredPlugins.TryGetValue(pluginId, out manifest);
        }

        if (manifest == null) {
            await DiscoverAndInitializePluginsAsync(cancellationToken);
            lock (_pluginLock) {
                _registeredPlugins.TryGetValue(pluginId, out manifest);
            }
        }

        if (manifest == null) {
            return PluginResult.Fail($"Plugin '{pluginId}' is not registered.");
        }

        if (!manifest.IsEnabled) {
            return PluginResult.Fail($"Plugin '{manifest.Name}' is disabled.");
        }

        if (manifest.PluginType == "CSharp") {
            if (_loadedCSharpPlugins.TryGetValue(pluginId, out ILoRAMancerPlugin? plugin)) {
                return await plugin.ExecuteAsync(command, parameters, cancellationToken);
            }
            return PluginResult.Fail($"C# plugin '{pluginId}' is not active.");
        }

        // Python plugin execution inside its own .venv or fallback to app venv / system python
        string venvPath = Path.Combine(manifest.DirectoryPath, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            string appVenv = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
            if (File.Exists(appVenv)) {
                pythonExe = appVenv;
            } else {
                string currentVenv = Path.Combine(Directory.GetCurrentDirectory(), ".venv", "Scripts", "python.exe");
                if (File.Exists(currentVenv)) {
                    pythonExe = currentVenv;
                } else {
                    pythonExe = "python.exe";
                }
            }
        }

        string scriptPath = Path.Combine(manifest.DirectoryPath, manifest.EntryPoint);
        if (!File.Exists(scriptPath)) {
            return PluginResult.Fail($"Entrypoint script '{manifest.EntryPoint}' was not found in plugin directory.");
        }

        string paramJson = JsonSerializer.Serialize(parameters);
        string tempParamFile = Path.Combine(Path.GetTempPath(), $"loramancer_plugin_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempParamFile, paramJson, cancellationToken);

        string outputData = string.Empty;

        string configPath = GetPluginConfigPath(manifest.Id);
        var envVars = new Dictionary<string, string> {
            ["LORAMANCER_PLUGIN_CONFIG_FILE"] = configPath
        };
        var userConfig = GetPluginConfig(manifest.Id);
        foreach (var kvp in userConfig) {
            envVars[$"LORAMANCER_CONFIG_{kvp.Key.ToUpperInvariant()}"] = kvp.Value;
            envVars[$"PLUGIN_{kvp.Key.ToUpperInvariant()}"] = kvp.Value;
        }

        try {
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\" --cmd \"{command}\" --data-file \"{tempParamFile}\"",
                manifest.DirectoryPath,
                envVars,
                line => {
                    outputData += line + "\n";
                    onOutputLine?.Invoke(line);
                },
                errLine => {
                    onErrorLine?.Invoke(errLine);
                },
                cancellationToken
            );

            if (exitCode == 0) {
                return PluginResult.Ok("Plugin executed successfully", outputData.Trim());
            }

            return PluginResult.Fail($"Python plugin exited with error code {exitCode}: {outputData.Trim()}");
        } finally {
            try {
                if (File.Exists(tempParamFile)) {
                    File.Delete(tempParamFile);
                }
            } catch {
                // Ignore cleanup errors
            }
        }
    }

    private sealed class PluginContext : IPluginContext {
        public string AppDirectory { get; }
        public string PluginDirectory { get; }
        public string PythonVenvDirectory { get; }

        public PluginContext(string appDir, string pluginDir, string pythonVenv) {
            AppDirectory = appDir;
            PluginDirectory = pluginDir;
            PythonVenvDirectory = pythonVenv;
        }

        public void LogInformation(string message) => Console.WriteLine($"[INFO] {message}");
        public void LogWarning(string message) => Console.WriteLine($"[WARN] {message}");
        public void LogError(string message, Exception? exception = null) => Console.WriteLine($"[ERROR] {message} {exception?.Message}");
    }

    private static string GetPowerShellExecutable() {
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string systemPowerShell = Path.Combine(winDir, "WindowsPowerShell", "v1.0", "powershell.exe");

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv)) {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
                try {
                    string pwshPath = Path.Combine(dir.Trim('\"'), "pwsh.exe");
                    if (File.Exists(pwshPath)) {
                        return "pwsh.exe";
                    }
                } catch {
                    // Ignore invalid path segments
                }
            }
        }

        if (File.Exists(systemPowerShell)) {
            return systemPowerShell;
        }

        return "powershell.exe";
    }

    public bool IsWebServerRunning(string pluginId) {
        lock (_processLock) {
            if (_runningWebServers.TryGetValue(pluginId, out var proc)) {
                if (!proc.HasExited) {
                    return true;
                }
                _runningWebServers.Remove(pluginId);
                _runningWebServerUrls.Remove(pluginId);
            }
            return false;
        }
    }

    public string? GetWebServerUrl(string pluginId) {
        lock (_processLock) {
            return _runningWebServerUrls.TryGetValue(pluginId, out string? url) ? url : null;
        }
    }

    public async Task<string> StartPluginServerAsync(string pluginId, int? port = null, CancellationToken cancellationToken = default) {
        PluginManifest? manifest;
        lock (_pluginLock) {
            _registeredPlugins.TryGetValue(pluginId, out manifest);
        }
        if (manifest == null) {
            throw new InvalidOperationException($"Plugin '{pluginId}' is not registered.");
        }

        lock (_processLock) {
            if (_runningWebServers.TryGetValue(pluginId, out var existingProc) && !existingProc.HasExited) {
                return _runningWebServerUrls[pluginId];
            }
        }

        int targetPort = port ?? manifest.WebPort ?? 8501;

        // Resolve python executable
        string venvPath = Path.Combine(manifest.DirectoryPath, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            string appVenv = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
            if (File.Exists(appVenv)) {
                pythonExe = appVenv;
            } else {
                string currentVenv = Path.Combine(Directory.GetCurrentDirectory(), ".venv", "Scripts", "python.exe");
                pythonExe = File.Exists(currentVenv) ? currentVenv : "python.exe";
            }
        }

        string entryPoint = Path.Combine(manifest.DirectoryPath, manifest.EntryPoint);
        string arguments;
        if (File.Exists(Path.Combine(manifest.DirectoryPath, "app.py")) || manifest.EntryPoint.EndsWith("app.py", StringComparison.OrdinalIgnoreCase)) {
            // Streamlit application
            arguments = $"-m streamlit run \"{entryPoint}\" --server.port {targetPort} --server.headless true --browser.serverAddress 127.0.0.1";
        } else {
            arguments = $"\"{entryPoint}\" --port {targetPort}";
        }

        System.Diagnostics.ProcessStartInfo psi = new() {
            FileName = pythonExe,
            Arguments = arguments,
            WorkingDirectory = manifest.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        System.Diagnostics.Process process = new() { StartInfo = psi };
        process.Start();

        string serverUrl = $"http://127.0.0.1:{targetPort}";
        lock (_processLock) {
            _runningWebServers[pluginId] = process;
            _runningWebServerUrls[pluginId] = serverUrl;
        }

        // Give server a moment to bind and launch
        await Task.Delay(1200, cancellationToken);
        return serverUrl;
    }

    public void StopPluginServer(string pluginId) {
        lock (_processLock) {
            if (_runningWebServers.TryGetValue(pluginId, out var proc)) {
                try {
                    if (!proc.HasExited) {
                        proc.Kill(entireProcessTree: true);
                    }
                } catch {
                    // Ignore errors during termination
                }
                _runningWebServers.Remove(pluginId);
                _runningWebServerUrls.Remove(pluginId);
            }
        }
    }

    /// <summary>
    /// Checks whether a plugin or plugin ID represents an image harvester scraper.
    /// </summary>
    public static bool IsScraperPluginId(string pluginId) {
        if (string.IsNullOrWhiteSpace(pluginId)) return false;
        return pluginId.EndsWith("-scraper", StringComparison.OrdinalIgnoreCase) ||
               pluginId.EndsWith("_scraper", StringComparison.OrdinalIgnoreCase) ||
               pluginId.StartsWith("scraper_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a plugin is an image harvester scraper.
    /// </summary>
    public static bool IsScraperPlugin(PluginManifest plugin) {
        if (plugin == null) return false;
        return string.Equals(plugin.UiSlot, "HarvesterScraper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(plugin.MenuSection, "Harvester Scraper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(plugin.PluginType, "HarvesterScraper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(plugin.PluginType, "scraper", StringComparison.OrdinalIgnoreCase) ||
               IsScraperPluginId(plugin.Id);
    }

    /// <summary>
    /// Gets the shared virtual environment directory for scraper plugins (~/.loramancer/scraper_venv).
    /// </summary>
    public static string GetSharedScraperVenvPath() {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".loramancer", "scraper_venv");
    }

    /// <summary>
    /// Ensures that the shared scraper virtual environment (~/.loramancer/scraper_venv) is created and has common dependencies installed.
    /// </summary>
    public async Task<string> EnsureSharedScraperVenvAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default) {
        string venvPath = GetSharedScraperVenvPath();
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            pythonExe = Path.Combine(venvPath, "bin", "python");
        }

        if (!File.Exists(pythonExe)) {
            Directory.CreateDirectory(Path.GetDirectoryName(venvPath)!);
            onProgress?.Invoke($"Creating shared scraper virtual environment in {venvPath}...");
            string shellExe = Environment.OSVersion.Platform == PlatformID.Win32NT ? "powershell.exe" : "bash";
            string cmd = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? $"-NoProfile -Command \"python -m venv '{venvPath}'\""
                : $"-c \"python3 -m venv '{venvPath}'\"";

            int exitCode = await _processRunner.RunAsync(shellExe, cmd, Environment.CurrentDirectory, null, msg => onProgress?.Invoke(msg), msg => onProgress?.Invoke(msg), cancellationToken);
            if (exitCode != 0) {
                throw new InvalidOperationException($"Failed to create shared scraper venv in {venvPath}.");
            }

            pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
            if (!File.Exists(pythonExe)) pythonExe = Path.Combine(venvPath, "bin", "python");

            onProgress?.Invoke("Installing shared scraper dependencies (requests, beautifulsoup4, cloudscraper, urllib3)...");
            string pipArgs = "-m pip install --upgrade requests beautifulsoup4 cloudscraper urllib3";
            await _processRunner.RunAsync(pythonExe, pipArgs, venvPath, null, msg => onProgress?.Invoke(msg), msg => onProgress?.Invoke(msg), cancellationToken);
        }

        return pythonExe;
    }

    /// <summary>
    /// Resolves the python executable path for a given plugin, preferring dedicated or shared scraper .venv if available.
    /// </summary>
    public string ResolvePythonExecutable(PluginManifest plugin) {
        // Scraper plugins check for shared scraper .venv first
        if (IsScraperPlugin(plugin)) {
            string sharedVenv = GetSharedScraperVenvPath();
            string sharedWin = Path.Combine(sharedVenv, "Scripts", "python.exe");
            if (File.Exists(sharedWin)) return sharedWin;
            string sharedUnix = Path.Combine(sharedVenv, "bin", "python");
            if (File.Exists(sharedUnix)) return sharedUnix;
        }

        if (!string.IsNullOrWhiteSpace(plugin.DirectoryPath)) {
            string winVenv = Path.Combine(plugin.DirectoryPath, ".venv", "Scripts", "python.exe");
            if (File.Exists(winVenv)) return winVenv;

            string unixVenv = Path.Combine(plugin.DirectoryPath, ".venv", "bin", "python");
            if (File.Exists(unixVenv)) return unixVenv;
        }

        string appVenv = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
        if (File.Exists(appVenv)) return appVenv;

        string currentVenv = Path.Combine(Directory.GetCurrentDirectory(), ".venv", "Scripts", "python.exe");
        if (File.Exists(currentVenv)) return currentVenv;

        return "python.exe";
    }

    /// <summary>
    /// Gets the base directory where scraper configuration files are stored (~/.loramancer/scrapers).
    /// </summary>
    public static string GetScraperConfigDirectory() {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string scraperDir = Path.Combine(userHome, ".loramancer", "scrapers");
        if (!Directory.Exists(scraperDir)) {
            Directory.CreateDirectory(scraperDir);
        }
        return scraperDir;
    }

    /// <summary>
    /// Gets the base directory where general user plugin configuration files are stored (~/.loramancer/plugin_configs).
    /// </summary>
    public static string GetPluginConfigDirectory() {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configDir = Path.Combine(userHome, ".loramancer", "plugin_configs");
        if (!Directory.Exists(configDir)) {
            Directory.CreateDirectory(configDir);
        }
        return configDir;
    }

    /// <summary>
    /// Gets the path to the configuration JSON file for a specific plugin.
    /// Scrapers are saved under ~/.loramancer/scrapers/<id>.json, other plugins under ~/.loramancer/plugin_configs/<id>.json.
    /// </summary>
    public static string GetPluginConfigPath(string pluginId) {
        string safeId = string.Join("_", pluginId.Split(Path.GetInvalidFileNameChars()));
        if (IsScraperPluginId(pluginId)) {
            return Path.Combine(GetScraperConfigDirectory(), $"{safeId}.json");
        }
        return Path.Combine(GetPluginConfigDirectory(), $"{safeId}.json");
    }

    /// <summary>
    /// Loads user configuration key-value pairs for a plugin.
    /// </summary>
    public Dictionary<string, string> GetPluginConfig(string pluginId) {
        string configPath = GetPluginConfigPath(pluginId);
        
        // Migrate / fallback from legacy plugin_configs if scraper
        if (!File.Exists(configPath) && IsScraperPluginId(pluginId)) {
            string safeId = string.Join("_", pluginId.Split(Path.GetInvalidFileNameChars()));
            string legacyPath = Path.Combine(GetPluginConfigDirectory(), $"{safeId}.json");
            if (File.Exists(legacyPath)) {
                try {
                    File.Copy(legacyPath, configPath, overwrite: true);
                } catch {
                    configPath = legacyPath;
                }
            }
        }

        if (!File.Exists(configPath)) {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try {
            string json = File.ReadAllText(configPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return loaded != null 
                ? new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase) 
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        } catch {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Saves user configuration key-value pairs for a plugin.
    /// </summary>
    public void SavePluginConfig(string pluginId, Dictionary<string, string> config) {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(config);

        string configPath = GetPluginConfigPath(pluginId);
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    /// <summary>
    /// Executes a Python plugin script and captures stdout/stderr with user configuration injected as environment variables.
    /// </summary>
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonPluginScriptAsync(
        PluginManifest plugin,
        string scriptArgs,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(plugin);

        string pythonExe = ResolvePythonExecutable(plugin);
        string scriptPath = Path.Combine(plugin.DirectoryPath, plugin.EntryPoint);
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        string configPath = GetPluginConfigPath(plugin.Id);
        var envVars = new Dictionary<string, string> {
            ["LORAMANCER_PLUGIN_CONFIG_FILE"] = configPath,
            ["LORAMANCER_SCRAPER_CONFIG_FILE"] = configPath
        };

        var userConfig = GetPluginConfig(plugin.Id);
        foreach (var kvp in userConfig) {
            envVars[$"LORAMANCER_CONFIG_{kvp.Key.ToUpperInvariant()}"] = kvp.Value;
            envVars[$"PLUGIN_{kvp.Key.ToUpperInvariant()}"] = kvp.Value;
        }

        int exitCode = await _processRunner.RunAsync(
            pythonExe,
            $"\"{scriptPath}\" {scriptArgs}",
            plugin.DirectoryPath,
            envVars,
            line => stdout.AppendLine(line),
            line => stderr.AppendLine(line),
            cancellationToken
        );

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    public void Dispose() {
        lock (_processLock) {
            foreach (var kvp in _runningWebServers) {
                try {
                    if (!kvp.Value.HasExited) {
                        kvp.Value.Kill(entireProcessTree: true);
                    }
                } catch {
                    // Ignore
                }
            }
            _runningWebServers.Clear();
            _runningWebServerUrls.Clear();
        }
    }
}
