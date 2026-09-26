using System.Text.Json.Nodes;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class DatasetCuratorAndSurgeryTests {
    [Fact]
    public void ComfyUiService_GenerateFluxPromptGraph_GeneratesValidNodes() {
        SettingsService settings = new();
        HttpClient httpClient = new();
        ComfyUiService comfy = new(httpClient, settings);

        JsonObject graph = comfy.GenerateFluxPromptGraph(
            checkpointOrUnet: "flux1-dev.safetensors",
            loraName: "my_character.safetensors",
            loraWeight: 0.85f,
            prompt: "a photo of ohwx character in neon city",
            width: 1024,
            height: 1024,
            steps: 20
        );

        Assert.NotNull(graph);
        Assert.True(graph.ContainsKey("1"));
        Assert.True(graph.ContainsKey("2")); // LoraLoader
        Assert.True(graph.ContainsKey("3")); // CLIPTextEncode
        Assert.True(graph.ContainsKey("5")); // KSampler

        var loraNode = graph["2"]?["inputs"]?.AsObject();
        Assert.NotNull(loraNode);
        Assert.Equal("my_character.safetensors", loraNode["lora_name"]?.GetValue<string>());
        Assert.Equal(0.85f, loraNode["strength_model"]?.GetValue<float>());

        var ksamplerNode = graph["5"]?["inputs"]?.AsObject();
        Assert.NotNull(ksamplerNode);
        Assert.Equal(20, ksamplerNode["steps"]?.GetValue<int>());
    }

    [Fact]
    public void ComfyUiService_GenerateSdxlPromptGraph_GeneratesDualPromptsAndLatents() {
        SettingsService settings = new();
        HttpClient httpClient = new();
        ComfyUiService comfy = new(httpClient, settings);

        JsonObject graph = comfy.GenerateSdxlPromptGraph(
            checkpointName: "sd_xl_base_1.0.safetensors",
            loraName: "style_lora.safetensors",
            loraWeight: 1.0f,
            prompt: "cyberpunk landscape",
            negativePrompt: "blurry, low quality",
            width: 832,
            height: 1216,
            steps: 30,
            cfg: 7.5f
        );

        Assert.NotNull(graph);
        Assert.True(graph.ContainsKey("3")); // Positive CLIP
        Assert.True(graph.ContainsKey("4")); // Negative CLIP
        Assert.True(graph.ContainsKey("5")); // EmptyLatentImage
        Assert.True(graph.ContainsKey("6")); // KSampler

        var latentNode = graph["5"]?["inputs"]?.AsObject();
        Assert.NotNull(latentNode);
        Assert.Equal(832, latentNode["width"]?.GetValue<int>());
        Assert.Equal(1216, latentNode["height"]?.GetValue<int>());
    }

    [Fact]
    public void DatasetCuratorService_ParseTags_SplitsAndCleansTagsCorrectly() {
        string caption = "ohwx person, solo, looking at viewer, 1girl, vibrant lighting, ";
        var tags = DatasetCuratorService.ParseTags(caption);

        Assert.Equal(5, tags.Count);
        Assert.Equal("ohwx person", tags[0]);
        Assert.Equal("solo", tags[1]);
        Assert.Equal("looking at viewer", tags[2]);
        Assert.Equal("1girl", tags[3]);
        Assert.Equal("vibrant lighting", tags[4]);
    }

    [Fact]
    public void DatasetCuratorService_ComputeTagFrequencies_CountsOccurrencesAccurately() {
        DatasetCuratorService curator = new();
        var items = new List<DatasetCuratorItem> {
            new() { CaptionText = "ohwx, solo, 1girl" },
            new() { CaptionText = "ohwx, 1girl, portrait" },
            new() { CaptionText = "ohwx, solo, outdoor" }
        };

        var freqs = curator.ComputeTagFrequencies(items);

        Assert.NotEmpty(freqs);
        var ohwxTag = freqs.FirstOrDefault(f => f.Tag == "ohwx");
        Assert.NotNull(ohwxTag);
        Assert.Equal(3, ohwxTag.Count);
        Assert.Equal(100.0, ohwxTag.Percentage);

        var soloTag = freqs.FirstOrDefault(f => f.Tag == "solo");
        Assert.NotNull(soloTag);
        Assert.Equal(2, soloTag.Count);
    }

    [Fact]
    public void DatasetCuratorService_ComputeBucketSummaries_GroupsByAspectRatio() {
        DatasetCuratorService curator = new();
        var items = new List<DatasetCuratorItem> {
            new() { Width = 1024, Height = 1024, BucketName = "Square (1:1)", AspectRatioLabel = "1:1" },
            new() { Width = 832, Height = 1216, BucketName = "Portrait (2:3)", AspectRatioLabel = "2:3" },
            new() { Width = 832, Height = 1216, BucketName = "Portrait (2:3)", AspectRatioLabel = "2:3" },
            new() { Width = 1216, Height = 832, BucketName = "Landscape (3:2)", AspectRatioLabel = "3:2" }
        };

        var buckets = curator.ComputeBucketSummaries(items);

        Assert.Equal(3, buckets.Count);
        var portraitBucket = buckets.FirstOrDefault(b => b.BucketName == "Portrait (2:3)");
        Assert.NotNull(portraitBucket);
        Assert.Equal(2, portraitBucket.Count);
        Assert.Equal(50.0, portraitBucket.Percentage);
    }

    [Fact]
    public async Task LoraSurgeryService_ResizeLoraAsync_RejectsInvalidRank() {
        ProcessRunner runner = new();
        LoraSurgeryService surgery = new(runner);

        var result = await surgery.ResizeLoraAsync("dummy.safetensors", "out.safetensors", -5);
        Assert.False(result.Success);
        Assert.Contains("Invalid target rank", result.Message);
    }
}
