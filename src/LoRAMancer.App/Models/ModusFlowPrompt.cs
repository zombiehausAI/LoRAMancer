using System.Text.Json.Serialization;

namespace LoRAMancer.App.Models;

/// <summary>
/// Represents a saved prompt pair fully compatible with ComfyUI-ModusFlow JSON format.
/// </summary>
public sealed class ModusFlowPrompt {
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("positive")]
    public string Positive { get; set; } = string.Empty;

    [JsonPropertyName("negative")]
    public string Negative { get; set; } = string.Empty;

    [JsonIgnore]
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime? LastModified { get; set; }

    public override string ToString() {
        return !string.IsNullOrWhiteSpace(Category)
            ? $"[{Category}] {Name}"
            : Name;
    }
}
