using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.Tests;

public sealed class AiToolkitConfigBuilderTests {
    [Fact]
    public void SanitizeOptimizer_ConvertsNvidia8BitToAmdBf16() {
        Assert.Equal("adamw_bf16", AiToolkitConfigBuilder.SanitizeOptimizer("adamw8bit"));
        Assert.Equal("adamw_bf16", AiToolkitConfigBuilder.SanitizeOptimizer("PagedAdamW"));
        Assert.Equal("adamw_bf16", AiToolkitConfigBuilder.SanitizeOptimizer("Lion8bit"));
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

        Assert.Contains("adamw_bf16", yaml);
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
}
