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
        var defaults = GetDefaultRecipes();
        bool anyMissing = false;
        foreach (var def in defaults) {
            string defPath = Path.Combine(dir, $"{def.Id}.json");
            if (!File.Exists(defPath)) {
                await SaveRecipeAsync(def, cancellationToken);
                anyMissing = true;
            } else if (def.IsBuiltIn) {
                try {
                    string existingJson = await File.ReadAllTextAsync(defPath, cancellationToken);
                    var existing = JsonSerializer.Deserialize<TrainingRecipe>(existingJson, _jsonOptions);
                    if (existing != null && existing.IsBuiltIn) {
                        def.IsFavorite = existing.IsFavorite;
                        await SaveRecipeAsync(def, cancellationToken);
                    }
                } catch { }
            }
        }
        if (anyMissing || files.Length == 0) {
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
                Id = "flux_character_likeness",
                Name = "FLUX.1 Dev - Likeness & Manipulability (Rank 4)",
                Description = "Ultra-lean Rank 4 / Alpha 16 setup with 2e-4 LR, 1,200 steps, and frozen text encoders for flawless facial likeness and complete prompt/scene steerability.",
                TargetBaseModel = "FLUX.1 Dev",
                SubjectType = "Character",
                NetworkDim = 4,
                NetworkAlpha = 16.0,
                LearningRate = 2e-4,
                UnetLearningRate = 2e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "photo of {trigger}, looking at the camera, natural daylight, highly detailed, sharp focus",
                SamplePrompt2 = "cinematic close-up portrait of {trigger}, dynamic pose, 8k resolution",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "FLUX", "Character", "Likeness", "Recommended" }
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
            },
            new() {
                Id = "chroma_character_likeness",
                Name = "Chroma - Character & Likeness Preserving (Rank 2)",
                Description = "Ultra-low Rank 2 / Alpha 16 setup with 5e-4 LR and cosine restarts for maximum face likeness and high pose/scene manipulability without background bleed.",
                TargetBaseModel = "Chroma",
                SubjectType = "Character",
                NetworkDim = 2,
                NetworkAlpha = 16.0,
                LearningRate = 5e-4,
                UnetLearningRate = 5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "photo of {trigger}, looking at viewer, studio lighting, highly detailed",
                SamplePrompt2 = "cinematic portrait of {trigger}, dynamic pose, sharp focus, 8k",
                NegativePrompt = "blurry, out of focus, low quality, deformed, disfigured",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Chroma", "Character", "Likeness", "Recommended" }
            },
            new() {
                Id = "pony_character_likeness",
                Name = "Pony Diffusion V6 - Likeness & Manipulability (Rank 4)",
                Description = "Ultra-lean Rank 4 / Alpha 16 setup with frozen text encoders and 1,200 steps for flawless likeness and prompt manipulability without pose/style stiffness.",
                TargetBaseModel = "Pony Diffusion V6 XL",
                SubjectType = "Character",
                NetworkDim = 4,
                NetworkAlpha = 16.0,
                LearningRate = 2e-4,
                UnetLearningRate = 2e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "score_9, score_8_up, score_7_up, source_pony, 1girl, {trigger}, solo, expressive eyes, dynamic pose, looking at viewer",
                SamplePrompt2 = "score_9, score_8_up, score_7_up, source_pony, portrait of {trigger}, cinematic lighting, masterpiece",
                NegativePrompt = "score_4, score_5, score_6, source_pony, simple background, ugly, bad hands, mutated fingers, blurry",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Pony", "Character", "Likeness", "Recommended" }
            },
            new() {
                Id = "illustrious_character_likeness",
                Name = "Illustrious-XL - Likeness & Manipulability (Rank 4)",
                Description = "Ultra-lean Rank 4 / Alpha 16 setup with frozen text encoders and 1,200 steps for crisp anime/illustrative likeness without style locking.",
                TargetBaseModel = "Illustrious-XL",
                SubjectType = "Character",
                NetworkDim = 4,
                NetworkAlpha = 16.0,
                LearningRate = 2e-4,
                UnetLearningRate = 2e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "masterpiece, newest, 1girl, {trigger}, solo, expressive eyes, dynamic pose, looking at viewer",
                SamplePrompt2 = "masterpiece, 1girl, portrait of {trigger}, dynamic lighting, masterpiece, sharp eyes",
                NegativePrompt = "worst quality, low quality, bad anatomy, bad hands, blurry, distorted",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Illustrious", "Character", "Likeness", "Anime", "Recommended" }
            },
            new() {
                Id = "sdxl_character_likeness",
                Name = "SDXL 1.0 - Likeness & Manipulability (Rank 4)",
                Description = "Ultra-lean Rank 4 / Alpha 16 setup with frozen dual text encoders and 1,200 steps for realistic likeness and full prompt/scene flexibility.",
                TargetBaseModel = "SDXL 1.0",
                SubjectType = "Character",
                NetworkDim = 4,
                NetworkAlpha = 16.0,
                LearningRate = 2e-4,
                UnetLearningRate = 2e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of {trigger}, looking at the camera, natural daylight, highly detailed, 8k resolution",
                SamplePrompt2 = "cinematic close-up portrait of {trigger}, dynamic pose, sharp focus, dramatic lighting",
                NegativePrompt = "blurry, low quality, distorted, deformed, bad anatomy, worst quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SDXL", "Character", "Likeness", "Photorealism", "Recommended" }
            },
            new() {
                Id = "sd15_character_likeness",
                Name = "Stable Diffusion 1.5 - Likeness & Manipulability (Rank 4)",
                Description = "Ultra-lean Rank 4 / Alpha 16 setup with frozen text encoder and 1,200 steps for clean facial likeness and maximum pose steerability.",
                TargetBaseModel = "SD 1.5",
                SubjectType = "Character",
                NetworkDim = 4,
                NetworkAlpha = 16.0,
                LearningRate = 2e-4,
                UnetLearningRate = 2e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of {trigger}, looking at viewer, studio lighting, highly detailed, sharp focus",
                SamplePrompt2 = "close up portrait of {trigger}, cinematic lighting, 4k, masterpiece",
                NegativePrompt = "blurry, low quality, distorted, deformed, bad anatomy, worst quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD1.5", "Character", "Likeness", "Recommended" }
            },
            new() {
                Id = "sd35_character_likeness",
                Name = "Stable Diffusion 3.5 - Likeness & Manipulability (Rank 4)",
                Description = "Ultra-lean Rank 4 / Alpha 16 MMDiT setup with frozen triple text encoders (T5/CLIP) and 1,200 steps for pristine character likeness and prompt compliance.",
                TargetBaseModel = "SD 3.5",
                SubjectType = "Character",
                NetworkDim = 4,
                NetworkAlpha = 16.0,
                LearningRate = 2e-4,
                UnetLearningRate = 2e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 1200,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of {trigger}, looking at the camera, natural lighting, highly detailed, sharp focus",
                SamplePrompt2 = "cinematic close-up portrait of {trigger}, dynamic pose, 8k resolution, professional photography",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD3.5", "Character", "Likeness", "MMDiT", "Recommended" }
            },
            new() {
                Id = "chroma_clothing_isolation",
                Name = "Chroma - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 setup with frozen text encoders, token shuffling, and 950 steps to capture sheer fabrics, hosiery, and garments without baking in background scenes or body morphology.",
                TargetBaseModel = "Chroma",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of a woman wearing {trigger}, standing on a street, full body, natural daylight, sharp focus",
                SamplePrompt2 = "close-up of legs wearing {trigger}, high heels, clean studio background, detailed sheer fabric texture",
                NegativePrompt = "blurry, out of focus, low quality, deformed, disfigured",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Chroma", "Clothing", "Attire", "Zero-Bleed", "Recommended" }
            },
            new() {
                Id = "pony_clothing_isolation",
                Name = "Pony Diffusion V6 - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 setup with frozen text encoders, CLIP Skip 2, token shuffling, and 950 steps for pristine garment/hosiery isolation without background bleed.",
                TargetBaseModel = "Pony Diffusion V6 XL",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "score_9, score_8_up, score_7_up, source_pony, 1girl, wearing {trigger}, standing, dynamic pose, highly detailed",
                SamplePrompt2 = "score_9, score_8_up, score_7_up, source_pony, lower body, legs, wearing {trigger}, high heels, masterpiece, textured fabric",
                NegativePrompt = "score_4, score_5, score_6, source_pony, simple background, ugly, bad hands, mutated fingers, blurry",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Pony", "Clothing", "Attire", "Zero-Bleed", "Recommended" }
            },
            new() {
                Id = "flux_clothing_isolation",
                Name = "FLUX.1 Dev - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 DiT setup with frozen dual text encoders (T5/CLIP) and 950 steps for exact fabric texture and sheerness transfer without background locking.",
                TargetBaseModel = "FLUX.1 Dev",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of a woman wearing {trigger}, walking down a city sidewalk, full body shot, daylight, sharp focus",
                SamplePrompt2 = "detailed close-up of legs wearing {trigger}, stylish footwear, neutral background, 8k resolution, crisp texture",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "FLUX", "Clothing", "Attire", "Zero-Bleed", "Recommended" }
            },
            new() {
                Id = "illustrious_clothing_isolation",
                Name = "Illustrious-XL - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 setup with frozen text encoders, CLIP Skip 2, token shuffling, and 950 steps for clean anime/illustrative outfit isolation.",
                TargetBaseModel = "Illustrious-XL",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "masterpiece, newest, 1girl, wearing {trigger}, full body, dynamic pose, looking at viewer, highly detailed",
                SamplePrompt2 = "masterpiece, 1girl, legs, lower body, wearing {trigger}, high heels, sharp focus, aesthetic, clean shading",
                NegativePrompt = "worst quality, low quality, bad anatomy, bad hands, blurry, distorted",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Illustrious", "Clothing", "Attire", "Zero-Bleed", "Anime", "Recommended" }
            },
            new() {
                Id = "sdxl_clothing_isolation",
                Name = "SDXL 1.0 - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 setup with frozen dual text encoders, token shuffling, and 950 steps for realistic outfit, sheer fabric, and hosiery application.",
                TargetBaseModel = "SDXL 1.0",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of a woman wearing {trigger}, standing in a modern space, full body shot, natural sunlight, 8k resolution",
                SamplePrompt2 = "close-up shot of legs wearing {trigger}, elegant shoes, neutral background, detailed fabric texture, sharp focus",
                NegativePrompt = "blurry, low quality, distorted, deformed, bad anatomy, worst quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SDXL", "Clothing", "Attire", "Zero-Bleed", "Recommended" }
            },
            new() {
                Id = "sd15_clothing_isolation",
                Name = "Stable Diffusion 1.5 - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 setup with frozen text encoder, token shuffling, and 950 steps for clean outfit and hosiery training without background bleed.",
                TargetBaseModel = "SD 1.5",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "photo of a woman wearing {trigger}, standing, full body, natural daylight, highly detailed",
                SamplePrompt2 = "close-up of legs wearing {trigger}, high heels, studio background, sharp focus, 4k",
                NegativePrompt = "blurry, low quality, distorted, deformed, bad anatomy, worst quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD1.5", "Clothing", "Attire", "Zero-Bleed", "Recommended" }
            },
            new() {
                Id = "sd35_clothing_isolation",
                Name = "Stable Diffusion 3.5 - Clothing & Attire Isolation (Rank 4 / Zero-Bleed)",
                Description = "Rank 4 / Alpha 4 MMDiT setup with frozen triple text encoders and 950 steps for garment and fabric material isolation without background bleed.",
                TargetBaseModel = "SD 3.5",
                SubjectType = "Clothing",
                NetworkDim = 4,
                NetworkAlpha = 4.0,
                LearningRate = 1.5e-4,
                UnetLearningRate = 1.5e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 10,
                BatchSize = 1,
                TotalSteps = 950,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a photo of a woman wearing {trigger}, standing on a city street, full body, natural lighting, sharp focus",
                SamplePrompt2 = "cinematic close-up of legs wearing {trigger}, high heels, clean studio backdrop, 8k resolution, intricate fabric weave",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD3.5", "Clothing", "Attire", "Zero-Bleed", "MMDiT", "Recommended" }
            },
            new() {
                Id = "chroma_environment_theme",
                Name = "Chroma - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Expanded Rank 32 / Alpha 16 setup with frozen text encoders and 2,000 steps designed for multi-concept game worlds, architecture, spaceships, and planetary environments without concept bleed.",
                TargetBaseModel = "Chroma",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "{trigger}, sc_cockpit, interior of spaceship cockpit, dual flight sticks, holographic display telemetry, starfield through canopy window",
                SamplePrompt2 = "{trigger}, sc_station, massive orbital space station, docking ring, solar panels, planetary atmosphere, 8k resolution",
                NegativePrompt = "blurry, low quality, deformed, distorted, artifact",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Chroma", "Environment", "GameWorld", "Theme", "Multi-Concept", "Recommended" }
            },
            new() {
                Id = "pony_environment_theme",
                Name = "Pony Diffusion V6 - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Rank 32 / Alpha 16 setup with CLIP Skip 2, gentle text encoder training, and 2,000 steps for separating distinct sci-fi locations, ship interiors, and game aesthetics.",
                TargetBaseModel = "Pony Diffusion V6 XL",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 4e-5,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "score_9, score_8_up, score_7_up, source_pony, {trigger}, sc_cockpit, interior of futuristic spaceship cockpit, glowing control consoles, detailed starfield",
                SamplePrompt2 = "score_9, score_8_up, score_7_up, source_pony, {trigger}, sc_station, giant orbital station, docking gantry, space background, masterpiece",
                NegativePrompt = "score_4, score_5, score_6, source_pony, simple background, blurry, low quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Pony", "Environment", "GameWorld", "Theme", "Multi-Concept", "Recommended" }
            },
            new() {
                Id = "flux_environment_theme",
                Name = "FLUX.1 Dev - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Expanded Rank 32 / Alpha 16 DiT setup with frozen dual text encoders and 2,000 steps for complex hard-surface sci-fi worlds, spaceship cockpits, hangars, and stations.",
                TargetBaseModel = "FLUX.1 Dev",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "a cinematic photograph of {trigger}, sc_cockpit, inside the cockpit of a heavy spacecraft, glowing green avionics, starfield through cockpit canopy, sharp focus",
                SamplePrompt2 = "a detailed wide shot of {trigger}, sc_station, massive space station exterior orbiting a gas giant, docking clamps, solar panels, cinematic lighting, 8k",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "FLUX", "Environment", "GameWorld", "Theme", "Multi-Concept", "Recommended" }
            },
            new() {
                Id = "illustrious_environment_theme",
                Name = "Illustrious-XL - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Rank 32 / Alpha 16 setup with CLIP Skip 2, gentle text encoder training, and 2,000 steps for crisp anime/illustrative sci-fi game worlds, ships, and outposts.",
                TargetBaseModel = "Illustrious-XL",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 4e-5,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "masterpiece, newest, {trigger}, sc_cockpit, interior of sci-fi pilot cockpit, glowing holographic HUD, starfield, high-tech controls, sharp focus",
                SamplePrompt2 = "masterpiece, newest, {trigger}, sc_station, exterior of massive orbital station, mechanical detail, docking bay, planetary horizon, wallpaper",
                NegativePrompt = "worst quality, low quality, blurry, distorted, monochrome",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Illustrious", "Environment", "GameWorld", "Theme", "Multi-Concept", "Anime", "Recommended" }
            },
            new() {
                Id = "sdxl_environment_theme",
                Name = "SDXL 1.0 - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Rank 32 / Alpha 16 setup with gentle dual text encoder training and 2,000 steps for photorealistic game environments, cockpits, stations, and planetary outposts.",
                TargetBaseModel = "SDXL 1.0",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 4e-5,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "a photo of {trigger}, sc_cockpit, cockpit interior of a spaceship, pilot seat, glowing holographic telemetry screens, dark space visible through windshield, 8k",
                SamplePrompt2 = "a wide shot of {trigger}, sc_station, giant orbital docking station in space, detailed titanium hull, solar arrays, cinematic sci-fi lighting",
                NegativePrompt = "blurry, low quality, distorted, deformed, worst quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SDXL", "Environment", "GameWorld", "Theme", "Multi-Concept", "Recommended" }
            },
            new() {
                Id = "sd15_environment_theme",
                Name = "Stable Diffusion 1.5 - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Rank 32 / Alpha 16 setup with text encoder training and 2,000 steps for classic UNet multi-concept game environments and sci-fi hardware.",
                TargetBaseModel = "SD 1.5",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 4e-5,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "photo of {trigger}, sc_cockpit, interior of a spaceship cockpit, control panels, holographic radar, space background, sharp focus, 4k",
                SamplePrompt2 = "wide cinematic shot of {trigger}, sc_station, massive space station orbiting a planet, detailed metal hull, dramatic lighting",
                NegativePrompt = "blurry, low quality, distorted, worst quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD1.5", "Environment", "GameWorld", "Theme", "Multi-Concept", "Recommended" }
            },
            new() {
                Id = "sd35_environment_theme",
                Name = "Stable Diffusion 3.5 - Environment & Game World (Rank 32 / Multi-Concept)",
                Description = "Rank 32 / Alpha 16 MMDiT setup with frozen triple text encoders and 2,000 steps for large-scale multi-concept game environments, space stations, and ship interiors.",
                TargetBaseModel = "SD 3.5",
                SubjectType = "Concept",
                NetworkDim = 32,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 2000,
                FlipAug = true,
                ShuffleTokens = false,
                KeepTokens = 2,
                SamplePrompt1 = "a photo of {trigger}, sc_cockpit, inside the cockpit of an advanced spaceship, glowing instrument panel, vast starfield, highly detailed, sharp focus",
                SamplePrompt2 = "cinematic wide shot of {trigger}, sc_station, colossal orbital station docking hub, solar panels, earth orbit, 8k resolution, photorealistic",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD3.5", "Environment", "GameWorld", "Theme", "Multi-Concept", "MMDiT", "Recommended" }
            },
            new() {
                Id = "chroma_artistic_style",
                Name = "Chroma - Artistic Style & Technique (Rank 16)",
                Description = "Rank 16 / Alpha 16 setup with frozen text encoders, token shuffling, and 1,500 steps for transferring distinct painterly techniques, brushwork, and color palettes onto any prompt.",
                TargetBaseModel = "Chroma",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "{trigger}, a vintage sports car driving through a misty coastal road at sunrise, detailed brushwork",
                SamplePrompt2 = "{trigger}, portrait of an elderly sea captain with a beard, intense gaze, atmospheric lighting",
                NegativePrompt = "blurry, low quality, deformed, distorted, artifact",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Chroma", "Style", "Artistic", "Technique", "Recommended" }
            },
            new() {
                Id = "pony_artistic_style",
                Name = "Pony Diffusion V6 - Artistic Style & Aesthetic (Rank 16)",
                Description = "Rank 16 / Alpha 16 setup with CLIP Skip 2, token shuffling, and 1,500 steps for capturing expressive anime, manga, and illustrative aesthetics without subject locking.",
                TargetBaseModel = "Pony Diffusion V6 XL",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "score_9, score_8_up, score_7_up, source_pony, {trigger}, 1girl, standing in a sunflower field, wind blowing hair, vibrant colors, aesthetic",
                SamplePrompt2 = "score_9, score_8_up, score_7_up, source_pony, {trigger}, fantasy dragon perched on a gothic cathedral spire, moonlit night, masterpiece",
                NegativePrompt = "score_4, score_5, score_6, source_pony, simple background, ugly, blurry, low quality",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Pony", "Style", "Aesthetic", "Anime", "Recommended" }
            },
            new() {
                Id = "flux_artistic_style",
                Name = "FLUX.1 Dev - Artistic Style & Technique (Rank 16)",
                Description = "Rank 16 / Alpha 16 DiT setup with frozen dual text encoders, token shuffling, and 1,500 steps for clean artistic style, medium, and aesthetic transfer without content bleed.",
                TargetBaseModel = "FLUX.1 Dev",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a painting in {trigger} style, an astronaut floating above a neon cityscape, intricate brushstrokes, atmospheric lighting",
                SamplePrompt2 = "artwork in {trigger} style, a lone cabin in the snowy woods, warm glowing windows, painterly aesthetic, 8k",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "FLUX", "Style", "Artistic", "Technique", "Recommended" }
            },
            new() {
                Id = "illustrious_artistic_style",
                Name = "Illustrious-XL - Artistic Style & Aesthetic (Rank 16)",
                Description = "Rank 16 / Alpha 16 setup with CLIP Skip 2, token shuffling, and 1,500 steps for crisp anime/manga art styles, line weight, and color grading transfer.",
                TargetBaseModel = "Illustrious-XL",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                ClipSkip = 2,
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "masterpiece, newest, {trigger}, 1girl, samurai warrior with katana in a bamboo forest, dynamic lighting, aesthetic",
                SamplePrompt2 = "masterpiece, newest, {trigger}, scenic landscape of floating islands with waterfalls, vibrant palette, detailed",
                NegativePrompt = "worst quality, low quality, blurry, distorted, monochrome",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "Illustrious", "Style", "Aesthetic", "Anime", "Recommended" }
            },
            new() {
                Id = "sdxl_style_aesthetic",
                Name = "SDXL 1.0 - Artistic Style & Aesthetic (Rank 16)",
                Description = "Rank 16 / Alpha 16 setup with frozen dual text encoders, token shuffling, and 1,500 steps for transferring painterly, watercolor, or graphic aesthetics onto any subject.",
                TargetBaseModel = "SDXL 1.0",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "artwork in {trigger} style, a vintage train crossing a stone viaduct in autumn, rich color palette, textured",
                SamplePrompt2 = "in {trigger} style, portrait of a woman with red hair, dramatic chiaroscuro lighting, painterly strokes",
                NegativePrompt = "blurry, low quality, distorted, photo, photorealistic, 3d render",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SDXL", "Style", "Artistic", "Aesthetic", "Recommended" }
            },
            new() {
                Id = "sd15_artistic_style",
                Name = "Stable Diffusion 1.5 - Artistic Style & Technique (Rank 16)",
                Description = "Rank 16 / Alpha 16 setup with frozen text encoder, token shuffling, and 1,500 steps for classic UNet artistic technique and medium transfer without subject locking.",
                TargetBaseModel = "SD 1.5",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "painting in {trigger} style, a lighthouse on a rocky cliff during a stormy sea, dramatic lighting, detailed",
                SamplePrompt2 = "in {trigger} style, portrait of a man in historical attire, fine brushwork, masterclass composition",
                NegativePrompt = "blurry, low quality, distorted, photo, photorealistic",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD1.5", "Style", "Artistic", "Technique", "Recommended" }
            },
            new() {
                Id = "sd35_artistic_style",
                Name = "Stable Diffusion 3.5 - Artistic Style & Technique (Rank 16)",
                Description = "Rank 16 / Alpha 16 MMDiT setup with frozen triple text encoders, token shuffling, and 1,500 steps for modern art style and visual medium adaptation.",
                TargetBaseModel = "SD 3.5",
                SubjectType = "Style",
                NetworkDim = 16,
                NetworkAlpha = 16.0,
                LearningRate = 1e-4,
                UnetLearningRate = 1e-4,
                TextEncoderLearningRate = 0.0,
                Optimizer = "adamw",
                LrScheduler = "cosine_with_restarts",
                Precision = "bf16",
                Epochs = 10,
                Repeats = 4,
                BatchSize = 1,
                TotalSteps = 1500,
                FlipAug = true,
                ShuffleTokens = true,
                KeepTokens = 1,
                SamplePrompt1 = "a painting in {trigger} style, a futuristic city garden with holographic flora, rich color grading, artistic brushwork",
                SamplePrompt2 = "in {trigger} style, portrait of a traveler with a lantern in a misty enchanted forest, evocative lighting, 8k",
                NegativePrompt = "",
                IsFavorite = true,
                IsBuiltIn = true,
                Tags = new() { "SD3.5", "Style", "Artistic", "Technique", "MMDiT", "Recommended" }
            }
        };
    }
}
