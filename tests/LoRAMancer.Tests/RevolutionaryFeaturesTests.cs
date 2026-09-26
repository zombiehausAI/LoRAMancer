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

    [Fact]
    public async Task OverbakeRadarService_AnalyzeLoraOverbake_ComputesScoresAndStatus() {
        ProcessRunner runner = new();
        OverbakeRadarService radar = new(runner);

        string tempLora = Path.Combine(Path.GetTempPath(), $"test_lora_{Guid.NewGuid():N}.safetensors");
        try {
            await File.WriteAllBytesAsync(tempLora, new byte[1024 * 1024 * 4]); // 4MB dummy file
            var report = await radar.AnalyzeLoraOverbakeAsync(tempLora);

            Assert.NotNull(report);
            Assert.True(report.TotalLoRALayers > 0);
            Assert.True(report.OverbakeScore >= 0 && report.OverbakeScore <= 100);
            Assert.NotEmpty(report.Verdict);
            Assert.NotEmpty(report.Recommendation);
            Assert.NotEmpty(report.LayerMetrics);
        } finally {
            if (File.Exists(tempLora)) {
                File.Delete(tempLora);
            }
        }
    }

    [Fact]
    public async Task OverbakeRadarService_AnalyzeEpochSequence_DetectsSweetSpot() {
        ProcessRunner runner = new();
        OverbakeRadarService radar = new(runner);

        string tempFolder = Path.Combine(Path.GetTempPath(), $"test_epochs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);

        try {
            for (int i = 1; i <= 5; i++) {
                string file = Path.Combine(tempFolder, $"lora_concept_epoch-000{i}.safetensors");
                await File.WriteAllBytesAsync(file, new byte[1024 * 1024 * (i + 1)]);
            }

            var result = await radar.AnalyzeEpochSequenceAsync(tempFolder);

            Assert.NotNull(result);
            Assert.Equal(5, result.TotalCheckpoints);
            Assert.True(result.RecommendedSweetSpotEpoch >= 1 && result.RecommendedSweetSpotEpoch <= 5);
            Assert.Contains(result.Trajectory, t => t.IsRecommendedSweetSpot);
            Assert.NotEmpty(result.SummaryMessage);
        } finally {
            if (Directory.Exists(tempFolder)) {
                Directory.Delete(tempFolder, true);
            }
        }
    }

    [Fact]
    public async Task LoraEchoHunterService_MissingFiles_ReturnsErrorResult() {
        ProcessRunner runner = new();
        LoraEchoHunterService hunter = new(runner);

        var result = await hunter.HuntAndRepelGhostVectorAsync("nonexistentA.safetensors", "nonexistentB.safetensors", "out.safetensors");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task LoraEchoHunterService_StyleDecoupler_MissingSource_ReturnsErrorResult() {
        ProcessRunner runner = new();
        LoraEchoHunterService hunter = new(runner);

        var result = await hunter.DecoupleStyleAndIdentityAsync("nonexistent.safetensors", "out.safetensors");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task LoraDeAnonymizerService_MissingFile_ThrowsFileNotFound() {
        LoraDeAnonymizerService deAnonymizer = new();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            deAnonymizer.ReverseEngineerLoraAsync("nonexistent_model.safetensors"));
    }

    [Fact]
    public async Task LoraDeAnonymizerService_DummyFile_RecoversFingerprintAndHeuristicTrigger() {
        LoraDeAnonymizerService deAnonymizer = new();

        string tempLora = Path.Combine(Path.GetTempPath(), $"cyberpunk_girl_v2_rank32.safetensors");
        try {
            await File.WriteAllBytesAsync(tempLora, new byte[1024 * 100]); // 100KB dummy

            var report = await deAnonymizer.ReverseEngineerLoraAsync(tempLora);

            Assert.NotNull(report);
            Assert.Equal("cyberpunk_girl_v2_rank32.safetensors", report.FileName);
            Assert.NotEmpty(report.DetectedArchitecture);
            Assert.True(report.EffectiveRank > 0);
            Assert.NotEmpty(report.ForensicSummary);
            Assert.NotEmpty(report.RecoveredHyperparameters);
            Assert.Contains(report.RecoveredTriggerTokens, t => t.Token.Contains("cyberpunk_girl"));
        } finally {
            if (File.Exists(tempLora)) {
                File.Delete(tempLora);
            }
        }
    }

    [Fact]
    public void LoraCategory_GetDefaultCategories_ProvidesEightCuratedCategories() {
        var categories = LoRAMancer.App.Models.LoraCategory.GetDefaultCategories();

        Assert.NotNull(categories);
        Assert.Equal(8, categories.Count);
        Assert.Contains(categories, c => c.Name == "Character" && !string.IsNullOrWhiteSpace(c.Color) && !string.IsNullOrWhiteSpace(c.Icon));
        Assert.Contains(categories, c => c.Name == "Style" && !string.IsNullOrWhiteSpace(c.Color));
        Assert.Contains(categories, c => c.Name == "Concept" && !string.IsNullOrWhiteSpace(c.Color));
        Assert.Contains(categories, c => c.Name == "Clothing" && !string.IsNullOrWhiteSpace(c.Color));
    }

    [Fact]
    public void LoraMetadata_CategoryAndTags_HandlesDefaultsAndDisplayCategory() {
        var lora = new LoRAMancer.App.Models.LoraMetadata {
            FileName = "mech_pilot.safetensors"
        };

        Assert.Equal("Uncategorized", lora.DisplayCategory);
        Assert.Empty(lora.Tags);

        lora.Category = "Vehicle";
        lora.Tags.AddRange(new[] { "mecha", "sci-fi", "armor" });

        Assert.Equal("Vehicle", lora.DisplayCategory);
        Assert.Equal(3, lora.Tags.Count);
        Assert.Contains("mecha", lora.Tags);
    }

    [Fact]
    public void LoraSorting_CategoryAndTags_SortsCorrectly() {
        var list = new List<LoRAMancer.App.Models.LoraMetadata> {
            new() { FileName = "bravo.safetensors", Category = "Style", Tags = new List<string> { "art" } },
            new() { FileName = "alpha.safetensors", Category = "Character", Tags = new List<string> { "girl", "anime", "portrait" } },
            new() { FileName = "charlie.safetensors", Category = null, Tags = new List<string>() }
        };

        // Sort by Category A-Z
        var sortedByCat = list.OrderBy(l => l.DisplayCategory).ThenBy(l => l.FileName).ToList();
        Assert.Equal("Character", sortedByCat[0].Category);
        Assert.Equal("Style", sortedByCat[1].Category);
        Assert.Null(sortedByCat[2].Category);

        // Sort by Tags count descending
        var sortedByTags = list.OrderByDescending(l => l.Tags.Count).ToList();
        Assert.Equal("alpha.safetensors", sortedByTags[0].FileName);
        Assert.Equal(3, sortedByTags[0].Tags.Count);
        Assert.Equal("bravo.safetensors", sortedByTags[1].FileName);
        Assert.Equal("charlie.safetensors", sortedByTags[2].FileName);
    }

    [Fact]
    public async Task ThemeService_DynamicAccentColor_OverridesAndNotifies() {
        var themeService = new LoRAMancer.App.Services.ThemeService();
        Assert.NotEmpty(LoRAMancer.App.Services.ThemeService.CuratedAccentSwatches);

        string defaultPrimary = themeService.EffectivePrimaryColor;
        Assert.False(string.IsNullOrWhiteSpace(defaultPrimary));

        bool eventFired = false;
        themeService.OnThemeChanged += () => eventFired = true;

        await themeService.SetCustomPrimaryColorAsync("#10b981");

        Assert.Equal("#10b981", themeService.EffectivePrimaryColor);
        Assert.Equal("#10b981", themeService.CustomPrimaryColor);
        Assert.True(eventFired);

        var mudTheme = themeService.GetMudTheme();
        Assert.Equal("rgba(16,185,129,1)", mudTheme.PaletteDark.Primary.ToString());

        // Reset to default
        await themeService.SetCustomPrimaryColorAsync(null);
        Assert.Null(themeService.CustomPrimaryColor);
        Assert.Equal(defaultPrimary, themeService.EffectivePrimaryColor);
    }

    [Fact]
    public void ModelArchitectureRegistry_ExpandedPresets_RegisteredAndResolvable() {
        var registry = new LoRAMancer.App.Engines.ModelArchitectureRegistry();
        var all = registry.GetAll();

        Assert.Contains(all, a => a.DisplayName == "Stable Diffusion 3.5" && a.Family == "SD3.5");
        Assert.Contains(all, a => a.DisplayName == "Wan 2.1" && a.Family == "Wan");
        Assert.Contains(all, a => a.DisplayName == "HunyuanVideo" && a.Family == "Hunyuan");
        Assert.Contains(all, a => a.DisplayName == "Stable Diffusion 2.1" && a.Family == "SD21");
        Assert.Contains(all, a => a.DisplayName == "AuraFlow" && a.Family == "AuraFlow");
        Assert.Contains(all, a => a.DisplayName == "Illustrious-XL");
        Assert.Contains(all, a => a.DisplayName == "Pony Diffusion V6 XL");
        Assert.Contains(all, a => a.DisplayName == "ChromaHD-1");
        Assert.Contains(all, a => a.DisplayName == "Stable Diffusion 1.5");
    }

    [Fact]
    public void ModelArchitectureRegistry_PathHeuristicInference_InfersFromParentFolders() {
        var registry = new LoRAMancer.App.Engines.ModelArchitectureRegistry();
        var emptyMeta = new Dictionary<string, string>();

        // Folder: Pony
        var pony = registry.InferFromPathOrMetadata(@"D:\LoRAs\Pony\anime_character.safetensors", emptyMeta);
        Assert.Equal("Pony Diffusion V6 XL", pony.DisplayName);

        // Folder: Illustrious
        var ill = registry.InferFromPathOrMetadata(@"D:\LoRAs\Illustrious\cyberpunk_style.safetensors", emptyMeta);
        Assert.Equal("Illustrious-XL", ill.DisplayName);

        // Folder: SD3.5
        var sd35 = registry.InferFromPathOrMetadata(@"D:\LoRAs\SD3.5\portrait_v1.safetensors", emptyMeta);
        Assert.Equal("Stable Diffusion 3.5", sd35.DisplayName);

        // Folder: SD1.5
        var sd15 = registry.InferFromPathOrMetadata(@"D:\LoRAs\SD1.5\retro_game.safetensors", emptyMeta);
        Assert.Equal("Stable Diffusion 1.5", sd15.DisplayName);

        // Folder: Chroma
        var chroma = registry.InferFromPathOrMetadata(@"D:\LoRAs\Chroma\lighting.safetensors", emptyMeta);
        Assert.Equal("ChromaHD-1", chroma.DisplayName);

        // Folder: Flux
        var flux = registry.InferFromPathOrMetadata(@"D:\LoRAs\Flux\realism.safetensors", emptyMeta);
        Assert.Equal("FLUX.1-dev", flux.DisplayName);

        // Folder: Wan
        var wan = registry.InferFromPathOrMetadata(@"D:\LoRAs\Wan\video_motion.safetensors", emptyMeta);
        Assert.Equal("Wan 2.1", wan.DisplayName);
    }

    [Fact]
    public void ModelArchitectureRegistry_CustomRegistration_WorksSeamlessly() {
        var registry = new LoRAMancer.App.Engines.ModelArchitectureRegistry();
        var custom = registry.RegisterCustom("Animagine 3.1", "SDXL", dim: 32, alpha: 32.0, lr: 0.00005, resolution: 1024);

        Assert.NotNull(custom);
        Assert.Equal("Animagine 3.1", custom.DisplayName);
        Assert.Equal("animagine_3_1", custom.Id);
        Assert.True(registry.TryGet("animagine_3_1", out var fetched));
        Assert.Equal("Animagine 3.1", fetched!.DisplayName);

        // Infer from metadata with custom keyword
        var inferred = registry.InferFromMetadata(new Dictionary<string, string> { ["base_model"] = "animagine_3_1" });
        Assert.Equal("Animagine 3.1", inferred.DisplayName);
    }

    [Fact]
    public void PostgreSqlConfig_BuildConnectionString_FormatsProperly() {
        var config = new LoRAMancer.App.Services.LoraDatabaseService.PostgreSqlConfig {
            Host = "192.168.1.50",
            Port = 5433,
            Database = "studio_loras",
            Username = "studio_user",
            Password = "SuperSecretPassword123!",
            SslMode = "Require"
        };

        string connStr = config.BuildConnectionString();
        Assert.Contains("Host=192.168.1.50", connStr);
        Assert.Contains("Port=5433", connStr);
        Assert.Contains("Database=studio_loras", connStr);
        Assert.Contains("Username=studio_user", connStr);
        Assert.Contains("Password=SuperSecretPassword123!", connStr);
        Assert.Contains("Require", connStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSqlConfig_Defaults_MatchStandardStudioProfile() {
        var config = new LoRAMancer.App.Services.LoraDatabaseService.PostgreSqlConfig();
        Assert.Equal("localhost", config.Host);
        Assert.Equal(5432, config.Port);
        Assert.Equal("loramancer_studio", config.Database);
        Assert.Equal("postgres", config.Username);
        Assert.Equal("Prefer", config.SslMode);
        Assert.Empty(config.Password);
    }

    [Fact]
    public void AppSettings_DatabaseProvider_DefaultsToSqlite_AndSupportsToggle() {
        var settings = new LoRAMancer.App.Models.AppSettings();
        Assert.Equal("SQLite", settings.DatabaseProvider);
        Assert.Equal("localhost", settings.PgHost);
        Assert.Equal(5432, settings.PgPort);
        Assert.Equal("loramancer_studio", settings.PgDatabase);

        settings.DatabaseProvider = "PostgreSQL";
        Assert.Equal("PostgreSQL", settings.DatabaseProvider);
    }

    [Fact]
    public void StudioSessionService_SetBaseModel_And_SetActiveLora_PersistsAccurately() {
        string tempDir = Path.Combine(Path.GetTempPath(), "loramancer_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sessionFile = Path.Combine(tempDir, "session.json");

        try {
            var session = new LoRAMancer.App.Services.StudioSessionService(sessionFile);
            session.SetActiveLora(@"D:\Models\Chroma\my_lora.safetensors", "ChromaHD-1");
            Assert.Equal(@"D:\Models\Chroma\my_lora.safetensors", session.ActiveLoraPath);
            Assert.Equal("ChromaHD-1", session.BaseModel);

            session.SetBaseModel("FLUX.1-dev");
            Assert.Equal("FLUX.1-dev", session.BaseModel);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void ModelArchitectureRegistry_InfersKnownAndCustomArchitecturesForDiagnostics() {
        var registry = new LoRAMancer.App.Engines.ModelArchitectureRegistry();

        // Check that all core architectures exist for diagnostics
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("FLUX")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("XL") || a.Family.Contains("SDXL")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("Pony")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("Illustrious")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("Chroma")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("Wan")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("Hunyuan")));
        Assert.NotNull(registry.GetAll().FirstOrDefault(a => a.DisplayName.Contains("3.5")));

        // Inference from file path
        var inferredPony = registry.InferFromPathOrMetadata(@"D:\LoRAs\Pony\anime_character.safetensors", new Dictionary<string, string>());
        Assert.Contains("Pony", inferredPony.DisplayName);

        var inferredChroma = registry.InferFromPathOrMetadata(@"D:\LoRAs\Chroma\lighting.safetensors", new Dictionary<string, string>());
        Assert.Contains("Chroma", inferredChroma.DisplayName);
    }
}
