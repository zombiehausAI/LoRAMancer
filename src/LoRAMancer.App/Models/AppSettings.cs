namespace LoRAMancer.App.Models;

public sealed class AppSettings {
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

    public string PreferredPythonPath { get; set; } = "python.exe";
    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public string UpdateManifestUrl { get; set; } = "https://raw.githubusercontent.com/loramancer/loramancer/main/installer/version.json";
    public bool AutoCheckUpdatesOnStartup { get; set; } = true;
    public bool EnableDarkTheme { get; set; } = true;

    // AI-Toolkit & Training Environment
    public string AiToolkitRepoUrl { get; set; } = "https://github.com/ostris/ai-toolkit.git";
    public string AiToolkitPath { get; set; } = string.Empty;
    public string HfHomeCachePath { get; set; } = string.Empty;

    // User Profile & Identifiable Information
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;

    // API Keys
    public string HuggingFaceToken { get; set; } = string.Empty;
    public string CivitaiApiKey { get; set; } = string.Empty;

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
}
