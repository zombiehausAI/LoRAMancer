using System.Text.Json;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services.Providers;

namespace LoRAMancer.App.Services;

/// <summary>
/// Result of an aggregated multi-provider lookup showing merged data and provider contributions.
/// </summary>
public sealed class AggregatedLoraEnrichmentResult {
    public bool Success => ContributingProviders.Count > 0;
    public List<string> ContributingProviders { get; set; } = new();
    public string? PrimaryModelName { get; set; }
    public string? BaseModel { get; set; }
    public List<string> MergedTriggerWords { get; set; } = new();
    public string? PreviewImageUrl { get; set; }
    public string? PrimarySourceUrl { get; set; }
    public Dictionary<string, string> ProviderSourceUrls { get; set; } = new();
}

/// <summary>
/// Coordinates multi-provider lookups across Civitai, Hugging Face, Danbooru, and local metadata.
/// Merges fields non-destructively so that incomplete results from one provider are backfilled by others.
/// </summary>
public sealed class LoraMetadataAggregatorService {
    private readonly List<ILoraMetadataProvider> _providers;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;

    public IReadOnlyList<ILoraMetadataProvider> Providers => _providers.AsReadOnly();

    public LoraMetadataAggregatorService(
        CivitaiService civitaiService,
        HuggingFaceService huggingFaceService,
        DanbooruTagService danbooruTagService,
        SettingsService settingsService,
        HttpClient? httpClient = null) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _httpClient = httpClient ?? new HttpClient();

        _providers = new List<ILoraMetadataProvider> {
            civitaiService,
            huggingFaceService,
            danbooruTagService
        }.OrderBy(p => p.Priority).ToList();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _cacheDirectory = Path.Combine(userProfile, ".loramancer", "lora_cache");
        if (!Directory.Exists(_cacheDirectory)) {
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    /// <summary>
    /// Enriches a LoRA model by querying available providers in priority order and merging missing fields.
    /// </summary>
    public async Task<AggregatedLoraEnrichmentResult> EnrichAsync(LoraMetadata meta, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(meta);

        AggregatedLoraEnrichmentResult aggregate = new();
        HashSet<string> existingTriggers = new(meta.TrainedWords, StringComparer.OrdinalIgnoreCase);

        foreach (ILoraMetadataProvider provider in _providers) {
            if (ct.IsCancellationRequested) {
                break;
            }

            try {
                ProviderLookupResult? result = await provider.LookupAsync(meta, ct);
                if (result == null) {
                    continue;
                }

                if (!aggregate.ContributingProviders.Contains(provider.DisplayName)) {
                    aggregate.ContributingProviders.Add(provider.DisplayName);
                }

                if (!string.IsNullOrWhiteSpace(result.ModelUrl)) {
                    aggregate.ProviderSourceUrls[provider.DisplayName] = result.ModelUrl;
                }

                // 1. Backfill Model Name
                if (string.IsNullOrWhiteSpace(aggregate.PrimaryModelName) && !string.IsNullOrWhiteSpace(result.ModelName)) {
                    aggregate.PrimaryModelName = result.ModelName;
                }

                // 2. Backfill Base Model if unknown
                if (!string.IsNullOrWhiteSpace(result.BaseModel)) {
                    if (string.IsNullOrWhiteSpace(aggregate.BaseModel)) {
                        aggregate.BaseModel = result.BaseModel;
                    }
                    if (meta.BaseModel == "Unknown" || string.IsNullOrWhiteSpace(meta.BaseModel)) {
                        meta.BaseModel = result.BaseModel;
                    }
                }

                // 3. Merge Trigger Words non-destructively
                if (result.TriggerWords != null && result.TriggerWords.Count > 0) {
                    foreach (string trigger in result.TriggerWords) {
                        string clean = trigger.Trim();
                        if (!string.IsNullOrWhiteSpace(clean) && existingTriggers.Add(clean)) {
                            meta.TrainedWords.Add(clean);
                        }
                    }
                }

                // 4. Backfill Preview Image
                if (string.IsNullOrWhiteSpace(aggregate.PreviewImageUrl) && !string.IsNullOrWhiteSpace(result.PreviewImageUrl)) {
                    aggregate.PreviewImageUrl = result.PreviewImageUrl;
                }

                // 5. Backfill Primary Source URL
                if (string.IsNullOrWhiteSpace(aggregate.PrimarySourceUrl) && !string.IsNullOrWhiteSpace(result.ModelUrl)) {
                    aggregate.PrimarySourceUrl = result.ModelUrl;
                }

                // 6. Synthesize or update CivitaiInfo compatibility model so existing UI / DB columns populate
                if (meta.CivitaiInfo == null && (!string.IsNullOrWhiteSpace(result.ModelName) || !string.IsNullOrWhiteSpace(result.PreviewImageUrl))) {
                    meta.CivitaiInfo = new CivitaiModelVersionInfo {
                        ModelName = result.ModelName ?? meta.FileName,
                        VersionName = result.VersionName ?? "v1.0",
                        BaseModel = result.BaseModel ?? meta.BaseModel,
                        Description = result.Description ?? string.Empty,
                        CivitaiUrl = result.ModelUrl ?? string.Empty,
                        PreviewImageUrl = result.PreviewImageUrl ?? string.Empty,
                        DownloadUrl = result.DownloadUrl ?? string.Empty,
                        TrainedWords = new List<string>(meta.TrainedWords),
                        SamplePrompts = result.SamplePrompts ?? new List<string>()
                    };
                } else if (meta.CivitaiInfo != null) {
                    if (string.IsNullOrWhiteSpace(meta.CivitaiInfo.PreviewImageUrl) && !string.IsNullOrWhiteSpace(result.PreviewImageUrl)) {
                        meta.CivitaiInfo.PreviewImageUrl = result.PreviewImageUrl;
                    }
                    if (string.IsNullOrWhiteSpace(meta.CivitaiInfo.CivitaiUrl) && !string.IsNullOrWhiteSpace(result.ModelUrl)) {
                        meta.CivitaiInfo.CivitaiUrl = result.ModelUrl;
                    }
                    meta.CivitaiInfo.TrainedWords = new List<string>(meta.TrainedWords);
                }
            } catch {
                // If one provider fails, continue to next provider in the chain
            }
        }

        aggregate.MergedTriggerWords = new List<string>(meta.TrainedWords);

        // Download remote thumbnail if local thumbnail is missing and preview image was found
        if (string.IsNullOrWhiteSpace(meta.ThumbnailPath) && !string.IsNullOrWhiteSpace(aggregate.PreviewImageUrl) && !string.IsNullOrWhiteSpace(meta.Sha256Hash)) {
            await DownloadAndCacheThumbnailAsync(meta, aggregate.PreviewImageUrl, ct);
        }

        return aggregate;
    }

    private async Task DownloadAndCacheThumbnailAsync(LoraMetadata meta, string imageUrl, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(meta.Sha256Hash)) {
            return;
        }

        try {
            string cachedPath = Path.Combine(_cacheDirectory, $"{meta.Sha256Hash}.png");
            if (File.Exists(cachedPath)) {
                meta.ThumbnailPath = cachedPath;
                return;
            }

            using HttpRequestMessage request = new(HttpMethod.Get, imageUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return;
            }

            byte[] imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(cachedPath, imageBytes, cancellationToken);
            meta.ThumbnailPath = cachedPath;
        } catch {
            // Silently handle thumbnail download issues
        }
    }
}
