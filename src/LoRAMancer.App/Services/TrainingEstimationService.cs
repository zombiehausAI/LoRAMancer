using System.Management;
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
        if (family.Contains("SD15", StringComparison.OrdinalIgnoreCase) || family.Contains("SD 1.5", StringComparison.OrdinalIgnoreCase)) {
            return dim * 2.25;
        }
        return dim * 3.4;
    }

    private static TimeSpan EstimateDuration(string family, int totalSteps) {
        double secondsPerStep = 0.48;
        if (family.Contains("Flux", StringComparison.OrdinalIgnoreCase)) {
            secondsPerStep = 1.45;
        } else if (family.Contains("SD15", StringComparison.OrdinalIgnoreCase) || family.Contains("SD 1.5", StringComparison.OrdinalIgnoreCase)) {
            secondsPerStep = 0.22;
        }
        return TimeSpan.FromSeconds(totalSteps * secondsPerStep);
    }

    private static (string GpuName, double VramGb) DetectAmdGpu() {
        string? nonAmdFallback = null;
        double nonAmdVram = 16.0;

        try {
            using ManagementObjectSearcher searcher = new("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get()) {
                string name = mo["Name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) {
                    continue;
                }

                double vramGb = 16.0;
                if (mo["AdapterRAM"] != null && double.TryParse(mo["AdapterRAM"].ToString(), out double ramBytes) && ramBytes > 0) {
                    vramGb = ramBytes / (1024.0 * 1024.0 * 1024.0);
                }
                if (vramGb < 4.0) {
                    vramGb = 16.0; // WMI 32-bit integer overflow fallback for modern high-VRAM GPUs
                }

                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) {
                    return (name, vramGb);
                }

                if (nonAmdFallback == null && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) {
                    nonAmdFallback = name;
                    nonAmdVram = vramGb;
                }
            }
        } catch {
            // Fallback for non-WMI environments
        }

        if (nonAmdFallback != null) {
            return ($"{nonAmdFallback} (Test Mode)", nonAmdVram);
        }

        return ("AMD Radeon Graphics (Simulated)", 16.0);
    }
}
