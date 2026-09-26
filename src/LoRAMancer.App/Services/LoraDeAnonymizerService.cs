using System.Diagnostics;
using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed record TriggerTokenCandidate {
    public string Token { get; init; } = "";
    public double ActivationStrength { get; init; }
    public double ConfidencePct { get; init; }
    public string TokenCategory { get; init; } = "Concept Hook";
}

public sealed record LoraForensicReport {
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DetectedArchitecture { get; init; } = "Unknown";
    public double ArchitectureConfidence { get; init; } // 0 - 100%
    public int EffectiveRank { get; init; }
    public double EffectiveAlpha { get; init; }
    public double AlphaRankRatio => EffectiveRank > 0 ? EffectiveAlpha / EffectiveRank : 1.0;
    public string Precision { get; init; } = "FP16";
    public long FileSizeBytes { get; init; }
    public string FormattedSize => (FileSizeBytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
    public bool HasTextEncoderWeights { get; init; }
    public bool HasUnetBackboneWeights { get; init; }
    public List<TriggerTokenCandidate> RecoveredTriggerTokens { get; init; } = new();
    public Dictionary<string, string> RecoveredHyperparameters { get; init; } = new();
    public string ForensicSummary { get; init; } = "";
    public TimeSpan Duration { get; init; }
}

public sealed class LoraDeAnonymizerService {
    private readonly SafeTensorsMetadataReader? _metadataReader;
    private readonly ModelArchitectureRegistry? _architectureRegistry;

    public LoraDeAnonymizerService(
        SafeTensorsMetadataReader? metadataReader = null,
        ModelArchitectureRegistry? architectureRegistry = null
    ) {
        _metadataReader = metadataReader;
        _architectureRegistry = architectureRegistry;
    }

    public async Task<LoraForensicReport> ReverseEngineerLoraAsync(
        string loraPath,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(loraPath)) {
            throw new FileNotFoundException($"LoRA file not found: {loraPath}");
        }

        var sw = Stopwatch.StartNew();
        string fileName = Path.GetFileName(loraPath);
        var fileInfo = new FileInfo(loraPath);

        LoraMetadata? metadata = null;
        if (_metadataReader != null) {
            try {
                metadata = await _metadataReader.ReadMetadataAsync(loraPath, cancellationToken);
            } catch { }
        }

        var rawMeta = metadata?.RawHeaderMetadata ?? new Dictionary<string, string>();

        // 1. Architecture Fingerprinting
        var (arch, conf) = FingerprintArchitecture(loraPath, rawMeta, metadata?.BaseModel);

        // 2. Rank & Alpha Reconstruction
        int rank = metadata?.NetworkDim ?? 16;
        double alpha = metadata?.NetworkAlpha ?? rank;
        string precision = !string.IsNullOrWhiteSpace(metadata?.Precision) ? metadata.Precision : "FP16";

        // 3. Trigger Token & Concept Forensic Recovery
        var triggers = RecoverTriggerTokens(metadata, rawMeta, fileName);

        // 4. Hyperparameter Recovery
        var hyperparams = ExtractHyperparameters(metadata, rawMeta, rank, alpha);

        bool hasTe = rawMeta.Keys.Any(k => k.Contains("lora_te") || k.Contains("text_model") || k.Contains("lora_clip")) ||
                     metadata?.TextEncoderLearningRate != null;
        bool hasUnet = rawMeta.Keys.Any(k => k.Contains("lora_unet") || k.Contains("double_blocks") || k.Contains("single_blocks") || k.Contains("transformer")) ||
                       metadata?.UnetLearningRate != null;

        sw.Stop();

        string summary = $"Forensic analysis identified {fileName} as {arch} (Confidence: {conf:F0}%). " +
                         $"Topology uses Rank {rank} / Alpha {alpha:F1} (Ratio: {(rank > 0 ? alpha / rank : 1):F2}). " +
                         (triggers.Count > 0 ? $"Primary reconstructed trigger word candidate: '{triggers[0].Token}'." : "No explicit trigger tokens found in metadata.");

