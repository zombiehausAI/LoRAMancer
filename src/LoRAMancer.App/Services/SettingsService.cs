using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class SettingsService {
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _settingsFilePath;
    private readonly HttpClient _httpClient;
    private AppSettings _currentSettings;

    public string SettingsDirectory { get; }
    public string SettingsFilePath => _settingsFilePath;

    public event Action<AppSettings>? OnSettingsChanged;

    public AppSettings Current => _currentSettings;

    public SettingsService(HttpClient? httpClient = null, string? customSettingsPath = null) {
        _httpClient = httpClient ?? new HttpClient();

        if (!string.IsNullOrWhiteSpace(customSettingsPath)) {
            _settingsFilePath = customSettingsPath;
            SettingsDirectory = Path.GetDirectoryName(customSettingsPath) ?? string.Empty;
        } else {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            SettingsDirectory = Path.Combine(userProfile, ".loramancer");
            if (!Directory.Exists(SettingsDirectory)) {
                Directory.CreateDirectory(SettingsDirectory);
            }
            _settingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

            // Migration: If .loramancer/settings.json does not exist yet, check and migrate legacy path
            string legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoRAMancer");
            string legacyFile = Path.Combine(legacyDir, "settings.json");
            if (!File.Exists(_settingsFilePath) && File.Exists(legacyFile)) {
                try {
                    File.Copy(legacyFile, _settingsFilePath, overwrite: false);
                } catch {
                    // Fall back to clean default if legacy copy fails
                }
            }
        }

        _currentSettings = LoadSettings();
    }

    public AppSettings LoadSettings() {
        if (!File.Exists(_settingsFilePath)) {
            return new AppSettings();
        }

        for (int attempt = 0; attempt < 5; attempt++) {
            try {
                using var stream = new FileStream(_settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                });
                return settings ?? new AppSettings();
            } catch (IOException) when (attempt < 4) {
                Thread.Sleep(50);
            } catch {
                return new AppSettings();
            }
        }

        return new AppSettings();
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

        await _fileLock.WaitAsync(cancellationToken);
        try {
            for (int attempt = 0; attempt < 5; attempt++) {
                try {
                    await File.WriteAllTextAsync(_settingsFilePath, json, cancellationToken);
                    break;
                } catch (IOException) when (attempt < 4) {
                    await Task.Delay(50, cancellationToken);
                }
            }
        } finally {
            _fileLock.Release();
        }

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

    public async Task<List<string>> ProbeOllamaModelsAsync(CancellationToken cancellationToken = default) {
        string cleanUrl = (_currentSettings.OllamaEndpointUrl ?? "http://localhost:11434").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(cleanUrl)) {
            cleanUrl = "http://localhost:11434";
        }

        var candidates = new List<string> { cleanUrl };
        if (cleanUrl.Contains("localhost")) {
            candidates.Add(cleanUrl.Replace("localhost", "127.0.0.1"));
        } else if (cleanUrl.Contains("127.0.0.1")) {
            candidates.Add(cleanUrl.Replace("127.0.0.1", "localhost"));
        }

        var discovered = new List<string>();

        foreach (var url in candidates) {
            try {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/tags");
                if (!string.IsNullOrWhiteSpace(_currentSettings.OllamaApiKey)) {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _currentSettings.OllamaApiKey.Trim());
                }

                using var response = await _httpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode) {
                    string json = await response.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array) {
                        foreach (var m in modelsElem.EnumerateArray()) {
                            string name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(name) && !discovered.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                                discovered.Add(name);
                            }
                        }
                    }
                    break;
                }
            } catch {
                // Try next candidate
            }
        }

        if (discovered.Count > 0) {
            _currentSettings.OllamaDiscoveredModels = discovered;

            // Retain saved default model across sessions:
            // Only assign if OllamaDefaultModel is empty or whitespace
            if (string.IsNullOrWhiteSpace(_currentSettings.OllamaDefaultModel)) {
                string? best = discovered.FirstOrDefault(m => m.Contains("vision", StringComparison.OrdinalIgnoreCase) || m.Contains("llava", StringComparison.OrdinalIgnoreCase) || m.Contains("vl", StringComparison.OrdinalIgnoreCase))
                             ?? discovered.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(best)) {
                    _currentSettings.OllamaDefaultModel = best;
                }
            }

            try {
                await SaveSettingsAsync(_currentSettings, cancellationToken);
            } catch {
                // Suppress save issues during background probe
            }
        }

        return discovered;
    }
}
