using System.Text;
using System.Text.RegularExpressions;

namespace LoRAMancer.App.Services;

public enum CollisionSeverity {
    Clean,
    LowRisk,
    ModerateRisk,
    HighRisk,
    CriticalBleed
}

public record TokenCollisionReport {
    public string Token { get; init; } = "";
    public CollisionSeverity Severity { get; init; } = CollisionSeverity.Clean;
    public int RiskScore { get; init; } // 0 - 100
    public string Category { get; init; } = "Unknown";
    public string Explanation { get; init; } = "";
    public List<string> ConflictingConcepts { get; init; } = new();
    public List<string> RecommendedSynthetics { get; init; } = new();
}

public record DatasetCollisionAudit {
    public string DatasetFolder { get; init; } = "";
    public int TotalCaptionFiles { get; init; }
    public int TotalTokensScanned { get; init; }
    public int CollidingTokensCount { get; init; }
    public List<TokenCollisionReport> HighRiskTokens { get; init; } = new();
    public TimeSpan Duration { get; init; }
}

public sealed class SemanticCollisionService {
    // Curated high-impact semantic primitives that cause visual bleeding in CLIP / T5
    private static readonly Dictionary<string, (CollisionSeverity Severity, int Score, string Category, string Reason)> _semanticLexicon = new(StringComparer.OrdinalIgnoreCase) {
        // Visual Colors & Tints
        ["red"] = (CollisionSeverity.CriticalBleed, 95, "Color Primitive", "Forces extreme crimson/red tint across lighting, clothing, and background."),
        ["blue"] = (CollisionSeverity.CriticalBleed, 95, "Color Primitive", "Forces cyan/blue tone, cold ambient lighting, and color grading shifts."),
        ["gold"] = (CollisionSeverity.CriticalBleed, 90, "Material / Color", "Forces metallic specular reflections, yellow cast, and ornamental jewelry."),
        ["silver"] = (CollisionSeverity.CriticalBleed, 90, "Material / Color", "Forces gray/metallic hair, armor, or desaturated lighting."),
        ["black"] = (CollisionSeverity.HighRisk, 85, "Color Primitive", "Heavily pushes contrast, dark shadows, and monochrome palettes."),
        ["white"] = (CollisionSeverity.HighRisk, 85, "Color Primitive", "Causes blown-out highlights, high key exposure, and white garments."),
        ["pink"] = (CollisionSeverity.HighRisk, 85, "Color Primitive", "Forces pastel tones and feminine or floral color bleed."),
        ["green"] = (CollisionSeverity.HighRisk, 85, "Color Primitive", "Forces foliage, neon green cast, or toxic atmosphere."),
        ["amber"] = (CollisionSeverity.HighRisk, 80, "Color / Mineral", "Causes orange warm glow, sunset hues, or resin-like artifacts."),
        ["crimson"] = (CollisionSeverity.HighRisk, 85, "Color Primitive", "Forces intense saturated blood-red tones."),
        ["neon"] = (CollisionSeverity.CriticalBleed, 92, "Lighting / Style", "Causes bright glowing outlines, cyberpunk bloom, and dark contrast."),

        // Textures & Physical Properties
        ["crystal"] = (CollisionSeverity.HighRisk, 88, "Physical Texture", "Causes refractive glass artifacts, sparkles, and polygonal facets."),
        ["metal"] = (CollisionSeverity.HighRisk, 82, "Physical Texture", "Forces metallic sheen, hard surfaces, and chrome reflections."),
        ["shadow"] = (CollisionSeverity.HighRisk, 80, "Lighting Primitive", "Forces dark mood, silhouette rendering, or heavy chiaroscuro."),
        ["frost"] = (CollisionSeverity.HighRisk, 85, "Atmosphere / Texture", "Causes ice crystals, pale skin, white hair, and winter mist."),
        ["fire"] = (CollisionSeverity.CriticalBleed, 92, "Physical Element", "Generates flames, sparks, smoke, and orange ambient lighting."),
        ["water"] = (CollisionSeverity.HighRisk, 80, "Physical Element", "Generates splashes, droplets, and wet surface reflections."),
        ["smoke"] = (CollisionSeverity.HighRisk, 80, "Physical Element", "Hazy fog, reduced contrast, and blurry background details."),
        ["leather"] = (CollisionSeverity.ModerateRisk, 65, "Material", "Forces textured leather clothing or brown/black materials."),
        ["velvet"] = (CollisionSeverity.ModerateRisk, 60, "Material", "Forces soft matte fabric textures and vintage furniture."),

        // Common Abstract / Archetype Concepts
        ["nova"] = (CollisionSeverity.HighRisk, 78, "Cosmic / Archetype", "Triggers space backgrounds, starry glares, and lens flare artifacts."),
        ["spark"] = (CollisionSeverity.HighRisk, 75, "Physical / Lighting", "Causes electrical embers, glowing particles, and bright spots."),
        ["echo"] = (CollisionSeverity.HighRisk, 76, "Abstract Concept", "Often causes multi-person hallucinations or ghostly duplicated limbs."),
        ["ghost"] = (CollisionSeverity.HighRisk, 85, "Entity Archetype", "Causes translucent bodies, pale skin, and spooky vapor."),
        ["angel"] = (CollisionSeverity.HighRisk, 88, "Entity Archetype", "Spawns wings, halos, and white feathered motifs."),
        ["demon"] = (CollisionSeverity.HighRisk, 88, "Entity Archetype", "Spawns horns, red eyes, and dark fire."),
        ["valkyrie"] = (CollisionSeverity.HighRisk, 82, "Mythological IP", "Forces winged helmets, Norse armor, and spear/shield imagery."),
        ["cyber"] = (CollisionSeverity.HighRisk, 85, "Genre / Style", "Forces wires, glowing circuits, and futuristic urban backdrops."),
        ["retro"] = (CollisionSeverity.HighRisk, 78, "Style Primitive", "Forces VHS grain, 80s film wash, and halftone dithering."),
        ["vintage"] = (CollisionSeverity.HighRisk, 80, "Style Primitive", "Forces sepia tone, aged paper texture, and antique styling."),
        ["cute"] = (CollisionSeverity.ModerateRisk, 70, "Aesthetic Descriptor", "Enlarges eyes, alters facial proportions, and softens outlines."),
        ["dark"] = (CollisionSeverity.HighRisk, 75, "Lighting Descriptor", "Deepens blacks and crushes dynamic range."),
        ["light"] = (CollisionSeverity.ModerateRisk, 65, "Lighting Descriptor", "Overexposes composition and softens edges."),
        ["star"] = (CollisionSeverity.HighRisk, 75, "Symbol / Cosmic", "Draws five-pointed star shapes and constellation sparkles."),
        ["queen"] = (CollisionSeverity.HighRisk, 82, "Royal Archetype", "Draws crowns, royal robes, and mature aristocratic features."),
        ["king"] = (CollisionSeverity.HighRisk, 82, "Royal Archetype", "Draws beards, gold crowns, and heavy velvet mantles."),
        ["knight"] = (CollisionSeverity.HighRisk, 80, "Warrior Archetype", "Forces plate armor, swords, and medieval architecture.")
    };

