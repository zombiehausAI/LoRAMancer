using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.PluginSdk;

namespace LoRAMancer.App.Services;

public sealed class PluginManagerService {
    private readonly ProcessRunner _processRunner;
    private readonly AmdVenvProvisioner? _provisioner;
    private readonly Dictionary<string, ILoRAMancerPlugin> _loadedCSharpPlugins = new();
    private readonly Dictionary<string, PluginManifest> _registeredPlugins = new();

    public string PluginsDirectory { get; }

    public IReadOnlyCollection<PluginManifest> Plugins => _registeredPlugins.Values;

    public PluginManagerService(ProcessRunner processRunner, AmdVenvProvisioner? provisioner = null) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _provisioner = provisioner;
        PluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
    }

    public async Task DiscoverAndInitializePluginsAsync(CancellationToken cancellationToken = default) {
        if (!Directory.Exists(PluginsDirectory)) {
            Directory.CreateDirectory(PluginsDirectory);
            return;
        }

        string[] subDirs = Directory.GetDirectories(PluginsDirectory);
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
                            IsGitRepo = isGit
                        };
                        _registeredPlugins[manifest.Id] = manifest;

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
        manifest.DirectoryPath = dir;
        manifest.PluginType = "Python";
        manifest.EntryPoint = File.Exists(pythonScript) ? Path.GetFileName(pythonScript) : "plugin.py";
        manifest.IsEnabled = !isDisabled;
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
        manifest.HasDedicatedVenv = Directory.Exists(venvPath);

        _registeredPlugins[manifest.Id] = manifest;
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

    public void TogglePluginState(string pluginId, bool isEnabled) {
        if (!_registeredPlugins.TryGetValue(pluginId, out PluginManifest? manifest)) {
            return;
        }

        manifest.IsEnabled = isEnabled;
        string disabledFlag = Path.Combine(manifest.DirectoryPath, ".disabled");

        if (!isEnabled) {
            File.WriteAllText(disabledFlag, $"Disabled at {DateTime.UtcNow:O}");
        } else if (File.Exists(disabledFlag)) {
            File.Delete(disabledFlag);
        }
    }

    public void DeletePlugin(PluginManifest plugin) {
        ArgumentNullException.ThrowIfNull(plugin);

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

        _registeredPlugins.Remove(plugin.Id);
        _loadedCSharpPlugins.Remove(plugin.Id);
    }

    public async Task<PluginResult> ExecutePluginAsync(
        string pluginId,
        string command,
        IDictionary<string, object?> parameters,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default
    ) {
        if (!_registeredPlugins.TryGetValue(pluginId, out PluginManifest? manifest)) {
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

        // Python plugin execution inside its own .venv
        string venvPath = Path.Combine(manifest.DirectoryPath, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            return PluginResult.Fail($"Plugin '{manifest.Name}' has not provisioned its .venv yet.");
        }

        string scriptPath = Path.Combine(manifest.DirectoryPath, manifest.EntryPoint);
        if (!File.Exists(scriptPath)) {
            return PluginResult.Fail($"Entrypoint script '{manifest.EntryPoint}' was not found in plugin directory.");
        }

        string paramJson = JsonSerializer.Serialize(parameters);
        string tempParamFile = Path.Combine(Path.GetTempPath(), $"loramancer_plugin_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempParamFile, paramJson, cancellationToken);

        string outputData = string.Empty;

        try {
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\" --cmd \"{command}\" --data-file \"{tempParamFile}\"",
                manifest.DirectoryPath,
                null,
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
}
