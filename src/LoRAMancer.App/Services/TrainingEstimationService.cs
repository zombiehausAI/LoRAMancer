using System.Management;
using Microsoft.Win32;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class TrainingEstimationService {
    private readonly ModelArchitectureRegistry _architectureRegistry;

    public TrainingEstimationService(ModelArchitectureRegistry architectureRegistry) {
        _architectureRegistry = architectureRegistry ?? throw new ArgumentNullException(nameof(architectureRegistry));
    }

    public IReadOnlyList<SubjectPreset> GetSubjectPresets() => new List<SubjectPreset> {
        new() {
            SubjectType = TrainingSubjectType.Character,
            DisplayName = "Character / Person",
            Description = "Ideal for specific people, anime/game characters, and facial/costume likeness.",
            DefaultDim = 16,
            DefaultAlpha = 16,
            DefaultLearningRate = 1e-4,
            DefaultEpochs = 10,
            DefaultRepeats = 10,
            RecommendedPromptTemplate = "{trigger}, a portrait of a person, detailed face, studio lighting"
        },
        new() {
            SubjectType = TrainingSubjectType.Style,
            DisplayName = "Art Style / Aesthetic",
            Description = "Captures an artistic medium, painting style, line art, or visual texture.",
            DefaultDim = 32,
            DefaultAlpha = 32,
            DefaultLearningRate = 5e-5,
            DefaultEpochs = 12,
            DefaultRepeats = 8,
            RecommendedPromptTemplate = "{trigger} artstyle, illustration of a vibrant cityscape"
        },
        new() {
            SubjectType = TrainingSubjectType.Concept,
            DisplayName = "Concept / Object",
            Description = "For objects, mechanical gear, vehicles, fantasy creatures, or specific poses.",
            DefaultDim = 16,
            DefaultAlpha = 16,
            DefaultLearningRate = 1e-4,
            DefaultEpochs = 10,
            DefaultRepeats = 12,
            RecommendedPromptTemplate = "{trigger}, a high-tech vehicle on an open road, sharp focus"
        },
        new() {
            SubjectType = TrainingSubjectType.Clothing,
            DisplayName = "Clothing / Outfit",
            Description = "Focuses on garment geometry and fabrics across diverse poses and figures.",
            DefaultDim = 16,
            DefaultAlpha = 16,
            DefaultLearningRate = 8e-5,
            DefaultEpochs = 10,
            DefaultRepeats = 10,
            RecommendedPromptTemplate = "{trigger} outfit, full body shot of person wearing uniform"
        },
        new() {
            SubjectType = TrainingSubjectType.Custom,
            DisplayName = "Custom / Advanced",
            Description = "Full manual control over all hyperparameters, schedules, and dimensions.",
            DefaultDim = 32,
            DefaultAlpha = 32,
            DefaultLearningRate = 1e-4,
            DefaultEpochs = 10,
            DefaultRepeats = 10,
            RecommendedPromptTemplate = "{trigger}, master quality photo"
        }
    };

    public SubjectPreset GetPreset(TrainingSubjectType type) =>
        GetSubjectPresets().FirstOrDefault(x => x.SubjectType == type) ?? GetSubjectPresets().First();

    public TrainingEstimates CalculateEstimates(
        string baseModelDisplayName,
        int imageCount,
        int repeats,
        int epochs,
        int batchSize,
        int networkDim
    ) {
        int safeBatch = Math.Max(1, batchSize);
        int safeImages = Math.Max(1, imageCount);
        int safeRepeats = Math.Max(1, repeats);
        int safeEpochs = Math.Max(1, epochs);
        int totalSteps = (safeImages * safeRepeats * safeEpochs) / safeBatch;

        ModelArchitectureInfo arch = _architectureRegistry.GetAll()
            .FirstOrDefault(x => x.DisplayName.Equals(baseModelDisplayName, StringComparison.OrdinalIgnoreCase))
            ?? _architectureRegistry.GetOrDefault("flux_1_dev");

        double estVramGb = EstimateVram(arch.Family, safeBatch);
        double estOutputMb = EstimateOutputSizeMb(arch.Family, networkDim);
        TimeSpan estDuration = EstimateDuration(arch.Family, totalSteps);

        return new TrainingEstimates {
            TotalSteps = totalSteps,
            EstimatedVramGb = estVramGb,
            EstimatedOutputSizeMb = estOutputMb,
            EstimatedDuration = estDuration,
            StepBreakdown = $"{safeImages} images × {safeRepeats} repeats × {safeEpochs} epochs ÷ batch {safeBatch} = {totalSteps:N0} total steps"
        };
    }

    public PreFlightCheckResult RunPreFlightCheck(string baseModelDisplayName, string outputDirectory, int batchSize) {
        PreFlightCheckResult result = new();
        (string gpuName, double vramGb) = DetectAmdGpu();
        result.GpuName = gpuName;
        result.TotalVramGb = vramGb;

        ModelArchitectureInfo arch = _architectureRegistry.GetAll()
            .FirstOrDefault(x => x.DisplayName.Equals(baseModelDisplayName, StringComparison.OrdinalIgnoreCase))
            ?? _architectureRegistry.GetOrDefault("flux_1_dev");

        result.EstimatedVramGb = EstimateVram(arch.Family, Math.Max(1, batchSize));

        if (result.EstimatedVramGb > result.TotalVramGb) {
            result.Warnings.Add($"Estimated VRAM ({result.EstimatedVramGb:F1} GB) exceeds detected VRAM ({result.TotalVramGb:F1} GB). Enable disk latent caching or use batch size 1 to avoid Out-Of-Memory.");
        }

        try {
            string targetDir = string.IsNullOrWhiteSpace(outputDirectory) ? AppContext.BaseDirectory : outputDirectory;
            string root = Path.GetPathRoot(Path.GetFullPath(targetDir)) ?? "C:\\";
            DriveInfo drive = new(root);
            result.AvailableDiskGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

            if (result.AvailableDiskGb < 10.0) {
                result.Warnings.Add($"Output drive has only {result.AvailableDiskGb:F1} GB free. Training checkpoints and cached latents require at least 10 GB.");
            }
        } catch {
            result.AvailableDiskGb = 50.0;
        }

        if (result.Errors.Count > 0) {
            result.Passed = false;
        }

        return result;
    }

    private static double EstimateVram(string family, int batchSize) {
        if (family.Contains("Flux", StringComparison.OrdinalIgnoreCase)) {
            return 13.8 + ((batchSize - 1) * 2.2);
        }
        if (family.Contains("Chroma", StringComparison.OrdinalIgnoreCase)) {
            return 14.2 + ((batchSize - 1) * 2.2);
        }
        if (family.Contains("SD15", StringComparison.OrdinalIgnoreCase) || family.Contains("SD 1.5", StringComparison.OrdinalIgnoreCase)) {
            return 5.2 + ((batchSize - 1) * 0.8);
        }
        // SDXL, Pony, Illustrious
        return 9.2 + ((batchSize - 1) * 1.5);
    }

    private static double EstimateOutputSizeMb(string family, int rankDim) {
        int dim = Math.Max(1, rankDim);
        if (family.Contains("Flux", StringComparison.OrdinalIgnoreCase)) {
            return dim * 13.8;
        }
        if (family.Contains("Chroma", StringComparison.OrdinalIgnoreCase)) {
            return dim * 13.5;
        }
        if (family.Contains("SD15", StringComparison.OrdinalIgnoreCase) || family.Contains("SD 1.5", StringComparison.OrdinalIgnoreCase)) {
            return dim * 2.25;
        }
        return dim * 3.4;
    }

    private static TimeSpan EstimateDuration(string family, int totalSteps) {
        double secondsPerStep = 0.48;
        if (family.Contains("Flux", StringComparison.OrdinalIgnoreCase)) {
            secondsPerStep = 1.45;
        } else if (family.Contains("Chroma", StringComparison.OrdinalIgnoreCase)) {
            secondsPerStep = 1.35;
        } else if (family.Contains("SD15", StringComparison.OrdinalIgnoreCase) || family.Contains("SD 1.5", StringComparison.OrdinalIgnoreCase)) {
            secondsPerStep = 0.22;
        }
        return TimeSpan.FromSeconds(totalSteps * secondsPerStep);
    }

    public (string GpuName, double VramGb) GetDetectedGpu() => DetectAmdGpu();

    private static double GetKnownGpuVram(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return 16.0;
        }

        string n = name.ToUpperInvariant();
        if (n.Contains("7900 XTX") || n.Contains("7900XTX")) return 24.0;
        if (n.Contains("7900 XT") || n.Contains("7900XT")) return 20.0;
        if (n.Contains("7900 GRE") || n.Contains("7900GRE")) return 16.0;
        if (n.Contains("7800 XT") || n.Contains("7800XT")) return 16.0;
        if (n.Contains("7700 XT") || n.Contains("7700XT")) return 12.0;
        if (n.Contains("7600 XT") || n.Contains("7600XT")) return 16.0;
        if (n.Contains("7600")) return 8.0;

        if (n.Contains("6950") || n.Contains("6900") || n.Contains("6800")) return 16.0;
        if (n.Contains("6750") || n.Contains("6700 XT") || n.Contains("6700XT")) return 12.0;
        if (n.Contains("6700")) return 10.0;
        if (n.Contains("6650") || n.Contains("6600")) return 8.0;

        if (n.Contains("W7900")) return 48.0;
        if (n.Contains("W7800") || n.Contains("W6800")) return 32.0;
        if (n.Contains("MI300")) return 192.0;
        if (n.Contains("MI250")) return 128.0;
        if (n.Contains("MI210")) return 64.0;

        if (n.Contains("4090") || n.Contains("3090")) return 24.0;
        if (n.Contains("4080")) return 16.0;
        if (n.Contains("4070 TI SUPER")) return 16.0;
        if (n.Contains("4070")) return 12.0;
        if (n.Contains("3080 TI")) return 12.0;
        if (n.Contains("3080")) return 10.0;
        if (n.Contains("3060")) return 12.0;

        return 16.0;
    }

    private static (string GpuName, double VramGb) DetectAmdGpu() {
        string? amdName = null;
        double amdVram = 0.0;
        string? fallbackName = null;
        double fallbackVram = 0.0;

        // 1. Try Windows Display Adapter Registry for true 64-bit qwMemorySize
        if (OperatingSystem.IsWindows()) {
            try {
                using var videoClassKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (videoClassKey != null) {
                    foreach (string subKeyName in videoClassKey.GetSubKeyNames()) {
                        if (subKeyName.Length != 4) continue;
                        using var subKey = videoClassKey.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        string desc = subKey.GetValue("DriverDesc")?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(desc) || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }

                        double detectedGb = 0.0;
                        object? qwObj = subKey.GetValue("HardwareInformation.qwMemorySize");
                        if (qwObj is long qwLong && qwLong > 0) {
                            detectedGb = qwLong / (1024.0 * 1024.0 * 1024.0);
                        } else if (qwObj is ulong qwUlong && qwUlong > 0) {
                            detectedGb = qwUlong / (1024.0 * 1024.0 * 1024.0);
                        } else if (qwObj is byte[] qwBytes && qwBytes.Length >= 8) {
                            ulong raw = BitConverter.ToUInt64(qwBytes, 0);
                            if (raw > 0) detectedGb = raw / (1024.0 * 1024.0 * 1024.0);
                        }

                        if (detectedGb <= 0.0) {
                            detectedGb = GetKnownGpuVram(desc);
                        }

                        if (desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) {
                            if (amdName == null || detectedGb > amdVram) {
                                amdName = desc;
                                amdVram = detectedGb;
                            }
                        } else if (fallbackName == null || detectedGb > fallbackVram) {
                            fallbackName = desc;
                            fallbackVram = detectedGb;
                        }
                    }
                }
            } catch { }
        }

        if (amdName != null && amdVram > 0) {
            return (amdName, Math.Round(amdVram, 1));
        }

        // 2. Fallback to WMI + Model Heuristic Lookup
        try {
            using ManagementObjectSearcher searcher = new("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get()) {
                string name = mo["Name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                double vramGb = 0.0;
                if (mo["AdapterRAM"] != null && double.TryParse(mo["AdapterRAM"].ToString(), out double ramBytes) && ramBytes > 0) {
                    vramGb = ramBytes / (1024.0 * 1024.0 * 1024.0);
                }

                if (vramGb <= 4.0 || vramGb > 128.0) {
                    vramGb = GetKnownGpuVram(name);
                }

                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) {
                    return (name, Math.Round(vramGb, 1));
                }

                if (fallbackName == null) {
                    fallbackName = name;
                    fallbackVram = vramGb;
                }
            }
        } catch { }

        if (fallbackName != null && fallbackVram > 0) {
            return ($"{fallbackName} (Test Mode)", Math.Round(fallbackVram, 1));
        }

        return ("AMD Radeon RX 7900 XTX (Default)", 24.0);
    }
}
