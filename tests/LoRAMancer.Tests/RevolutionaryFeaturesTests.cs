using LoRAMancer.App.Engines;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class RevolutionaryFeaturesTests {
    [Fact]
    public void SemanticCollisionService_CriticalArchetypeToken_FlagsSevereBleed() {
        var service = new SemanticCollisionService();

        var report = service.AnalyzeToken("silver");

        Assert.Equal(CollisionSeverity.CriticalBleed, report.Severity);
        Assert.True(report.RiskScore >= 90);
        Assert.NotEmpty(report.RecommendedSynthetics);
        Assert.Contains("metallic", report.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticCollisionService_RareSyntheticToken_IsClean() {
        var service = new SemanticCollisionService();

        var report = service.AnalyzeToken("ohwx_manc9");

        Assert.Equal(CollisionSeverity.Clean, report.Severity);
        Assert.True(report.RiskScore <= 15);
    }

    [Fact]
    public void SemanticCollisionService_GenerateSyntheticTokens_ProducesPronounceableTokens() {
        var service = new SemanticCollisionService();

        var tokens = service.GenerateSyntheticTokens(5, "cyber");

        Assert.Equal(5, tokens.Count);
        foreach (var t in tokens) {
            Assert.NotEmpty(t);
            Assert.False(t.Contains(' '));
        }
    }

    [Fact]
    public void LoraBenchmarkService_ExtractEpochNumber_ParsesVariousPatterns() {
        Assert.Equal(5, LoraBenchmarkService.ExtractEpochNumber("my_character_epoch-0005.safetensors"));
        Assert.Equal(12, LoraBenchmarkService.ExtractEpochNumber("flux_lora_ep12.safetensors"));
        Assert.Equal(500, LoraBenchmarkService.ExtractEpochNumber("style_step500.safetensors"));
        Assert.Equal(42, LoraBenchmarkService.ExtractEpochNumber("model_e42.safetensors"));
    }

    [Fact]
    public void LoraBenchmarkService_GenerateDefaultPrompts_ContainsStandardBattery() {
        SettingsService settings = new();
        HttpClient httpClient = new();
        ComfyUiService comfy = new(httpClient, settings);
        LoraBenchmarkService benchmark = new(comfy, httpClient, settings);

        var prompts = benchmark.GenerateDefaultPrompts("ohwx");

        Assert.Equal(4, prompts.Count);
        Assert.Contains(prompts, p => p.Category == "Likeness" && p.Prompt.Contains("ohwx"));
        Assert.Contains(prompts, p => p.Category == "Flexibility" && p.Prompt.Contains("watercolor"));
        Assert.Contains(prompts, p => p.Category == "Bleed Stress" && p.Prompt.Contains("cyberpunk"));
    }

    [Fact]
    public void LoraDiffService_ExportMarkdownReport_ProducesValidMarkdown() {
        ProcessRunner runner = new();
        LoraDiffService diffService = new(runner);

        var summary = new LoraDiffSummary {
            PathA = @"D:\models\loraA.safetensors",
            PathB = @"D:\models\loraB.safetensors",
            ModelNameA = "loraA",
            ModelNameB = "loraB",
            ArchA = "FLUX.1",
            ArchB = "FLUX.1",
            TotalTensorsA = 120,
            TotalTensorsB = 120,
            SharedTensorsCount = 120,
            AverageCosineSimilarity = 0.9850,
            MaxDriftScore = 0.045,
            CompatibilityStatus = "Fully Compatible",
            LayerDiffs = new List<LayerDiffItem> {
                new LayerDiffItem {
                    LayerName = "transformer.down_blocks.0.weight",
                    Status = "Matched",
                    ShapeA = "16x320",
                    ShapeB = "16x320",
                    CosineSimilarity = 0.9820,
                    NormA = 4.12,
                    NormB = 4.35,
                    RelativeDeltaPct = 5.58
                }
            },
            MetadataDiffs = new List<MetadataDiffItem> {
                new MetadataDiffItem("ss_learning_rate", "1e-4", "2e-4", true)
            },
            Duration = TimeSpan.FromSeconds(1.2)
        };

        string md = diffService.ExportMarkdownReport(summary);

        Assert.NotNull(md);
        Assert.Contains("# LoRAMancer LoRA Visual Diff Report", md);
        Assert.Contains("loraA", md);
        Assert.Contains("0.9850", md);
        Assert.Contains("ss_learning_rate", md);
    }
}
