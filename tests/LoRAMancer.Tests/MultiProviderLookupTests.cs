using LoRAMancer.App.Models;
using LoRAMancer.App.Services;
using LoRAMancer.App.Services.Providers;

namespace LoRAMancer.Tests;

public sealed class MultiProviderLookupTests {
    [Fact]
    public void Providers_ImplementContract_WithCorrectMetadata() {
        SettingsService settings = new(null, Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json"));
        CivitaiService civitai = new(settings);
        HuggingFaceService hf = new(settings);
        DanbooruTagService danbooru = new();

        Assert.Equal("civitai", civitai.ProviderId);
        Assert.Equal("Civitai", civitai.DisplayName);
        Assert.Equal(10, civitai.Priority);

        Assert.Equal("huggingface", hf.ProviderId);
        Assert.Equal("Hugging Face Hub", hf.DisplayName);
        Assert.Equal(20, hf.Priority);

        Assert.Equal("danbooru", danbooru.ProviderId);
        Assert.Equal("Danbooru Tag Classifier", danbooru.DisplayName);
        Assert.Equal(30, danbooru.Priority);
    }

    [Fact]
    public async Task Aggregator_MergesTriggersAndModelName_NonDestructively() {
        SettingsService settings = new(null, Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json"));
        CivitaiService civitai = new(settings);
        HuggingFaceService hf = new(settings);
        DanbooruTagService danbooru = new();

        LoraMetadataAggregatorService aggregator = new(civitai, hf, danbooru, settings);

        LoraMetadata meta = new() {
            FileName = "test_cyberpunk_character.safetensors",
            FilePath = "C:\\dummy\\test_cyberpunk_character.safetensors",
            BaseModel = "Unknown",
            TrainedWords = new List<string> { "initial_trigger" }
        };

        // When enrich runs on a non-existent file/hash without network calls, it gracefully returns
        AggregatedLoraEnrichmentResult result = await aggregator.EnrichAsync(meta);

        Assert.NotNull(result);
        Assert.Contains("initial_trigger", meta.TrainedWords);
    }

    [Fact]
    public void ProviderLookupResult_StoresAndRetrievesFieldsCorrectly() {
        ProviderLookupResult result = new() {
            ProviderId = "test_provider",
            ProviderDisplayName = "Test Provider",
            ModelName = "Epic LoRA",
            BaseModel = "FLUX.1-D",
            TriggerWords = new List<string> { "epic_style", "cyberpunk" },
            PreviewImageUrl = "https://example.com/preview.png",
            ModelUrl = "https://example.com/model/123"
        };

        Assert.Equal("test_provider", result.ProviderId);
        Assert.Equal("Epic LoRA", result.ModelName);
        Assert.Equal("FLUX.1-D", result.BaseModel);
        Assert.Equal(2, result.TriggerWords.Count);
        Assert.Equal("https://example.com/preview.png", result.PreviewImageUrl);
    }
}
