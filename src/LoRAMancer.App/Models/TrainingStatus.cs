namespace LoRAMancer.App.Models;

public enum TrainingStatus {
    Idle,
    Queued,
    Initializing,
    Training,
    Paused,
    Completed,
    Failed,
    Cancelled
}
