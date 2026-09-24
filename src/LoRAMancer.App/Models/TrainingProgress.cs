namespace LoRAMancer.App.Models;

public sealed class TrainingProgress {
    public TrainingStatus Status { get; set; } = TrainingStatus.Idle;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public int CurrentEpoch { get; set; }
    public int TotalEpochs { get; set; }
    public double CurrentLoss { get; set; }
    public double CurrentLearningRate { get; set; }
    public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;
    public TimeSpan EstimatedRemaining { get; set; } = TimeSpan.Zero;
    public double ProgressPercentage => TotalSteps > 0 ? Math.Clamp((double)CurrentStep / TotalSteps * 100.0, 0.0, 100.0) : 0.0;
}
