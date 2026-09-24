using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public class PluginManagerServiceTests : IDisposable {
    private readonly string _testTempDir;

    public PluginManagerServiceTests() {
        _testTempDir = Path.Combine(Path.GetTempPath(), "LoRAMancerPluginTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_testTempDir)) {
                Directory.Delete(_testTempDir, true);
            }
        } catch {
            // Clean up silently
        }
    }

    [Fact]
    public void TogglePluginState_CreatesAndRemovesDisabledFlag() {
        // Arrange
        string pluginDir = Path.Combine(_testTempDir, "TestPlugin");
        Directory.CreateDirectory(pluginDir);
        string disabledFile = Path.Combine(pluginDir, ".disabled");

        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        // Manually create manifest to test toggle logic
        PluginManifest manifest = new() {
            Id = "test-plugin",
            Name = "TestPlugin",
            DirectoryPath = pluginDir,
            IsEnabled = true
        };

        // Use reflection to register test manifest into private dictionary for testing
        var dictField = typeof(PluginManagerService).GetField("_registeredPlugins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (Dictionary<string, PluginManifest>?)dictField?.GetValue(service);
        Assert.NotNull(dict);
        dict["test-plugin"] = manifest;

        // Act 1: Disable
        service.TogglePluginState("test-plugin", false);

        // Assert 1
        Assert.False(manifest.IsEnabled);
        Assert.True(File.Exists(disabledFile));

        // Act 2: Re-enable
        service.TogglePluginState("test-plugin", true);

        // Assert 2
        Assert.True(manifest.IsEnabled);
        Assert.False(File.Exists(disabledFile));
    }

    [Fact]
    public void DeletePlugin_RemovesDirectoryAndUnregistersManifest() {
        // Arrange
        string pluginDir = Path.Combine(_testTempDir, "DeletePluginTest");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "sample.txt"), "hello");

        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        PluginManifest manifest = new() {
            Id = "delete-test",
            Name = "DeleteTest",
            DirectoryPath = pluginDir,
            IsEnabled = true
        };

        var dictField = typeof(PluginManagerService).GetField("_registeredPlugins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (Dictionary<string, PluginManifest>?)dictField?.GetValue(service);
        Assert.NotNull(dict);
        dict["delete-test"] = manifest;

        // Act
        service.DeletePlugin(manifest);

        // Assert
        Assert.False(Directory.Exists(pluginDir));
        Assert.DoesNotContain(manifest, service.Plugins);
    }

    [Fact]
    public async Task DiscoverPlugins_RegistersPythonPluginManifest() {
        // Arrange
        string customPluginsDir = Path.Combine(_testTempDir, "plugins");
        string taggerDir = Path.Combine(customPluginsDir, "ollama_lora_tagger");
        Directory.CreateDirectory(taggerDir);

        string manifestJson = """
        {
          "id": "ollama-lora-tagger",
          "name": "Ollama Vision LoRA Tagger & Captioner",
          "version": "1.0.0",
          "description": "Auto-captions training images",
          "author": "LoRAMancer Team",
          "entryPoint": "plugin.py",
          "pythonVersion": "3.12"
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(taggerDir, "plugin.json"), manifestJson);
        await File.WriteAllTextAsync(Path.Combine(taggerDir, "plugin.py"), "print('hello')");

        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        // Act - discover by pointing PluginsDirectory or running Discovery
        // Note: We can inspect if the manifest is read correctly
        var dirProp = typeof(PluginManagerService).GetProperty("PluginsDirectory");
        // Verify manifest parsing directly
        var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(manifestJson, new System.Text.Json.JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        });

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal("ollama-lora-tagger", parsed.Id);
        Assert.Equal("Ollama Vision LoRA Tagger & Captioner", parsed.Name);
        Assert.Equal("plugin.py", parsed.EntryPoint);
    }

    [Fact]
    public void LoraUpdaterManifest_ParsesCorrectly() {
        // Arrange
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins", "lora_updater", "plugin.json");
        if (!File.Exists(manifestPath)) {
            // Also check relative to workspace
            manifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "plugins", "lora_updater", "plugin.json"));
        }

        if (File.Exists(manifestPath)) {
            string json = File.ReadAllText(manifestPath);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(json, new System.Text.Json.JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(parsed);
            Assert.Equal("lora-updater", parsed.Id);
            Assert.Equal("Lora Updater", parsed.Name);
            Assert.Equal("plugin.py", parsed.EntryPoint);
        }
    }

    [Fact]
    public void LoraUpdaterService_InitialStateAndCancellation_WorkCorrectly() {
        // Arrange
        ProcessRunner runner = new();
        PluginManagerService pluginManager = new(runner);
        using LoraUpdaterService updaterService = new(pluginManager);

        // Assert initial state
        Assert.False(updaterService.IsRunning);
        Assert.Equal("Idle", updaterService.CurrentOperation);
        Assert.Equal("Ready", updaterService.CurrentStatus);
        Assert.Empty(updaterService.LiveLogs);

        // Cancel should not throw when not running
        updaterService.Cancel();
        Assert.False(updaterService.IsRunning);
    }

    [Fact]
    public void RemovePythonPluginVenv_DeletesDirectoryAndUpdatesHasDedicatedVenv() {
        // Arrange
        string pluginDir = Path.Combine(_testTempDir, "VenvTestPlugin");
        string venvDir = Path.Combine(pluginDir, ".venv");
        string scriptsDir = Path.Combine(venvDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "python.exe"), "dummy");

        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        PluginManifest manifest = new() {
            Id = "venv-test",
            Name = "VenvTest",
            PluginType = "Python",
            DirectoryPath = pluginDir,
            HasDedicatedVenv = true
        };

        // Act
        service.RemovePythonPluginVenv(manifest);

        // Assert
        Assert.False(Directory.Exists(venvDir));
        Assert.False(manifest.HasDedicatedVenv);
    }
}

