namespace LoRAMancer.App.Models;

public sealed class LoraMetadata {
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public string BaseModel { get; init; } = "Unknown";
    public int? NetworkDim { get; init; }
    public double? NetworkAlpha { get; init; }
    public string NetworkModule { get; init; } = string.Empty;
    public double? LearningRate { get; init; }
    public double? UnetLearningRate { get; init; }
    public double? TextEncoderLearningRate { get; init; }
    public string Optimizer { get; init; } = string.Empty;
    public string LrScheduler { get; init; } = string.Empty;
    public int? Epochs { get; init; }
    public int? TotalSteps { get; init; }
    public string Resolution { get; init; } = string.Empty;
    public string Precision { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> RawHeaderMetadata { get; init; } = new Dictionary<string, string>();
}