        return new LoraForensicReport {
            FilePath = loraPath,
            FileName = fileName,
            DetectedArchitecture = arch,
            ArchitectureConfidence = conf,
            EffectiveRank = rank,
            EffectiveAlpha = alpha,
            Precision = precision,
            FileSizeBytes = fileInfo.Length,
            HasTextEncoderWeights = hasTe,
            HasUnetBackboneWeights = hasUnet,
            RecoveredTriggerTokens = triggers,
            RecoveredHyperparameters = hyperparams,
            ForensicSummary = summary,
            Duration = sw.Elapsed
        };
    }

    private (string Architecture, double Confidence) FingerprintArchitecture(
        string loraPath,
        IReadOnlyDictionary<string, string> rawMeta,
        string? baseModelHint
    ) {
        if (!string.IsNullOrWhiteSpace(baseModelHint) && baseModelHint != "Unknown") {
            if (_architectureRegistry != null && _architectureRegistry.TryGet(baseModelHint, out var known)) {
                return (known!.DisplayName, 98.0);
            }
            return (baseModelHint, 95.0);
        }

        // Check using ModelArchitectureRegistry if available
        if (_architectureRegistry != null) {
            var inferred = _architectureRegistry.InferFromPathOrMetadata(loraPath, rawMeta);
            if (inferred != null && !inferred.Id.Equals("flux_1_dev", StringComparison.OrdinalIgnoreCase)) {
                return (inferred.DisplayName, 96.0);
            }
        }

        if (rawMeta.TryGetValue("ss_base_model_version", out var baseVer) && !string.IsNullOrWhiteSpace(baseVer)) {
            if (baseVer.Contains("flux", StringComparison.OrdinalIgnoreCase)) return ("FLUX.1-dev", 98.0);
            if (baseVer.Contains("sdxl", StringComparison.OrdinalIgnoreCase)) return ("Stable Diffusion XL 1.0", 98.0);
            if (baseVer.Contains("v1", StringComparison.OrdinalIgnoreCase) || baseVer.Contains("1.5", StringComparison.OrdinalIgnoreCase)) return ("Stable Diffusion 1.5", 98.0);
        }

        // Structural key fingerprinting
        var keys = rawMeta.Keys.ToList();
        int fluxKeys = keys.Count(k => k.Contains("double_blocks") || k.Contains("single_blocks") || k.Contains("img_attn") || k.Contains("txt_attn"));
        int sdxlKeys = keys.Count(k => k.Contains("input_blocks") || k.Contains("middle_block") || k.Contains("output_blocks") || k.Contains("1024"));
        int hunyuanKeys = keys.Count(k => k.Contains("hunyuan") || k.Contains("double_stream_blocks"));
        int wanKeys = keys.Count(k => k.Contains("wan") || k.Contains("cross_attn_norm"));

        if (fluxKeys > 5) return ("FLUX.1-dev", 95.0);
        if (hunyuanKeys > 2) return ("HunyuanVideo", 92.0);
        if (wanKeys > 2) return ("Wan 2.1", 92.0);
        if (sdxlKeys > 5) {
            bool hasClipG = keys.Any(k => k.Contains("lora_te2") || k.Contains("clip_g"));
            return (hasClipG ? "Pony Diffusion V6 XL" : "Stable Diffusion XL 1.0", 90.0);
        }

        if (keys.Any(k => k.Contains("lora_unet") || k.Contains("to_k") || k.Contains("to_v"))) {
            return ("Stable Diffusion 1.5", 85.0);
        }

        if (_architectureRegistry != null) {
            var fallback = _architectureRegistry.GetOrDefault("flux_1_dev");
            return (fallback.DisplayName, 70.0);
        }

        return ("Generic Diffusion LoRA", 65.0);
    }

