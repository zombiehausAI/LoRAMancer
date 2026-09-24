namespace LoRAMancer.App.Models;

public sealed class UpdateCheckResult {
    public bool IsUpdateAvailable { get; init; }
    public string CurrentVersion { get; init; } = "1.0.0";
    public string LatestVersion { get; init; } = "1.0.0";
    public DateTime? ReleaseDate { get; init; }
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256Checksum { get; init; } = string.Empty;
    public string Changelog { get; init; } = string.Empty;
    public bool IsMandatory { get; init; }
}
