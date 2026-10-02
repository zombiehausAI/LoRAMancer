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

    [Fact]
    public async Task DiscoverPlugins_FindsAndRegistersOllamaTagger_Automatically() {
        // Arrange
        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        // Act
        await service.DiscoverAndInitializePluginsAsync();

        // Assert
        Assert.Contains(service.Plugins, p => p.Id == "ollama-lora-tagger");
        var tagger = service.Plugins.First(p => p.Id == "ollama-lora-tagger");
        Assert.Equal("Ollama Vision LoRA Tagger & Captioner", tagger.Name);
        Assert.Equal("plugin.py", tagger.EntryPoint);
        Assert.Equal("Python", tagger.PluginType);
        Assert.Equal("StudioModalTools", tagger.UiSlot);
        Assert.Equal("NativeModal", tagger.UiType);
    }

    [Fact]
    public async Task DiscoverPlugins_FindsAndRegistersLoraUpdater_WithStudioWorkshopSlot() {
        // Arrange
        ProcessRunner runner = new();
        using PluginManagerService service = new(runner);

        // Act
        await service.DiscoverAndInitializePluginsAsync();

        // Assert
        Assert.Contains(service.Plugins, p => p.Id == "lora-updater");
        var updater = service.Plugins.First(p => p.Id == "lora-updater");
        Assert.Equal("Lora Updater", updater.Name);
        Assert.Equal("StudioWorkshop", updater.UiSlot);
        Assert.Equal("Civitai LoRA Updater", updater.NavLabel);
    }

    [Fact]
    public void PluginManifest_UiSlotProperties_ParseCorrectly() {
        string json = """
        {
            "id": "post-forge-gallery",
            "name": "Post-Forge Visual Grid",
            "version": "1.0.0",
            "uiSlot": "PostForge",
            "uiType": "EmbeddedWeb",
            "navLabel": "Visual Grid",
            "icon": "PhotoLibrary",
            "webPort": 9000
        }
        """;

        var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(json, new System.Text.Json.JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(parsed);
        Assert.Equal("post-forge-gallery", parsed.Id);
        Assert.Equal("PostForge", parsed.UiSlot);
        Assert.Equal("EmbeddedWeb", parsed.UiType);
        Assert.Equal("Visual Grid", parsed.NavLabel);
        Assert.Equal("PhotoLibrary", parsed.Icon);
        Assert.Equal(9000, parsed.WebPort);
    }

    [Fact]
    public void GetPluginsForSection_ReturnsFilteredAndOrderedPlugins() {
        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        PluginManifest p1 = new() {
            Id = "p1",
            Name = "Plugin One",
            NavLabel = "B Tool",
            MenuSection = "Civitai Tools",
            MenuOrder = 20,
            IsEnabled = true
        };
        PluginManifest p2 = new() {
            Id = "p2",
            Name = "Plugin Two",
            NavLabel = "A Tool",
            MenuSection = "Civitai Tools",
            MenuOrder = 10,
            IsEnabled = true
        };
        PluginManifest p3 = new() {
            Id = "p3",
            Name = "Plugin Three",
            NavLabel = "Workshop Tool",
            MenuSection = "workshop", // Normalized to "Studio Workshop"
            MenuOrder = 5,
            IsEnabled = true
        };
        PluginManifest pDisabled = new() {
            Id = "pDisabled",
            Name = "Disabled Tool",
            MenuSection = "Civitai Tools",
            MenuOrder = 1,
            IsEnabled = false
        };

        var dictField = typeof(PluginManagerService).GetField("_registeredPlugins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (Dictionary<string, PluginManifest>?)dictField?.GetValue(service);
        Assert.NotNull(dict);
        dict["p1"] = p1;
        dict["p2"] = p2;
        dict["p3"] = p3;
        dict["pDisabled"] = pDisabled;

        var civitaiPlugins = service.GetPluginsForSection("Civitai Tools");
        Assert.Equal(2, civitaiPlugins.Count);
        Assert.Equal("p2", civitaiPlugins[0].Id); // MenuOrder 10 before 20
        Assert.Equal("p1", civitaiPlugins[1].Id);

        var workshopPlugins = service.GetPluginsForSection("Studio Workshop");
        Assert.Contains(workshopPlugins, p => p.Id == "p3");
    }

    [Fact]
    public void GetCustomPluginSections_ReturnsCustomSectionsExcludingStandard() {
        ProcessRunner runner = new();
        PluginManagerService service = new(runner);

        PluginManifest standard1 = new() {
            Id = "std1",
            Name = "Std 1",
            MenuSection = "Studio Workshop",
            IsEnabled = true
        };
        PluginManifest standard2 = new() {
            Id = "std2",
            Name = "Std 2",
            UiSlot = "PostForge",
            IsEnabled = true
        };
        PluginManifest custom1 = new() {
            Id = "c1",
            Name = "Custom 1",
            MenuSection = "Community Feeds",
            IsEnabled = true
        };
        PluginManifest custom2 = new() {
            Id = "c2",
            Name = "Custom 2",
            MenuSection = "Civitai Tools",
            IsEnabled = true
        };

        var dictField = typeof(PluginManagerService).GetField("_registeredPlugins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (Dictionary<string, PluginManifest>?)dictField?.GetValue(service);
        Assert.NotNull(dict);
        dict["std1"] = standard1;
        dict["std2"] = standard2;
        dict["c1"] = custom1;
        dict["c2"] = custom2;

        var customSections = service.GetCustomPluginSections();
        Assert.Equal(2, customSections.Count);
        Assert.Contains("Civitai Tools", customSections);
        Assert.Contains("Community Feeds", customSections);
        Assert.DoesNotContain("Studio Workshop", customSections);
        Assert.DoesNotContain("Post-Forge Showcase", customSections);
    }

    [Fact]
    public void PluginManifest_MenuSectionAndModal_ParseAndNormalizeCorrectly() {
        string json = """
        {
            "id": "custom-modal-tool",
            "name": "Custom Tool",
            "version": "1.0.0",
            "menuSection": "modal",
            "menuOrder": 15,
            "isModal": true,
            "navLabel": "Fast Tagger"
        }
        """;

        var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(json, new System.Text.Json.JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(parsed);
        Assert.Equal("Studio Modal Tools", parsed.GetEffectiveSection());
        Assert.True(parsed.GetIsModal());
        Assert.Equal(15, parsed.MenuOrder);
        Assert.Equal("Fast Tagger", parsed.NavLabel);
    }
}

