using System.Text;
using LoRAMancer.App.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LoRAMancer.App.Engines;

public sealed class AiToolkitConfigBuilder {
    private readonly ModelArchitectureRegistry _registry;

    public AiToolkitConfigBuilder(ModelArchitectureRegistry? registry = null) {
        _registry = registry ?? new ModelArchitectureRegistry();
    }

    public ModelArchitectureInfo ResolveArchitecture(string targetBaseModel) {
        if (_registry.TryGet(targetBaseModel, out ModelArchitectureInfo? info) && info != null) {
            return info;
        }
        foreach (ModelArchitectureInfo arch in _registry.GetAll()) {
            if (string.Equals(arch.DisplayName, targetBaseModel, StringComparison.OrdinalIgnoreCase)) {
                return arch;
            }
        }
        return _registry.InferFromMetadata(new Dictionary<string, string> { ["base_model"] = targetBaseModel });
    }

    public TrainingConfig CloneFromDonor(LoraMetadata donor, string runName = "", string datasetDirectory = "", string outputDirectory = "") {
        ArgumentNullException.ThrowIfNull(donor);

        ModelArchitectureInfo arch = ResolveArchitecture(donor.BaseModel);
        string sanitizedOptimizer = SanitizeOptimizer(donor.Optimizer);

        return new TrainingConfig {
            RunName = runName ?? string.Empty,
            DonorLoraPath = donor.FilePath,
            DatasetDirectory = datasetDirectory,
            OutputDirectory = outputDirectory,
            TargetBaseModel = arch.DisplayName,
            NetworkDim = donor.NetworkDim ?? arch.DefaultDim,
            NetworkAlpha = donor.NetworkAlpha ?? arch.DefaultAlpha,
            LearningRate = donor.LearningRate ?? arch.DefaultLearningRate,
            UnetLearningRate = donor.UnetLearningRate,
            TextEncoderLearningRate = donor.TextEncoderLearningRate,
            Optimizer = sanitizedOptimizer,
            LrScheduler = string.IsNullOrWhiteSpace(donor.LrScheduler) ? "cosine" : donor.LrScheduler,
            AttentionMechanism = "sdpa",
            Precision = !string.IsNullOrWhiteSpace(donor.Precision) ? donor.Precision : "bf16",
            Quantize = false,
            CacheLatentsToDisk = true,
            TriggerWord = string.Empty,
            SamplePrompts = !string.IsNullOrWhiteSpace(arch.RecommendedSamplePrompt2)
                ? new List<string> { arch.RecommendedSamplePrompt, arch.RecommendedSamplePrompt2 }
                : new List<string> { arch.RecommendedSamplePrompt },
            NegativePrompt = arch.RecommendedNegativePrompt,
            MaxTrainEpochs = donor.Epochs ?? 10,
            TotalSteps = donor.TotalSteps,
            SaveEveryNEpochs = 1,
            Repeats = 1,
            FlipAug = false,
            ShuffleTokens = false,
            KeepTokens = 1,
            ClipSkip = string.Equals(arch.Family, "Pony", StringComparison.OrdinalIgnoreCase) || string.Equals(arch.DisplayName, "Pony Diffusion V6 XL", StringComparison.OrdinalIgnoreCase) ? 2 : null
        };
    }

