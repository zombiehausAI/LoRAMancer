namespace LoRAMancer.App.Models;

public sealed class AppSettings {
    // AMD ROCm Wheels Configuration (Default: Official AMD Radeon Windows Wheels)
    public string RocmBaseUrl { get; set; } = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1";
    public string PyTorchVersion { get; set; } = "2.9.1+rocm7.2.1";
    public string TorchWheelUrl { get; set; } = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl";
    public string TorchAudioWheelUrl { get; set; } = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl";
    public string TorchVisionWheelUrl { get; set; } = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl";

    public List<string> RocmSdkWheels { get; set; } = new() {
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl"
    };

    // NVIDIA CUDA Configuration (Default: Official PyTorch CUDA 12.4 index)
    public string NvidiaIndexUrl { get; set; } = "https://download.pytorch.org/whl/cu124";
    public string NvidiaPackageSpec { get; set; } = "torch torchvision torchaudio";
    public bool NvidiaUseCustomWheels { get; set; } = false;
    public string NvidiaCustomWheelUrls { get; set; } = string.Empty;

    // Intel Arc / XPU Configuration (Default: Official PyTorch XPU index)
    public string IntelIndexUrl { get; set; } = "https://download.pytorch.org/whl/xpu";
    public string IntelPackageSpec { get; set; } = "torch torchvision torchaudio";
    public bool IntelUseCustomWheels { get; set; } = false;
    public string IntelCustomWheelUrls { get; set; } = string.Empty;

    // CPU Fallback Configuration
    public string CpuIndexUrl { get; set; } = "https://download.pytorch.org/whl/cpu";
    public string CpuPackageSpec { get; set; } = "torch torchvision torchaudio";

    public string PreferredPythonPath { get; set; } = "python.exe";
    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public string UpdateManifestUrl { get; set; } = "https://raw.githubusercontent.com/loramancer/loramancer/main/installer/version.json";
    public bool AutoCheckUpdatesOnStartup { get; set; } = true;
    public bool EnableDarkTheme { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // AI-Toolkit & Training Environment
    public string AiToolkitRepoUrl { get; set; } = "https://github.com/ostris/ai-toolkit.git";
    public string AiToolkitPath { get; set; } = string.Empty;
    public string HfHomeCachePath { get; set; } = string.Empty;

    // User Profile & Identifiable Information
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;

    // API Keys & Integrations
    public string HuggingFaceToken { get; set; } = string.Empty;
    public string CivitaiApiKey { get; set; } = string.Empty;
    public string OllamaEndpointUrl { get; set; } = "http://localhost:11434";
    public string OllamaApiKey { get; set; } = string.Empty;
    public string OllamaDefaultModel { get; set; } = "llama3.2-vision";

    // Network Server / Remote Engine
    public bool ServerEnabled { get; set; }
    public int ServerPort { get; set; } = 8420;
    public string ServerBindAddress { get; set; } = "0.0.0.0";
    public string ServerAccessToken { get; set; } = string.Empty;
    public bool RequireAuthForWebAccess { get; set; } = true;

    // Public Internet Sharing & Tunneling
    public bool EnablePublicInternetTunnel { get; set; }
    public string PublicCustomDomainUrl { get; set; } = string.Empty;
    public string PublicTunnelType { get; set; } = "cloudflare"; // cloudflare, custom

    // Remote Client Node Connection
    public bool ClientRemoteMode { get; set; }
    public string ClientRemoteHostUrl { get; set; } = "http://localhost:8420";
    public string ClientRemoteAccessToken { get; set; } = string.Empty;

    public void UpdateFromBaseUrl(string newBaseUrl, string newTorchVersion, string pythonTag = "cp312-cp312") {
        if (string.IsNullOrWhiteSpace(newBaseUrl)) {
            return;
        }

        string trimmedBase = newBaseUrl.TrimEnd('/');
        RocmBaseUrl = trimmedBase;
        PyTorchVersion = newTorchVersion;

        TorchWheelUrl = $"{trimmedBase}/torch-{newTorchVersion}-{pythonTag}-win_amd64.whl";
        TorchAudioWheelUrl = $"{trimmedBase}/torchaudio-{newTorchVersion}-{pythonTag}-win_amd64.whl";
        TorchVisionWheelUrl = $"{trimmedBase}/torchvision-0.24.1+{newTorchVersion.Split('+').Last()}-{pythonTag}-win_amd64.whl";
    }

    public void ResetAmdToDefault() {
        RocmBaseUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1";
        PyTorchVersion = "2.9.1+rocm7.2.1";
        TorchWheelUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl";
        TorchAudioWheelUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl";
        TorchVisionWheelUrl = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl";
        RocmSdkWheels = new List<string> {
            "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz",
            "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
            "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
            "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl"
        };
    }

    public void ResetNvidiaToDefault() {
        NvidiaIndexUrl = "https://download.pytorch.org/whl/cu124";
        NvidiaPackageSpec = "torch torchvision torchaudio";
        NvidiaUseCustomWheels = false;
        NvidiaCustomWheelUrls = string.Empty;
    }

    public void ResetIntelToDefault() {
        IntelIndexUrl = "https://download.pytorch.org/whl/xpu";
        IntelPackageSpec = "torch torchvision torchaudio";
        IntelUseCustomWheels = false;
        IntelCustomWheelUrls = string.Empty;
    }

    public void ResetCpuToDefault() {
        CpuIndexUrl = "https://download.pytorch.org/whl/cpu";
        CpuPackageSpec = "torch torchvision torchaudio";
    }
}
