using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

/// <summary>
/// Service managing saved prompts with 100% ModusFlow JSON schema compatibility.
/// Allows loading, saving, and categorizing prompt pairs (positive + negative)
/// seamlessly between LoRAMancer and ComfyUI-ModusFlow.
/// </summary>
public sealed class ModusFlowPromptService {
    private readonly SettingsService _settingsService;
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public event Action? OnPromptsUpdated;

    public ModusFlowPromptService(SettingsService settingsService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// Resolves the active directory containing saved prompts.
    /// Checks AppSettings first, defaulting to the user's home settings directory (~/.loramancer/saved_prompts/).
    /// </summary>
    public string ResolvePromptsDirectory() {
        string configured = _settingsService.Current.SavedPromptsDirectory?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured)) {
            try {
                if (!Directory.Exists(configured)) {
                    Directory.CreateDirectory(configured);
                }
                return configured;
            } catch {
                // Fall back to default on invalid directory path
            }
        }

        // Default to ~/.loramancer/saved_prompts/ (User Home Settings directory)
        string defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".loramancer",
            "saved_prompts"
        );
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }

    /// <summary>
    /// Loads all ModusFlow-compatible prompt JSON files from the prompt directory.
    /// If the directory is empty, seeds default high-quality starter prompts.
    /// </summary>
    public async Task<IReadOnlyList<ModusFlowPrompt>> GetAllPromptsAsync(CancellationToken cancellationToken = default) {
        string dir = ResolvePromptsDirectory();
        Directory.CreateDirectory(dir);

        string[] jsonFiles = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
        if (jsonFiles.Length == 0) {
            await SeedDefaultPromptsAsync(dir, cancellationToken);
            jsonFiles = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
        }

        List<ModusFlowPrompt> list = new();
        foreach (string file in jsonFiles) {
            try {
                string json = await File.ReadAllTextAsync(file, cancellationToken);
                var prompt = JsonSerializer.Deserialize<ModusFlowPrompt>(json, JsonOptions);
                if (prompt != null) {
                    prompt.Name = Path.GetFileNameWithoutExtension(file);
                    prompt.FilePath = file;
                    prompt.LastModified = File.GetLastWriteTimeUtc(file);
                    list.Add(prompt);
                }
            } catch {
                // Ignore corrupt or unreadable files
            }
        }

        return list
            .OrderBy(p => string.IsNullOrWhiteSpace(p.Category) ? "ZZZ" : p.Category)
            .ThenBy(p => p.Name)
            .ToList();
    }

    /// <summary>
    /// Saves a positive and negative prompt pair to a ModusFlow-compatible JSON file.
    /// </summary>
    public async Task<ModusFlowPrompt> SavePromptAsync(
        string name,
        string positive,
        string negative,
        string category = "",
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string dir = ResolvePromptsDirectory();
        Directory.CreateDirectory(dir);

        // Sanitize name for filename
        string safeName = Path.GetInvalidFileNameChars()
            .Aggregate(name.Trim(), (current, c) => current.Replace(c, '_'));

        if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            safeName += ".json";
        }

        string filePath = Path.Combine(dir, safeName);
        var prompt = new ModusFlowPrompt {
            Name = Path.GetFileNameWithoutExtension(safeName),
            Category = category.Trim(),
            Positive = positive ?? string.Empty,
            Negative = negative ?? string.Empty,
            FilePath = filePath,
            LastModified = DateTime.UtcNow
        };

        string json = JsonSerializer.Serialize(prompt, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        OnPromptsUpdated?.Invoke();
        return prompt;
    }

    /// <summary>
    /// Deletes a prompt JSON file by name or path.
    /// </summary>
    public async Task<bool> DeletePromptAsync(string nameOrPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(nameOrPath)) {
            return false;
        }

        string dir = ResolvePromptsDirectory();
        string targetPath = File.Exists(nameOrPath)
            ? nameOrPath
            : Path.Combine(dir, nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : $"{nameOrPath}.json");

        if (File.Exists(targetPath)) {
            File.Delete(targetPath);
            OnPromptsUpdated?.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns all distinct categories across saved prompts.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default) {
        var prompts = await GetAllPromptsAsync(cancellationToken);
        return prompts
            .Select(p => p.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    private async Task SeedDefaultPromptsAsync(string dir, CancellationToken cancellationToken) {
        var defaults = new[] {
            new {
                File = "Default Studio Portrait.json",
                Category = "Portraits",
                Positive = "masterpiece, best quality, ultra-detailed, professional studio portrait, high resolution, 8k, sharp focus, cinematic soft lighting, award-winning photography, photorealistic",
                Negative = "blurry, low quality, distorted, extra limbs, bad anatomy, deformed eyes, disfigured, watermark, text, signature"
            },
            new {
                File = "Cinematic Fantasy.json",
                Category = "Cinematic",
                Positive = "epic cinematic fantasy scene, atmospheric lighting, volumetric rays, highly detailed digital painting, vibrant color palette, octane render style, dramatic composition, dynamic angle",
                Negative = "washed out, flat lighting, sketch, lowres, ugly, artifacting, draft, cartoon, bad proportions"
            },
            new {
                File = "Anime Masterpiece.json",
                Category = "Anime",
                Positive = "masterpiece, top aesthetic anime art style, exquisite details, vibrant expressive lighting, dynamic pose, rich textures, official wallpaper quality, clean line art",
                Negative = "jpeg artifacts, signature, watermark, username, bad hands, missing fingers, extra digits, monochrome, muted colors"
            },
            new {
                File = "Flux Realistic Likeness.json",
                Category = "Flux",
                Positive = "RAW photo, candid snapshot of a subject looking towards camera, natural skin texture, subsurface scattering, ambient daylight, shot on 35mm lens, f/1.8 aperture, realistic depth of field",
                Negative = "plastic skin, airbrushed, 3D CGI render, doll-like, oversharpened, overexposed, oversaturated"
            }
        };

        foreach (var def in defaults) {
            string path = Path.Combine(dir, def.File);
            if (!File.Exists(path)) {
                var p = new ModusFlowPrompt {
                    Category = def.Category,
                    Positive = def.Positive,
                    Negative = def.Negative
                };
                string json = JsonSerializer.Serialize(p, JsonOptions);
                await File.WriteAllTextAsync(path, json, cancellationToken);
            }
        }
    }
}
