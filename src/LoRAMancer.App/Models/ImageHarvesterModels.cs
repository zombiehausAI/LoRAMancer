namespace LoRAMancer.App.Models;

public enum HarvestEngine {
    DuckDuckGo,
    Reddit,
    Wikimedia,
    Unsplash,
    Safebooru,
    Danbooru,
    Openverse,
    Flickr,
    DirectUrls,
    CustomRest,
    PythonPlugin
}

public enum OllamaAuditStatus {
    NotAudited,
    Clean,
    Flagged
}

public sealed class HarvestProviderDefinition {
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "Search";
    public bool IsEnabled { get; set; } = true;
    public bool IsCustom { get; set; } = false;
    public string? PluginId { get; set; }
    public HarvestEngine Engine { get; set; } = HarvestEngine.DuckDuckGo;
    public string SearchUrlTemplate { get; set; } = string.Empty;
    public string JsonResultsPath { get; set; } = string.Empty;
    public string JsonImageUrlKey { get; set; } = "url";
    public string JsonThumbUrlKey { get; set; } = "thumbnail";
    public string JsonTitleKey { get; set; } = "title";
}

public sealed class HarvestedCandidateItem {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public HarvestEngine SourceEngine { get; set; } = HarvestEngine.DuckDuckGo;
    public string ProviderId { get; set; } = "duckduckgo";
    public string ProviderName { get; set; } = "DuckDuckGo";
    public bool IsSelected { get; set; } = true;
    public OllamaAuditStatus AuditStatus { get; set; } = OllamaAuditStatus.NotAudited;
    public string AuditReason { get; set; } = string.Empty;
    public string? LocalPreviewDataUri { get; set; }
    public string? DownloadedFilePath { get; set; }
}

public sealed class HarvestSearchQuery {
    public HarvestEngine Engine { get; set; } = HarvestEngine.DuckDuckGo;
    public string ProviderId { get; set; } = "duckduckgo";
    public bool SearchAllProviders { get; set; } = false;
    public List<string> SelectedProviderIds { get; set; } = new();
    public string Query { get; set; } = string.Empty;
    public string Subreddit { get; set; } = "wallpaper";
    public string Tags { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 60;
    public bool SafeSearch { get; set; } = false;
    public string DirectUrlsText { get; set; } = string.Empty;
    public int MinWidth { get; set; } = 512;
    public int MinHeight { get; set; } = 512;
}

public sealed class HarvestDownloadProgress {
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public bool IsComplete { get; set; }
    public string? ErrorMessage { get; set; }
}

