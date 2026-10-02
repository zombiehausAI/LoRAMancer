using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.Tests;

public sealed class AiToolkitConfigBuilderTests {
    [Fact]
    public void SanitizeOptimizer_ConvertsNvidia8BitToAmdBf16() {
        Assert.Equal("adamw", AiToolkitConfigBuilder.SanitizeOptimizer("adamw8bit"));
        Assert.Equal("adamw", AiToolkitConfigBuilder.SanitizeOptimizer("PagedAdamW"));
        Assert.Equal("adamw", AiToolkitConfigBuilder.SanitizeOptimizer("Lion8bit"));
        Assert.Equal("adamw", AiToolkitConfigBuilder.SanitizeOptimizer("adamw"));
        Assert.Equal("prodigy", AiToolkitConfigBuilder.SanitizeOptimizer("prodigy"));
    }

    [Fact]
    public void BuildAiToolkitYaml_EnforcesAmdSafetyRules_Flux() {
        AiToolkitConfigBuilder builder = new();
        LoraMetadata donor = new() {
            FileName = "donor_test.safetensors",
            FilePath = "C:\\loras\\donor_test.safetensors",
            BaseModel = "FLUX.1-dev",
            NetworkDim = 32,
            NetworkAlpha = 16.0,
            LearningRate = 0.0001,
            Optimizer = "adamw8bit"
        };

        TrainingConfig config = builder.CloneFromDonor(donor, "test_run", "D:\\datasets\\flux", "D:\\output\\flux");
        string yaml = builder.BuildAiToolkitYaml(config);

        Assert.Contains("adamw", yaml);
        Assert.Contains("sdpa", yaml);
        Assert.Contains("bf16", yaml);
        Assert.Contains("cache_latents_to_disk: true", yaml);
        Assert.Contains("quantize: false", yaml);
        Assert.Contains("noise_scheduler: flowmatch", yaml);
        Assert.Contains("is_flux: true", yaml);
    }

    [Fact]
    public void BuildAiToolkitYaml_PonyXLV6_GeneratesSdxlEulerConfig() {
        AiToolkitConfigBuilder builder = new();
        LoraMetadata donor = new() {
            FileName = "donor_pony.safetensors",
            FilePath = "C:\\loras\\donor_pony.safetensors",
            BaseModel = "Pony Diffusion V6 XL",
            NetworkDim = 32,
            NetworkAlpha = 16.0,
            LearningRate = 0.00005,
            Optimizer = "adamw8bit"
        };

        TrainingConfig config = builder.CloneFromDonor(donor, "pony_run", "D:\\datasets\\pony", "D:\\output\\pony");
        string yaml = builder.BuildAiToolkitYaml(config);

        Assert.Contains("noise_scheduler: euler", yaml);
        Assert.Contains("is_flux: false", yaml);
        Assert.Contains("arch: sdxl", yaml);
        Assert.Contains("score_9", yaml);
    }

    [Fact]
    public void BuildAiToolkitYaml_IllustriousXL_GeneratesIllustriousConfig() {
        AiToolkitConfigBuilder builder = new();
        LoraMetadata donor = new() {
            FileName = "donor_illustrious.safetensors",
            FilePath = "C:\\loras\\donor_illustrious.safetensors",
            BaseModel = "Illustrious-XL",
            NetworkDim = 32,
            NetworkAlpha = 16.0,
            LearningRate = 0.00005,
            Optimizer = "adamw"
        };

        TrainingConfig config = builder.CloneFromDonor(donor, "illustrious_run", "D:\\datasets\\illustrious", "D:\\output\\illustrious");
        string yaml = builder.BuildAiToolkitYaml(config);

        Assert.Contains("noise_scheduler: euler", yaml);
        Assert.Contains("is_flux: false", yaml);
        Assert.Contains("arch: sdxl", yaml);
        Assert.Contains("masterpiece, newest, anime", yaml);
    }

