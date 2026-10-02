using System.Text.Json;
using System.Text.Json.Nodes;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public class ModusFlowPromptTests : IDisposable {
    private readonly string _testTempDir;
    private readonly SettingsService _settingsService;

    public ModusFlowPromptTests() {
        _testTempDir = Path.Combine(Path.GetTempPath(), "LoRAMancer_ModusFlowTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);

        _settingsService = new SettingsService();
        _settingsService.Current.SavedPromptsDirectory = _testTempDir;
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_testTempDir)) {
                Directory.Delete(_testTempDir, true);
            }
        } catch {
            // Clean up silently
        }
    }

    [Fact]
    public async Task GetAllPromptsAsync_EmptyDirectory_SeedsDefaultModusFlowPrompts() {
        var service = new ModusFlowPromptService(_settingsService);

        var prompts = await service.GetAllPromptsAsync();

        Assert.NotEmpty(prompts);
        Assert.Contains(prompts, p => p.Name.Contains("Portrait", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(prompts, p => p.Name.Contains("Fantasy", StringComparison.OrdinalIgnoreCase));

        // Verify each prompt has positive and negative text
        var portrait = prompts.First(p => p.Name.Contains("Portrait", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(portrait.Positive));
        Assert.False(string.IsNullOrWhiteSpace(portrait.Negative));
        Assert.Equal("Portraits", portrait.Category);
    }

    [Fact]
    public async Task SavePromptAsync_CreatesExactModusFlowJsonSchema() {
        var service = new ModusFlowPromptService(_settingsService);

        var saved = await service.SavePromptAsync(
            name: "Cyberpunk Samurai",
            positive: "masterpiece, 1boy, cybernetic katana, neon rainy street",
            negative: "blurry, low quality, deformed, extra arms",
            category: "Cyberpunk"
        );

        Assert.NotNull(saved);
        Assert.Equal("Cyberpunk Samurai", saved.Name);
        Assert.Equal("Cyberpunk", saved.Category);
        Assert.True(File.Exists(saved.FilePath));

        // Read raw JSON from disk and verify schema matches ComfyUI-ModusFlow
        string rawJson = await File.ReadAllTextAsync(saved.FilePath);
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("category", out var catProp));
        Assert.Equal("Cyberpunk", catProp.GetString());

        Assert.True(root.TryGetProperty("positive", out var posProp));
        Assert.Equal("masterpiece, 1boy, cybernetic katana, neon rainy street", posProp.GetString());

        Assert.True(root.TryGetProperty("type", out var typeProp));
        Assert.Equal("prompt", typeProp.GetString());

        Assert.True(root.TryGetProperty("negative", out var negProp));
        Assert.Equal("blurry, low quality, deformed, extra arms", negProp.GetString());

        // Verify file is placed in the dedicated prompts/ subdirectory
        Assert.Contains(Path.DirectorySeparatorChar + "prompts" + Path.DirectorySeparatorChar, saved.FilePath);
    }

    [Fact]
    public async Task DeletePromptAsync_RemovesFileAndUpdatesList() {
        var service = new ModusFlowPromptService(_settingsService);

        var saved = await service.SavePromptAsync("To Be Deleted", "prompt text", "negative text");
        Assert.True(File.Exists(saved.FilePath));

        bool deleted = await service.DeletePromptAsync("To Be Deleted");
        Assert.True(deleted);
        Assert.False(File.Exists(saved.FilePath));
    }

    [Fact]
    public async Task GetAllPromptsAsync_AutoMigratesLooseFilesAndScansSubdirectories() {
        var service = new ModusFlowPromptService(_settingsService);

        // Create loose files in root directory
        string loosePromptPath = Path.Combine(_testTempDir, "Legacy Loose Prompt.json");
        string looseSongPath = Path.Combine(_testTempDir, "Legacy Song.json");

        await File.WriteAllTextAsync(loosePromptPath, JsonSerializer.Serialize(new {
            category = "Legacy",
            positive = "vintage loose prompt",
            negative = "bad"
        }));

        await File.WriteAllTextAsync(looseSongPath, JsonSerializer.Serialize(new {
            type = "song",
            category = "Rock",
            title = "Neon Nights",
            tags = "80s Rock, Guitar Solo",
            lyrics = "[Verse 1]\nDriving through the night",
            negative_style = "acoustic, slow"
        }));

        // Trigger GetAllPromptsAsync which executes auto-migration and subdirectory scan
        var prompts = await service.GetAllPromptsAsync();

        // 1. Verify auto-migration moved loose files into their subdirectories
        Assert.False(File.Exists(loosePromptPath));
        Assert.False(File.Exists(looseSongPath));

        string migratedPromptPath = Path.Combine(_testTempDir, "prompts", "Legacy Loose Prompt.json");
        string migratedSongPath = Path.Combine(_testTempDir, "songs", "Legacy Song.json");
        Assert.True(File.Exists(migratedPromptPath));
        Assert.True(File.Exists(migratedSongPath));

        // 2. Verify all prompts were loaded with proper fallback handling
        var promptItem = prompts.FirstOrDefault(p => p.Name == "Legacy Loose Prompt");
        Assert.NotNull(promptItem);
        Assert.Equal("vintage loose prompt", promptItem.Positive);

        var songItem = prompts.FirstOrDefault(p => p.Name == "Legacy Song");
        Assert.NotNull(songItem);
        Assert.Equal("Rock", songItem.Category);
        Assert.Contains("80s Rock", songItem.Positive);
        Assert.Contains("[Verse 1]", songItem.Positive);
        Assert.Equal("acoustic, slow", songItem.Negative);
    }

    [Fact]
    public void GenerateFluxPromptGraph_WithMultipleStackedLoras_ChainsLoraLoaders() {
        using var client = new HttpClient();
        var comfyService = new ComfyUiService(client, _settingsService);

        var auxLoras = new List<AdditionalLoraItem> {
            new() { Name = "CyberpunkStyle.safetensors", Weight = 0.8f },
            new() { Name = "DetailEnhancer.safetensors", Weight = 0.5f }
        };

        JsonObject graph = comfyService.GenerateFluxPromptGraph(
            checkpointOrUnet: "flux1-dev.safetensors",
            loraName: "MyCharacter.safetensors",
            loraWeight: 1.0f,
            prompt: "A beautiful cinematic shot",
            additionalLoras: auxLoras
        );

        // Primary LoRA node 2
        Assert.True(graph.ContainsKey("2"));
        Assert.Equal("LoraLoader", graph["2"]?["class_type"]?.ToString());
        Assert.Equal("MyCharacter.safetensors", graph["2"]?["inputs"]?["lora_name"]?.ToString());

        // First auxiliary LoRA node 2_1
        Assert.True(graph.ContainsKey("2_1"));
        Assert.Equal("LoraLoader", graph["2_1"]?["class_type"]?.ToString());
        Assert.Equal("CyberpunkStyle.safetensors", graph["2_1"]?["inputs"]?["lora_name"]?.ToString());
        // Input model should come from primary LoRA node "2"
        var modelInputAux1 = graph["2_1"]?["inputs"]?["model"]?.AsArray();
        Assert.Equal("2", modelInputAux1?[0]?.ToString());

        // Second auxiliary LoRA node 2_2
        Assert.True(graph.ContainsKey("2_2"));
        Assert.Equal("LoraLoader", graph["2_2"]?["class_type"]?.ToString());
        Assert.Equal("DetailEnhancer.safetensors", graph["2_2"]?["inputs"]?["lora_name"]?.ToString());
        // Input model should come from first aux node "2_1"
        var modelInputAux2 = graph["2_2"]?["inputs"]?["model"]?.AsArray();
        Assert.Equal("2_1", modelInputAux2?[0]?.ToString());

        // CLIPTextEncode node 3 clip input should come from last LoRA node "2_2"
        var clipInput = graph["3"]?["inputs"]?["clip"]?.AsArray();
        Assert.Equal("2_2", clipInput?[0]?.ToString());

        // KSampler node 5 model input should come from last LoRA node "2_2"
        var ksamplerModel = graph["5"]?["inputs"]?["model"]?.AsArray();
        Assert.Equal("2_2", ksamplerModel?[0]?.ToString());
    }

    [Fact]
    public void GenerateSdxlPromptGraph_WithMultipleStackedLoras_ChainsLoraLoaders() {
        using var client = new HttpClient();
        var comfyService = new ComfyUiService(client, _settingsService);

        var auxLoras = new List<AdditionalLoraItem> {
            new() { Name = "AnimeStyle.safetensors", Weight = 0.75f }
        };

        JsonObject graph = comfyService.GenerateSdxlPromptGraph(
            checkpointName: "sd_xl_base_1.0.safetensors",
            loraName: "HeroSuit.safetensors",
            loraWeight: 0.9f,
            prompt: "Action pose in neon city",
            negativePrompt: "blurry, low quality",
            additionalLoras: auxLoras
        );

        // Primary LoRA node 2
        Assert.True(graph.ContainsKey("2"));
        // Auxiliary LoRA node 2_1
        Assert.True(graph.ContainsKey("2_1"));
        Assert.Equal("AnimeStyle.safetensors", graph["2_1"]?["inputs"]?["lora_name"]?.ToString());

        // Both positive (node 3) and negative (node 4) CLIPTextEncode should connect to "2_1"
        var posClip = graph["3"]?["inputs"]?["clip"]?.AsArray();
        var negClip = graph["4"]?["inputs"]?["clip"]?.AsArray();
        Assert.Equal("2_1", posClip?[0]?.ToString());
        Assert.Equal("2_1", negClip?[0]?.ToString());

        // KSampler node 6 should connect to "2_1"
        var ksamplerModel = graph["6"]?["inputs"]?["model"]?.AsArray();
        Assert.Equal("2_1", ksamplerModel?[0]?.ToString());
    }
}