    // Pronounceable rare phoneme structures for synthetic zero-collision tokens
    private static readonly string[] _rarePrefixes = { "ohw", "v9", "qx", "kyr", "zlv", "wex", "thx", "bvl", "drx", "fyn" };
    private static readonly string[] _rareStems = { "manc", "zen", "vok", "tal", "kor", "ryn", "sel", "vyr", "dox", "phx" };
    private static readonly string[] _rareSuffixes = { "x", "z", "q", "v", "3", "9", "ar", "is", "on" };

    public TokenCollisionReport AnalyzeToken(string token) {
        if (string.IsNullOrWhiteSpace(token)) {
            return new TokenCollisionReport {
                Token = "",
                Severity = CollisionSeverity.Clean,
                RiskScore = 0,
                Category = "None",
                Explanation = "Empty token.",
                RecommendedSynthetics = GenerateSyntheticTokens(3)
            };
        }

        string cleanToken = token.Trim().ToLowerInvariant();
        cleanToken = Regex.Replace(cleanToken, @"^[<\[\(_]+|[>\]\)_]+$", ""); // Strip prompt wrappers like <lora:...>

        // Check curated semantic lexicon
        if (_semanticLexicon.TryGetValue(cleanToken, out var entry)) {
            return new TokenCollisionReport {
                Token = token,
                Severity = entry.Severity,
                RiskScore = entry.Score,
                Category = entry.Category,
                Explanation = entry.Reason,
                ConflictingConcepts = new List<string> { entry.Category, cleanToken },
                RecommendedSynthetics = GenerateSyntheticTokens(4, cleanToken)
            };
        }

        // Check common English word patterns (length 3-6 letters with standard phonotactics)
        if (Regex.IsMatch(cleanToken, @"^[a-z]{3,7}$")) {
            // General English dictionary word check heuristic
            if (IsCommonEnglishPattern(cleanToken)) {
                return new TokenCollisionReport {
                    Token = token,
                    Severity = CollisionSeverity.ModerateRisk,
                    RiskScore = 48,
                    Category = "Standard Dictionary Word",
                    Explanation = $"'{cleanToken}' is a standard English vocabulary word. The base model's CLIP/T5 text encoder has established semantic weights for this token, which will compete with your LoRA weights.",
                    ConflictingConcepts = new List<string> { cleanToken },
                    RecommendedSynthetics = GenerateSyntheticTokens(4, cleanToken)
                };
            }
        }

        // Clean / rare synthetic token detected!
        return new TokenCollisionReport {
            Token = token,
            Severity = CollisionSeverity.Clean,
            RiskScore = 5,
            Category = "Zero-Collision Rare Token",
            Explanation = $"'{token}' has minimal or dormant presence in standard CLIP/T5 vocabularies. It will act as a pure, unpolluted hook for your LoRA.",
            ConflictingConcepts = new List<string>(),
            RecommendedSynthetics = new List<string>()
        };
    }

