namespace LoRAMancer.App.Models;

public sealed class LoraMetadata {
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string BaseModel { get; set; } = "Unknown";
    public int? NetworkDim { get; set; }
    public double? NetworkAlpha { get; set; }
    public string NetworkModule { get; set; } = string.Empty;
    public double? LearningRate { get; set; }
    public double? UnetLearningRate { get; set; }
    public double? TextEncoderLearningRate { get; set; }
    public string Optimizer { get; set; } = string.Empty;
    public string LrScheduler { get; set; } = string.Empty;
    public int? Epochs { get; set; }
    public int? TotalSteps { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public string Precision { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> RawHeaderMetadata { get; set; } = new Dictionary<string, string>();

    // Visual Browser & Civitai Enrichment
    public string? ThumbnailPath { get; set; }
    public string? Sha256Hash { get; set; }
    public CivitaiModelVersionInfo? CivitaiInfo { get; set; }
    public List<string> TrainedWords { get; set; } = new();

    // User Customization & Persistence
    public bool IsFavorite { get; set; }
    public string? UserBaseModel { get; set; }
    public DateTime? LastModifiedUtc { get; set; }

    public string EffectiveBaseModel => !string.IsNullOrWhiteSpace(UserBaseModel) ? UserBaseModel : BaseModel;
    public string FormattedSize => (FileSizeBytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
}

