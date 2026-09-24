namespace LoRAMancer.App.Models;

public sealed class LoraHistoryRecord {
    public string Id { get; set; } = $"lora_{Guid.NewGuid():N}";
    public string Name { get; set; } = string.Empty;
    public string BaseArchitecture { get; set; } = "flux1"; // flux1, sdxl, chroma, etc.
    public string TriggerWord { get; set; } = string.Empty;
    public List<string> SamplePrompts { get; set; } = new();
    public int Steps { get; set; } = 1500;
    public int Epochs { get; set; } = 10;
    public int Dim { get; set; } = 16;
    public int Alpha { get; set; } = 16;
    public double LearningRate { get; set; } = 1e-4;
    public string Optimizer { get; set; } = "adamw_bf16";
    public double FinalLoss { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration => CompletedAt > StartedAt ? CompletedAt - StartedAt : TimeSpan.Zero;
    public string OutputFilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string DatasetPath { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public string ConfigYamlPath { get; set; } = string.Empty;
    public string ConfigYamlContent { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public string Status { get; set; } = "Completed"; // Completed, Failed, Cancelled, InProgress
    public List<string> Tags { get; set; } = new();
    public List<string> PreviewImagePaths { get; set; } = new();

    public string FormattedFileSize {
        get {
            if (FileSizeBytes <= 0) {
                return "-";
            }
            double mb = FileSizeBytes / (1024.0 * 1024.0);
            return mb >= 1024.0 ? $"{(mb / 1024.0):F2} GB" : $"{mb:F1} MB";
        }
    }

    public string FormattedDuration {
        get {
            if (Duration.TotalHours >= 1) {
                return $"{(int)Duration.TotalHours}h {Duration.Minutes}m";
            }
            if (Duration.TotalMinutes >= 1) {
                return $"{Duration.Minutes}m {Duration.Seconds}s";
            }
            return $"{Duration.Seconds}s";
        }
    }
}