    [Fact]
    public void ModelArchitectureRegistry_InfersPonyAndIllustrious() {
        ModelArchitectureRegistry registry = new();

        var ponyMeta = new Dictionary<string, string> {
            ["modelspec.architecture"] = "sd_xl_v1_0/pony_v6"
        };
        var ponyArch = registry.InferFromMetadata(ponyMeta);
        Assert.Equal("pony_xl_v6", ponyArch.Id);

        var illMeta = new Dictionary<string, string> {
            ["ss_sd_model_name"] = "OnomaAIResearch/Illustrious-xl-early-release-v1.0"
        };
        var illArch = registry.InferFromMetadata(illMeta);
        Assert.Equal("illustrious_xl", illArch.Id);
    }

    [Fact]
    public void ModelArchitectureRegistry_InfersChroma() {
        ModelArchitectureRegistry registry = new();

        var chromaMeta = new Dictionary<string, string> {
            ["ss_sd_model_name"] = "Kelsey_Kernstine_Chroma_V1"
        };
        var chromaArch = registry.InferFromMetadata(chromaMeta);
        Assert.Equal("chroma_hd_1", chromaArch.Id);
        Assert.Equal("ChromaHD-1", chromaArch.DisplayName);
        Assert.Equal("Chroma", chromaArch.Family);
    }

    [Fact]
    public void BuildAiToolkitYaml_WithSingleSamplePrompt_OutputsSinglePrompt() {
        AiToolkitConfigBuilder builder = new();
        TrainingConfig config = new() {
            RunName = "single_prompt_run",
            TargetBaseModel = "FLUX.1-dev",
            SamplePrompts = new List<string> { "photo of ohwx person in a garden" },
            NegativePrompt = ""
        };

        string yaml = builder.BuildAiToolkitYaml(config);
        Assert.Contains("photo of ohwx person in a garden", yaml);
        Assert.DoesNotContain("close up portrait", yaml);
    }

    [Fact]
    public void BuildAiToolkitYaml_WithTwoSamplePromptsAndNegative_OutputsBothAndNeg() {
        AiToolkitConfigBuilder builder = new();
        TrainingConfig config = new() {
            RunName = "dual_prompt_run",
            TargetBaseModel = "Illustrious-XL",
            SamplePrompts = new List<string> {
                "masterpiece, 1girl, solo, portrait",
                "masterpiece, 1girl, full body, cinematic lighting"
            },
            NegativePrompt = "blurry, worst quality, low quality"
        };

        string yaml = builder.BuildAiToolkitYaml(config);
        Assert.Contains("masterpiece, 1girl, solo, portrait", yaml);
        Assert.Contains("masterpiece, 1girl, full body, cinematic lighting", yaml);
        Assert.Contains("blurry, worst quality, low quality", yaml);
    }

    [Fact]
    public void CloneFromDonor_DoesNotImportNameOrActivationTag() {
        AiToolkitConfigBuilder builder = new();
        LoraMetadata donor = new() {
            FileName = "Kelsey_Kernstine_Chroma_V1.safetensors",
            FilePath = "D:\\AI\\Models\\Kelsey_Kernstine_Chroma_V1.safetensors",
            BaseModel = "FLUX.1-dev",
            NetworkDim = 4,
            NetworkAlpha = 16.0,
            LearningRate = 0.0005,
            Epochs = 20,
            TrainedWords = new List<string> { "kelsey", "woman" }
        };

        TrainingConfig cloned = builder.CloneFromDonor(donor);

        Assert.Equal(string.Empty, cloned.RunName);
        Assert.Equal(string.Empty, cloned.TriggerWord);
        Assert.Equal(4, cloned.NetworkDim);
        Assert.Equal(16.0, cloned.NetworkAlpha);
        Assert.Equal(0.0005, cloned.LearningRate);
        Assert.Equal(20, cloned.MaxTrainEpochs);
    }

