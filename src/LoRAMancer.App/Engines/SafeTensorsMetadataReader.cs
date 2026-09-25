using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Engines;

public sealed class SafeTensorsMetadataReader {
    private const long MaxHeaderSizeBytes = 100 * 1024 * 1024; // 100 MB safeguard
    private readonly ModelArchitectureRegistry _registry;

    public SafeTensorsMetadataReader(ModelArchitectureRegistry? registry = null) {
        _registry = registry ?? new ModelArchitectureRegistry();
    }

    public async Task<LoraMetadata> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) {
            throw new FileNotFoundException("SafeTensors file not found", filePath);
        }

        FileInfo fileInfo = new FileInfo(filePath);
        long fileSize = fileInfo.Length;
        if (fileSize < 8) {
            throw new InvalidDataException("File is too small to be a valid SafeTensors file");
        }

        await using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);

        byte[] headerSizeBytes = new byte[8];
        int bytesRead = await stream.ReadAsync(headerSizeBytes.AsMemory(0, 8), cancellationToken);
        if (bytesRead < 8) {
            throw new InvalidDataException("Failed to read SafeTensors header size");
        }

        ulong headerLengthUlong = BinaryPrimitives.ReadUInt64LittleEndian(headerSizeBytes);
        if (headerLengthUlong > (ulong)MaxHeaderSizeBytes || (long)headerLengthUlong > fileSize - 8) {
            throw new InvalidDataException($"Invalid SafeTensors header length: {headerLengthUlong} bytes");
        }

        int headerLength = (int)headerLengthUlong;
        byte[] headerBytes = new byte[headerLength];
        int totalHeaderRead = 0;
        while (totalHeaderRead < headerLength) {
            int chunk = await stream.ReadAsync(headerBytes.AsMemory(totalHeaderRead, headerLength - totalHeaderRead), cancellationToken);
            if (chunk == 0) {
                break;
            }
            totalHeaderRead += chunk;
        }

        if (totalHeaderRead < headerLength) {
            throw new InvalidDataException("Unexpected end of file while reading SafeTensors header payload");
        }

        string headerJson = Encoding.UTF8.GetString(headerBytes);
        return ParseHeaderJson(filePath, fileInfo.Name, fileSize, headerJson);
    }

    private LoraMetadata ParseHeaderJson(string filePath, string fileName, long fileSize, string json) {
        Dictionary<string, string> metadataDict = new(StringComparer.OrdinalIgnoreCase);

        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("__metadata__", out JsonElement metaElement) && metaElement.ValueKind == JsonValueKind.Object) {
                foreach (JsonProperty prop in metaElement.EnumerateObject()) {
                    metadataDict[prop.Name] = prop.Value.GetString() ?? prop.Value.GetRawText();
                }
            } else {
                foreach (JsonProperty prop in root.EnumerateObject()) {
                    if (prop.Name != "__metadata__" && prop.Value.ValueKind == JsonValueKind.String) {
                        metadataDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }
        } catch (JsonException) {
            // Return base metadata if JSON is non-conforming
        }

        int? networkDim = ExtractInt(metadataDict, "ss_network_dim", "network_dim", "rank");
        double? networkAlpha = ExtractDouble(metadataDict, "ss_network_alpha", "network_alpha", "alpha");
        string networkModule = ExtractString(metadataDict, "ss_network_module", "network_module");
        double? learningRate = ExtractDouble(metadataDict, "ss_learning_rate", "learning_rate", "lr");
        double? unetLr = ExtractDouble(metadataDict, "ss_unet_lr", "unet_lr");
        double? textEncoderLr = ExtractDouble(metadataDict, "ss_text_encoder_lr", "text_encoder_lr");
        string optimizer = ExtractString(metadataDict, "ss_optimizer", "optimizer");
        string lrScheduler = ExtractString(metadataDict, "ss_lr_scheduler", "lr_scheduler", "scheduler");
        int? epochs = ExtractInt(metadataDict, "ss_epoch", "epoch", "epochs", "ss_num_epochs");
        int? totalSteps = ExtractInt(metadataDict, "ss_max_train_steps", "max_train_steps", "steps");
        string resolution = ExtractString(metadataDict, "ss_resolution", "resolution");
        string precision = ExtractString(metadataDict, "ss_mixed_precision", "mixed_precision", "precision");

        // Check explicit architecture specifications first, before generic model checkpoint filenames
        string baseModel = ExtractString(metadataDict, "modelspec.architecture", "ss_base_model_version", "ss_model_type", "base_model", "ss_sd_model_name");
        ModelArchitectureInfo inferred = _registry.InferFromMetadata(metadataDict);
        if (!string.IsNullOrWhiteSpace(baseModel) && !baseModel.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) {
            var specificInferred = _registry.InferFromMetadata(new Dictionary<string, string> { ["base_model"] = baseModel });
            if (!specificInferred.Id.Equals("flux_1_dev", StringComparison.OrdinalIgnoreCase) || baseModel.Contains("flux", StringComparison.OrdinalIgnoreCase)) {
                inferred = specificInferred;
            }
        }
        baseModel = inferred.DisplayName;

        return new LoraMetadata {
            FileName = fileName,
            FilePath = filePath,
            FileSizeBytes = fileSize,
            BaseModel = string.IsNullOrWhiteSpace(baseModel) ? "Unknown" : baseModel,
            NetworkDim = networkDim,
            NetworkAlpha = networkAlpha,
            NetworkModule = networkModule,
            LearningRate = learningRate,
            UnetLearningRate = unetLr,
            TextEncoderLearningRate = textEncoderLr,
            Optimizer = optimizer,
            LrScheduler = lrScheduler,
            Epochs = epochs,
            TotalSteps = totalSteps,
            Resolution = resolution,
            Precision = precision,
            RawHeaderMetadata = metadataDict
        };
    }


    private static string ExtractString(IReadOnlyDictionary<string, string> dict, params string[] candidateKeys) {
        foreach (string key in candidateKeys) {
            if (dict.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)) {
                return value;
            }
        }
        return string.Empty;
    }

    private static int? ExtractInt(IReadOnlyDictionary<string, string> dict, params string[] candidateKeys) {
        foreach (string key in candidateKeys) {
            if (dict.TryGetValue(key, out string? value) && int.TryParse(value, out int result)) {
                return result;
            }
        }
        return null;
    }

    private static double? ExtractDouble(IReadOnlyDictionary<string, string> dict, params string[] candidateKeys) {
        foreach (string key in candidateKeys) {
            if (dict.TryGetValue(key, out string? value) && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double result)) {
                return result;
            }
        }
        return null;
    }
}