    public string BuildAiToolkitYaml(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        TrainingConfig sanitized = SanitizeForAmd(config);
        ModelArchitectureInfo archInfo = ResolveArchitecture(sanitized.TargetBaseModel);

        int totalSteps = CalculateTotalSteps(sanitized);
        int saveEverySteps = CalculateSaveEverySteps(sanitized);
        int sampleEvery = Math.Max(20, Math.Min(200, totalSteps / 5));

                        var validPrompts = sanitized.SamplePrompts
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .ToList();
                        if (validPrompts.Count == 0) {
                            validPrompts.Add(string.IsNullOrEmpty(sanitized.TriggerWord) ? archInfo.RecommendedSamplePrompt : $"{sanitized.TriggerWord}, {archInfo.RecommendedSamplePrompt}");
                        }

                        var sampleDict = new Dictionary<string, object> {
                            ["sampler"] = "euler",
                            ["sample_every"] = 200,
                            ["width"] = archInfo.DefaultResolution,
                            ["height"] = archInfo.DefaultResolution,
                            ["neg"] = sanitized.NegativePrompt ?? string.Empty,
                            ["prompts"] = validPrompts
                        };

                        var trainDict = new Dictionary<string, object> {
                            ["batch_size"] = sanitized.BatchSize,
                            ["steps"] = totalSteps,
                            ["gradient_accumulation_steps"] = sanitized.GradientAccumulationSteps,
                            ["train_unet"] = true,
                            ["train_text_encoder"] = !archInfo.IsFlux && !string.Equals(archInfo.Family, "Chroma", StringComparison.OrdinalIgnoreCase) && (sanitized.TextEncoderLearningRate == null || sanitized.TextEncoderLearningRate > 0),
                            ["gradient_checkpointing"] = true,
                            ["noise_scheduler"] = archInfo.NoiseScheduler,
                            ["optimizer"] = sanitized.Optimizer,
                            ["lr_scheduler"] = string.IsNullOrWhiteSpace(sanitized.LrScheduler) ? "cosine" : sanitized.LrScheduler,
                            ["lr"] = sanitized.LearningRate,
                            ["attention_mechanism"] = sanitized.AttentionMechanism,
                            ["dtype"] = sanitized.Precision,
                            ["quantize"] = sanitized.Quantize
                        };
                        if (sanitized.UnetLearningRate.HasValue && sanitized.UnetLearningRate.Value > 0) {
                            trainDict["unet_lr"] = sanitized.UnetLearningRate.Value;
                        }
                        if (sanitized.TextEncoderLearningRate.HasValue) {
                            trainDict["text_encoder_lr"] = sanitized.TextEncoderLearningRate.Value;
                        }
                        if (sanitized.ClipSkip.HasValue && sanitized.ClipSkip.Value > 0) {
                            trainDict["clip_skip"] = sanitized.ClipSkip.Value;
                        }

                        var trainerProcess = new Dictionary<string, object> {
                            ["type"] = "sd_trainer",
                            ["training_folder"] = sanitized.OutputDirectory,
                            ["device"] = "cuda:0", // PyTorch ROCm maps AMD GPU as cuda:0
                            ["network"] = new Dictionary<string, object> {
                                ["type"] = "lora",
                                ["linear"] = sanitized.NetworkDim,
                                ["linear_alpha"] = sanitized.NetworkAlpha
                            },
                            ["save"] = new Dictionary<string, object> {
                                ["dtype"] = sanitized.Precision,
                                ["save_every"] = saveEverySteps,
                                ["max_step_saves_to_keep"] = Math.Max(4, (int)Math.Ceiling((double)totalSteps / Math.Max(1, saveEverySteps)))
                            },
                            ["datasets"] = new List<object> {
                                new Dictionary<string, object> {
                                    ["folder_path"] = sanitized.DatasetDirectory,
                                    ["caption_ext"] = "txt",
                                    ["caption_dropout_rate"] = 0.05,
                                    ["shuffle_tokens"] = sanitized.ShuffleTokens,
                                    ["keep_tokens"] = sanitized.KeepTokens,
                                    ["flip_aug"] = sanitized.FlipAug,
                                    ["cache_latents_to_disk"] = sanitized.CacheLatentsToDisk,
                                    ["resolution"] = new List<int> { archInfo.DefaultResolution }
                                }
                            },
                            ["train"] = trainDict,
                            ["model"] = new Dictionary<string, object> {
                                ["name_or_path"] = ResolveModelPath(sanitized.TargetBaseModel, archInfo),
                                ["is_flux"] = archInfo.IsFlux,
                                ["quantize"] = false,
                                ["arch"] = archInfo.Family.ToLowerInvariant()
                            },
                            ["sample"] = sampleDict
                        };

                        var root = new Dictionary<string, object> {
                            ["job"] = "extension",
                            ["config"] = new Dictionary<string, object> {
                                ["name"] = sanitized.RunName,
                                ["process"] = new List<object> { trainerProcess }
                            }
                        };

        ISerializer serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return serializer.Serialize(root);
    }

