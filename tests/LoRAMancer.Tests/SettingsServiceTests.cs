using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class SettingsServiceTests {
    [Fact]
    public async Task SaveAndLoadSettingsAsync_PersistsCorrectly() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}.json");

        try {
            SettingsService service = new(null, tempFile);
            AppSettings newSettings = new() {
                RocmBaseUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.3.0",
                PyTorchVersion = "2.10.0+rocm7.3.0",
                TorchWheelUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.3.0/torch-2.10.0+rocm7.3.0-cp312-cp312-win_amd64.whl"
            };

            await service.SaveSettingsAsync(newSettings);

            SettingsService reloadedService = new(null, tempFile);
            AppSettings loaded = reloadedService.LoadSettings();

            Assert.Equal("https://repo.radeon.com/rocm/windows/rocm-rel-7.3.0", loaded.RocmBaseUrl);
            Assert.Equal("2.10.0+rocm7.3.0", loaded.PyTorchVersion);
            Assert.Equal("https://repo.radeon.com/rocm/windows/rocm-rel-7.3.0/torch-2.10.0+rocm7.3.0-cp312-cp312-win_amd64.whl", loaded.TorchWheelUrl);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void AppSettings_UpdateFromBaseUrl_GeneratesConsistentUrls() {
        AppSettings settings = new();
        settings.UpdateFromBaseUrl("https://repo.radeon.com/rocm/windows/rocm-rel-8.0.0", "3.0.0+rocm8.0.0");

        Assert.Equal("https://repo.radeon.com/rocm/windows/rocm-rel-8.0.0", settings.RocmBaseUrl);
        Assert.Equal("3.0.0+rocm8.0.0", settings.PyTorchVersion);
        Assert.Contains("torch-3.0.0+rocm8.0.0-cp312-cp312-win_amd64.whl", settings.TorchWheelUrl);
        Assert.Contains("torchaudio-3.0.0+rocm8.0.0-cp312-cp312-win_amd64.whl", settings.TorchAudioWheelUrl);
    }
}