    [Fact]
    public void BuildAiToolkitYaml_CarriesAllDonorHyperparametersAccurately() {
        AiToolkitConfigBuilder builder = new();
        LoraMetadata donor = new() {
            FileName = "Kelsey_Kernstine_Chroma_V1.safetensors",
            FilePath = "D:\\AI\\Models\\Kelsey_Kernstine_Chroma_V1.safetensors",
            BaseModel = "FLUX.1-dev",
            NetworkDim = 2,
            NetworkAlpha = 16.0,
            LearningRate = 0.0005,
            UnetLearningRate = 0.0005,
            TextEncoderLearningRate = 0,
            Optimizer = "bitsandbytes.optim.adamw.AdamW8bit(weight_decay=0.01,eps=1e-08,betas=(0.9, 0.999))",
            LrScheduler = "cosine_with_restarts",
            Epochs = 20,
            TotalSteps = 1200,
            Precision = "bf16"
        };

        TrainingConfig config = builder.CloneFromDonor(donor, "miku_chroma_run", "D:\\datasets\\miku", "D:\\output\\miku");
        string yaml = builder.BuildAiToolkitYaml(config);

        Assert.Contains("linear: 2", yaml);
        Assert.Contains("linear_alpha: 16", yaml);
        Assert.Contains("lr: 0.0005", yaml);
        Assert.Contains("unet_lr: 0.0005", yaml);
        Assert.Contains("text_encoder_lr: 0", yaml);
        Assert.Contains("lr_scheduler: cosine_with_restarts", yaml);
        Assert.Contains("optimizer: adamw", yaml);
        Assert.Contains("steps: 1200", yaml);
        Assert.Contains("dtype: bf16", yaml);
        Assert.Contains("save_every: 60", yaml);
        Assert.Contains("max_step_saves_to_keep: 20", yaml);
    }

    [Fact]
    public void BuildAiToolkitYaml_WithAugmentationsAndClipSkip_OutputsCorrectProperties() {
        AiToolkitConfigBuilder builder = new();
        TrainingConfig config = new() {
            RunName = "pony_test",
            DatasetDirectory = "D:\\datasets\\pony",
            OutputDirectory = "D:\\output\\pony",
            TargetBaseModel = "Pony Diffusion V6 XL",
            FlipAug = true,
            ShuffleTokens = true,
            KeepTokens = 3,
            ClipSkip = 2,
            TotalSteps = 1000
        };

        string yaml = builder.BuildAiToolkitYaml(config);
        string kohya = builder.BuildKohyaConfig(config);

        Assert.Contains("flip_aug: true", yaml);
        Assert.Contains("shuffle_tokens: true", yaml);
        Assert.Contains("keep_tokens: 3", yaml);
        Assert.Contains("clip_skip: 2", yaml);

        Assert.Contains("flip_aug = true", kohya);
        Assert.Contains("shuffle_caption = true", kohya);
        Assert.Contains("keep_tokens = 3", kohya);
        Assert.Contains("clip_skip = 2", kohya);
    }

    [Fact]
    public void BuildAiToolkitYaml_WithAuxiliaryLora_EmitsAssistantLoraAndExtraLoras() {
        AiToolkitConfigBuilder builder = new();
        TrainingConfig config = new() {
            RunName = "aux_lora_test",
            DatasetDirectory = "D:\\datasets\\test",
            OutputDirectory = "D:\\output\\test",
            TargetBaseModel = "FLUX.1-dev",
            AuxiliaryLoraPath = "C:\\loras\\anime_style_v1.safetensors",
            AuxiliaryLoraWeight = 0.85
        };

        string yaml = builder.BuildAiToolkitYaml(config);
        string kohya = builder.BuildKohyaConfig(config);

        Assert.Contains("assistant_lora_path: C:\\loras\\anime_style_v1.safetensors", yaml);
        Assert.Contains("extra_loras:", yaml);
        Assert.Contains("scale: 0.85", yaml);

        Assert.Contains("network_weights = \"C:\\loras\\anime_style_v1.safetensors\"", kohya);
        Assert.Contains("network_multiplier = 0.85", kohya);
    }
}


