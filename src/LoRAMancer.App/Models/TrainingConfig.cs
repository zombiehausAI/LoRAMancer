namespace LoRAMancer.App.Models;

public sealed class TrainingConfig {
    public string RunName { get; set; } = "lora_run_01";
    public string DonorLoraPath { get; set; } = string.Empty;
    public string AuxiliaryLoraPath { get; set; } = string.Empty;
    public double AuxiliaryLoraWeight { get; set; } = 1.0;
    public string DatasetDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string TargetBaseModel { get; set; } = "FLUX.1-dev";
    public int NetworkDim { get; set; } = 16;
    public double NetworkAlpha { get; set; } = 16.0;
    public double LearningRate { get; set; } = 0.0001;
    public double? UnetLearningRate { get; set; }
    public double? TextEncoderLearningRate { get; set; }
    public string Optimizer { get; set; } = "adamw";
    public string LrScheduler { get; set; } = "cosine_with_restarts";
    public string AttentionMechanism { get; set; } = "sdpa";
    public string Precision { get; set; } = "bf16";
    public bool Quantize { get; set; } = false;
    public bool CacheLatentsToDisk { get; set; } = true;
    public string TriggerWord { get; set; } = string.Empty;
    public List<string> SamplePrompts { get; set; } = new();
    public string NegativePrompt { get; set; } = string.Empty;
    public int MaxTrainEpochs { get; set; } = 10;
    public int? TotalSteps { get; set; }
    public int Repeats { get; set; } = 1;
    public int BatchSize { get; set; } = 1;
    public int GradientAccumulationSteps { get; set; } = 1;
    public int SaveEveryNEpochs { get; set; } = 1;
    public bool FlipAug { get; set; } = false;
    public bool ShuffleTokens { get; set; } = false;
    public int KeepTokens { get; set; } = 1;
    public int? ClipSkip { get; set; }
}