    public string BuildKohyaConfig(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        TrainingConfig sanitized = SanitizeForAmd(config);
        ModelArchitectureInfo archInfo = ResolveArchitecture(sanitized.TargetBaseModel);

        StringBuilder sb = new();
        sb.AppendLine("# LoRAMancer AMD Sanitized Kohya Configuration");
        sb.AppendLine($"# Architecture: {archInfo.DisplayName} ({archInfo.Family})");
        sb.AppendLine($"# Generated at {DateTime.UtcNow:O}");
        sb.AppendLine($"pretrained_model_name_or_path = \"{archInfo.PretrainedModelPath}\"");
        sb.AppendLine($"train_data_dir = \"{sanitized.DatasetDirectory}\"");
        sb.AppendLine($"output_dir = \"{sanitized.OutputDirectory}\"");
        sb.AppendLine($"output_name = \"{sanitized.RunName}\"");
        sb.AppendLine($"network_dim = {sanitized.NetworkDim}");
        sb.AppendLine($"network_alpha = {sanitized.NetworkAlpha}");
        sb.AppendLine($"learning_rate = {sanitized.LearningRate}");
        sb.AppendLine($"optimizer_type = \"{sanitized.Optimizer}\"");
        sb.AppendLine($"lr_scheduler = \"{sanitized.LrScheduler}\"");
        sb.AppendLine($"mixed_precision = \"{sanitized.Precision}\"");
        sb.AppendLine($"save_precision = \"{sanitized.Precision}\"");
        sb.AppendLine("cache_latents = true");
        sb.AppendLine("cache_latents_to_disk = true");
        sb.AppendLine("gradient_checkpointing = true");
        sb.AppendLine("sdpa = true");
        sb.AppendLine("xformers = false");
        sb.AppendLine($"max_train_epochs = {sanitized.MaxTrainEpochs}");
        sb.AppendLine($"train_batch_size = {sanitized.BatchSize}");
        if (sanitized.FlipAug) {
            sb.AppendLine("flip_aug = true");
        }
        if (sanitized.ShuffleTokens) {
            sb.AppendLine("shuffle_caption = true");
        }
        if (sanitized.KeepTokens > 0) {
            sb.AppendLine($"keep_tokens = {sanitized.KeepTokens}");
        }
        if (sanitized.ClipSkip.HasValue && sanitized.ClipSkip.Value > 0) {
            sb.AppendLine($"clip_skip = {sanitized.ClipSkip.Value}");
        }
        return sb.ToString();
    }

    public static TrainingConfig SanitizeForAmd(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        return new TrainingConfig {
            RunName = config.RunName,
            DonorLoraPath = config.DonorLoraPath,
            DatasetDirectory = config.DatasetDirectory,
            OutputDirectory = config.OutputDirectory,
            TargetBaseModel = config.TargetBaseModel,
            NetworkDim = config.NetworkDim,
            NetworkAlpha = config.NetworkAlpha,
            LearningRate = config.LearningRate,
            UnetLearningRate = config.UnetLearningRate,
            TextEncoderLearningRate = config.TextEncoderLearningRate,
            Optimizer = SanitizeOptimizer(config.Optimizer),
            LrScheduler = config.LrScheduler,
            AttentionMechanism = "sdpa",
            Precision = !string.IsNullOrWhiteSpace(config.Precision) ? config.Precision : "bf16",
            Quantize = false,
            CacheLatentsToDisk = true,
            TriggerWord = config.TriggerWord,
            SamplePrompts = new List<string>(config.SamplePrompts),
            NegativePrompt = config.NegativePrompt ?? string.Empty,
            MaxTrainEpochs = config.MaxTrainEpochs,
            TotalSteps = config.TotalSteps,
            Repeats = config.Repeats,
            BatchSize = config.BatchSize,
            GradientAccumulationSteps = config.GradientAccumulationSteps,
            SaveEveryNEpochs = config.SaveEveryNEpochs,
            FlipAug = config.FlipAug,
            ShuffleTokens = config.ShuffleTokens,
            KeepTokens = Math.Max(0, config.KeepTokens),
            ClipSkip = config.ClipSkip
        };
    }

