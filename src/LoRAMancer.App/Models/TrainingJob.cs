namespace LoRAMancer.App.Models;

public sealed class TrainingJob {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ConfigYamlPath { get; set; } = string.Empty;
    public string VenvPath { get; set; } = string.Empty;
    public string? ScriptPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TrainingStatus Status { get; set; } = TrainingStatus.Queued;
    public string? ErrorMessage { get; set; }

    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; } = 1000;
    public double CurrentLoss { get; set; }
    public double CurrentLearningRate { get; set; }
    public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;
    public TimeSpan EstimatedRemaining { get; set; } = TimeSpan.Zero;
}
