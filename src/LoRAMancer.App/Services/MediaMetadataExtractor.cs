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
            if (string.IsNullOrWhiteSpace(item.Prompt)) {
                ParseComfyUiWorkflowJson(item, text);
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
                    ParseModusFlowTextEditor(item, inputs, doc.RootElement);
                }
                // 2b. ModusFlow Prompt Mixer
                else if (classType.Equals("ModusFlowPromptMixer", StringComparison.OrdinalIgnoreCase)) {
                    string mixed = EvaluateModusFlowPromptMixer(inputs, doc.RootElement);
                    if (!string.IsNullOrWhiteSpace(mixed) && string.IsNullOrWhiteSpace(item.Prompt)) {
                        item.Prompt = mixed;
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

    private static void ParseModusFlowTextEditor(ShowcaseMediaItem item, JsonElement inputs, JsonElement rootDoc) {
        string? rawPos = ResolveInputString(inputs, "positive_input", rootDoc);
        if (string.IsNullOrWhiteSpace(rawPos)) {
            rawPos = ResolveInputString(inputs, "positive", rootDoc) ?? string.Empty;
        }

        string? rawNeg = ResolveInputString(inputs, "negative_input", rootDoc);
        if (string.IsNullOrWhiteSpace(rawNeg)) {
            rawNeg = ResolveInputString(inputs, "negative", rootDoc) ?? string.Empty;
        }

        string? curator1 = ResolveInputString(inputs, "curator_input", rootDoc);
        string? curator2 = ResolveInputString(inputs, "curator_input_2", rootDoc);
        string? curatorNeg = ResolveInputString(inputs, "curator_negative", rootDoc);
        string? posEmbedding = ResolveInputString(inputs, "positive_embedding", rootDoc);
        string? negEmbedding = ResolveInputString(inputs, "negative_embedding", rootDoc);

        string weightMode = "Pass-Through (SDXL / Pony)";
        if (inputs.TryGetProperty("weight_mode", out JsonElement wmElem) && wmElem.ValueKind == JsonValueKind.String) {
            weightMode = wmElem.GetString() ?? weightMode;
        }

        long seed = 0;
        if (inputs.TryGetProperty("seed", out JsonElement sElem) && sElem.TryGetInt64(out long parsedSeed)) {
            seed = parsedSeed;
            if (seed > 0 && (item.Seed == null || item.Seed == 0)) {
                item.Seed = seed;
            }
        }

        bool muteNegative = false;
        if (inputs.TryGetProperty("mute_negative", out JsonElement mnElem)) {
            if (mnElem.ValueKind == JsonValueKind.True || mnElem.ValueKind == JsonValueKind.False) {
                muteNegative = mnElem.GetBoolean();
            } else if (mnElem.ValueKind == JsonValueKind.String && bool.TryParse(mnElem.GetString(), out bool parsedMn)) {
                muteNegative = parsedMn;
            } else if (mnElem.ValueKind == JsonValueKind.Number && mnElem.TryGetInt32(out int mnInt)) {
                muteNegative = mnInt != 0;
            }
        }

        string cleanPos = ProcessModusFlowText(rawPos, weightMode, seed, curator1, curator2, null, posEmbedding, item.UsedLoras);
        if (!string.IsNullOrWhiteSpace(cleanPos)) {
            if (string.IsNullOrWhiteSpace(item.Prompt)) {
                item.Prompt = cleanPos;
            } else if (!item.Prompt.Contains(cleanPos)) {
                item.Prompt = $"{item.Prompt}, {cleanPos}";
            }
        }

        if (muteNegative) {
            item.NegativePrompt = string.Empty;
        } else {
            string cleanNeg = ProcessModusFlowText(rawNeg, weightMode, seed, null, null, curatorNeg, negEmbedding, item.UsedLoras);
            if (!string.IsNullOrWhiteSpace(cleanNeg)) {
                if (string.IsNullOrWhiteSpace(item.NegativePrompt)) {
                    item.NegativePrompt = cleanNeg;
                } else if (!item.NegativePrompt.Contains(cleanNeg)) {
                    item.NegativePrompt = $"{item.NegativePrompt}, {cleanNeg}";
                }
            }
        }
    }

    private static string ProcessModusFlowText(
        string text,
        string weightMode,
        long seed,
        string? curatorInput1,
        string? curatorInput2,
        string? curatorNegative,
        string? embedding,
        IList<string> usedLoras) {

        if (string.IsNullOrWhiteSpace(text)) {
            return string.Empty;
        }

        var loraMatches = Regex.Matches(text, @"<lora:([^:>]+):?([^>]*)>");
        foreach (Match m in loraMatches) {
            string name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !usedLoras.Contains(name)) {
                usedLoras.Add(name);
            }
        }
        text = Regex.Replace(text, @"<lora:[^>]+>", "");

        if (!string.IsNullOrWhiteSpace(curatorInput1)) {
            string c1 = curatorInput1.Trim();
            var p1 = new Regex(@"\{(?:curator|curator1|curator_1|list|item)\}", RegexOptions.IgnoreCase);
            if (p1.IsMatch(text)) {
                text = p1.Replace(text, c1);
            } else {
                text = $"{text}, {c1}".Trim(',', ' ');
            }
        }

        if (!string.IsNullOrWhiteSpace(curatorInput2)) {
            string c2 = curatorInput2.Trim();
            var p2 = new Regex(@"\{(?:curator2|curator_2|list2|item2)\}", RegexOptions.IgnoreCase);
            if (p2.IsMatch(text)) {
                text = p2.Replace(text, c2);
            } else {
                text = $"{text}, {c2}".Trim(',', ' ');
            }
        }

        if (!string.IsNullOrWhiteSpace(curatorNegative)) {
            string cn = curatorNegative.Trim();
            var pn = new Regex(@"\{(?:curator|curator_negative|list|item)\}", RegexOptions.IgnoreCase);
            if (pn.IsMatch(text)) {
                text = pn.Replace(text, cn);
            } else {
                text = $"{text}, {cn}".Trim(',', ' ');
            }
        }

        text = FilterModusFlowComments(text);
        text = ResolveDynamicPrompts(text, seed);
        text = TranslateWeights(text, weightMode);

        if (!string.IsNullOrWhiteSpace(embedding)) {
            text = $"{text}, {embedding.Trim()}".Trim(',', ' ');
        }

        text = Regex.Replace(text, @"\r\n|\r|\n", ", ");
        text = Regex.Replace(text, @",\s*,+", ", ");
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"^[,\s]+", "");
        text = Regex.Replace(text, @"[,\s]+$", "");

        return text.Trim();
    }

    private static string FilterModusFlowComments(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return string.Empty;
        }

        text = Regex.Replace(text, @"/\*[\s\S]*?\*/", "");
        text = Regex.Replace(text, @"^\s*(?:#|//).*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"(?<!https:)(?<!http:)\s+//.*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\s+#\s+.*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @",\s*,+", ", ");
        text = Regex.Replace(text, @"[ \t]+", " ");

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        List<string> cleanLines = new();
        foreach (var rawLine in lines) {
            string line = rawLine.Trim();
            line = Regex.Replace(line, @"^,\s*", "");
            line = Regex.Replace(line, @",\s*,+", ", ");
            if (!string.IsNullOrWhiteSpace(line) && line != ",") {
                cleanLines.Add(line);
            }
        }

        return string.Join("\n", cleanLines).Trim();
    }

    private static string ResolveDynamicPrompts(string text, long seed) {
        if (string.IsNullOrWhiteSpace(text)) {
            return string.Empty;
        }

        Random rng = seed != 0 ? new Random((int)(seed & 0x7FFFFFFF)) : new Random();

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        List<string> cleanLines = new();
        foreach (var line in lines) {
            var m = Regex.Match(line, @"^\s*\$([a-zA-Z0-9_]+)\s*=\s*([^;\n]+)[;\n]?");
            if (m.Success) {
                string varName = m.Groups[1].Value.Trim();
                string varExpr = m.Groups[2].Value.Trim();
                variables[varName] = varExpr;
            } else {
                cleanLines.Add(line);
            }
        }
        text = string.Join("\n", cleanLines);

        foreach (var kvp in variables) {
            text = Regex.Replace(text, $@"\${Regex.Escape(kvp.Key)}\b", kvp.Value);
        }

        text = Regex.Replace(text, @"\{shuffle:\s*([^{}]+)\}", m => {
            var items = m.Groups[1].Value.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            for (int i = items.Count - 1; i > 0; i--) {
                int j = rng.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
            return string.Join(", ", items);
        }, RegexOptions.IgnoreCase);

        var choicePattern = new Regex(@"\{([^{}]+)\}");
        for (int pass = 0; pass < 10; pass++) {
            if (!choicePattern.IsMatch(text)) {
                break;
            }
            text = choicePattern.Replace(text, m => {
                string content = m.Groups[1].Value.Trim();
                string? countSpec = null;
                if (content.Contains("$$")) {
                    var parts = content.Split(new[] { "$$" }, 2, StringSplitOptions.None);
                    countSpec = parts[0].Trim();
                    content = parts[1].Trim();
                }

                string[] rawOptions;
                if (content.Contains('|')) {
                    rawOptions = content.Split('|');
                } else if (countSpec != null && content.Contains(',')) {
                    rawOptions = content.Split(',');
                } else {
                    rawOptions = new[] { content };
                }

                List<(string Item, double Weight)> weightedOptions = new();
                foreach (var opt in rawOptions) {
                    string trimmed = opt.Trim();
                    if (string.IsNullOrEmpty(trimmed)) {
                        continue;
                    }
                    var wm = Regex.Match(trimmed, @"^([0-9.]+)::(.*)$");
                    if (wm.Success && double.TryParse(wm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w)) {
                        weightedOptions.Add((wm.Groups[2].Value.Trim(), Math.Max(0.001, w)));
                    } else {
                        weightedOptions.Add((trimmed, 1.0));
                    }
                }

                if (weightedOptions.Count == 0) {
                    return string.Empty;
                }

                int k = 1;
                if (!string.IsNullOrEmpty(countSpec)) {
                    if (countSpec.Contains('-')) {
                        var range = countSpec.Split('-');
                        if (range.Length == 2 && int.TryParse(range[0], out int low) && int.TryParse(range[1], out int high)) {
                            low = Math.Max(1, Math.Min(low, weightedOptions.Count));
                            high = Math.Max(low, Math.Min(high, weightedOptions.Count));
                            k = rng.Next(low, high + 1);
                        }
                    } else if (int.TryParse(countSpec, out int parsedK)) {
                        k = Math.Max(1, Math.Min(parsedK, weightedOptions.Count));
                    }
                }

                List<string> picked = new();
                var pool = new List<(string Item, double Weight)>(weightedOptions);
                for (int i = 0; i < k && pool.Count > 0; i++) {
                    double totalWeight = pool.Sum(p => p.Weight);
                    double r = rng.NextDouble() * totalWeight;
                    double acc = 0;
                    int chosenIdx = pool.Count - 1;
                    for (int pIdx = 0; pIdx < pool.Count; pIdx++) {
                        acc += pool[pIdx].Weight;
                        if (r <= acc) {
                            chosenIdx = pIdx;
                            break;
                        }
                    }
                    picked.Add(pool[chosenIdx].Item);
                    pool.RemoveAt(chosenIdx);
                }

                return string.Join(", ", picked);
            });
        }

        text = Regex.Replace(text, @"__(?:([0-9]+(?:-[0-9]+)?)\$\$)?([a-zA-Z0-9_\-]+)__", "$2");
        return text;
    }

    private static string TranslateWeights(string text, string weightMode) {
        if (string.IsNullOrWhiteSpace(text) || weightMode.StartsWith("Pass-Through", StringComparison.OrdinalIgnoreCase)) {
            return text;
        }

        List<string> frontItems = new();
        text = Regex.Replace(text, @"\(([^():]+):([0-9.]+)\)", match => {
            string term = match.Groups[1].Value.Trim();
            string wStr = match.Groups[2].Value.Trim();
            if (!double.TryParse(wStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w)) {
                return match.Value;
            }

            if (weightMode.StartsWith("Strip", StringComparison.OrdinalIgnoreCase)) {
                return term;
            }

            string transformed;
            if (w >= 1.35) {
                transformed = $"strikingly intense {term}, emphasizing {term}";
            } else if (w >= 1.20) {
                transformed = $"prominently featuring {term}, distinct {term}";
            } else if (w >= 1.10) {
                transformed = $"vivid {term}";
            } else if (w <= 0.75) {
                transformed = $"faint, barely visible {term}";
            } else if (w <= 0.90) {
                transformed = $"subtle {term}";
            } else {
                transformed = term;
            }

            if (weightMode.StartsWith("Front-Load", StringComparison.OrdinalIgnoreCase) && w >= 1.20) {
                frontItems.Add(transformed);
                return string.Empty;
            }
            return transformed;
        });

        if (weightMode.StartsWith("Strip", StringComparison.OrdinalIgnoreCase) ||
            weightMode.Contains("Chroma", StringComparison.OrdinalIgnoreCase) ||
            weightMode.Contains("Flux", StringComparison.OrdinalIgnoreCase)) {
            text = Regex.Replace(text, @"\(([a-zA-Z0-9_\s\-]+)\)", "$1");
        }

        text = Regex.Replace(text, @",\s*,+", ", ");
        text = Regex.Replace(text, @"^[,\s]+", "");
        text = Regex.Replace(text, @"[,\s]+$", "");

        if (frontItems.Count > 0) {
            string frontStr = string.Join(", ", frontItems);
            text = string.IsNullOrWhiteSpace(text) ? frontStr : $"{frontStr}, {text}";
        }

        return text;
    }

    private static string? ResolveInputString(JsonElement inputs, string propName, JsonElement rootDoc) {
        if (!inputs.TryGetProperty(propName, out JsonElement elem)) {
            return null;
        }
        if (elem.ValueKind == JsonValueKind.String) {
            return elem.GetString();
        }
        if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() >= 2) {
            string? targetNodeId = null;
            var firstElem = elem[0];
            if (firstElem.ValueKind == JsonValueKind.String) {
                targetNodeId = firstElem.GetString();
            } else if (firstElem.ValueKind == JsonValueKind.Number) {
                targetNodeId = firstElem.GetInt64().ToString();
            }
            int slot = 0;
            var secondElem = elem[1];
            if (secondElem.ValueKind == JsonValueKind.Number) {
                slot = secondElem.GetInt32();
            }

            if (!string.IsNullOrEmpty(targetNodeId) && rootDoc.ValueKind == JsonValueKind.Object && rootDoc.TryGetProperty(targetNodeId, out JsonElement targetNode)) {
                return ResolveNodeOutput(targetNode, slot, rootDoc);
            }
        }
        return null;
    }

    private static string? ResolveNodeOutput(JsonElement node, int slot, JsonElement rootDoc) {
        if (!node.TryGetProperty("class_type", out JsonElement classTypeElem)) {
            return null;
        }
        string classType = classTypeElem.GetString() ?? string.Empty;
        if (!node.TryGetProperty("inputs", out JsonElement inputs)) {
            return null;
        }

        if (classType.Equals("ModusFlowPromptMixer", StringComparison.OrdinalIgnoreCase)) {
            return EvaluateModusFlowPromptMixer(inputs, rootDoc);
        }
        if (classType.Equals("ModusFlowListCurator", StringComparison.OrdinalIgnoreCase)) {
            return EvaluateModusFlowListCurator(inputs);
        }
        if (classType.Equals("ModusFlowTextEditor", StringComparison.OrdinalIgnoreCase)) {
            string prop = slot == 1 ? "negative" : "positive";
            string inputProp = slot == 1 ? "negative_input" : "positive_input";
            string? val = ResolveInputString(inputs, inputProp, rootDoc);
            if (string.IsNullOrWhiteSpace(val)) {
                val = ResolveInputString(inputs, prop, rootDoc);
            }
            return val;
        }
        if (inputs.TryGetProperty("text", out JsonElement tElem) && tElem.ValueKind == JsonValueKind.String) {
            return tElem.GetString();
        }
        if (inputs.TryGetProperty("string", out JsonElement sElem) && sElem.ValueKind == JsonValueKind.String) {
            return sElem.GetString();
        }
        if (inputs.TryGetProperty("value", out JsonElement vElem) && vElem.ValueKind == JsonValueKind.String) {
            return vElem.GetString();
        }
        if (inputs.TryGetProperty("text_input", out JsonElement tiElem) && tiElem.ValueKind == JsonValueKind.String) {
            return tiElem.GetString();
        }
        return null;
    }

    private static string EvaluateModusFlowPromptMixer(JsonElement inputs, JsonElement rootDoc) {
        string delimiter = ", ";
        if (inputs.TryGetProperty("delimiter", out JsonElement dElem) && dElem.ValueKind == JsonValueKind.String) {
            delimiter = dElem.GetString() ?? ", ";
        }

        bool cleanPunctuation = true;
        if (inputs.TryGetProperty("clean_punctuation", out JsonElement cpElem) && (cpElem.ValueKind == JsonValueKind.True || cpElem.ValueKind == JsonValueKind.False)) {
            cleanPunctuation = cpElem.GetBoolean();
        }

        List<string> parts = new();

        string? basePrompt = ResolveInputString(inputs, "base_prompt", rootDoc);
        if (!string.IsNullOrWhiteSpace(basePrompt)) {
            parts.Add(basePrompt.Trim());
        }

        for (int i = 1; i <= 5; i++) {
            bool enabled = true;
            if (inputs.TryGetProperty($"slot{i}_enabled", out JsonElement enElem) && (enElem.ValueKind == JsonValueKind.True || enElem.ValueKind == JsonValueKind.False)) {
                enabled = enElem.GetBoolean();
            }
            if (!enabled) {
                continue;
            }

            string? text = ResolveInputString(inputs, $"slot{i}_in", rootDoc);
            if (string.IsNullOrWhiteSpace(text)) {
                text = ResolveInputString(inputs, $"slot{i}_text", rootDoc);
            }
            if (string.IsNullOrWhiteSpace(text)) {
                continue;
            }

            text = text.Trim();
            string prefix = string.Empty;
            if (inputs.TryGetProperty($"slot{i}_prefix", out JsonElement prefElem) && prefElem.ValueKind == JsonValueKind.String) {
                prefix = prefElem.GetString()?.Trim() ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(prefix)) {
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    text = $"{prefix} {text}";
                }
            }

            parts.Add(text);
        }

        string joined = string.Join(delimiter, parts).Trim();

        if (cleanPunctuation) {
            joined = Regex.Replace(joined, @",\s*,+", ", ");
            joined = Regex.Replace(joined, @"^[,\s]+", "");
            joined = Regex.Replace(joined, @"[,\s]+$", "");
            joined = Regex.Replace(joined, @"[ \t]+", " ");
        }

        return joined;
    }

    private static string EvaluateModusFlowListCurator(JsonElement inputs) {
        string custom = string.Empty;
        if (inputs.TryGetProperty("custom_entries", out JsonElement cElem) && cElem.ValueKind == JsonValueKind.String) {
            custom = cElem.GetString()?.Trim() ?? string.Empty;
        }

        string wildcard = string.Empty;
        if (inputs.TryGetProperty("wildcard_list", out JsonElement wElem) && wElem.ValueKind == JsonValueKind.String) {
            string w = wElem.GetString()?.Trim() ?? string.Empty;
            if (!w.StartsWith("--", StringComparison.OrdinalIgnoreCase)) {
                wildcard = w;
            }
        }

        string prefix = string.Empty;
        if (inputs.TryGetProperty("prefix", out JsonElement pElem) && pElem.ValueKind == JsonValueKind.String) {
            prefix = pElem.GetString()?.Trim() ?? string.Empty;
        }

        string suffix = string.Empty;
        if (inputs.TryGetProperty("suffix", out JsonElement sElem) && sElem.ValueKind == JsonValueKind.String) {
            suffix = sElem.GetString()?.Trim() ?? string.Empty;
        }

        string result = string.Empty;
        if (!string.IsNullOrWhiteSpace(custom)) {
            string[] lines = custom.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0) {
                string mode = string.Empty;
                if (inputs.TryGetProperty("mode", out JsonElement mElem) && mElem.ValueKind == JsonValueKind.String) {
                    mode = mElem.GetString() ?? string.Empty;
                }
                if (mode.Contains("All Items", StringComparison.OrdinalIgnoreCase)) {
                    result = string.Join(mode.Contains("Newline", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", lines.Select(l => l.Trim()));
                } else {
                    result = lines[0].Trim();
                }
            }
        } else if (!string.IsNullOrWhiteSpace(wildcard)) {
            result = wildcard;
        }

        if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(result)) {
            result = $"{prefix} {result}".Trim();
        }
        if (!string.IsNullOrEmpty(suffix) && !string.IsNullOrEmpty(result)) {
            result = $"{result} {suffix}".Trim();
        }

        return result;
    }

    private static void ParseComfyUiWorkflowJson(ShowcaseMediaItem item, string jsonText) {
        try {
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            if (!doc.RootElement.TryGetProperty("nodes", out JsonElement nodesElem) || nodesElem.ValueKind != JsonValueKind.Array) {
                return;
            }

            foreach (JsonElement node in nodesElem.EnumerateArray()) {
                if (!node.TryGetProperty("type", out JsonElement typeElem)) {
                    continue;
                }
                string nodeType = typeElem.GetString() ?? string.Empty;

                if (nodeType.Contains("ModusFlow", StringComparison.OrdinalIgnoreCase)) {
                    item.IsAiGenerated = true;
                    item.AiGenerator = "ComfyUI (ModusFlow)";
                }

                if (nodeType.Equals("ModusFlowTextEditor", StringComparison.OrdinalIgnoreCase)) {
                    if (node.TryGetProperty("widgets_values", out JsonElement wvElem) && wvElem.ValueKind == JsonValueKind.Array) {
                        string pos = wvElem.GetArrayLength() > 0 && wvElem[0].ValueKind == JsonValueKind.String ? wvElem[0].GetString() ?? string.Empty : string.Empty;
                        string neg = wvElem.GetArrayLength() > 1 && wvElem[1].ValueKind == JsonValueKind.String ? wvElem[1].GetString() ?? string.Empty : string.Empty;
                        string weightMode = wvElem.GetArrayLength() > 3 && wvElem[3].ValueKind == JsonValueKind.String ? wvElem[3].GetString() ?? "Pass-Through (SDXL / Pony)" : "Pass-Through (SDXL / Pony)";
                        long seed = 0;
                        if (wvElem.GetArrayLength() > 4 && wvElem[4].ValueKind == JsonValueKind.Number) {
                            wvElem[4].TryGetInt64(out seed);
                        }

                        if (seed > 0 && (item.Seed == null || item.Seed == 0)) {
                            item.Seed = seed;
                        }

                        string cleanPos = ProcessModusFlowText(pos, weightMode, seed, null, null, null, null, item.UsedLoras);
                        if (!string.IsNullOrWhiteSpace(cleanPos)) {
                            if (string.IsNullOrWhiteSpace(item.Prompt)) {
                                item.Prompt = cleanPos;
                            } else if (!item.Prompt.Contains(cleanPos)) {
                                item.Prompt = $"{item.Prompt}, {cleanPos}";
                            }
                        }

                        string cleanNeg = ProcessModusFlowText(neg, weightMode, seed, null, null, null, null, item.UsedLoras);
                        if (!string.IsNullOrWhiteSpace(cleanNeg)) {
                            if (string.IsNullOrWhiteSpace(item.NegativePrompt)) {
                                item.NegativePrompt = cleanNeg;
                            } else if (!item.NegativePrompt.Contains(cleanNeg)) {
                                item.NegativePrompt = $"{item.NegativePrompt}, {cleanNeg}";
                            }
                        }
                    }
                }
            }
        } catch {
            // Ignore workflow parse failure
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
                            } else if (doc.RootElement.TryGetProperty("workflow", out JsonElement wfElem)) {
                                ParseComfyUiWorkflowJson(item, wfElem.GetRawText());
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
                } else if (doc.RootElement.TryGetProperty("workflow", out JsonElement wf)) {
                    ParseComfyUiWorkflowJson(item, wf.GetRawText());
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
