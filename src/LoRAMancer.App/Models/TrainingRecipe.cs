namespace LoRAMancer.App.Models;

/// <summary>
/// Represents a user-customizable or built-in LoRA training hyperparameter recipe/preset.
/// Stored as individual JSON files in ~/.loramancer/training_recipes/.
/// </summary>
public sealed class TrainingRecipe {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My LoRA Recipe";
    public string Description { get; set; } = string.Empty;
    public string TargetBaseModel { get; set; } = "FLUX.1 Dev";
    public string? CustomBaseModelPath { get; set; }
    public string SubjectType { get; set; } = "Character";

    public int NetworkDim { get; set; } = 16;
    public double NetworkAlpha { get; set; } = 16.0;
    public double LearningRate { get; set; } = 0.0001;
    public double? UnetLearningRate { get; set; }
    public double? TextEncoderLearningRate { get; set; }
    public string Optimizer { get; set; } = "adamw";
    public string LrScheduler { get; set; } = "cosine_with_restarts";
    public string Precision { get; set; } = "bf16";

    public int Epochs { get; set; } = 10;
    public int Repeats { get; set; } = 10;
    public int BatchSize { get; set; } = 1;
    public int? TotalSteps { get; set; }
    public int SaveEveryNEpochs { get; set; } = 1;

    public bool FlipAug { get; set; } = false;
    public bool ShuffleTokens { get; set; } = true;
    public int KeepTokens { get; set; } = 1;
    public int? ClipSkip { get; set; }

    public string SamplePrompt1 { get; set; } = string.Empty;
    public string SamplePrompt2 { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;

    public bool IsFavorite { get; set; } = true;
    public bool IsBuiltIn { get; set; } = false;
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
