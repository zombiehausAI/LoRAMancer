namespace LoRAMancer.App.Models;

public sealed class AmdEnvironmentInfo {
    public bool IsAmdGpuDetected { get; set; }
    public string GpuName { get; set; } = string.Empty;
    public bool RocmDriverFound { get; set; }
    public string RocmVersion { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = string.Empty;
    public string PythonVersion { get; set; } = string.Empty;
    public string VenvPath { get; set; } = string.Empty;
    public string PyTorchVersion { get; set; } = string.Empty;
    public bool HasRocmSupport { get; set; }
    public string ComfyUiPath { get; set; } = string.Empty;
    public string ComfyUiScriptPath { get; set; } = string.Empty;
    public bool IsReadyForTraining => IsAmdGpuDetected && !string.IsNullOrEmpty(VenvPath) && HasRocmSupport;
}
