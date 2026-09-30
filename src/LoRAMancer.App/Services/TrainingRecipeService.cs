using System.Text;
using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

/// <summary>
/// Manages user-saved favorite and custom training recipes stored as individual JSON files in ~/.loramancer/training_recipes/.
/// </summary>
public sealed class TrainingRecipeService {
    private readonly JsonSerializerOptions _jsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string GetRecipeDirectory() {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "training_recipes");
        if (!Directory.Exists(dir)) {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    /// <summary>
    /// Retrieves all user-saved recipes and built-in seeded presets, sorted with favorites first.
    /// </summary>
    public async Task<List<TrainingRecipe>> GetAllRecipesAsync(CancellationToken cancellationToken = default) {
        string dir = GetRecipeDirectory();
        var recipes = new List<TrainingRecipe>();

        string[] files = Directory.GetFiles(dir, "*.json");
        if (files.Length == 0) {
            await SeedDefaultRecipesAsync(cancellationToken);
            files = Directory.GetFiles(dir, "*.json");
        }

        foreach (string file in files) {
            try {
                string json = await File.ReadAllTextAsync(file, cancellationToken);
                var recipe = JsonSerializer.Deserialize<TrainingRecipe>(json, _jsonOptions);
                if (recipe != null) {
                    if (string.IsNullOrWhiteSpace(recipe.Name)) {
                        recipe.Name = Path.GetFileNameWithoutExtension(file);
                    }
                    recipes.Add(recipe);
                }
            } catch {
                // Ignore corrupted or unreadable recipe files
            }
        }

        return recipes
            .OrderByDescending(r => r.IsFavorite)
            .ThenBy(r => r.Name)
            .ToList();
    }

    /// <summary>
    /// Creates a TrainingRecipe from an existing LoRA's extracted metadata, omitting dataset paths, output paths, and original model identity.
    /// </summary>
    public TrainingRecipe CreateRecipeFromLora(LoraMetadata lora, string? customRecipeName = null) {
        string baseName = !string.IsNullOrWhiteSpace(lora.CivitaiInfo?.ModelName)
            ? lora.CivitaiInfo.ModelName
            : !string.IsNullOrWhiteSpace(lora.FileName)
                ? Path.GetFileNameWithoutExtension(lora.FileName)
                : "LoRA";

        string name = !string.IsNullOrWhiteSpace(customRecipeName)
            ? customRecipeName.Trim()
            : $"{baseName} Recipe";

        string targetBaseModel = !string.IsNullOrWhiteSpace(lora.EffectiveBaseModel) && lora.EffectiveBaseModel != "Unknown"
            ? lora.EffectiveBaseModel
            : "FLUX.1 Dev";

        string optimizer = !string.IsNullOrWhiteSpace(lora.Optimizer)
            ? lora.Optimizer.ToLowerInvariant()
            : "adamw";

        int? clipSkip = null;
        if (targetBaseModel.Contains("Pony", StringComparison.OrdinalIgnoreCase) || targetBaseModel.Contains("Illustrious", StringComparison.OrdinalIgnoreCase)) {
            clipSkip = 2;
        }

        return new TrainingRecipe {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = $"Extracted recipe from donor LoRA {lora.FileName ?? "model"} (Rank {lora.NetworkDim ?? 16}, Alpha {lora.NetworkAlpha ?? 16.0}).",
            TargetBaseModel = targetBaseModel,
            SubjectType = "Custom",
            NetworkDim = lora.NetworkDim ?? 16,
            NetworkAlpha = lora.NetworkAlpha ?? 16.0,
            LearningRate = lora.LearningRate ?? 1e-4,
            UnetLearningRate = lora.UnetLearningRate,
            TextEncoderLearningRate = lora.TextEncoderLearningRate,
            Optimizer = optimizer,
            LrScheduler = !string.IsNullOrWhiteSpace(lora.LrScheduler) ? lora.LrScheduler : "cosine_with_restarts",
            Precision = !string.IsNullOrWhiteSpace(lora.Precision) ? lora.Precision : "bf16",
            Epochs = lora.Epochs ?? 10,
            Repeats = 10,
            BatchSize = 1,
            TotalSteps = lora.TotalSteps,
            SaveEveryNEpochs = 1,
            FlipAug = false,
            ShuffleTokens = clipSkip.HasValue,
            KeepTokens = 1,
            ClipSkip = clipSkip,
            SamplePrompt1 = "a photo of {trigger}, cinematic lighting, highly detailed",
            SamplePrompt2 = "close up portrait of {trigger}, studio lighting",
            NegativePrompt = "blurry, low quality, distorted, bad anatomy",
            IsFavorite = true,
            IsBuiltIn = false,
            Tags = new List<string> { targetBaseModel, "Donor" },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Saves a training recipe as a JSON file in the recipes directory.
    /// </summary>
    public async Task<string> SaveRecipeAsync(TrainingRecipe recipe, CancellationToken cancellationToken = default) {
        string dir = GetRecipeDirectory();
        if (string.IsNullOrWhiteSpace(recipe.Id)) {
            recipe.Id = Guid.NewGuid().ToString("N");
        }
        if (string.IsNullOrWhiteSpace(recipe.Name)) {
            recipe.Name = $"Recipe_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
        recipe.CreatedAtUtc = DateTime.UtcNow;

        string sanitizedName = recipe.Name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) {
            sanitizedName = sanitizedName.Replace(c, '_');
        }

        string filePath = Path.Combine(dir, $"{sanitizedName}.json");
        string json = JsonSerializer.Serialize(recipe, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, cancellationToken);
        return filePath;
    }

    /// <summary>
    /// Deletes a recipe by Id or file name.
    /// </summary>
    public async Task<bool> DeleteRecipeAsync(string idOrName, CancellationToken cancellationToken = default) {
        string dir = GetRecipeDirectory();
        if (!Directory.Exists(dir)) {
            return false;
        }

        string[] files = Directory.GetFiles(dir, "*.json");
        foreach (string file in files) {
            try {
                string json = await File.ReadAllTextAsync(file, cancellationToken);
                var r = JsonSerializer.Deserialize<TrainingRecipe>(json, _jsonOptions);
                if (r != null && (string.Equals(r.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(r.Name, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(Path.GetFileNameWithoutExtension(file), idOrName, StringComparison.OrdinalIgnoreCase))) {
                    File.Delete(file);
                    return true;
                }
            } catch { }
        }
        return false;
    }

    /// <summary>
    /// Toggles the favorite flag on a recipe and saves it back to disk.
    /// </summary>
    public async Task<bool> ToggleFavoriteAsync(string recipeId, CancellationToken cancellationToken = default) {
        string dir = GetRecipeDirectory();
        if (!Directory.Exists(dir)) {
            return false;
        }

        string[] files = Directory.GetFiles(dir, "*.json");
        foreach (string file in files) {
            try {
                string json = await File.ReadAllTextAsync(file, cancellationToken);
                var r = JsonSerializer.Deserialize<TrainingRecipe>(json, _jsonOptions);
                if (r != null && string.Equals(r.Id, recipeId, StringComparison.OrdinalIgnoreCase)) {
                    r.IsFavorite = !r.IsFavorite;
                    string updatedJson = JsonSerializer.Serialize(r, _jsonOptions);
                    await File.WriteAllTextAsync(file, updatedJson, Encoding.UTF8, cancellationToken);
                    return true;
                }
            } catch { }
        }
        return false;
    }

    /// <summary>
    /// Seeds recommended starter recipes into the user directory if none exist.
    /// </summary>
    public async Task SeedDefaultRecipesAsync(CancellationToken cancellationToken = default) {
        var defaults = GetDefaultRecipes();
        foreach (var recipe in defaults) {
            await SaveRecipeAsync(recipe, cancellationToken);
        }
    }

    public static List<TrainingRecipe> GetDefaultRecipes() {
        return new List<TrainingRecipe> {
            new() {
                Id = "flux_character_gold",
                Name = "FLUX.1 Dev - High Detail Character (Rank 16)",
                Description = "Balanced low-rank decomposition with 1e-4 LR and cosine schedule for high-fidelity character likeness.",
                TargetBaseModel = "FLUX.1 Dev",
                SubjectType = "Character",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of {trigger}, cinematic lighting, 8k resolution, highly detailed",
                SamplePrompt2 = "close up portrait of {trigger}, studio lighting, highly detailed",
                NegativePrompt = "blurry, low quality, distorted, bad anatomy",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "FLUX", "Character", "Recommended" }
            },
            new() {
                Id = "sdxl_artistic_style",
                Name = "SDXL 1.0 - Artistic Style & Texture (Rank 32)",
                Description = "Expanded rank 32 capacity tailored for capturing artistic brush strokes, palettes, and compositions.",
                TargetBaseModel = "SDXL 1.0",
                SubjectType = "Style",
                NetworkDim = 32,
                NetworkAlpha = 32.0,
                LearningRate = 1e-4,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 12,
                Repeats = 8,
                BatchSize = 1,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "artwork in the style of {trigger}, masterpiece, fine details",
                SamplePrompt2 = "a landscape painting in {trigger} style, vivid color palette",
                NegativePrompt = "photo, realistic, 3d render, watermark, text",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SDXL", "Style", "High-Rank" }
            },
            new() {
                Id = "pony_anime_character",
                Name = "Pony Diffusion V6 - Booru Anime (CLIP Skip 2)",
                Description = "Optimized for booru tag shuffling with CLIP Skip 2 and AdamW for expressive anime/illustrative characters.",
                TargetBaseModel = "Pony Diffusion V6 XL",
                SubjectType = "Character",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                ClipSkip = 2,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "score_9, score_8_up, 1girl, {trigger}, solo, expressive eyes, dynamic pose",
                SamplePrompt2 = "score_9, score_8_up, portrait of {trigger}, looking at viewer, detailed face",
                NegativePrompt = "score_4, score_5, score_6, source_pony, simple background, ugly, bad hands, mutated fingers",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Pony", "Anime", "Booru" }
            },
            new() {
                Id = "chroma_photo_concept",
                Name = "Chroma - Photorealistic Object / Concept",
                Description = "Precision settings for standalone Chroma diffusion models with clean token weighting.",
                TargetBaseModel = "Chroma",
                SubjectType = "Concept",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 8e-5,
                Optimizer = "adamw",
                LrScheduler = "cosine",
                Precision = "bf16",
                Epochs = 8,
                Repeats = 10,
                BatchSize = 1,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "a clean commercial product photograph of {trigger}, clean studio background, sharp focus",
                SamplePrompt2 = "{trigger} standing in a modern showroom, natural daylight",
                NegativePrompt = "blurry, out of focus, low contrast, artifact, noise",
                IsFavorite = false,
                IsBuiltIn = true,
                Tags = new() { "Chroma", "Concept", "Photorealism" }
            }
        };
    }
}
