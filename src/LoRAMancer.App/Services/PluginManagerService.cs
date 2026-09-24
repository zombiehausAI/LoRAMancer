using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.PluginSdk;

namespace LoRAMancer.App.Services;

public sealed class PluginManagerService {
    private readonly ProcessRunner _processRunner;
    private readonly Dictionary<string, ILoRAMancerPlugin> _loadedCSharpPlugins = new();
    private readonly Dictionary<string, PluginManifest> _registeredPlugins = new();

    public string PluginsDirectory { get; }

    public IReadOnlyCollection<PluginManifest> Plugins => _registeredPlugins.Values;

    public PluginManagerService(ProcessRunner processRunner) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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

            // 1. Check for C# plugin (.dll matching folder or any .dll implementing ILoRAMancerPlugin)
            string[] dllFiles = Directory.GetFiles(dir, "*.dll");
            if (dllFiles.Length > 0) {
                await LoadCSharpPluginFromDirectoryAsync(dir, dllFiles, cancellationToken);
                continue;
            }

            // 2. Check for Python plugin (folder containing plugin.json or plugin.py)
            string pythonScript = Path.Combine(dir, "plugin.py");
            string manifestFile = Path.Combine(dir, "plugin.json");
            if (File.Exists(pythonScript) || File.Exists(manifestFile)) {
                await RegisterPythonPluginAsync(dir, manifestFile, pythonScript, folderName, cancellationToken);
            }
        }
    }

    private async Task LoadCSharpPluginFromDirectoryAsync(string dir, string[] dllFiles, CancellationToken cancellationToken) {
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
                            IsEnabled = true,
                            HasDedicatedVenv = false
                        };
                        _registeredPlugins[manifest.Id] = manifest;

                        PluginContext context = new(AppContext.BaseDirectory, dir, string.Empty);
                        await plugin.InitializeAsync(context, cancellationToken);
                        break;
                    }
                }
            } catch {
                // Skip invalid or non-plugin assemblies
            }
        }
    }

    private async Task RegisterPythonPluginAsync(string dir, string manifestFile, string pythonScript, string folderName, CancellationToken cancellationToken) {
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

        // Python plugin must live in its own folder with its own .venv inside that folder
        string venvPath = Path.Combine(dir, ".venv");
        manifest.HasDedicatedVenv = Directory.Exists(venvPath);

        _registeredPlugins[manifest.Id] = manifest;
    }

    public async Task EnsurePythonPluginVenvAsync(PluginManifest plugin, Action<string>? onProgress, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.PluginType != "Python") {
            return;
        }

        string venvPath = Path.Combine(plugin.DirectoryPath, ".venv");
        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");

        if (!File.Exists(pythonExe)) {
            onProgress?.Invoke($"[Plugin: {plugin.Name}] Creating isolated virtual environment in {venvPath}...");
            int venvExit = await _processRunner.RunAsync(
                "pwsh.exe",
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

        string reqFile = Path.Combine(plugin.DirectoryPath, "requirements.txt");
        if (File.Exists(reqFile)) {
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

        onProgress?.Invoke($"[Plugin: {plugin.Name}] Ready!");
    }

    public async Task<PluginResult> ExecutePluginAsync(
        string pluginId,
        string command,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default
    ) {
        if (!_registeredPlugins.TryGetValue(pluginId, out PluginManifest? manifest)) {
            return PluginResult.Fail($"Plugin '{pluginId}' is not registered.");
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
        string outputData = string.Empty;

        int exitCode = await _processRunner.RunAsync(
            pythonExe,
            $"\"{scriptPath}\" --cmd \"{command}\" --data '{paramJson}'",
            manifest.DirectoryPath,
            null,
            line => outputData += line + "\n",
            _ => { },
            cancellationToken
        );

        if (exitCode == 0) {
            return PluginResult.Ok("Plugin executed successfully", outputData.Trim());
        }

        return PluginResult.Fail($"Python plugin exited with error code {exitCode}: {outputData.Trim()}");
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

        public void LogInformation(string message) {
            Console.WriteLine($"[INFO] {message}");
        }

        public void LogWarning(string message) {
            Console.WriteLine($"[WARN] {message}");
        }

        public void LogError(string message, Exception? exception = null) {
            Console.WriteLine($"[ERROR] {message} {exception?.Message}");
        }
    }
}
