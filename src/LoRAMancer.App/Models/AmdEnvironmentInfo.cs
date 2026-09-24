namespace LoRAMancer.App.Models;

public enum HardwareVendor {
    Amd,
    Nvidia,
    Intel,
    Cpu
}

public sealed class AmdEnvironmentInfo {
    public HardwareVendor DetectedVendor { get; set; } = HardwareVendor.Amd;
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
    public bool IsReadyForTraining => !string.IsNullOrEmpty(VenvPath) && (HasRocmSupport || DetectedVendor != HardwareVendor.Amd);
}

public sealed class VenvPackageStatus {
    public bool IsVenvCreated { get; set; }
    public string PipVersion { get; set; } = "Not detected";
    public string TorchVersion { get; set; } = "Not installed";
}
