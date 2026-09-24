using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class HuggingFaceService {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public HuggingFaceService(SettingsService settingsService, HttpClient? httpClient = null) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<(bool Valid, string Username, string Details)> ValidateTokenAsync(string? token = null, CancellationToken cancellationToken = default) {
        string effectiveToken = token ?? _settingsService.Current.HuggingFaceToken;
        if (string.IsNullOrWhiteSpace(effectiveToken)) {
            return (false, string.Empty, "No HuggingFace token provided.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "https://huggingface.co/api/whoami-v2");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", effectiveToken.Trim());

        try {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return (false, string.Empty, $"Authentication failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string username = root.TryGetProperty("name", out JsonElement nameElem) ? (nameElem.GetString() ?? string.Empty) : "Authenticated User";
            string type = root.TryGetProperty("type", out JsonElement typeElem) ? (typeElem.GetString() ?? "user") : "user";
            string role = root.TryGetProperty("role", out JsonElement roleElem) ? (roleElem.GetString() ?? string.Empty) : string.Empty;

            string detail = $"Logged in as {username} ({type})";
            if (!string.IsNullOrEmpty(role)) {
                detail += $" [Role: {role}]";
            }

            return (true, username, detail);
        } catch (Exception ex) {
            return (false, string.Empty, $"Connection error: {ex.Message}");
        }
    }

    public async Task<(bool Accessible, string Message)> CheckModelAccessAsync(string modelRepoId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRepoId);

        string url = $"https://huggingface.co/api/models/{modelRepoId}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);

        string token = _settingsService.Current.HuggingFaceToken;
        if (!string.IsNullOrWhiteSpace(token)) {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
        }

        try {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) {
                return (true, $"Access granted to {modelRepoId}");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden) {
                return (false, $"Gated or private model: Access denied for {modelRepoId}. Ensure your HF token has accepted model agreements on huggingface.co.");
            }

            return (false, $"Model check returned HTTP {(int)response.StatusCode}");
        } catch (Exception ex) {
            return (false, $"Error checking model: {ex.Message}");
        }
    }
}
