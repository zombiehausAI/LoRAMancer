using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class SettingsService {
    private readonly string _settingsFilePath;
    private readonly HttpClient _httpClient;
    private AppSettings _currentSettings;

    public event Action<AppSettings>? OnSettingsChanged;

    public AppSettings Current => _currentSettings;

    public SettingsService(HttpClient? httpClient = null, string? customSettingsPath = null) {
        _httpClient = httpClient ?? new HttpClient();

        if (!string.IsNullOrWhiteSpace(customSettingsPath)) {
            _settingsFilePath = customSettingsPath;
        } else {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "LoRAMancer");
            Directory.CreateDirectory(appFolder);
            _settingsFilePath = Path.Combine(appFolder, "settings.json");
        }

        _currentSettings = LoadSettings();
    }

    public AppSettings LoadSettings() {
        if (!File.Exists(_settingsFilePath)) {
            return new AppSettings();
        }

        try {
            string json = File.ReadAllText(_settingsFilePath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });
            return settings ?? new AppSettings();
        } catch {
            return new AppSettings();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(settings);

        _currentSettings = settings;
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions {
            WriteIndented = true
        });

        string? dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(_settingsFilePath, json, cancellationToken);
        OnSettingsChanged?.Invoke(_currentSettings);
    }

    public async Task<AppSettings> ResetToDefaultsAsync(CancellationToken cancellationToken = default) {
        AppSettings defaultSettings = new();
        await SaveSettingsAsync(defaultSettings, cancellationToken);
        return defaultSettings;
    }

    public async Task<(bool Reachable, string StatusMessage)> TestWheelUrlAsync(string url, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(url)) {
            return (false, "URL is empty");
        }

        try {
            using HttpRequestMessage request = new(HttpMethod.Head, url);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode) {
                long? length = response.Content.Headers.ContentLength;
                string sizeStr = length.HasValue ? $" ({(length.Value / (1024.0 * 1024.0)):F1} MB)" : string.Empty;
                return (true, $"Reachable: HTTP {(int)response.StatusCode}{sizeStr}");
            }

            // Fallback: Some repositories reject HEAD, test with range GET
            using HttpRequestMessage getRequest = new(HttpMethod.Get, url);
            getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1024);
            using HttpResponseMessage getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (getResponse.IsSuccessStatusCode) {
                return (true, $"Reachable: HTTP {(int)getResponse.StatusCode}");
            }

            return (false, $"HTTP Error: {(int)getResponse.StatusCode} {getResponse.ReasonPhrase}");
        } catch (Exception ex) {
            return (false, $"Connection Failed: {ex.Message}");
        }
    }
}
