using System.Text.Json;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services.Providers;

namespace LoRAMancer.App.Services;

/// <summary>
/// Free & Open provider that analyzes embedded ss_tag_frequency metadata against
/// the public Danbooru tag API to classify character, series, and artist trigger words.
/// </summary>
public sealed class DanbooruTagService : ILoraMetadataProvider {
    private readonly HttpClient _httpClient;

    public string ProviderId => "danbooru";
    public string DisplayName => "Danbooru Tag Classifier";
    public int Priority => 30;

    public DanbooruTagService(HttpClient? httpClient = null) {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<ProviderLookupResult?> LookupAsync(LoraMetadata meta, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(meta);

        // Extract raw tags from header metadata if available
        List<string> candidateTags = ExtractCandidateTags(meta);
        if (candidateTags.Count == 0) {
            return null;
        }

        // Danbooru tag categories: 0=General, 1=Artist, 3=Copyright, 4=Character, 5=Meta
        List<string> classifiedTriggers = await ClassifyTagsAsync(candidateTags, ct);
        if (classifiedTriggers.Count == 0) {
            return null;
        }

        return new ProviderLookupResult {
            ProviderId = ProviderId,
            ProviderDisplayName = DisplayName,
            TriggerWords = classifiedTriggers,
            ExtraMetadata = new Dictionary<string, string> {
                ["ExtractedFrom"] = "ss_tag_frequency",
                ["ClassifiedCount"] = classifiedTriggers.Count.ToString()
            }
        };
    }

    private List<string> ExtractCandidateTags(LoraMetadata meta) {
        List<string> candidates = new();

        if (meta.RawHeaderMetadata == null || meta.RawHeaderMetadata.Count == 0) {
            return candidates;
        }

        // Kohya / ai-toolkit stores ss_tag_frequency as JSON in raw header
        foreach (var kvp in meta.RawHeaderMetadata) {
            if (kvp.Key.Contains("tag_frequency", StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains("dataset_dirs", StringComparison.OrdinalIgnoreCase)) {
                try {
                    using JsonDocument doc = JsonDocument.Parse(kvp.Value);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) {
                        foreach (JsonProperty folderProp in doc.RootElement.EnumerateObject()) {
                            if (folderProp.Value.ValueKind == JsonValueKind.Object) {
                                foreach (JsonProperty tagProp in folderProp.Value.EnumerateObject()) {
                                    string tag = CleanTag(tagProp.Name);
                                    if (!string.IsNullOrWhiteSpace(tag) && !candidates.Contains(tag, StringComparer.OrdinalIgnoreCase)) {
                                        candidates.Add(tag);
                                    }
                                }
                            }
                        }
                    }
                } catch {
                    // Ignore malformed tag frequency JSON
                }
            }
        }

        return candidates.Take(40).ToList();
    }

    private static string CleanTag(string raw) {
        string t = raw.Trim();
        // Kohya format: "10_tagname" -> "tagname"
        int underscoreIdx = t.IndexOf('_');
        if (underscoreIdx > 0 && int.TryParse(t[..underscoreIdx], out _)) {
            t = t[(underscoreIdx + 1)..];
        }
        return t.Trim().Replace(" ", "_");
    }

    private async Task<List<string>> ClassifyTagsAsync(List<string> tags, CancellationToken ct) {
        List<string> triggers = new();
        if (tags.Count == 0) {
            return triggers;
        }

        string namesComma = string.Join(",", tags.Select(Uri.EscapeDataString));
        string url = $"https://danbooru.donmai.us/tags.json?search[name_comma]={namesComma}&limit=50";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0");

        try {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) {
                return triggers;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) {
                return triggers;
            }

            foreach (JsonElement tagElem in doc.RootElement.EnumerateArray()) {
                int category = tagElem.TryGetProperty("category", out JsonElement catElem) ? catElem.GetInt32() : 0;
                string name = tagElem.TryGetProperty("name", out JsonElement nameElem) ? (nameElem.GetString() ?? string.Empty) : string.Empty;

                // Priority: Character (4), Copyright/Series (3), Artist (1)
                if ((category == 4 || category == 3 || category == 1) && !string.IsNullOrWhiteSpace(name)) {
                    string readableName = name.Replace("_", " ");
                    if (!triggers.Contains(readableName, StringComparer.OrdinalIgnoreCase)) {
                        triggers.Add(readableName);
                    }
                }
            }
        } catch {
            // Free network query failed, gracefully return empty
        }

        return triggers;
    }
}