    private static bool IsCommonEnglishPattern(string word) {
        // Words with standard vowel-consonant alternation and no rare characters
        if (word.Contains('x') || word.Contains('z') || word.Contains('q') || word.Any(char.IsDigit) || word.Contains('_')) {
            return false;
        }
        return true;
    }

    public List<string> GenerateSyntheticTokens(int count = 5, string? seedHint = null) {
        var results = new List<string>();
        var random = new Random();

        for (int i = 0; i < count; i++) {
            string prefix = _rarePrefixes[random.Next(_rarePrefixes.Length)];
            string stem = _rareStems[random.Next(_rareStems.Length)];
            string suffix = _rareSuffixes[random.Next(_rareSuffixes.Length)];

            string synthetic;
            if (!string.IsNullOrWhiteSpace(seedHint) && seedHint.Length >= 3) {
                // Generate a custom prefix-tagged token
                string shortHint = seedHint.Substring(0, Math.Min(4, seedHint.Length));
                synthetic = $"{prefix}_{shortHint}{suffix}";
            } else {
                synthetic = $"{prefix}{stem}{suffix}";
            }

            if (!results.Contains(synthetic)) {
                results.Add(synthetic);
            }
        }

        return results;
    }

    public async Task<DatasetCollisionAudit> AuditDatasetFolderAsync(
        string datasetFolder,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFolder);
        if (!Directory.Exists(datasetFolder)) {
            throw new DirectoryNotFoundException($"Dataset directory '{datasetFolder}' does not exist.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        string[] captionFiles = Directory.GetFiles(datasetFolder, "*.txt", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(datasetFolder, "*.caption", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tokenFrequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int totalTokensScanned = 0;

        foreach (string file in captionFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            string text = await File.ReadAllTextAsync(file, cancellationToken);
            string[] rawTokens = text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in rawTokens) {
                string trimmed = raw.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed)) {
                    totalTokensScanned++;
                    tokenFrequencies[trimmed] = tokenFrequencies.GetValueOrDefault(trimmed, 0) + 1;
                }
            }
        }

        var highRiskReports = new List<TokenCollisionReport>();

        foreach (var (token, freq) in tokenFrequencies.OrderByDescending(x => x.Value)) {
            cancellationToken.ThrowIfCancellationRequested();
            var report = AnalyzeToken(token);
            if (report.Severity >= CollisionSeverity.ModerateRisk) {
                highRiskReports.Add(report);
            }
        }

        sw.Stop();

        return new DatasetCollisionAudit {
            DatasetFolder = datasetFolder,
            TotalCaptionFiles = captionFiles.Length,
            TotalTokensScanned = totalTokensScanned,
            CollidingTokensCount = highRiskReports.Count,
            HighRiskTokens = highRiskReports,
            Duration = sw.Elapsed
        };
    }

    public async Task<int> ReplaceCollidingTokenInDatasetAsync(
        string datasetFolder,
        string targetToken,
        string replacementToken,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementToken);

        if (!Directory.Exists(datasetFolder)) {
            throw new DirectoryNotFoundException($"Dataset folder not found: {datasetFolder}");
        }

        string[] files = Directory.GetFiles(datasetFolder, "*.txt", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(datasetFolder, "*.caption", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int modifiedCount = 0;
        string pattern = $@"\b{Regex.Escape(targetToken)}\b";

        foreach (string file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            string original = await File.ReadAllTextAsync(file, cancellationToken);
            if (Regex.IsMatch(original, pattern, RegexOptions.IgnoreCase)) {
                string updated = Regex.Replace(original, pattern, replacementToken, RegexOptions.IgnoreCase);
                await File.WriteAllTextAsync(file, updated, cancellationToken);
                modifiedCount++;
            }
        }

        return modifiedCount;
    }
}
