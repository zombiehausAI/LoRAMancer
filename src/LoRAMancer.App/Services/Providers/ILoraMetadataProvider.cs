namespace LoRAMancer.App.Services.Providers;

using LoRAMancer.App.Models;

/// <summary>
/// Represents normalized metadata retrieved from any external provider.
/// </summary>
public sealed class ProviderLookupResult {
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public string? VersionName { get; set; }
    public string? BaseModel { get; set; }
    public List<string> TriggerWords { get; set; } = new();
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? PreviewImageUrl { get; set; }
    public string? ModelUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public List<string> SamplePrompts { get; set; } = new();
    public Dictionary<string, string> ExtraMetadata { get; set; } = new();
}

/// <summary>
/// Common contract for metadata providers (Civitai, Hugging Face, Danbooru, Ollama).
/// </summary>
public interface ILoraMetadataProvider {
    string ProviderId { get; }
    string DisplayName { get; }
    int Priority { get; }

    /// <summary>
    /// Attempts to look up or infer metadata for the specified LoRA.
    /// Can return null if no matching model or enrichment was found.
    /// </summary>
    Task<ProviderLookupResult?> LookupAsync(LoraMetadata meta, CancellationToken ct = default);
}