    public static int CountDatasetImages(string? directoryPath) {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) {
            return 0;
        }

        try {
            string[] imageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
            return Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Count(file => imageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()));
        } catch {
            return 0;
        }
    }

    public static int CalculateTotalSteps(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        if (config.TotalSteps.HasValue && config.TotalSteps.Value > 0) {
            return config.TotalSteps.Value;
        }
        int imageCount = CountDatasetImages(config.DatasetDirectory);
        int safeImages = imageCount > 0 ? imageCount : 50;
        int safeBatch = Math.Max(1, config.BatchSize);
        int safeGradAccum = Math.Max(1, config.GradientAccumulationSteps);
        int safeEpochs = Math.Max(1, config.MaxTrainEpochs);
        int safeRepeats = Math.Max(1, config.Repeats);

        int stepsPerEpoch = Math.Max(1, (int)Math.Ceiling((double)(safeImages * safeRepeats) / (safeBatch * safeGradAccum)));
        return Math.Max(1, safeEpochs * stepsPerEpoch);
    }

    public static int CalculateSaveEverySteps(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        int safeSaveEpochs = Math.Max(1, config.SaveEveryNEpochs);

        if (config.TotalSteps.HasValue && config.TotalSteps.Value > 0 && config.MaxTrainEpochs > 0) {
            int derivedStepsPerEpoch = Math.Max(1, (int)Math.Round((double)config.TotalSteps.Value / config.MaxTrainEpochs));
            return Math.Max(1, Math.Min(config.TotalSteps.Value, safeSaveEpochs * derivedStepsPerEpoch));
        }

        int imageCount = CountDatasetImages(config.DatasetDirectory);
        int safeImages = imageCount > 0 ? imageCount : 50;
        int safeBatch = Math.Max(1, config.BatchSize);
        int safeGradAccum = Math.Max(1, config.GradientAccumulationSteps);
        int safeRepeats = Math.Max(1, config.Repeats);

        int stepsPerEpoch = Math.Max(1, (int)Math.Ceiling((double)(safeImages * safeRepeats) / (safeBatch * safeGradAccum)));
        return Math.Max(1, safeSaveEpochs * stepsPerEpoch);
    }

    public static string ResolveModelPath(string? targetBaseModel, ModelArchitectureInfo archInfo) {
        if (string.IsNullOrWhiteSpace(targetBaseModel)) {
            return archInfo.PretrainedModelPath;
        }

        string trimmed = targetBaseModel.Trim();
        if (string.Equals(trimmed, archInfo.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, archInfo.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, archInfo.Family, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(' ')) {
            return archInfo.PretrainedModelPath;
        }

        return trimmed;
    }

    public static string SanitizeOptimizer(string? rawOptimizer) {
        if (string.IsNullOrWhiteSpace(rawOptimizer)) {
            return "adamw";
        }

        string opt = rawOptimizer.Trim().ToLowerInvariant();
        if (opt.Contains("prodigy")) {
            return "prodigy";
        }
        if (opt.Contains("adafactor")) {
            return "adafactor";
        }
        if (opt.Contains("lion") && !opt.Contains("8bit")) {
            return "lion";
        }
        return "adamw";
    }
}
