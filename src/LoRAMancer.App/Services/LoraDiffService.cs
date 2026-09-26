using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public record LayerDiffItem {
    public string LayerName { get; init; } = "";
    public string Status { get; init; } = "Matched"; // Matched, OnlyInA, OnlyInB, ShapeMismatch
    public string ShapeA { get; init; } = "-";
    public string ShapeB { get; init; } = "-";
    public string DTypeA { get; init; } = "-";
    public string DTypeB { get; init; } = "-";
    public double NormA { get; init; }
    public double NormB { get; init; }
    public double RelativeDeltaPct { get; init; }
    public double CosineSimilarity { get; init; } = 1.0;
    public double DriftScore => Math.Max(0.0, 1.0 - CosineSimilarity);
    public string DivergenceLevel {
        get {
            if (Status == "OnlyInA" || Status == "OnlyInB") return "Missing";
            if (Status == "ShapeMismatch") return "Mismatch";
            if (DriftScore < 0.005) return "Identical";
            if (DriftScore < 0.05) return "Subtle Drift";
            if (DriftScore < 0.20) return "Moderate Drift";
            return "Heavy Divergence";
        }
    }
}

public record MetadataDiffItem(string Key, string? ValueA, string? ValueB, bool IsDifferent);

public record LoraDiffSummary {
    public string PathA { get; init; } = "";
    public string PathB { get; init; } = "";
    public string ModelNameA { get; init; } = "";
    public string ModelNameB { get; init; } = "";
    public string ArchA { get; init; } = "";
    public string ArchB { get; init; } = "";
    public int TotalTensorsA { get; init; }
    public int TotalTensorsB { get; init; }
    public int SharedTensorsCount { get; init; }
    public int OnlyInACount { get; init; }
    public int OnlyInBCount { get; init; }
    public long TotalParamsA { get; init; }
    public long TotalParamsB { get; init; }
    public double AverageCosineSimilarity { get; init; } = 1.0;
    public double AverageDriftScore => Math.Max(0.0, 1.0 - AverageCosineSimilarity);
    public double MaxDriftScore { get; init; }
    public string CompatibilityStatus { get; init; } = "Compatible";
    public List<LayerDiffItem> LayerDiffs { get; init; } = new();
    public List<MetadataDiffItem> MetadataDiffs { get; init; } = new();
    public TimeSpan Duration { get; init; }
}

public sealed class LoraDiffService {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService? _settingsService;
    private readonly AiToolkitSetupService? _toolkitSetup;
    private readonly SafeTensorsMetadataReader? _metadataReader;

