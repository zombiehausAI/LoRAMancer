namespace LoRAMancer.App.Models;

public sealed class ServerHealthDto {
    public string Status { get; set; } = "Ready";
    public string MachineName { get; set; } = Environment.MachineName;
    public string OsDescription { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public string GpuInfo { get; set; } = string.Empty;
    public string PyTorchVersion { get; set; } = string.Empty;
    public bool IsTraining { get; set; }
    public string ActiveRunName { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public float CurrentLoss { get; set; }
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;
}

public sealed class StartRemoteTrainingRequest {
    public string RunName { get; set; } = string.Empty;
    public string BaseArchitecture { get; set; } = "flux1";
    public string ConfigYaml { get; set; } = string.Empty;
    public string DatasetZipFileName { get; set; } = string.Empty;
    public string TriggerWord { get; set; } = string.Empty;
    public int Steps { get; set; } = 1500;
}

public sealed class TrainingTelemetryDto {
    public string EventType { get; set; } = "log"; // log, step, completed, failed
    public int Step { get; set; }
    public int TotalSteps { get; set; }
    public float Loss { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public sealed class DatasetUploadResultDto {
    public bool Success { get; set; }
    public string ExtractedPath { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public int CaptionCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
