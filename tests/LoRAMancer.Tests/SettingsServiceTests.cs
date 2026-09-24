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

    [Fact]
    public void SettingsService_DefaultPath_IsUnderUserProfileLoramancer() {
        SettingsService service = new();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expectedDir = Path.Combine(userProfile, ".loramancer");
        string expectedFile = Path.Combine(expectedDir, "settings.json");

        Assert.Equal(expectedDir, service.SettingsDirectory);
        Assert.Equal(expectedFile, service.SettingsFilePath);
    }

    [Fact]
    public async Task SaveAndLoadSettingsAsync_PersistsUserIdentifiableInfoAndApiKeys() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"credentials_test_{Guid.NewGuid():N}.json");

        try {
            SettingsService service = new(null, tempFile);
            AppSettings settings = new() {
                UserEmail = "creator@loramancer.ai",
                UserDisplayName = "WizardDev",
                HuggingFaceToken = "hf_secret_token_12345",
                CivitaiApiKey = "civitai_secret_key_67890"
            };

            await service.SaveSettingsAsync(settings);

            SettingsService reloaded = new(null, tempFile);
            AppSettings loaded = reloaded.LoadSettings();

            Assert.Equal("creator@loramancer.ai", loaded.UserEmail);
            Assert.Equal("WizardDev", loaded.UserDisplayName);
            Assert.Equal("hf_secret_token_12345", loaded.HuggingFaceToken);
            Assert.Equal("civitai_secret_key_67890", loaded.CivitaiApiKey);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task SaveAndLoadSettingsAsync_PersistsOllamaEndpointAndCredentials() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"ollama_settings_test_{Guid.NewGuid():N}.json");

        try {
            SettingsService service = new(null, tempFile);
            AppSettings settings = new() {
                OllamaEndpointUrl = "https://ollama.internal.datacenter.net:11434",
                OllamaApiKey = "datacenter_bearer_token_xyz",
                OllamaDefaultModel = "llava:34b"
            };

            await service.SaveSettingsAsync(settings);

            SettingsService reloaded = new(null, tempFile);
            AppSettings loaded = reloaded.LoadSettings();

            Assert.Equal("https://ollama.internal.datacenter.net:11434", loaded.OllamaEndpointUrl);
            Assert.Equal("datacenter_bearer_token_xyz", loaded.OllamaApiKey);
            Assert.Equal("llava:34b", loaded.OllamaDefaultModel);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task SaveAndLoadSettingsAsync_PersistsNetworkServerAndRemoteClientConfig() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"network_server_settings_test_{Guid.NewGuid():N}.json");

        try {
            SettingsService service = new(null, tempFile);
            AppSettings settings = new() {
                ServerEnabled = true,
                ServerPort = 9000,
                ServerBindAddress = "0.0.0.0",
                ServerAccessToken = "secure_pin_1234",
                ClientRemoteMode = true,
                ClientRemoteHostUrl = "http://192.168.1.150:9000",
                ClientRemoteAccessToken = "secure_pin_1234"
            };

            await service.SaveSettingsAsync(settings);

            SettingsService reloaded = new(null, tempFile);
            AppSettings loaded = reloaded.LoadSettings();

            Assert.True(loaded.ServerEnabled);
            Assert.Equal(9000, loaded.ServerPort);
            Assert.Equal("0.0.0.0", loaded.ServerBindAddress);
            Assert.Equal("secure_pin_1234", loaded.ServerAccessToken);
            Assert.True(loaded.ClientRemoteMode);
            Assert.Equal("http://192.168.1.150:9000", loaded.ClientRemoteHostUrl);
            Assert.Equal("secure_pin_1234", loaded.ClientRemoteAccessToken);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task SaveAndLoadSettingsAsync_PersistsMinimizeToTrayOnClose() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tray_test_{Guid.NewGuid():N}.json");

        try {
            SettingsService service = new(null, tempFile);
            Assert.True(service.Current.MinimizeToTrayOnClose);

            AppSettings updated = new() {
                MinimizeToTrayOnClose = false
            };
            await service.SaveSettingsAsync(updated);

            SettingsService reloaded = new(null, tempFile);
            AppSettings loaded = reloaded.LoadSettings();

            Assert.False(loaded.MinimizeToTrayOnClose);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }
}