    public LoraDiffService(
        ProcessRunner processRunner,
        SettingsService? settingsService = null,
        AiToolkitSetupService? toolkitSetup = null,
        SafeTensorsMetadataReader? metadataReader = null
    ) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _settingsService = settingsService;
        _toolkitSetup = toolkitSetup;
        _metadataReader = metadataReader;
    }

    private string ResolvePythonExecutable() {
        string defaultVenv = AiToolkitSetupService.GetDefaultVenvPath();
        string venvPython = Path.Combine(defaultVenv, "Scripts", "python.exe");
        if (File.Exists(venvPython)) {
            return venvPython;
        }

        string preferred = _settingsService?.Current.PreferredPythonPath ?? "python.exe";
        if (File.Exists(preferred)) {
            return preferred;
        }

        return "python.exe";
    }

    public async Task<LoraDiffSummary> CompareLorasAsync(
        string pathA,
        string pathB,
        bool deepTensorAnalysis = true,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathA);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathB);

        var stopwatch = Stopwatch.StartNew();

        if (!File.Exists(pathA)) {
            throw new FileNotFoundException("Model A file not found", pathA);
        }
        if (!File.Exists(pathB)) {
            throw new FileNotFoundException("Model B file not found", pathB);
        }

        onProgress?.Invoke("Parsing SafeTensors headers for Model A and B...");

        var headerA = await ReadHeaderAsync(pathA, cancellationToken);
        var headerB = await ReadHeaderAsync(pathB, cancellationToken);

        string nameA = Path.GetFileNameWithoutExtension(pathA);
        string nameB = Path.GetFileNameWithoutExtension(pathB);

        string archA = DetectArch(headerA.Metadata);
        string archB = DetectArch(headerB.Metadata);

        var metaKeys = new HashSet<string>(headerA.Metadata.Keys, StringComparer.OrdinalIgnoreCase);
        metaKeys.UnionWith(headerB.Metadata.Keys);

        var metaDiffs = new List<MetadataDiffItem>();
        foreach (var key in metaKeys.OrderBy(k => k)) {
            headerA.Metadata.TryGetValue(key, out string? valA);
            headerB.Metadata.TryGetValue(key, out string? valB);
            bool isDiff = !string.Equals(valA, valB, StringComparison.Ordinal);
            metaDiffs.Add(new MetadataDiffItem(key, valA, valB, isDiff));
        }

        // Structural layer comparison
        var tensorsA = headerA.Tensors;
        var tensorsB = headerB.Tensors;

        var allTensorKeys = new HashSet<string>(tensorsA.Keys, StringComparer.OrdinalIgnoreCase);
        allTensorKeys.UnionWith(tensorsB.Keys);

        var layerDiffs = new List<LayerDiffItem>();
        int sharedCount = 0;
        int onlyACount = 0;
        int onlyBCount = 0;

        foreach (var key in allTensorKeys.OrderBy(k => k)) {
            bool inA = tensorsA.TryGetValue(key, out var infoA);
            bool inB = tensorsB.TryGetValue(key, out var infoB);

            if (inA && inB) {
                sharedCount++;
                string shapeA = string.Join("x", infoA!.Shape);
                string shapeB = string.Join("x", infoB!.Shape);
                bool shapeMatch = shapeA == shapeB;

                layerDiffs.Add(new LayerDiffItem {
                    LayerName = key,
                    Status = shapeMatch ? "Matched" : "ShapeMismatch",
                    ShapeA = shapeA,
                    ShapeB = shapeB,
                    DTypeA = infoA.DType,
                    DTypeB = infoB.DType,
                    CosineSimilarity = 1.0
                });
            } else if (inA) {
                onlyACount++;
                layerDiffs.Add(new LayerDiffItem {
                    LayerName = key,
                    Status = "OnlyInA",
                    ShapeA = string.Join("x", infoA!.Shape),
                    ShapeB = "-",
                    DTypeA = infoA.DType,
                    DTypeB = "-",
                    CosineSimilarity = 0.0
                });
            } else {
                onlyBCount++;
                layerDiffs.Add(new LayerDiffItem {
                    LayerName = key,
                    Status = "OnlyInB",
                    ShapeA = "-",
                    ShapeB = string.Join("x", infoB!.Shape),
                    DTypeA = "-",
                    DTypeB = infoB.DType,
                    CosineSimilarity = 0.0
                });
            }
        }

        // Deep PyTorch Cosine Similarity & Norm Analysis
        if (deepTensorAnalysis && sharedCount > 0) {
            onProgress?.Invoke("Running PyTorch Deep Weight Cosine & Norm analysis...");
            try {
                var deepMetrics = await RunDeepCosineAnalysisAsync(pathA, pathB, onProgress, cancellationToken);
                if (deepMetrics != null && deepMetrics.Count > 0) {
                    for (int i = 0; i < layerDiffs.Count; i++) {
                        var item = layerDiffs[i];
                        if (deepMetrics.TryGetValue(item.LayerName, out var metric)) {
                            double deltaPct = 0;
                            if (metric.NormA > 0) {
                                deltaPct = Math.Round(((metric.NormB - metric.NormA) / metric.NormA) * 100.0, 2);
                            }
                            layerDiffs[i] = item with {
                                NormA = Math.Round(metric.NormA, 4),
                                NormB = Math.Round(metric.NormB, 4),
                                RelativeDeltaPct = deltaPct,
                                CosineSimilarity = Math.Round(metric.CosineSimilarity, 5)
                            };
                        }
                    }
                }
            } catch (Exception ex) {
                onProgress?.Invoke($"Deep analysis warning: {ex.Message} (falling back to structural diff)");
            }
        }

        var matchedItems = layerDiffs.Where(x => x.Status == "Matched").ToList();
        double avgCos = matchedItems.Count > 0 ? matchedItems.Average(x => x.CosineSimilarity) : 0.0;
        double maxDrift = matchedItems.Count > 0 ? matchedItems.Max(x => x.DriftScore) : 1.0;

        string compat = "Fully Compatible";
        if (archA != "Unknown" && archB != "Unknown" && !string.Equals(archA, archB, StringComparison.OrdinalIgnoreCase)) {
            compat = $"Architecture Mismatch ({archA} vs {archB})";
        } else if (onlyACount > 0 || onlyBCount > 0) {
            compat = "Partial Topology Match";
        } else if (avgCos > 0.999) {
            compat = "Twin / Near Identical";
        }

        stopwatch.Stop();

        return new LoraDiffSummary {
            PathA = pathA,
            PathB = pathB,
            ModelNameA = nameA,
            ModelNameB = nameB,
            ArchA = archA,
            ArchB = archB,
            TotalTensorsA = tensorsA.Count,
            TotalTensorsB = tensorsB.Count,
            SharedTensorsCount = sharedCount,
            OnlyInACount = onlyACount,
            OnlyInBCount = onlyBCount,
            TotalParamsA = headerA.TotalParams,
            TotalParamsB = headerB.TotalParams,
            AverageCosineSimilarity = Math.Round(avgCos, 5),
            MaxDriftScore = Math.Round(maxDrift, 5),
            CompatibilityStatus = compat,
            LayerDiffs = layerDiffs,
            MetadataDiffs = metaDiffs,
            Duration = stopwatch.Elapsed
        };
    }

    private sealed record TensorMetric(double CosineSimilarity, double NormA, double NormB);

    private async Task<Dictionary<string, TensorMetric>> RunDeepCosineAnalysisAsync(
        string pathA,
        string pathB,
        Action<string>? onProgress,
        CancellationToken cancellationToken
    ) {
        string pythonExe = ResolvePythonExecutable();
        string tempScript = Path.Combine(Path.GetTempPath(), $"loramancer_diff_{Guid.NewGuid():N}.py");

        string scriptContent = @"import sys
import json
import torch
from safetensors.torch import load_file

def main():
    path_a = sys.argv[1]
    path_b = sys.argv[2]
    
    tensors_a = load_file(path_a, device='cpu')
    tensors_b = load_file(path_b, device='cpu')
    
    results = {}
    for key in tensors_a.keys():
        if key in tensors_b:
            ta = tensors_a[key].float().flatten()
            tb = tensors_b[key].float().flatten()
            
            norm_a = float(torch.norm(ta).item())
            norm_b = float(torch.norm(tb).item())
            
            if ta.shape == tb.shape and ta.numel() > 0:
                cos = float(torch.nn.functional.cosine_similarity(ta.unsqueeze(0), tb.unsqueeze(0)).item())
                if torch.isnan(torch.tensor(cos)):
                    cos = 1.0 if norm_a == 0 and norm_b == 0 else 0.0
            else:
                cos = 0.0
                
            results[key] = {
                'cos': cos,
                'norm_a': norm_a,
                'norm_b': norm_b
            }
            
    print('__JSON_DIFF_START__')
    print(json.dumps(results))
    print('__JSON_DIFF_END__')

if __name__ == '__main__':
    main()
";

        await File.WriteAllTextAsync(tempScript, scriptContent, Encoding.UTF8, cancellationToken);

        try {
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = pythonExe,
                    Arguments = $"\"{tempScript}\" \"{pathA}\" \"{pathB}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, e) => {
                if (e.Data != null) {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);

            string output = outputBuilder.ToString();
            int startIdx = output.IndexOf("__JSON_DIFF_START__", StringComparison.Ordinal);
            int endIdx = output.IndexOf("__JSON_DIFF_END__", StringComparison.Ordinal);

            if (startIdx >= 0 && endIdx > startIdx) {
                string json = output.Substring(startIdx + "__JSON_DIFF_START__".Length, endIdx - (startIdx + "__JSON_DIFF_START__".Length)).Trim();
                using var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, TensorMetric>(StringComparer.OrdinalIgnoreCase);

                foreach (var prop in doc.RootElement.EnumerateObject()) {
                    double cos = prop.Value.GetProperty("cos").GetDouble();
                    double na = prop.Value.GetProperty("norm_a").GetDouble();
                    double nb = prop.Value.GetProperty("norm_b").GetDouble();
                    dict[prop.Name] = new TensorMetric(cos, na, nb);
                }
                return dict;
            }
        } finally {
            try {
                if (File.Exists(tempScript)) {
                    File.Delete(tempScript);
                }
            } catch {
                // Ignore cleanup errors
            }
        }

        return new Dictionary<string, TensorMetric>();
    }

    private sealed record ParsedHeader(Dictionary<string, string> Metadata, Dictionary<string, TensorInfo> Tensors, long TotalParams);
    private sealed record TensorInfo(string DType, List<long> Shape, long Numel);

    private static async Task<ParsedHeader> ReadHeaderAsync(string filePath, CancellationToken cancellationToken) {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        byte[] sizeBuf = new byte[8];
        int read = await stream.ReadAsync(sizeBuf.AsMemory(0, 8), cancellationToken);
        if (read < 8) throw new InvalidDataException("Invalid SafeTensors header size");

        ulong headerLen = BinaryPrimitives.ReadUInt64LittleEndian(sizeBuf);
        byte[] headerBytes = new byte[(int)headerLen];
        int totalRead = 0;
        while (totalRead < (int)headerLen) {
            int chunk = await stream.ReadAsync(headerBytes.AsMemory(totalRead, (int)headerLen - totalRead), cancellationToken);
            if (chunk == 0) break;
            totalRead += chunk;
        }

        string headerJson = Encoding.UTF8.GetString(headerBytes);
        using var doc = JsonDocument.Parse(headerJson);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tensors = new Dictionary<string, TensorInfo>(StringComparer.OrdinalIgnoreCase);
        long totalParams = 0;

        foreach (var prop in doc.RootElement.EnumerateObject()) {
            if (prop.NameEquals("__metadata__")) {
                if (prop.Value.ValueKind == JsonValueKind.Object) {
                    foreach (var m in prop.Value.EnumerateObject()) {
                        meta[m.Name] = m.Value.GetString() ?? m.Value.GetRawText();
                    }
                }
            } else {
                string dtype = prop.Value.TryGetProperty("dtype", out var dt) ? dt.GetString() ?? "F16" : "F16";
                var shapeList = new List<long>();
                long numel = 1;
                if (prop.Value.TryGetProperty("shape", out var sh) && sh.ValueKind == JsonValueKind.Array) {
                    foreach (var dim in sh.EnumerateArray()) {
                        long d = dim.GetInt64();
                        shapeList.Add(d);
                        numel *= d;
                    }
                }
                totalParams += numel;
                tensors[prop.Name] = new TensorInfo(dtype, shapeList, numel);
            }
        }

        return new ParsedHeader(meta, tensors, totalParams);
    }

    private static string DetectArch(Dictionary<string, string> metadata) {
        if (metadata.TryGetValue("ss_base_model_version", out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        if (metadata.TryGetValue("modelspec.architecture", out var a) && !string.IsNullOrWhiteSpace(a)) return a;
        return "Unknown";
    }

    public string ExportMarkdownReport(LoraDiffSummary summary) {
        var sb = new StringBuilder();
        sb.AppendLine($"# LoRAMancer LoRA Visual Diff Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Analysis Duration: {summary.Duration.TotalSeconds:F2}s");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine($"| Metric | Model A | Model B |");
        sb.AppendLine($"|---|---|---|");
        sb.AppendLine($"| **Filename** | `{summary.ModelNameA}` | `{summary.ModelNameB}` |");
        sb.AppendLine($"| **Architecture** | {summary.ArchA} | {summary.ArchB} |");
        sb.AppendLine($"| **Tensors** | {summary.TotalTensorsA} | {summary.TotalTensorsB} |");
        sb.AppendLine($"| **Parameters** | {summary.TotalParamsA:N0} | {summary.TotalParamsB:N0} |");
        sb.AppendLine($"| **Shared Tensors** | {summary.SharedTensorsCount} | {summary.SharedTensorsCount} |");
        sb.AppendLine($"| **Exclusive Tensors** | {summary.OnlyInACount} | {summary.OnlyInBCount} |");
        sb.AppendLine($"| **Avg Cosine Similarity** | `{summary.AverageCosineSimilarity:F4}` | `(Drift: {summary.AverageDriftScore:P2})` |");
        sb.AppendLine($"| **Compatibility Status** | **{summary.CompatibilityStatus}** | - |");
        sb.AppendLine();

        sb.AppendLine("## Top Divergent Layers");
        sb.AppendLine("| Layer Name | Cosine Sim | Drift | Norm A | Norm B | Rel Delta % | Level |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var layer in summary.LayerDiffs.Where(x => x.Status == "Matched").OrderByDescending(x => x.DriftScore).Take(25)) {
            sb.AppendLine($"| `{layer.LayerName}` | {layer.CosineSimilarity:F4} | {layer.DriftScore:F4} | {layer.NormA:F3} | {layer.NormB:F3} | {layer.RelativeDeltaPct:+0.0;-0.0;0.0}% | {layer.DivergenceLevel} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Recipe & Training Metadata Diffs");
        sb.AppendLine("| Key | Model A | Model B | Status |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var meta in summary.MetadataDiffs.Where(x => x.IsDifferent)) {
            sb.AppendLine($"| `{meta.Key}` | `{meta.ValueA ?? "-"}` | `{meta.ValueB ?? "-"}` | ⚠️ Changed |");
        }

        return sb.ToString();
    }
}
