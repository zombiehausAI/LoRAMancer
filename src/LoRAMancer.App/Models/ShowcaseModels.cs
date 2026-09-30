namespace LoRAMancer.App.Models;

public enum ShowcaseMediaType {
    Image,
    Video,
    Audio
}

public sealed class ShowcaseMediaItem {
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public ShowcaseMediaType MediaType { get; set; } = ShowcaseMediaType.Image;
    public long FileSizeBytes { get; set; }
    public string FormattedSize { get; set; } = "0 B";
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public string FolderPath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;

    // AI Generation Metadata
    public bool IsAiGenerated { get; set; }
    public string AiGenerator { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public long? Seed { get; set; }
    public int? Steps { get; set; }
    public string Sampler { get; set; } = string.Empty;
    public string Scheduler { get; set; } = string.Empty;
    public double? CfgScale { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public List<string> UsedLoras { get; set; } = new();
    public int? Width { get; set; }
    public int? Height { get; set; }
    public TimeSpan? Duration { get; set; }
    public Dictionary<string, string> RawMetadata { get; set; } = new();

    // User Categorization & Tags
    public string Category { get; set; } = "Uncategorized";
    public List<string> UserTags { get; set; } = new();
    public bool IsFavorite { get; set; }
    public string UserNotes { get; set; } = string.Empty;

    // Associated LoRA Connections (Assigned as Previews)
    public List<string> AssociatedLoraNames { get; set; } = new();
    public List<string> AssociatedLoraFilePaths { get; set; } = new();
}

public sealed class ShowcaseLibrary {
    public string DirectoryPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime LastScanned { get; set; }
    public int ItemCount { get; set; }
}

public sealed class ShowcaseFolderNode {
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int DirectMediaCount { get; set; }
    public int TotalMediaCount { get; set; }
    public int SubfolderCount { get; set; }
    public string? PreviewFileUrl { get; set; }
    public string? PreviewFilePath { get; set; }
}

public sealed class ShowcaseBreadcrumb {
    public string Name { get; set; } = string.Empty;
    public string? FullPath { get; set; }
}

