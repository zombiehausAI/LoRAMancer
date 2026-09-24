using LoRAMancer.App.Models;

namespace LoRAMancer.App.Engines;

public sealed class ModelArchitectureRegistry {
    private readonly Dictionary<string, ModelArchitectureInfo> _architectures = new(StringComparer.OrdinalIgnoreCase);

    public ModelArchitectureRegistry() {
        RegisterDefaultArchitectures();
    }

    public IReadOnlyCollection<ModelArchitectureInfo> GetAll() {
        return _architectures.Values;
    }

    public bool TryGet(string id, out ModelArchitectureInfo? info) {
        return _architectures.TryGetValue(id, out info);
    }

    public ModelArchitectureInfo GetOrDefault(string id, string defaultId = "flux_1_dev") {
        if (_architectures.TryGetValue(id, out ModelArchitectureInfo? info)) {
            return info;
        }
        if (_architectures.TryGetValue(defaultId, out ModelArchitectureInfo? defaultInfo)) {
            return defaultInfo;
        }
        return _architectures.Values.First();
    }

    public void Register(ModelArchitectureInfo architecture) {
        ArgumentNullException.ThrowIfNull(architecture);
        _architectures[architecture.Id] = architecture;
    }

    public ModelArchitectureInfo InferFromMetadata(IReadOnlyDictionary<string, string> metadata) {
        ArgumentNullException.ThrowIfNull(metadata);

        foreach (KeyValuePair<string, string> kvp in metadata) {
            string val = kvp.Value.ToLowerInvariant();

            // Match against registered architectures based on keywords
            foreach (ModelArchitectureInfo arch in _architectures.Values) {
                foreach (string keyword in arch.DetectionKeywords) {
                    if (val.Contains(keyword, StringComparison.OrdinalIgnoreCase)) {
                        return arch;
                    }
                }
            }
        }

        return GetOrDefault("flux_1_dev");
    }

    private void RegisterDefaultArchitectures() {
        Register(new ModelArchitectureInfo {
            Id = "flux_1_dev",
            DisplayName = "FLUX.1-dev",
            Family = "Flux",
            PretrainedModelPath = "black-forest-labs/FLUX.1-dev",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 1024,
            NoiseScheduler = "flowmatch",
            IsFlux = true,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "photo of a subject, highly detailed, sharp focus",
            DetectionKeywords = new[] { "flux.1-dev", "flux-dev", "flux.1", "flux" }
        });

        Register(new ModelArchitectureInfo {
            Id = "flux_1_schnell",
            DisplayName = "FLUX.1-schnell",
            Family = "Flux",
            PretrainedModelPath = "black-forest-labs/FLUX.1-schnell",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 1024,
            NoiseScheduler = "flowmatch",
            IsFlux = true,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "photo of a subject, highly detailed",
            DetectionKeywords = new[] { "flux.1-schnell", "flux-schnell", "schnell" }
        });

        Register(new ModelArchitectureInfo {
            Id = "pony_xl_v6",
            DisplayName = "Pony Diffusion V6 XL",
            Family = "SDXL",
            PretrainedModelPath = "AstraliteHeart/pony-diffusion-v6-xl",
            DefaultDim = 32,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.00005,
            DefaultResolution = 1024,
            NoiseScheduler = "euler",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "score_9, score_8_up, score_7_up, source_anime, 1girl, solo, masterpiece",
            DetectionKeywords = new[] { "pony", "ponyxl", "ponyv6", "pony_v6", "v6xl" }
        });

        Register(new ModelArchitectureInfo {
            Id = "illustrious_xl",
            DisplayName = "Illustrious-XL",
            Family = "SDXL",
            PretrainedModelPath = "OnomaAIResearch/Illustrious-xl-early-release-v1.0",
            DefaultDim = 32,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.00005,
            DefaultResolution = 1024,
            NoiseScheduler = "euler",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "masterpiece, newest, anime, 1girl, high quality",
            DetectionKeywords = new[] { "illustrious", "illustrious-xl", "illustrious_xl", "noobai" }
        });

        Register(new ModelArchitectureInfo {
            Id = "sdxl_1_0",
            DisplayName = "Stable Diffusion XL 1.0",
            Family = "SDXL",
            PretrainedModelPath = "stabilityai/stable-diffusion-xl-base-1.0",
            DefaultDim = 32,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.00005,
            DefaultResolution = 1024,
            NoiseScheduler = "euler",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "photograph of a majestic mountain landscape, 8k resolution",
            DetectionKeywords = new[] { "sdxl", "stable-diffusion-xl" }
        });

        Register(new ModelArchitectureInfo {
            Id = "sd_1_5",
            DisplayName = "Stable Diffusion 1.5",
            Family = "SD15",
            PretrainedModelPath = "runwayml/stable-diffusion-v1-5",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 512,
            NoiseScheduler = "ddim",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "portrait of a subject, highly detailed, 4k",
            DetectionKeywords = new[] { "v1-5", "sd15", "sd1.5", "stable-diffusion-v1-5" }
        });
    }
}
