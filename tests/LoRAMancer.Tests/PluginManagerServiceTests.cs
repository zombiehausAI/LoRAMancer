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
}
