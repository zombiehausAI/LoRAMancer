namespace LoRAMancer.App.Models;

public sealed class CivitaiModelVersionInfo {
    public long ModelId { get; set; }
    public long VersionId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string VersionName { get; set; } = string.Empty;
    public string BaseModel { get; set; } = string.Empty;
    public List<string> TrainedWords { get; set; } = new();
    public string PreviewImageUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string CivitaiUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> SamplePrompts { get; set; } = new();
}
