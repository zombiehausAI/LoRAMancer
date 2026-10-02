using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public static class MediaMetadataExtractor {
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".mp4", ".webm", ".mov", ".mkv", ".avi"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".wav", ".mp3", ".flac", ".ogg", ".m4a"
    };

    public static ShowcaseMediaType GetMediaType(string extension) {
        if (VideoExtensions.Contains(extension)) {
            return ShowcaseMediaType.Video;
        }
        if (AudioExtensions.Contains(extension)) {
            return ShowcaseMediaType.Audio;
        }
        return ShowcaseMediaType.Image;
    }

    public static bool IsSupportedMedia(string extension) {
        return ImageExtensions.Contains(extension) ||
               VideoExtensions.Contains(extension) ||
               AudioExtensions.Contains(extension);
    }

    public static async Task<ShowcaseMediaItem> ExtractAsync(string filePath, CancellationToken cancellationToken = default) {
        FileInfo fi = new(filePath);
        string ext = fi.Extension.ToLowerInvariant();
        ShowcaseMediaType type = GetMediaType(ext);

        ShowcaseMediaItem item = new() {
            FilePath = filePath,
            FileName = fi.Name,
            FileExtension = ext,
            MediaType = type,
            FileSizeBytes = fi.Exists ? fi.Length : 0,
            FormattedSize = FormatBytes(fi.Exists ? fi.Length : 0),
            CreatedDate = fi.Exists ? fi.CreationTimeUtc : DateTime.UtcNow,
            FolderPath = fi.DirectoryName ?? string.Empty,
            FolderName = Path.GetFileName(fi.DirectoryName ?? string.Empty),
            FileUrl = "file:///" + filePath.Replace('\\', '/')
        };

        if (!fi.Exists) {
            return item;
        }

        try {
            if (ext == ".png") {
                await ExtractPngMetadataAsync(item, filePath, cancellationToken);
            } else if (ext == ".jpg" || ext == ".jpeg" || ext == ".webp") {
                await ExtractExifAndTextMetadataAsync(item, filePath, cancellationToken);
            }

            // Check for companion metadata files (.json or .txt with prompt/recipe)
            await CheckCompanionMetadataAsync(item, filePath, cancellationToken);
        } catch {
            // Keep item with basic file info if metadata parsing encounters format peculiarities
        }

        return item;
    }

    private static async Task ExtractPngMetadataAsync(ShowcaseMediaItem item, string filePath, CancellationToken cancellationToken) {
        byte[] pngHeader = new byte[8];
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (await fs.ReadAsync(pngHeader.AsMemory(0, 8), cancellationToken) != 8) {
            return;
        }

        // Verify PNG signature
        if (pngHeader[0] != 0x89 || pngHeader[1] != 0x50 || pngHeader[2] != 0x4E || pngHeader[3] != 0x47) {
            return;
        }

        byte[] lengthBuffer = new byte[4];
        byte[] typeBuffer = new byte[4];

        while (fs.Position < fs.Length - 8) {
            if (await fs.ReadAsync(lengthBuffer.AsMemory(0, 4), cancellationToken) != 4) {
                break;
            }
            if (await fs.ReadAsync(typeBuffer.AsMemory(0, 4), cancellationToken) != 4) {
                break;
            }

            Array.Reverse(lengthBuffer);
            uint chunkLength = BitConverter.ToUInt32(lengthBuffer, 0);
            string chunkType = Encoding.ASCII.GetString(typeBuffer);

            if (chunkType == "IHDR" && chunkLength >= 8) {
                byte[] ihdr = new byte[chunkLength];
                if (await fs.ReadAsync(ihdr.AsMemory(0, (int)chunkLength), cancellationToken) == chunkLength) {
                    byte[] wBuf = new byte[4];
                    byte[] hBuf = new byte[4];
                    Array.Copy(ihdr, 0, wBuf, 0, 4);
                    Array.Copy(ihdr, 4, hBuf, 0, 4);
                    Array.Reverse(wBuf);
                    Array.Reverse(hBuf);
                    item.Width = (int)BitConverter.ToUInt32(wBuf, 0);
                    item.Height = (int)BitConverter.ToUInt32(hBuf, 0);
                }
                fs.Seek(4, SeekOrigin.Current); // skip CRC
                continue;
            }

            if (chunkType == "tEXt" && chunkLength < 10_000_000) {
                byte[] chunkData = new byte[chunkLength];
                if (await fs.ReadAsync(chunkData.AsMemory(0, (int)chunkLength), cancellationToken) == chunkLength) {
                    ParseTextChunk(item, chunkData);
                }
                fs.Seek(4, SeekOrigin.Current); // skip CRC
                continue;
            }

            if (chunkType == "IEND") {
                break;
            }

            // Skip chunk data + 4-byte CRC
            fs.Seek(chunkLength + 4, SeekOrigin.Current);
        }
    }

    private static void ParseTextChunk(ShowcaseMediaItem item, byte[] data) {
        int nullIdx = Array.IndexOf(data, (byte)0);
        if (nullIdx <= 0) {
            return;
        }

        string keyword = Encoding.Latin1.GetString(data, 0, nullIdx);
        string text = Encoding.UTF8.GetString(data, nullIdx + 1, data.Length - (nullIdx + 1)).Trim();

        item.RawMetadata[keyword] = text;

        if (string.Equals(keyword, "parameters", StringComparison.OrdinalIgnoreCase)) {
            item.IsAiGenerated = true;
            item.AiGenerator = "Automatic1111 / Forge";
            ParseA1111Parameters(item, text);
        } else if (string.Equals(keyword, "prompt", StringComparison.OrdinalIgnoreCase)) {
            item.IsAiGenerated = true;
            item.AiGenerator = "ComfyUI Workflow";
            ParseComfyUiPromptJson(item, text);
        } else if (string.Equals(keyword, "workflow", StringComparison.OrdinalIgnoreCase)) {
            item.IsAiGenerated = true;
            if (string.IsNullOrWhiteSpace(item.AiGenerator)) {
                item.AiGenerator = "ComfyUI Workflow";
            }
        } else if (string.Equals(keyword, "Software", StringComparison.OrdinalIgnoreCase) && text.Contains("NovelAI", StringComparison.OrdinalIgnoreCase)) {
            item.IsAiGenerated = true;
            item.AiGenerator = "NovelAI";
        }
    }

    private static void ParseA1111Parameters(ShowcaseMediaItem item, string text) {
        string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        List<string> promptLines = new();
        string? negLine = null;
        string? paramsLine = null;

        foreach (string line in lines) {
            if (line.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase)) {
                negLine = line.Substring("Negative prompt:".Length).Trim();
            } else if (line.Contains("Steps:", StringComparison.OrdinalIgnoreCase) && line.Contains("Sampler:", StringComparison.OrdinalIgnoreCase)) {
                paramsLine = line;
            } else if (negLine == null && paramsLine == null) {
                promptLines.Add(line);
            }
        }

        if (string.IsNullOrWhiteSpace(item.Prompt) && promptLines.Count > 0) {
            item.Prompt = string.Join(" ", promptLines).Trim();
        }

        if (!string.IsNullOrWhiteSpace(negLine)) {
            item.NegativePrompt = negLine;
        }

        if (!string.IsNullOrWhiteSpace(paramsLine)) {
            var parts = paramsLine.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts) {
                var kvp = part.Split(':', 2);
                if (kvp.Length != 2) {
                    continue;
                }
                string k = kvp[0].Trim();
                string v = kvp[1].Trim();

                if (string.Equals(k, "Steps", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int steps)) {
                    item.Steps = steps;
                } else if (string.Equals(k, "Sampler", StringComparison.OrdinalIgnoreCase)) {
                    item.Sampler = v;
                } else if (string.Equals(k, "CFG scale", StringComparison.OrdinalIgnoreCase) && double.TryParse(v, out double cfg)) {
                    item.CfgScale = cfg;
                } else if (string.Equals(k, "Seed", StringComparison.OrdinalIgnoreCase) && long.TryParse(v, out long seed)) {
                    item.Seed = seed;
                } else if (string.Equals(k, "Model", StringComparison.OrdinalIgnoreCase)) {
                    item.ModelName = v;
                }
            }
        }

        // Extract <lora:name:strength>
        var loraMatches = Regex.Matches(item.Prompt, @"<lora:([^:>]+):?([^>]*)>");
        foreach (Match match in loraMatches) {
            string loraName = match.Groups[1].Value.Trim();
            if (!item.UsedLoras.Contains(loraName)) {
                item.UsedLoras.Add(loraName);
            }
        }
    }

    private static void ParseComfyUiPromptJson(ShowcaseMediaItem item, string jsonText) {
        try {
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            foreach (JsonProperty node in doc.RootElement.EnumerateObject()) {
                if (!node.Value.TryGetProperty("class_type", out JsonElement classTypeElem)) {
                    continue;
                }
                string classType = classTypeElem.GetString() ?? string.Empty;

                if (!node.Value.TryGetProperty("inputs", out JsonElement inputs)) {
                    continue;
                }

                bool isModusFlow = classType.Contains("ModusFlow", StringComparison.OrdinalIgnoreCase) ||
                                   classType.StartsWith("OllamaPromptRefiner", StringComparison.OrdinalIgnoreCase) ||
                                   classType.StartsWith("OllamaTextRefiner", StringComparison.OrdinalIgnoreCase) ||
                                   classType.StartsWith("AllInOneDetailer", StringComparison.OrdinalIgnoreCase);

                if (isModusFlow) {
                    item.IsAiGenerated = true;
                    item.AiGenerator = "ComfyUI (ModusFlow)";
                }

                // 1. ModusFlow Multi-CLIP Text Encode (supports up to 4 CLIP inputs)
                if (classType.Equals("ModusFlowMultiCLIPTextEncode", StringComparison.OrdinalIgnoreCase)) {
                    List<string> clipTexts = new();
                    for (int c = 1; c <= 4; c++) {
                        string key = $"text_clip{c}";
                        string enableKey = $"enable_clip{c}";
                        bool isEnabled = true;
                        if (inputs.TryGetProperty(enableKey, out JsonElement enElem) && (enElem.ValueKind == JsonValueKind.True || enElem.ValueKind == JsonValueKind.False)) {
                            isEnabled = enElem.GetBoolean();
                        }
                        if (isEnabled && inputs.TryGetProperty(key, out JsonElement tElem) && tElem.ValueKind == JsonValueKind.String) {
                            string val = tElem.GetString()?.Trim() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(val)) {
                                clipTexts.Add(val);
                            }
                        }
                    }
                    if (clipTexts.Count > 0) {
                        string combined = string.Join(", ", clipTexts);
                        if (string.IsNullOrWhiteSpace(item.Prompt)) {
                            item.Prompt = combined;
                        } else if (!item.Prompt.Contains(combined)) {
                            item.Prompt = $"{item.Prompt}, {combined}";
                        }
                    }
                }
                // 2. ModusFlow Text Editor
                else if (classType.Equals("ModusFlowTextEditor", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("positive", out JsonElement posElem) && posElem.ValueKind == JsonValueKind.String) {
                        string pos = posElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(pos)) {
                            if (string.IsNullOrWhiteSpace(item.Prompt)) {
                                item.Prompt = pos;
                            } else if (!item.Prompt.Contains(pos)) {
                                item.Prompt = $"{item.Prompt}, {pos}";
                            }
                        }
                    }
                    if (inputs.TryGetProperty("negative", out JsonElement negElem) && negElem.ValueKind == JsonValueKind.String) {
                        string neg = negElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(neg)) {
                            if (string.IsNullOrWhiteSpace(item.NegativePrompt)) {
                                item.NegativePrompt = neg;
                            } else if (!item.NegativePrompt.Contains(neg)) {
                                item.NegativePrompt = $"{item.NegativePrompt}, {neg}";
                            }
                        }
                    }
                }
                // 3. ModusFlow Ollama Prompt Refiner
                else if (classType.Contains("PromptRefiner", StringComparison.OrdinalIgnoreCase) || classType.Contains("OllamaTextRefiner", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("text_input", out JsonElement inElem) && inElem.ValueKind == JsonValueKind.String) {
                        string txt = inElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(txt) && string.IsNullOrWhiteSpace(item.Prompt)) {
                            item.Prompt = txt;
                        }
                    }
                    if (inputs.TryGetProperty("negative_prompt", out JsonElement npElem) && npElem.ValueKind == JsonValueKind.String) {
                        string ntxt = npElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(ntxt) && string.IsNullOrWhiteSpace(item.NegativePrompt)) {
                            item.NegativePrompt = ntxt;
                        }
                    }
                }
                // 4. ModusFlow Show Text
                else if (classType.Equals("ModusFlowShowText", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("text", out JsonElement showElem) && showElem.ValueKind == JsonValueKind.String) {
                        string st = showElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(st) && string.IsNullOrWhiteSpace(item.Prompt)) {
                            item.Prompt = st;
                        }
                    }
                }
                // 5. Standard CLIPTextEncode / Prompt nodes
                else if (classType.Equals("CLIPTextEncode", StringComparison.OrdinalIgnoreCase) || classType.Contains("Prompt", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("text", out JsonElement textProp) && textProp.ValueKind == JsonValueKind.String) {
                        string t = textProp.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(t)) {
                            if (string.IsNullOrWhiteSpace(item.Prompt)) {
                                item.Prompt = t;
                            } else if (string.IsNullOrWhiteSpace(item.NegativePrompt) && (t.Contains("low quality", StringComparison.OrdinalIgnoreCase) || t.Contains("worst quality", StringComparison.OrdinalIgnoreCase) || t.Length < item.Prompt.Length / 2)) {
                                item.NegativePrompt = t;
                            }
                        }
                    }
                }
                // 6. Audio nodes (ACE Step Audio)
                else if (classType.Contains("AceStepAudio", StringComparison.OrdinalIgnoreCase) || classType.Contains("SaveAudio", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("tags", out JsonElement tagsElem) && tagsElem.ValueKind == JsonValueKind.String) {
                        string tags = tagsElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(tags)) {
                            item.Prompt = tags;
                        }
                    }
                    if (inputs.TryGetProperty("lyrics", out JsonElement lyricsElem) && lyricsElem.ValueKind == JsonValueKind.String) {
                        string lyrics = lyricsElem.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(lyrics)) {
                            item.UserNotes = $"[Lyrics]: {lyrics}";
                        }
                    }
                }

                // KSampler parameters (Standard and ModusFlow KSampler / BatchKSampler)
                if (classType.Contains("KSampler", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("seed", out JsonElement seedProp) && seedProp.TryGetInt64(out long s)) {
                        item.Seed = s;
                    }
                    if (inputs.TryGetProperty("steps", out JsonElement stepsProp) && stepsProp.TryGetInt32(out int st)) {
                        item.Steps = st;
                    }
                    if (inputs.TryGetProperty("cfg", out JsonElement cfgProp) && cfgProp.TryGetDouble(out double cfg)) {
                        item.CfgScale = cfg;
                    }
                    if (inputs.TryGetProperty("sampler_name", out JsonElement samplerProp)) {
                        item.Sampler = samplerProp.GetString() ?? string.Empty;
                    }
                    if (inputs.TryGetProperty("scheduler", out JsonElement schedProp)) {
                        item.Scheduler = schedProp.GetString() ?? string.Empty;
                    }
                }

                // Checkpoint loaders (Standard, ModusFlow ModelLoader / FluxLoader)
                if (classType.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase) || classType.Contains("ModelLoader", StringComparison.OrdinalIgnoreCase) || classType.Contains("FluxLoader", StringComparison.OrdinalIgnoreCase)) {
                    if (inputs.TryGetProperty("ckpt_name", out JsonElement ckptProp)) {
                        string ckpt = ckptProp.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(ckpt)) {
                            item.ModelName = ckpt;
                        }
                    } else if (inputs.TryGetProperty("model_name", out JsonElement modelProp)) {
                        string m = modelProp.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(m)) {
                            item.ModelName = m;
                        }
                    }
                }

                // LoRA Loaders (Standard, ModusFlow PowerLoraLoader / LoraLoader)
                if (classType.Contains("Lora", StringComparison.OrdinalIgnoreCase)) {
                    foreach (var prop in inputs.EnumerateObject()) {
                        if (prop.Name.Contains("lora", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) {
                            string loraVal = prop.Value.GetString()?.Trim() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(loraVal) && !string.Equals(loraVal, "None", StringComparison.OrdinalIgnoreCase) && !item.UsedLoras.Contains(loraVal)) {
                                item.UsedLoras.Add(loraVal);
                            }
                        }
                    }
                }
            }
        } catch {
            // Ignore JSON parse errors on malformed payloads
        }
    }

    private static async Task ExtractExifAndTextMetadataAsync(ShowcaseMediaItem item, string filePath, CancellationToken cancellationToken) {
        // Sample first 128KB to search for AI parameters, ModusFlow, or ComfyUI strings embedded in JPEG/WebP
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int readSize = (int)Math.Min(fs.Length, 131072);
        byte[] buffer = new byte[readSize];
        int read = await fs.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken);
        if (read <= 0) {
            return;
        }

        string rawString = Encoding.Latin1.GetString(buffer, 0, read);

        // Check for embedded ComfyUI / ModusFlow workflow JSON in EXIF ImageDescription (JPEG) or XMP (WebP)
        if (rawString.Contains("ModusFlow", StringComparison.OrdinalIgnoreCase)) {
            item.IsAiGenerated = true;
            item.AiGenerator = "ComfyUI (ModusFlow)";
        }

        if (rawString.Contains("\"prompt\":") || rawString.Contains("\"workflow\":") || rawString.Contains("CLIPTextEncode") || rawString.Contains("ModusFlow")) {
            item.IsAiGenerated = true;
            if (string.IsNullOrWhiteSpace(item.AiGenerator)) {
                item.AiGenerator = "ComfyUI";
            }

            int promptIdx = rawString.IndexOf("{\"prompt\":", StringComparison.OrdinalIgnoreCase);
            if (promptIdx < 0) {
                promptIdx = rawString.IndexOf("\"prompt\":", StringComparison.OrdinalIgnoreCase);
            }

            if (promptIdx >= 0) {
                int start = rawString.LastIndexOf('{', promptIdx);
                if (start >= 0) {
                    string candidate = rawString.Substring(start);
                    int end = candidate.LastIndexOf('}');
                    if (end > 0) {
                        try {
                            string jsonCandidate = candidate.Substring(0, end + 1);
                            using var doc = JsonDocument.Parse(jsonCandidate);
                            if (doc.RootElement.TryGetProperty("prompt", out JsonElement pElem)) {
                                ParseComfyUiPromptJson(item, pElem.GetRawText());
                            } else {
                                ParseComfyUiPromptJson(item, jsonCandidate);
                            }
                        } catch {
                            // Ignore fallback json extraction failure
                        }
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(item.Prompt) && (rawString.Contains("parameters", StringComparison.OrdinalIgnoreCase) ||
            rawString.Contains("Steps:", StringComparison.OrdinalIgnoreCase) && rawString.Contains("Sampler:", StringComparison.OrdinalIgnoreCase))) {
            item.IsAiGenerated = true;
            if (string.IsNullOrWhiteSpace(item.AiGenerator)) {
                item.AiGenerator = "Stable Diffusion";
            }
            int idx = rawString.IndexOf("Steps:", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) {
                int start = Math.Max(0, idx - 300);
                string snippet = rawString.Substring(start, Math.Min(600, rawString.Length - start));
                ParseA1111Parameters(item, snippet);
            }
        }
    }

    private static async Task CheckCompanionMetadataAsync(ShowcaseMediaItem item, string filePath, CancellationToken cancellationToken) {
        string withoutExt = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, Path.GetFileNameWithoutExtension(filePath));
        string companionJson = withoutExt + ".json";
        string companionTxt = withoutExt + ".txt";

        if (File.Exists(companionJson)) {
            try {
                string json = await File.ReadAllTextAsync(companionJson, cancellationToken);
                item.RawMetadata["companion_json"] = json;
                item.IsAiGenerated = true;

                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("prompt", out JsonElement p)) {
                    if (p.ValueKind == JsonValueKind.Object) {
                        ParseComfyUiPromptJson(item, p.GetRawText());
                    } else if (p.ValueKind == JsonValueKind.String) {
                        item.Prompt = p.GetString() ?? item.Prompt;
                    }
                } else if (doc.RootElement.ValueKind == JsonValueKind.Object) {
                    // Try parsing whole document as Comfy prompt graph if it has node keys
                    ParseComfyUiPromptJson(item, json);
                }

                if (json.Contains("ModusFlow", StringComparison.OrdinalIgnoreCase)) {
                    item.AiGenerator = "ComfyUI (ModusFlow)";
                } else if (string.IsNullOrWhiteSpace(item.AiGenerator)) {
                    item.AiGenerator = "AI-Toolkit / Diffusers";
                }

                if (doc.RootElement.TryGetProperty("seed", out JsonElement s) && s.TryGetInt64(out long seedVal)) {
                    item.Seed = seedVal;
                }
                if (doc.RootElement.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String) {
                    item.ModelName = m.GetString() ?? item.ModelName;
                }
            } catch {
                // Ignore companion read failure
            }
        } else if (File.Exists(companionTxt) && string.IsNullOrWhiteSpace(item.Prompt)) {
            try {
                string txt = await File.ReadAllTextAsync(companionTxt, cancellationToken);
                if (!string.IsNullOrWhiteSpace(txt) && txt.Length < 1000) {
                    item.Prompt = txt.Trim();
                    item.IsAiGenerated = true;
                    if (string.IsNullOrWhiteSpace(item.AiGenerator)) {
                        item.AiGenerator = "Generative AI";
                    }
                }
            } catch {
                // Ignore
            }
        }

        // Path heuristic: files inside 'samples' or 'ComfyUI/output' directories
        if (!item.IsAiGenerated) {
            string lower = filePath.ToLowerInvariant();
            if (lower.Contains(@"\samples\") || lower.Contains("/samples/") ||
                lower.Contains(@"\comfyui\output") || lower.Contains("/comfyui/output")) {
                item.IsAiGenerated = true;
                item.AiGenerator = "Generative AI";
            }
        }
    }

    private static string FormatBytes(long bytes) {
        if (bytes < 1024) {
            return $"{bytes} B";
        }
        if (bytes < 1024 * 1024) {
            return $"{bytes / 1024.0:F1} KB";
        }
        if (bytes < 1024 * 1024 * 1024) {
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
