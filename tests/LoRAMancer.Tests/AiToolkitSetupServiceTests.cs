using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public class AiToolkitSetupServiceTests : IDisposable {
    private readonly string _testTempDir;

    public AiToolkitSetupServiceTests() {
        _testTempDir = Path.Combine(Path.GetTempPath(), "LoRAMancerToolkitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_testTempDir)) {
                Directory.Delete(_testTempDir, true);
            }
        } catch {
            // Silently clean up
        }
    }

    [Fact]
    public void GetInstallDirectory_DefaultsToToolsAiToolkit() {
        string settingsFile = Path.Combine(_testTempDir, "settings.json");
        SettingsService settingsService = new(null, settingsFile);
        ProcessRunner runner = new();
        AiToolkitSetupService service = new(runner, settingsService);

        string expectedPath = Path.Combine(AppContext.BaseDirectory, "tools", "ai-toolkit");
        Assert.Equal(expectedPath, service.GetInstallDirectory());
    }

    [Fact]
    public async Task GetInstallDirectory_RespectsCustomSettingsPath() {
        string settingsFile = Path.Combine(_testTempDir, "settings.json");
        SettingsService settingsService = new(null, settingsFile);

        string customPath = Path.Combine(_testTempDir, "custom-toolkit");
        await settingsService.SaveSettingsAsync(new AppSettings {
            AiToolkitPath = customPath
        });

        ProcessRunner runner = new();
        AiToolkitSetupService service = new(runner, settingsService);

        Assert.Equal(customPath, service.GetInstallDirectory());
    }

    [Fact]
    public void IsGitRepository_DetectsPresenceOfGitFolder() {
        string settingsFile = Path.Combine(_testTempDir, "settings.json");
        SettingsService settingsService = new(null, settingsFile);

        string toolkitDir = Path.Combine(_testTempDir, "toolkit_git_test");
        Directory.CreateDirectory(toolkitDir);
        Directory.CreateDirectory(Path.Combine(toolkitDir, ".git"));

        settingsService.Current.AiToolkitPath = toolkitDir;

        ProcessRunner runner = new();
        AiToolkitSetupService service = new(runner, settingsService);

        Assert.True(service.IsGitRepository());
    }
}
