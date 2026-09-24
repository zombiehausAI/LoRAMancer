namespace LoRAMancer.App.Models;

public enum TrainingStatus {
    Idle,
    Initializing,
    Training,
    Paused,
    Completed,
    Failed,
    Cancelled
}
