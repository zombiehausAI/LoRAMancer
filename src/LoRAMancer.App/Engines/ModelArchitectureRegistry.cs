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

    public ModelArchitectureInfo RegisterCustom(string name, string family = "Custom", int dim = 16, double alpha = 16.0, double lr = 0.0001, int resolution = 1024) {
        string cleanName = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanName)) {
            return GetOrDefault("flux_1_dev");
        }

        string id = cleanName.ToLowerInvariant().Replace(' ', '_').Replace('.', '_').Replace('-', '_');
        var customArch = new ModelArchitectureInfo {
            Id = id,
            DisplayName = cleanName,
            Family = family,
            PretrainedModelPath = cleanName,
            DefaultDim = dim,
            DefaultAlpha = alpha,
            DefaultLearningRate = lr,
            DefaultResolution = resolution,
            NoiseScheduler = "flowmatch",
            IsFlux = cleanName.Contains("flux", StringComparison.OrdinalIgnoreCase),
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "masterpiece, high quality, sharp focus",
            RecommendedSamplePrompt2 = "portrait of a subject, detailed background",
            RecommendedNegativePrompt = "blurry, low quality, distorted",
            DetectionKeywords = new[] { cleanName.ToLowerInvariant(), id }
        };
        Register(customArch);
        return customArch;
    }

    public ModelArchitectureInfo InferFromPathOrMetadata(string? filePath, IReadOnlyDictionary<string, string> metadata) {
        ArgumentNullException.ThrowIfNull(metadata);

        // 1. High-priority explicit metadata check (modelspec.architecture, base_model, etc.)
        foreach (KeyValuePair<string, string> kvp in metadata) {
            string val = kvp.Value.ToLowerInvariant();
            foreach (ModelArchitectureInfo arch in _architectures.Values) {
                foreach (string keyword in arch.DetectionKeywords) {
                    if (val.Contains(keyword, StringComparison.OrdinalIgnoreCase)) {
                        return arch;
                    }
                }
            }
        }

        // 2. Folder path / subfolder heuristic detection (e.g. folder named "Pony", "Illustrious", "SD3.5", "SD1.5")
        if (!string.IsNullOrWhiteSpace(filePath)) {
            string normalizedPath = filePath.Replace('\\', '/');
            string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Check directory names from immediate parent upwards
            for (int i = segments.Length - 1; i >= 0; i--) {
                string seg = segments[i].ToLowerInvariant();
                if (seg.Contains("illustrious") || seg.Contains("noobai")) return GetOrDefault("illustrious_xl");
                if (seg.Contains("pony")) return GetOrDefault("pony_xl_v6");
                if (seg.Contains("sd3.5") || seg.Contains("sd35") || seg.Contains("sd 3.5")) return GetOrDefault("sd_3_5");
                if (seg.Contains("sd1.5") || seg.Contains("sd15") || seg.Contains("sd 1.5")) return GetOrDefault("sd_1_5");
                if (seg.Contains("sd2.1") || seg.Contains("sd21") || seg.Contains("sd 2.1")) return GetOrDefault("sd_2_1");
                if (seg.Contains("chroma")) return GetOrDefault("chroma_hd_1");
                if (seg.Contains("wan2.1") || seg.Contains("wan21") || seg.Contains("wan 2.1") || seg.Equals("wan")) return GetOrDefault("wan_2_1");
                if (seg.Contains("hunyuan")) return GetOrDefault("hunyuan_video");
                if (seg.Contains("flux")) return GetOrDefault("flux_1_dev");
                if (seg.Contains("sdxl")) return GetOrDefault("sdxl_1_0");
            }
        }

        return GetOrDefault("flux_1_dev");
    }

    public ModelArchitectureInfo InferFromMetadata(IReadOnlyDictionary<string, string> metadata) {
        return InferFromPathOrMetadata(null, metadata);
    }

    public int ScanAiToolkitInstallation(string? toolkitDir = null) {
        int discovered = 0;
        try {
            string root = !string.IsNullOrWhiteSpace(toolkitDir)
                ? toolkitDir
                : Path.Combine(AppContext.BaseDirectory, "tools", "ai-toolkit");

            if (!Directory.Exists(root)) {
                return 0;
            }

            // 1. Scan config/examples/*.yaml for training presets
            string examplesDir = Path.Combine(root, "config", "examples");
            if (Directory.Exists(examplesDir)) {
                string[] yamlFiles = Directory.GetFiles(examplesDir, "*.yaml");
                foreach (string yamlFile in yamlFiles) {
                    string filename = Path.GetFileNameWithoutExtension(yamlFile).ToLowerInvariant();
                    if (filename.StartsWith("train_lora_")) {
                        string archHint = filename.Replace("train_lora_", "").Split('_')[0];
                        if (!string.IsNullOrWhiteSpace(archHint) && !_architectures.ContainsKey(archHint)) {
                            string dispName = char.ToUpperInvariant(archHint[0]) + archHint[1..];
                            Register(new ModelArchitectureInfo {
                                Id = archHint,
                                DisplayName = dispName,
                                Family = "AI-Toolkit",
                                PretrainedModelPath = archHint,
                                DefaultDim = 16,
                                DefaultAlpha = 16.0,
                                DefaultLearningRate = 0.0001,
                                DefaultResolution = 1024,
                                NoiseScheduler = "flowmatch",
                                IsFlux = archHint.Contains("flux"),
                                DefaultTriggerWord = string.Empty,
                                RecommendedSamplePrompt = "masterpiece, high quality",
                                RecommendedNegativePrompt = "blurry, low quality",
                                DetectionKeywords = new[] { archHint }
                            });
                            discovered++;
                        }
                    }
                }
            }

            // 2. Scan extensions_built_in/diffusion_models
            string extDir = Path.Combine(root, "extensions_built_in", "diffusion_models");
            if (Directory.Exists(extDir)) {
                string[] modelDirs = Directory.GetDirectories(extDir);
                foreach (string mDir in modelDirs) {
                    string dirName = Path.GetFileName(mDir).ToLowerInvariant();
                    if (!_architectures.ContainsKey(dirName) && !dirName.StartsWith('.')) {
                        string dispName = char.ToUpperInvariant(dirName[0]) + dirName[1..];
                        Register(new ModelArchitectureInfo {
                            Id = dirName,
                            DisplayName = dispName,
                            Family = "Extension",
                            PretrainedModelPath = dirName,
                            DefaultDim = 16,
                            DefaultAlpha = 16.0,
                            DefaultLearningRate = 0.0001,
                            DefaultResolution = 1024,
                            NoiseScheduler = "flowmatch",
                            IsFlux = dirName.Contains("flux"),
                            DefaultTriggerWord = string.Empty,
                            RecommendedSamplePrompt = "masterpiece, high quality",
                            RecommendedNegativePrompt = "blurry, low quality",
                            DetectionKeywords = new[] { dirName }
                        });
                        discovered++;
                    }
                }
            }
        } catch {
            // Non-critical auto-discovery
        }
        return discovered;
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
            RecommendedSamplePrompt2 = "close up portrait of a subject, natural lighting, sharp focus",
            RecommendedNegativePrompt = string.Empty,
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
            RecommendedSamplePrompt2 = "portrait of a subject, sharp focus, 8k",
            RecommendedNegativePrompt = string.Empty,
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
            RecommendedSamplePrompt2 = "score_9, score_8_up, score_7_up, 1girl, portrait, dynamic lighting, masterpiece",
            RecommendedNegativePrompt = "score_4, score_5, score_6, source_furry, source_pony, rating_safe, deformed, blurry, bad anatomy",
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
            RecommendedSamplePrompt2 = "masterpiece, 1girl, solo, portrait, sharp eyes, detailed background",
            RecommendedNegativePrompt = "worst quality, low quality, bad anatomy, bad hands, blurry, distorted",
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
            RecommendedSamplePrompt2 = "cinematic portrait of a person, dramatic studio lighting, 8k",
            RecommendedNegativePrompt = "blurry, low quality, distorted, deformed, bad anatomy, worst quality",
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
            RecommendedSamplePrompt2 = "full shot of a subject, masterpiece, sharp focus",
            RecommendedNegativePrompt = "blurry, low quality, distorted, deformed, bad anatomy, worst quality",
            DetectionKeywords = new[] { "v1-5", "sd15", "sd1.5", "stable-diffusion-v1-5" }
        });

        Register(new ModelArchitectureInfo {
            Id = "chroma_hd_1",
            DisplayName = "ChromaHD-1",
            Family = "Chroma",
            PretrainedModelPath = "lodestones/Chroma1-HD",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 1024,
            NoiseScheduler = "flowmatch",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "photo of a subject, highly detailed, sharp focus",
            RecommendedSamplePrompt2 = "close-up portrait of a subject, natural lighting, professional photography",
            RecommendedNegativePrompt = string.Empty,
            DetectionKeywords = new[] { "chroma", "chromahd", "chroma1", "chroma1-hd", "chromahd-1" }
        });

        Register(new ModelArchitectureInfo {
            Id = "sd_3_5",
            DisplayName = "Stable Diffusion 3.5",
            Family = "SD3.5",
            PretrainedModelPath = "stabilityai/stable-diffusion-3.5-large",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 1024,
            NoiseScheduler = "flowmatch",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "high quality studio photo of a subject, sharp focus, 8k",
            RecommendedSamplePrompt2 = "cinematic portrait, intricate details, photorealistic",
            RecommendedNegativePrompt = "blurry, low quality, distorted, bad anatomy",
            DetectionKeywords = new[] { "sd3.5", "sd35", "sd 3.5", "stable-diffusion-3.5", "sd3" }
        });

        Register(new ModelArchitectureInfo {
            Id = "sd_2_1",
            DisplayName = "Stable Diffusion 2.1",
            Family = "SD21",
            PretrainedModelPath = "stabilityai/stable-diffusion-2-1",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 768,
            NoiseScheduler = "ddim",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "photograph of a subject, highly detailed, 4k",
            RecommendedSamplePrompt2 = "masterpiece portrait of a subject, studio lighting",
            RecommendedNegativePrompt = "blurry, low quality, distorted, deformed",
            DetectionKeywords = new[] { "sd2.1", "sd21", "sd 2.1", "stable-diffusion-2-1", "v2-1" }
        });

        Register(new ModelArchitectureInfo {
            Id = "wan_2_1",
            DisplayName = "Wan 2.1",
            Family = "Wan",
            PretrainedModelPath = "Wan-AI/Wan2.1-T2V-14B",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 720,
            NoiseScheduler = "flowmatch",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "cinematic video clip of a subject moving smoothly, 4k resolution",
            RecommendedSamplePrompt2 = "dynamic motion shot of a subject, photorealistic lighting",
            RecommendedNegativePrompt = "jitter, blurry, distorted, low quality",
            DetectionKeywords = new[] { "wan2.1", "wan_2_1", "wan21", "wan 2.1", "wan" }
        });

        Register(new ModelArchitectureInfo {
            Id = "hunyuan_video",
            DisplayName = "HunyuanVideo",
            Family = "Hunyuan",
            PretrainedModelPath = "tencent/HunyuanVideo",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 720,
            NoiseScheduler = "flowmatch",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "high definition video of a subject in motion, photorealistic",
            RecommendedSamplePrompt2 = "cinematic video scene, smooth movement, clear focus",
            RecommendedNegativePrompt = "blurry, distorted, artifacts, low resolution",
            DetectionKeywords = new[] { "hunyuan", "hunyuanvideo", "hunyuan_video" }
        });

        Register(new ModelArchitectureInfo {
            Id = "auraflow",
            DisplayName = "AuraFlow",
            Family = "AuraFlow",
            PretrainedModelPath = "fal/AuraFlow-v0.3",
            DefaultDim = 16,
            DefaultAlpha = 16.0,
            DefaultLearningRate = 0.0001,
            DefaultResolution = 1024,
            NoiseScheduler = "flowmatch",
            IsFlux = false,
            DefaultTriggerWord = string.Empty,
            RecommendedSamplePrompt = "masterpiece photo of a subject, sharp focus, vibrant colors",
            RecommendedSamplePrompt2 = "detailed portrait, professional lighting",
            RecommendedNegativePrompt = "blurry, low quality, distorted",
            DetectionKeywords = new[] { "auraflow", "aura_flow" }
        });
    }
}
