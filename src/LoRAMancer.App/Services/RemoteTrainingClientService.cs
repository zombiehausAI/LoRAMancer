using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class RemoteTrainingClientService {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public RemoteTrainingClientService(HttpClient httpClient, SettingsService settingsService) {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    private string GetEffectiveBaseUrl(string? overrideUrl = null) {
        string url = !string.IsNullOrWhiteSpace(overrideUrl) ? overrideUrl : _settingsService.Current.ClientRemoteHostUrl;
        return url.TrimEnd('/');
    }

    private string GetEffectiveToken(string? overrideToken = null) {
        return !string.IsNullOrWhiteSpace(overrideToken) ? overrideToken : _settingsService.Current.ClientRemoteAccessToken;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? overrideUrl = null, string? overrideToken = null) {
        string baseUrl = GetEffectiveBaseUrl(overrideUrl);
        HttpRequestMessage req = new(method, $"{baseUrl}{path}");
        string token = GetEffectiveToken(overrideToken);
        if (!string.IsNullOrWhiteSpace(token)) {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    public async Task<ServerHealthDto?> CheckHealthAsync(string? overrideUrl = null, string? overrideToken = null, CancellationToken cancellationToken = default) {
        using HttpRequestMessage req = CreateRequest(HttpMethod.Get, "/api/v1/health", overrideUrl, overrideToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(req, cancellationToken);
        if (!response.IsSuccessStatusCode) {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ServerHealthDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<DatasetUploadResultDto> UploadDatasetZipAsync(string zipFilePath, string? overrideUrl = null, string? overrideToken = null, CancellationToken cancellationToken = default) {
        if (!File.Exists(zipFilePath)) {
            throw new FileNotFoundException("ZIP dataset file not found.", zipFilePath);
        }

        string baseUrl = GetEffectiveBaseUrl(overrideUrl);
        using MultipartFormDataContent form = new();
        await using FileStream fs = File.OpenRead(zipFilePath);
        using StreamContent fileContent = new(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(zipFilePath));

        using HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/api/v1/datasets/upload") {
            Content = form
        };

        string token = GetEffectiveToken(overrideToken);
        if (!string.IsNullOrWhiteSpace(token)) {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(req, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<DatasetUploadResultDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new DatasetUploadResultDto { Success = false, Message = "Failed to parse upload response." };
    }

    public async Task<bool> StartRemoteTrainingAsync(StartRemoteTrainingRequest request, string? overrideUrl = null, string? overrideToken = null, CancellationToken cancellationToken = default) {
        using HttpRequestMessage req = CreateRequest(HttpMethod.Post, "/api/v1/training/start", overrideUrl, overrideToken);
        string json = JsonSerializer.Serialize(request);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _httpClient.SendAsync(req, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> StopRemoteTrainingAsync(string? overrideUrl = null, string? overrideToken = null, CancellationToken cancellationToken = default) {
        using HttpRequestMessage req = CreateRequest(HttpMethod.Post, "/api/v1/training/stop", overrideUrl, overrideToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(req, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task StartTelemetryStreamAsync(Action<TrainingTelemetryDto> onTelemetry, CancellationToken cancellationToken, string? overrideUrl = null, string? overrideToken = null) {
        using HttpRequestMessage req = CreateRequest(HttpMethod.Get, "/api/v1/training/stream", overrideUrl, overrideToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream);

        while (!cancellationToken.IsCancellationRequested) {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            string payload = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(payload)) {
                continue;
            }

            try {
                TrainingTelemetryDto? telemetry = JsonSerializer.Deserialize<TrainingTelemetryDto>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (telemetry != null) {
                    onTelemetry(telemetry);
                }
            } catch {
                // Ignore malformed line
            }
        }
    }
}
