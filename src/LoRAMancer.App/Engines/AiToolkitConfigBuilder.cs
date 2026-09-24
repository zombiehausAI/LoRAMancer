using System.Text;
using LoRAMancer.App.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LoRAMancer.App.Engines;

public sealed class AiToolkitConfigBuilder {
    public TrainingConfig CloneFromDonor(LoraMetadata donor, string runName, string datasetDirectory, string outputDirectory) {
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentException.ThrowIfNullOrWhiteSpace(runName);

        string sanitizedOptimizer = SanitizeOptimizer(donor.Optimizer);
        string sanitizedBaseModel = donor.BaseModel;
        if (sanitizedBaseModel == "Unknown" || string.IsNullOrWhiteSpace(sanitizedBaseModel)) {
            sanitizedBaseModel = "FLUX.1-dev";
        }

        return new TrainingConfig {
            RunName = runName,
            DonorLoraPath = donor.FilePath,
            DatasetDirectory = datasetDirectory,
            OutputDirectory = outputDirectory,
            TargetBaseModel = sanitizedBaseModel,
            NetworkDim = donor.NetworkDim ?? 16,
            NetworkAlpha = donor.NetworkAlpha ?? 16.0,
            LearningRate = donor.LearningRate ?? 0.0001,
            UnetLearningRate = donor.UnetLearningRate,
            TextEncoderLearningRate = donor.TextEncoderLearningRate,
            Optimizer = sanitizedOptimizer,
            LrScheduler = string.IsNullOrWhiteSpace(donor.LrScheduler) ? "cosine" : donor.LrScheduler,
            AttentionMechanism = "sdpa",
            Precision = "bf16",
            Quantize = false,
            CacheLatentsToDisk = true,
            TriggerWord = string.Empty,
            SamplePrompts = new List<string>(),
            MaxTrainEpochs = donor.Epochs ?? 10,
            BatchSize = 1,
            GradientAccumulationSteps = 1,
            SaveEveryNEpochs = 1
        };
    }

    public string BuildAiToolkitYaml(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        var sanitized = SanitizeForAmd(config);

        var root = new Dictionary<string, object> {
            ["job"] = "extension",
            ["config"] = new Dictionary<string, object> {
                ["name"] = sanitized.RunName,
                ["process"] = new List<object> {
                    new Dictionary<string, object> {
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
                            ["save_every"] = sanitized.SaveEveryNEpochs,
                            ["max_step_saves_to_keep"] = 4
                        },
                        ["datasets"] = new List<object> {
                            new Dictionary<string, object> {
                                ["folder_path"] = sanitized.DatasetDirectory,
                                ["caption_ext"] = "txt",
                                ["caption_dropout_rate"] = 0.05,
                                ["shuffle_tokens"] = false,
                                ["cache_latents_to_disk"] = sanitized.CacheLatentsToDisk,
                                ["resolution"] = new List<int> { 512, 768, 1024 }
                            }
                        },
                        ["train"] = new Dictionary<string, object> {
                            ["batch_size"] = sanitized.BatchSize,
                            ["steps"] = sanitized.MaxTrainEpochs * 100,
                            ["gradient_accumulation_steps"] = sanitized.GradientAccumulationSteps,
                            ["train_unet"] = true,
                            ["train_text_encoder"] = false,
                            ["gradient_checkpointing"] = true,
                            ["noise_scheduler"] = "flowmatch",
                            ["optimizer"] = sanitized.Optimizer,
                            ["lr"] = sanitized.LearningRate,
                            ["attention_mechanism"] = sanitized.AttentionMechanism,
                            ["dtype"] = sanitized.Precision,
                            ["quantize"] = sanitized.Quantize
                        },
                        ["model"] = new Dictionary<string, object> {
                            ["name_or_path"] = sanitized.TargetBaseModel,
                            ["is_flux"] = sanitized.TargetBaseModel.Contains("FLUX", StringComparison.OrdinalIgnoreCase),
                            ["quantize"] = false,
                            ["arch"] = sanitized.TargetBaseModel
                        },
                        ["sample"] = new Dictionary<string, object> {
                            ["sampler"] = "euler",
                            ["sample_every"] = 200,
                            ["width"] = 1024,
                            ["height"] = 1024,
                            ["prompts"] = sanitized.SamplePrompts.Count > 0 ? sanitized.SamplePrompts : new List<string> {
                                string.IsNullOrEmpty(sanitized.TriggerWord) ? "photo of a subject, highly detailed" : $"photo of {sanitized.TriggerWord}, highly detailed"
                            }
                        }
                    }
                }
            }
        };

        ISerializer serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return serializer.Serialize(root);
    }

    public string BuildKohyaConfig(TrainingConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        var sanitized = SanitizeForAmd(config);

        StringBuilder sb = new();
        sb.AppendLine($"# LoRAMancer AMD Sanitized Kohya Configuration");
        sb.AppendLine($"# Generated at {DateTime.UtcNow:O}");
        sb.AppendLine($"pretrained_model_name_or_path = \"{sanitized.TargetBaseModel}\"");
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
        sb.AppendLine($"cache_latents = true");
        sb.AppendLine($"cache_latents_to_disk = true");
        sb.AppendLine($"gradient_checkpointing = true");
        sb.AppendLine($"sdpa = true");
        sb.AppendLine($"xformers = false");
        sb.AppendLine($"max_train_epochs = {sanitized.MaxTrainEpochs}");
        sb.AppendLine($"train_batch_size = {sanitized.BatchSize}");
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
            Precision = "bf16",
            Quantize = false,
            CacheLatentsToDisk = true,
            TriggerWord = config.TriggerWord,
            SamplePrompts = new List<string>(config.SamplePrompts),
            MaxTrainEpochs = config.MaxTrainEpochs,
            BatchSize = config.BatchSize,
            GradientAccumulationSteps = config.GradientAccumulationSteps,
            SaveEveryNEpochs = config.SaveEveryNEpochs
        };
    }

    public static string SanitizeOptimizer(string? rawOptimizer) {
        if (string.IsNullOrWhiteSpace(rawOptimizer)) {
            return "adamw";
        }

        string opt = rawOptimizer.Trim().ToLowerInvariant();
        if (opt.Contains("bf16")) {
            return "adamw_bf16";
        }
        if (opt.Contains("8bit") || opt.Contains("paged") || opt.Contains("lion8bit")) {
            return "adamw_bf16";
        }
        if (opt.Contains("prodigy")) {
            return "prodigy";
        }
        if (opt.Contains("adafactor")) {
            return "adafactor";
        }
        return "adamw";
    }
}
