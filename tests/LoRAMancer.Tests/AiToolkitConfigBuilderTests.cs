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
    public void BuildAiToolkitYaml_EnforcesAmdSafetyRules() {
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
    }
}
