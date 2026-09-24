namespace LoRAMancer.App.Models;

public sealed class ModelArchitectureInfo {
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Family { get; init; }
    public required string PretrainedModelPath { get; init; }
    public int DefaultDim { get; init; } = 16;
    public double DefaultAlpha { get; init; } = 16.0;
    public double DefaultLearningRate { get; init; } = 0.0001;
    public int DefaultResolution { get; init; } = 1024;
    public string NoiseScheduler { get; init; } = "flowmatch";
    public bool IsFlux { get; init; }
    public string DefaultTriggerWord { get; init; } = string.Empty;
    public string RecommendedSamplePrompt { get; init; } = "masterpiece, 1girl, highly detailed";
    public IReadOnlyList<string> DetectionKeywords { get; init; } = Array.Empty<string>();
}
