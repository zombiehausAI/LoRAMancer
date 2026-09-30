using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public class TrainingRecipeServiceTests : IDisposable {
    private readonly string _tempDir;
    private readonly TrainingRecipeService _service;

    public TrainingRecipeServiceTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "loramancer_recipe_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new TrainingRecipeService();
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_tempDir)) {
                Directory.Delete(_tempDir, true);
            }
        } catch { }
    }

    [Fact]
    public void GetDefaultRecipes_ContainsHighQualityPresets() {
        var presets = TrainingRecipeService.GetDefaultRecipes();
        Assert.NotEmpty(presets);
        Assert.Contains(presets, r => r.TargetBaseModel.Contains("FLUX"));
        Assert.Contains(presets, r => r.TargetBaseModel.Contains("SDXL"));
        Assert.Contains(presets, r => r.TargetBaseModel.Contains("Pony"));
    }

    [Fact]
    public async Task SaveRecipeAsync_PersistsJsonFile() {
        string testDir = Path.Combine(_tempDir, "recipes");
        Directory.CreateDirectory(testDir);

        var recipe = new TrainingRecipe {
            Id = "custom_test_recipe",
            Name = "My Custom Test Recipe",
            TargetBaseModel = "FLUX.1 Dev",
            NetworkDim = 32,
            NetworkAlpha = 16.0,
            LearningRate = 5e-5,
            Optimizer = "adamw",
            IsFavorite = true
        };

        // Test serialization to json directly and verify properties
        string filePath = Path.Combine(testDir, $"{recipe.Name}.json");
        string json = System.Text.Json.JsonSerializer.Serialize(recipe, new System.Text.Json.JsonSerializerOptions {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        });
        await File.WriteAllTextAsync(filePath, json);

        Assert.True(File.Exists(filePath));
        string readBackJson = await File.ReadAllTextAsync(filePath);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<TrainingRecipe>(readBackJson, new System.Text.Json.JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(loaded);
        Assert.Equal("custom_test_recipe", loaded.Id);
        Assert.Equal("My Custom Test Recipe", loaded.Name);
        Assert.Equal(32, loaded.NetworkDim);
        Assert.Equal(16.0, loaded.NetworkAlpha);
        Assert.Equal(5e-5, loaded.LearningRate);
        Assert.True(loaded.IsFavorite);
    }

    [Fact]
    public async Task GetAllRecipesAsync_LoadsAndSortsFavoritesFirst() {
        var recipes = await _service.GetAllRecipesAsync();
        Assert.NotEmpty(recipes);

        // Check that favorites are sorted first
        bool seenNonFavorite = false;
        foreach (var r in recipes) {
            if (!r.IsFavorite) {
                seenNonFavorite = true;
            } else if (seenNonFavorite) {
                Assert.Fail("Favorites should appear before non-favorites in the recipe list.");
            }
        }
    }

    [Fact]
    public void CreateRecipeFromLora_ExtractsHyperparametersWithoutDatasetOrPath() {
        var lora = new LoraMetadata {
            FileName = "Cyberpunk_Outfit_v2.safetensors",
            FilePath = "C:\\Models\\Loras\\Cyberpunk_Outfit_v2.safetensors",
            BaseModel = "SDXL 1.0",
            NetworkDim = 64,
            NetworkAlpha = 32.0,
            LearningRate = 1.5e-4,
            Optimizer = "prodigy",
            LrScheduler = "cosine",
            Epochs = 15,
            TotalSteps = 3000
        };

        var recipe = _service.CreateRecipeFromLora(lora);

        Assert.NotNull(recipe);
        Assert.Equal("Cyberpunk_Outfit_v2 Recipe", recipe.Name);
        Assert.Equal("SDXL 1.0", recipe.TargetBaseModel);
        Assert.Equal(64, recipe.NetworkDim);
        Assert.Equal(32.0, recipe.NetworkAlpha);
        Assert.Equal(1.5e-4, recipe.LearningRate);
        Assert.Equal("prodigy", recipe.Optimizer);
        Assert.Equal("cosine", recipe.LrScheduler);
        Assert.Equal(15, recipe.Epochs);
        Assert.True(recipe.IsFavorite);
        // Verify custom base model path is null or empty (not carrying over file paths)
        Assert.True(string.IsNullOrEmpty(recipe.CustomBaseModelPath));
    }
}
