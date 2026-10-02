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
    /// Resolves the root directory containing saved prompts.
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
    /// Resolves the dedicated prompts subdirectory under saved_prompts (e.g. saved_prompts/prompts/).
    /// Matches ComfyUI-ModusFlow's organized directory placement.
    /// </summary>
    public string ResolvePromptsSubdirectory() {
        string baseDir = ResolvePromptsDirectory();
        string subDir = Path.Combine(baseDir, "prompts");
        Directory.CreateDirectory(subDir);
        return subDir;
    }

    /// <summary>
    /// Ensures ModusFlow subdirectory structure exists and auto-migrates any loose .json files
    /// from the root saved_prompts directory into prompts/ or songs/ subdirectories.
    /// </summary>
    public void EnsureDirectoryStructure() {
        try {
            string baseDir = ResolvePromptsDirectory();
            string promptsDir = Path.Combine(baseDir, "prompts");
            string songsDir = Path.Combine(baseDir, "songs");

            Directory.CreateDirectory(promptsDir);
            Directory.CreateDirectory(songsDir);

            // Auto-migrate loose .json files in root saved_prompts/
            string[] looseFiles = Directory.GetFiles(baseDir, "*.json", SearchOption.TopDirectoryOnly);
            foreach (string file in looseFiles) {
                string filename = Path.GetFileName(file);
                bool isSong = false;
                try {
                    string text = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if ((root.TryGetProperty("type", out var typeElem) && (typeElem.ValueEquals("song") || typeElem.ValueEquals("ace_song"))) ||
                        root.TryGetProperty("lyrics", out _)) {
                        isSong = true;
                    }
                } catch {
                    // Fall back to treating as regular prompt
                }

                string destDir = isSong ? songsDir : promptsDir;
                string destFile = Path.Combine(destDir, filename);
                if (!File.Exists(destFile)) {
                    File.Move(file, destFile);
                }
            }
        } catch {
            // Best-effort auto-migration, never crash caller
        }
    }

    /// <summary>
    /// Loads all ModusFlow-compatible prompt JSON files across prompts/, root saved_prompts/, and songs/.
    /// If completely empty, seeds default high-quality starter prompts.
    /// </summary>
    public async Task<IReadOnlyList<ModusFlowPrompt>> GetAllPromptsAsync(CancellationToken cancellationToken = default) {
        string baseDir = ResolvePromptsDirectory();
        EnsureDirectoryStructure();

        string promptsDir = Path.Combine(baseDir, "prompts");
        string songsDir = Path.Combine(baseDir, "songs");

        string[] scanDirs = [promptsDir, baseDir, songsDir];

        bool anyFilesFound = scanDirs.Any(d => Directory.Exists(d) && Directory.GetFiles(d, "*.json", SearchOption.TopDirectoryOnly).Length > 0);
        if (!anyFilesFound) {
            await SeedDefaultPromptsAsync(promptsDir, cancellationToken);
        }

        HashSet<string> seenFilenames = new(StringComparer.OrdinalIgnoreCase);
        List<ModusFlowPrompt> list = new();

        foreach (string dir in scanDirs) {
            if (!Directory.Exists(dir)) {
                continue;
            }

            string[] jsonFiles = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            foreach (string file in jsonFiles) {
                string filename = Path.GetFileName(file);
                if (!seenFilenames.Add(filename)) {
                    continue; // Skip files already loaded from a higher-priority directory
                }

                try {
                    string json = await File.ReadAllTextAsync(file, cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string category = root.TryGetProperty("category", out var catProp) ? catProp.GetString() ?? "" : "";

                    // Support standard positive prompt with ModusFlow fallback for lyrics/tags/text
                    string positive = root.TryGetProperty("positive", out var posProp) ? posProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(positive)) {
                        if (root.TryGetProperty("lyrics", out var lyrProp) && root.TryGetProperty("tags", out var tagProp)) {
                            positive = $"{tagProp.GetString()}\n\n{lyrProp.GetString()}".Trim();
                        } else if (root.TryGetProperty("lyrics", out lyrProp)) {
                            positive = lyrProp.GetString() ?? "";
                        } else if (root.TryGetProperty("text", out var txtProp)) {
                            positive = txtProp.GetString() ?? "";
                        }
                    }

                    // Support standard negative prompt with ModusFlow fallback for negative_style
                    string negative = root.TryGetProperty("negative", out var negProp) ? negProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(negative) && root.TryGetProperty("negative_style", out var nsProp)) {
                        negative = nsProp.GetString() ?? "";
                    }

                    var prompt = new ModusFlowPrompt {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Category = category,
                        Positive = positive,
                        Negative = negative,
                        FilePath = file,
                        LastModified = File.GetLastWriteTimeUtc(file)
                    };
                    list.Add(prompt);
                } catch {
                    // Ignore corrupt or unreadable files
                }
            }
        }

        return list
            .OrderBy(p => string.IsNullOrWhiteSpace(p.Category) ? "ZZZ" : p.Category)
            .ThenBy(p => p.Name)
            .ToList();
    }

    /// <summary>
    /// Saves a positive and negative prompt pair to a ModusFlow-compatible JSON file
    /// into the dedicated prompts/ subdirectory.
    /// </summary>
    public async Task<ModusFlowPrompt> SavePromptAsync(
        string name,
        string positive,
        string negative,
        string category = "",
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string promptsDir = ResolvePromptsSubdirectory();

        // Sanitize name for filename
        string safeName = Path.GetInvalidFileNameChars()
            .Aggregate(name.Trim(), (current, c) => current.Replace(c, '_'));

        if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            safeName += ".json";
        }

        string filePath = Path.Combine(promptsDir, safeName);

        // ModusFlow 100% compatible schema (including "type": "prompt")
        var payload = new Dictionary<string, object> {
            ["type"] = "prompt",
            ["category"] = category.Trim(),
            ["positive"] = positive ?? string.Empty,
            ["negative"] = negative ?? string.Empty
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        // If a legacy loose copy existed in root saved_prompts, remove it to keep directories clean
        string baseDir = ResolvePromptsDirectory();
        string legacyPath = Path.Combine(baseDir, safeName);
        if (File.Exists(legacyPath)) {
            try {
                File.Delete(legacyPath);
            } catch { }
        }

        var prompt = new ModusFlowPrompt {
            Name = Path.GetFileNameWithoutExtension(safeName),
            Category = category.Trim(),
            Positive = positive ?? string.Empty,
            Negative = negative ?? string.Empty,
            FilePath = filePath,
            LastModified = DateTime.UtcNow
        };

        OnPromptsUpdated?.Invoke();
        return prompt;
    }

    /// <summary>
    /// Deletes a prompt JSON file by name or path across prompts/, root saved_prompts/, or songs/.
    /// </summary>
    public async Task<bool> DeletePromptAsync(string nameOrPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(nameOrPath)) {
            return false;
        }

        if (File.Exists(nameOrPath)) {
            File.Delete(nameOrPath);
            OnPromptsUpdated?.Invoke();
            return true;
        }

        string baseDir = ResolvePromptsDirectory();
        string safeName = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : $"{nameOrPath}.json";

        string[] candidatePaths = [
            Path.Combine(baseDir, "prompts", safeName),
            Path.Combine(baseDir, safeName),
            Path.Combine(baseDir, "songs", safeName)
        ];

        foreach (string candidate in candidatePaths) {
            if (File.Exists(candidate)) {
                File.Delete(candidate);
                OnPromptsUpdated?.Invoke();
                return true;
            }
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
        Directory.CreateDirectory(dir);

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
                var payload = new Dictionary<string, object> {
                    ["type"] = "prompt",
                    ["category"] = def.Category,
                    ["positive"] = def.Positive,
                    ["negative"] = def.Negative
                };
                string json = JsonSerializer.Serialize(payload, JsonOptions);
                await File.WriteAllTextAsync(path, json, cancellationToken);
            }
        }
    }
}