    private static List<TriggerTokenCandidate> RecoverTriggerTokens(
        LoraMetadata? metadata,
        IReadOnlyDictionary<string, string> rawMeta,
        string fileName
    ) {
        var results = new List<TriggerTokenCandidate>();

        if (metadata != null && metadata.TrainedWords.Count > 0) {
            foreach (var word in metadata.TrainedWords.Distinct(StringComparer.OrdinalIgnoreCase).Take(5)) {
                results.Add(new TriggerTokenCandidate {
                    Token = word,
                    ActivationStrength = 1.0,
                    ConfidencePct = 98.0,
                    TokenCategory = "Trained Tag"
                });
            }
        }

        if (rawMeta.TryGetValue("ss_tag_frequency", out var tagFreqJson) && !string.IsNullOrWhiteSpace(tagFreqJson)) {
            try {
                using var doc = JsonDocument.Parse(tagFreqJson);
                foreach (var prop in doc.RootElement.EnumerateObject()) {
                    if (prop.Value.ValueKind == JsonValueKind.Object) {
                        foreach (var tagProp in prop.Value.EnumerateObject().OrderByDescending(x => x.Value.GetInt32()).Take(5)) {
                            if (!results.Any(r => string.Equals(r.Token, tagProp.Name, StringComparison.OrdinalIgnoreCase))) {
                                results.Add(new TriggerTokenCandidate {
                                    Token = tagProp.Name,
                                    ActivationStrength = 0.95,
                                    ConfidencePct = 92.0,
                                    TokenCategory = "Dataset Frequent Tag"
                                });
                            }
                        }
                    }
                }
            } catch { }
        }

        // Heuristic fallback from filename
        if (results.Count == 0) {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string cleanName = System.Text.RegularExpressions.Regex.Replace(baseName, @"[-_]?(v\d+|\d+ep|epoch\d+|step\d+|rank\d+|fp16|bf16)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim('_', '-');
            if (!string.IsNullOrWhiteSpace(cleanName) && cleanName.Length >= 3) {
                results.Add(new TriggerTokenCandidate {
                    Token = cleanName.ToLowerInvariant(),
                    ActivationStrength = 0.70,
                    ConfidencePct = 65.0,
                    TokenCategory = "Filename Heuristic Hook"
                });
            }
        }

        return results;
    }

    private static Dictionary<string, string> ExtractHyperparameters(
        LoraMetadata? metadata,
        IReadOnlyDictionary<string, string> rawMeta,
        int rank,
        double alpha
    ) {
        var dict = new Dictionary<string, string> {
            ["Network Rank (dim)"] = rank.ToString(),
            ["Network Alpha"] = alpha.ToString("F1"),
            ["Alpha/Rank Ratio"] = (rank > 0 ? (alpha / rank).ToString("F2") : "1.00")
        };

        if (metadata != null) {
            if (metadata.LearningRate != null) dict["Learning Rate"] = metadata.LearningRate.Value.ToString("E2");
            if (metadata.UnetLearningRate != null) dict["UNet LR"] = metadata.UnetLearningRate.Value.ToString("E2");
            if (metadata.TextEncoderLearningRate != null) dict["Text Encoder LR"] = metadata.TextEncoderLearningRate.Value.ToString("E2");
            if (!string.IsNullOrWhiteSpace(metadata.Optimizer)) dict["Optimizer"] = metadata.Optimizer;
            if (!string.IsNullOrWhiteSpace(metadata.LrScheduler)) dict["LR Scheduler"] = metadata.LrScheduler;
            if (metadata.TotalSteps != null) dict["Total Steps"] = metadata.TotalSteps.Value.ToString();
            if (metadata.Epochs != null) dict["Epochs"] = metadata.Epochs.Value.ToString();
            if (!string.IsNullOrWhiteSpace(metadata.Resolution)) dict["Resolution"] = metadata.Resolution;
        }

        if (rawMeta.TryGetValue("ss_total_batch_size", out var bs)) dict["Batch Size"] = bs;

        return dict;
    }
}
