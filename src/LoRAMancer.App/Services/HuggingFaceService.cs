using System.Text.Json;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services.Providers;

namespace LoRAMancer.App.Services;

public sealed class HuggingFaceService : ILoraMetadataProvider {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public string ProviderId => "huggingface";
    public string DisplayName => "Hugging Face Hub";
    public int Priority => 20;

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

    public async Task<ProviderLookupResult?> LookupAsync(LoraMetadata meta, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(meta);

        string searchTarget = Path.GetFileNameWithoutExtension(meta.FileName);
        if (string.IsNullOrWhiteSpace(searchTarget)) {
            return null;
        }

        return await LookupModelBySearchAsync(searchTarget, ct);
    }

    public async Task<ProviderLookupResult?> LookupModelBySearchAsync(string searchTerm, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(searchTerm)) {
            return null;
        }

        // Clean search term: remove common version extensions like _v1, -v1.0, etc.
        string cleanQuery = Regex.Replace(searchTerm, @"[_\-](v\d+(\.\d+)?|epoch\d+|final)$", "", RegexOptions.IgnoreCase).Trim();
        string url = $"https://huggingface.co/api/models?search={Uri.EscapeDataString(cleanQuery)}&filter=lora&limit=5&full=true";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0");

        string token = _settingsService.Current.HuggingFaceToken;
        if (!string.IsNullOrWhiteSpace(token)) {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
        }

        try {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) {
                return null;
            }

            // Pick the first matching model
            JsonElement firstModel = doc.RootElement[0];
            string repoId = firstModel.TryGetProperty("id", out JsonElement idElem) ? (idElem.GetString() ?? string.Empty) : string.Empty;
            if (string.IsNullOrWhiteSpace(repoId)) {
                return null;
            }

            string author = repoId.Contains('/') ? repoId.Split('/')[0] : string.Empty;
            string modelName = repoId.Contains('/') ? repoId.Split('/')[1] : repoId;

            string baseModel = string.Empty;
            List<string> triggerWords = new();
            List<string> tags = new();

            if (firstModel.TryGetProperty("tags", out JsonElement tagsElem) && tagsElem.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement tag in tagsElem.EnumerateArray()) {
                    string? t = tag.GetString();
                    if (string.IsNullOrWhiteSpace(t)) {
                        continue;
                    }

                    tags.Add(t);
                    if (t.StartsWith("base_model:", StringComparison.OrdinalIgnoreCase)) {
                        baseModel = t["base_model:".Length..];
                    } else if (string.IsNullOrEmpty(baseModel) && (t.Equals("flux", StringComparison.OrdinalIgnoreCase) || t.Equals("sdxl", StringComparison.OrdinalIgnoreCase) || t.Equals("sd15", StringComparison.OrdinalIgnoreCase))) {
                        baseModel = t.ToUpperInvariant();
                    }
                }
            }

            // Try reading cardData for instance_prompt or extra tags
            if (firstModel.TryGetProperty("cardData", out JsonElement cardElem) && cardElem.ValueKind == JsonValueKind.Object) {
                if (cardElem.TryGetProperty("instance_prompt", out JsonElement instElem)) {
                    string? prompt = instElem.GetString();
                    if (!string.IsNullOrWhiteSpace(prompt)) {
                        triggerWords.Add(prompt.Trim());
                    }
                }

                if (string.IsNullOrEmpty(baseModel) && cardElem.TryGetProperty("base_model", out JsonElement bmElem)) {
                    baseModel = bmElem.GetString() ?? string.Empty;
                }
            }

            // Also attempt to fetch README.md to parse trigger words if still empty
            if (triggerWords.Count == 0) {
                await TryExtractTriggersFromReadmeAsync(repoId, triggerWords, cancellationToken);
            }

            string previewImageUrl = string.Empty;
            // HuggingFace model cards often have thumbnail/widget sample images
            if (firstModel.TryGetProperty("widgetData", out JsonElement widgetElem) && widgetElem.ValueKind == JsonValueKind.Array && widgetElem.GetArrayLength() > 0) {
                JsonElement firstWidget = widgetElem[0];
                if (firstWidget.TryGetProperty("output", out JsonElement outElem) && outElem.TryGetProperty("url", out JsonElement outUrlElem)) {
                    previewImageUrl = outUrlElem.GetString() ?? string.Empty;
                }
            }

            return new ProviderLookupResult {
                ProviderId = ProviderId,
                ProviderDisplayName = DisplayName,
                ModelName = modelName,
                Author = author,
                BaseModel = baseModel,
                TriggerWords = triggerWords,
                ModelUrl = $"https://huggingface.co/{repoId}",
                PreviewImageUrl = previewImageUrl,
                ExtraMetadata = new Dictionary<string, string> {
                    ["RepoId"] = repoId,
                    ["HfTags"] = string.Join(", ", tags.Take(10))
                }
            };
        } catch {
            return null;
        }
    }

    private async Task TryExtractTriggersFromReadmeAsync(string repoId, List<string> triggerWords, CancellationToken ct) {
        try {
            string rawReadmeUrl = $"https://huggingface.co/{repoId}/raw/main/README.md";
            using HttpRequestMessage req = new(HttpMethod.Get, rawReadmeUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0");

            string token = _settingsService.Current.HuggingFaceToken;
            if (!string.IsNullOrWhiteSpace(token)) {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            }

            using HttpResponseMessage res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) {
                return;
            }

            string text = await res.Content.ReadAsStringAsync(ct);

            // Pattern 1: instance_prompt: "..." or instance_prompt: word
            Match match = Regex.Match(text, @"instance_prompt:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
            if (match.Success) {
                string word = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(word) && !triggerWords.Contains(word, StringComparer.OrdinalIgnoreCase)) {
                    triggerWords.Add(word);
                }
            }

            // Pattern 2: Trigger word(s): `word` or **Trigger:** word
            Match triggerMatch = Regex.Match(text, @"(?:trigger\s*(?:words?|tag)?|activation\s*tag):\s*[`\*""']*([a-zA-Z0-9_\-\s,]+)[`\*""']*", RegexOptions.IgnoreCase);
            if (triggerMatch.Success) {
                string rawWords = triggerMatch.Groups[1].Value;
                foreach (string w in rawWords.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                    string clean = w.Trim().Trim('`', '*', '"', '\'');
                    if (!string.IsNullOrWhiteSpace(clean) && !triggerWords.Contains(clean, StringComparer.OrdinalIgnoreCase)) {
                        triggerWords.Add(clean);
                    }
                }
            }
        } catch {
            // Non-critical background extraction
        }
    }
}

