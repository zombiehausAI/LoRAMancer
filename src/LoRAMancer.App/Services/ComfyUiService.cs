using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public record ComfyUiConnectionStatus(
    bool IsConnected,
    string Endpoint,
    string Version,
    string Os,
    string DeviceName,
    List<string> AvailableCheckpoints,
    List<string> AvailableLoras,
    string? ErrorMessage = null
);

public record ComfyUiProgress(
    string Stage,
    int Step,
    int TotalSteps,
    int Percentage,
    string? PreviewImageBase64 = null
);

public sealed class ComfyUiService : IDisposable {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly string _clientId;

    public ComfyUiService(HttpClient httpClient, SettingsService settingsService) {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _clientId = Guid.NewGuid().ToString("N");
    }

    public string GetEffectiveEndpoint() {
        string ep = _settingsService.Current.ComfyUiEndpointUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ep)) {
            ep = "http://127.0.0.1:8188";
        }
        return ep.TrimEnd('/');
    }

    public async Task<ComfyUiConnectionStatus> CheckConnectionAsync(string? customEndpoint = null, CancellationToken cancellationToken = default) {
        string endpoint = (customEndpoint ?? GetEffectiveEndpoint()).TrimEnd('/');
        try {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/system_stats");
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) {
                return new ComfyUiConnectionStatus(false, endpoint, "", "", "", new(), new(), $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            var statsJson = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            string os = statsJson.TryGetProperty("system", out var sys) && sys.TryGetProperty("os", out var o) ? o.GetString() ?? "Unknown" : "Unknown";
            string version = statsJson.TryGetProperty("system", out sys) && sys.TryGetProperty("comfyui_version", out var cv) ? cv.GetString() ?? "Online" : "Online";
            
            string device = "GPU";
            if (statsJson.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0) {
                var firstDev = devs[0];
                if (firstDev.TryGetProperty("name", out var dName)) {
                    device = dName.GetString() ?? "GPU";
                }
            }

            // Fetch available object_info for checkpoints and loras
            List<string> checkpoints = new();
            List<string> loras = new();
            try {
                using var objReq = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/object_info");
                using var objResp = await _httpClient.SendAsync(objReq, cancellationToken);
                if (objResp.IsSuccessStatusCode) {
                    var objInfo = await objResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                    if (objInfo.TryGetProperty("CheckpointLoaderSimple", out var ckptNode) &&
                        ckptNode.TryGetProperty("input", out var ckptInput) &&
                        ckptInput.TryGetProperty("required", out var ckptReq) &&
                        ckptReq.TryGetProperty("ckpt_name", out var ckptNames) &&
                        ckptNames.ValueKind == JsonValueKind.Array &&
                        ckptNames.GetArrayLength() > 0 &&
                        ckptNames[0].ValueKind == JsonValueKind.Array) {
                        foreach (var item in ckptNames[0].EnumerateArray()) {
                            checkpoints.Add(item.GetString() ?? string.Empty);
                        }
                    }

                    if (objInfo.TryGetProperty("LoraLoader", out var loraNode) &&
                        loraNode.TryGetProperty("input", out var loraInput) &&
                        loraInput.TryGetProperty("required", out var loraReq) &&
                        loraReq.TryGetProperty("lora_name", out var loraNames) &&
                        loraNames.ValueKind == JsonValueKind.Array &&
                        loraNames.GetArrayLength() > 0 &&
                        loraNames[0].ValueKind == JsonValueKind.Array) {
                        foreach (var item in loraNames[0].EnumerateArray()) {
                            loras.Add(item.GetString() ?? string.Empty);
                        }
                    }
                }
            } catch { }

            return new ComfyUiConnectionStatus(true, endpoint, version, os, device, checkpoints, loras);
        } catch (Exception ex) {
            return new ComfyUiConnectionStatus(false, endpoint, "", "", "", new(), new(), ex.Message);
        }
    }

    public async Task<string?> DeployLoraLocallyAsync(string loraFilePath) {
        if (!File.Exists(loraFilePath)) {
            return null;
        }

        string loraName = Path.GetFileName(loraFilePath);
        string lorasDir = _settingsService.Current.ComfyUiLorasDirectory;

        if (string.IsNullOrWhiteSpace(lorasDir) || !Directory.Exists(lorasDir)) {
            // Auto-detect common ComfyUI locations on the same drive or system
            string[] commonRoots = {
                @"D:\AI\ComfyUI\models\loras",
                @"D:\ComfyUI\models\loras",
                @"C:\AI\ComfyUI\models\loras",
                @"C:\ComfyUI\models\loras",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ComfyUI", "models", "loras")
            };

            foreach (var cand in commonRoots) {
                if (Directory.Exists(cand)) {
                    lorasDir = cand;
                    _settingsService.Current.ComfyUiLorasDirectory = lorasDir;
                    await _settingsService.SaveSettingsAsync(_settingsService.Current);
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(lorasDir) || !Directory.Exists(lorasDir)) {
            return null;
        }

        string targetLink = Path.Combine(lorasDir, loraName);
        if (File.Exists(targetLink)) {
            return targetLink;
        }

        try {
            // Create symlink first, fall back to copy if filesystem permissions restrict symlinking
            File.CreateSymbolicLink(targetLink, loraFilePath);
            return targetLink;
        } catch {
            try {
                File.Copy(loraFilePath, targetLink, overwrite: true);
                return targetLink;
            } catch {
                return null;
            }
        }
    }

    public async Task<bool> UploadLoraToRemoteComfyUiAsync(string loraFilePath, CancellationToken cancellationToken = default) {
        if (!File.Exists(loraFilePath)) {
            return false;
        }

        string endpoint = GetEffectiveEndpoint();
        try {
            using var form = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(loraFilePath);
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            
            form.Add(streamContent, "image", Path.GetFileName(loraFilePath));
            form.Add(new StringContent("loras"), "subfolder");
            form.Add(new StringContent("true"), "overwrite");

            using var resp = await _httpClient.PostAsync($"{endpoint}/upload/image", form, cancellationToken);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    public JsonObject GenerateFluxPromptGraph(
        string checkpointOrUnet,
        string loraName,
        float loraWeight,
        string prompt,
        int width = 1024,
        int height = 1024,
        int steps = 20,
        long seed = 42
    ) {
        var root = new JsonObject();

        root["1"] = new JsonObject {
            ["class_type"] = "CheckpointLoaderSimple",
            ["inputs"] = new JsonObject {
                ["ckpt_name"] = checkpointOrUnet
            }
        };

        root["2"] = new JsonObject {
            ["class_type"] = "LoraLoader",
            ["inputs"] = new JsonObject {
                ["lora_name"] = loraName,
                ["strength_model"] = loraWeight,
                ["strength_clip"] = loraWeight,
                ["model"] = new JsonArray { "1", 0 },
                ["clip"] = new JsonArray { "1", 1 }
            }
        };

        root["3"] = new JsonObject {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject {
                ["text"] = prompt,
                ["clip"] = new JsonArray { "2", 1 }
            }
        };

        root["4"] = new JsonObject {
            ["class_type"] = "EmptySD3LatentImage",
            ["inputs"] = new JsonObject {
                ["width"] = width,
                ["height"] = height,
                ["batch_size"] = 1
            }
        };

        root["5"] = new JsonObject {
            ["class_type"] = "KSampler",
            ["inputs"] = new JsonObject {
                ["seed"] = seed,
                ["steps"] = steps,
                ["cfg"] = 3.5,
                ["sampler_name"] = "euler",
                ["scheduler"] = "simple",
                ["denoise"] = 1.0,
                ["model"] = new JsonArray { "2", 0 },
                ["positive"] = new JsonArray { "3", 0 },
                ["negative"] = new JsonArray { "3", 0 },
                ["latent_image"] = new JsonArray { "4", 0 }
            }
        };

        root["6"] = new JsonObject {
            ["class_type"] = "VAEDecode",
            ["inputs"] = new JsonObject {
                ["samples"] = new JsonArray { "5", 0 },
                ["vae"] = new JsonArray { "1", 2 }
            }
        };

        root["7"] = new JsonObject {
            ["class_type"] = "SaveImage",
            ["inputs"] = new JsonObject {
                ["filename_prefix"] = "LoRAMancer_Test",
                ["images"] = new JsonArray { "6", 0 }
            }
        };

        return root;
    }

    public JsonObject GenerateSdxlPromptGraph(
        string checkpointName,
        string loraName,
        float loraWeight,
        string prompt,
        string negativePrompt = "blurry, low quality, distorted, watermark",
        int width = 1024,
        int height = 1024,
        int steps = 25,
        float cfg = 7.0f,
        long seed = 42
    ) {
        var root = new JsonObject();

        root["1"] = new JsonObject {
            ["class_type"] = "CheckpointLoaderSimple",
            ["inputs"] = new JsonObject {
                ["ckpt_name"] = checkpointName
            }
        };

        root["2"] = new JsonObject {
            ["class_type"] = "LoraLoader",
            ["inputs"] = new JsonObject {
                ["lora_name"] = loraName,
                ["strength_model"] = loraWeight,
                ["strength_clip"] = loraWeight,
                ["model"] = new JsonArray { "1", 0 },
                ["clip"] = new JsonArray { "1", 1 }
            }
        };

        root["3"] = new JsonObject {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject {
                ["text"] = prompt,
                ["clip"] = new JsonArray { "2", 1 }
            }
        };

        root["4"] = new JsonObject {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject {
                ["text"] = negativePrompt,
                ["clip"] = new JsonArray { "2", 1 }
            }
        };

        root["5"] = new JsonObject {
            ["class_type"] = "EmptyLatentImage",
            ["inputs"] = new JsonObject {
                ["width"] = width,
                ["height"] = height,
                ["batch_size"] = 1
            }
        };

        root["6"] = new JsonObject {
            ["class_type"] = "KSampler",
            ["inputs"] = new JsonObject {
                ["seed"] = seed,
                ["steps"] = steps,
                ["cfg"] = cfg,
                ["sampler_name"] = "euler_ancestral",
                ["scheduler"] = "karras",
                ["denoise"] = 1.0,
                ["model"] = new JsonArray { "2", 0 },
                ["positive"] = new JsonArray { "3", 0 },
                ["negative"] = new JsonArray { "4", 0 },
                ["latent_image"] = new JsonArray { "5", 0 }
            }
        };

        root["7"] = new JsonObject {
            ["class_type"] = "VAEDecode",
            ["inputs"] = new JsonObject {
                ["samples"] = new JsonArray { "6", 0 },
                ["vae"] = new JsonArray { "1", 2 }
            }
        };

        root["8"] = new JsonObject {
            ["class_type"] = "SaveImage",
            ["inputs"] = new JsonObject {
                ["filename_prefix"] = "LoRAMancer_Test",
                ["images"] = new JsonArray { "7", 0 }
            }
        };

        return root;
    }

    public async Task<byte[]?> QueuePromptAndRenderAsync(
        JsonObject promptGraph,
        Action<ComfyUiProgress>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        string endpoint = GetEffectiveEndpoint();
        Uri httpUri = new Uri(endpoint);
        string wsScheme = httpUri.Scheme == "https" ? "wss" : "ws";
        Uri wsUri = new Uri($"{wsScheme}://{httpUri.Host}:{httpUri.Port}/ws?clientId={_clientId}");

        using var ws = new ClientWebSocket();
        try {
            await ws.ConnectAsync(wsUri, cancellationToken);
        } catch (Exception ex) {
            onProgress?.Invoke(new ComfyUiProgress($"WebSocket connection failed: {ex.Message}", 0, 0, 0));
        }

        // Post the prompt
        var payload = new JsonObject {
            ["prompt"] = promptGraph,
            ["client_id"] = _clientId
        };

        string promptId = string.Empty;
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/prompt")) {
            req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) {
                string err = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"ComfyUI rejected prompt ({resp.StatusCode}): {err}");
            }
            var respJson = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            promptId = respJson.GetProperty("prompt_id").GetString() ?? string.Empty;
        }

        onProgress?.Invoke(new ComfyUiProgress("Queued in ComfyUI", 0, 0, 5));

        // Listen on WebSocket until execution completes
        byte[] buffer = new byte[65536];
        string? outputFilename = null;
        string? outputSubfolder = null;
        string? outputType = null;

        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested) {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text) {
                string msgText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                try {
                    using var doc = JsonDocument.Parse(msgText);
                    string type = doc.RootElement.GetProperty("type").GetString() ?? string.Empty;
                    var data = doc.RootElement.GetProperty("data");

                    if (type == "status") {
                        onProgress?.Invoke(new ComfyUiProgress("Waiting in queue...", 0, 0, 10));
                    } else if (type == "executing") {
                        string? node = data.TryGetProperty("node", out var n) ? n.GetString() : null;
                        if (node == null) {
                            // Execution completed!
                            break;
                        } else {
                            onProgress?.Invoke(new ComfyUiProgress($"Executing node #{node}...", 0, 0, 30));
                        }
                    } else if (type == "progress") {
                        int val = data.GetProperty("value").GetInt32();
                        int max = data.GetProperty("max").GetInt32();
                        int pct = max > 0 ? (int)((float)val / max * 70.0f) + 30 : 50;
                        onProgress?.Invoke(new ComfyUiProgress($"Sampling step {val}/{max}", val, max, pct));
                    } else if (type == "executed") {
                        if (data.TryGetProperty("output", out var output) &&
                            output.TryGetProperty("images", out var images) &&
                            images.ValueKind == JsonValueKind.Array &&
                            images.GetArrayLength() > 0) {
                            var firstImg = images[0];
                            outputFilename = firstImg.GetProperty("filename").GetString();
                            outputSubfolder = firstImg.TryGetProperty("subfolder", out var sf) ? sf.GetString() : "";
                            outputType = firstImg.TryGetProperty("type", out var tp) ? tp.GetString() : "output";
                        }
                    } else if (type == "execution_error") {
                        string err = data.TryGetProperty("exception_message", out var em) ? em.GetString() ?? "Unknown error" : "Error";
                        throw new InvalidOperationException($"ComfyUI node failed: {err}");
                    }
                } catch (JsonException) { }
            }
        }

        if (string.IsNullOrEmpty(outputFilename)) {
            return null;
        }

        onProgress?.Invoke(new ComfyUiProgress("Fetching rendered image...", 100, 100, 100));

        // Fetch image bytes
        string imageUrl = $"{endpoint}/view?filename={Uri.EscapeDataString(outputFilename)}&subfolder={Uri.EscapeDataString(outputSubfolder ?? "")}&type={Uri.EscapeDataString(outputType ?? "output")}";
        using var imgResp = await _httpClient.GetAsync(imageUrl, cancellationToken);
        if (imgResp.IsSuccessStatusCode) {
            return await imgResp.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        return null;
    }

    public void Dispose() {
    }
}
