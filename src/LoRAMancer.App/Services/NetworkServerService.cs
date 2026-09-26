using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LoRAMancer.App.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LoRAMancer.App.Services;

public sealed class AuthLoginDto {
    public string? PinOrToken { get; set; }
}

public sealed class CreateTokenDto {
    public string Name { get; set; } = string.Empty;
    public string? CustomToken { get; set; }
    public string? Role { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class LabInspectRequestDto {
    public string? FilePath { get; set; }
}

public sealed class LabRescaleRequestDto {
    public string? FilePath { get; set; }
    public double TeScale { get; set; } = 1.0;
    public double UnetScale { get; set; } = 1.0;
    public int TargetRank { get; set; } = 16;
}

public sealed class ChopDonorInspectDto {
    public string? FilePath { get; set; }
}

public sealed class ComfyUiTestRequestDto {
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? LoraName { get; set; }
    public float LoraWeight { get; set; } = 1.0f;
    public int Steps { get; set; } = 20;
    public float Cfg { get; set; } = 7.0f;
    public long? Seed { get; set; }
    public string? Architecture { get; set; }
}

public sealed class NetworkServerService : IAsyncDisposable {
    private readonly SettingsService _settingsService;
    private readonly TrainingRunnerService _trainingRunner;
    private readonly DatasetInspectorService _datasetInspector;
    private readonly AmdVenvProvisioner _venvProvisioner;
    private readonly PublicTunnelService? _publicTunnelService;
    private readonly AuthTokenManagerService? _authTokenManager;
    private readonly LoraHistoryService? _historyService;
    private readonly LoraLibraryService? _libraryService;
    private readonly LoraChopShopService? _chopShopService;
    private readonly LoraSurgeryService? _surgeryService;
    private readonly OverbakeRadarService? _overbakeRadar;
    private readonly ComfyUiService? _comfyUiService;
    private readonly ConcurrentBag<HttpResponse> _sseClients = new();

    private WebApplication? _webApp;
    private CancellationTokenSource? _serverCts;

    public bool IsRunning { get; private set; }
    public string ListeningUrl { get; private set; } = string.Empty;
    public string PublicInternetUrl => _publicTunnelService?.ActivePublicUrl ?? string.Empty;
    public int ActiveClientCount => _sseClients.Count;

    public event Action<bool, string>? OnServerStateChanged;

    public NetworkServerService(
        SettingsService settingsService,
        TrainingRunnerService trainingRunner,
        DatasetInspectorService datasetInspector,
        AmdVenvProvisioner venvProvisioner,
        PublicTunnelService? publicTunnelService = null,
        AuthTokenManagerService? authTokenManager = null,
        LoraHistoryService? historyService = null,
        LoraLibraryService? libraryService = null,
        LoraChopShopService? chopShopService = null,
        LoraSurgeryService? surgeryService = null,
        OverbakeRadarService? overbakeRadar = null,
        ComfyUiService? comfyUiService = null
    ) {
        _settingsService = settingsService;
        _trainingRunner = trainingRunner;
        _datasetInspector = datasetInspector;
        _venvProvisioner = venvProvisioner;
        _publicTunnelService = publicTunnelService;
        _authTokenManager = authTokenManager;
        _historyService = historyService;
        _libraryService = libraryService;
        _chopShopService = chopShopService;
        _surgeryService = surgeryService;
        _overbakeRadar = overbakeRadar;
        _comfyUiService = comfyUiService;

        _trainingRunner.OnProgressUpdated += HandleProgressUpdated;
        _trainingRunner.OnLogReceived += HandleLogReceived;
    }

    public async Task StartServerAsync(int port = 8420, string bindAddress = "0.0.0.0", string? accessToken = null) {
        if (IsRunning) {
            return;
        }

        _serverCts = new CancellationTokenSource();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => {
            IPAddress ip = bindAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(bindAddress);
            options.Listen(ip, port);
        });

        builder.Services.AddRouting();

        WebApplication app = builder.Build();

        app.UseRouting();

        // 1. PWA Web App Manifest (Protected)
        app.MapGet("/manifest.json", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            var manifest = new {
                name = "LoRAMancer Remote Studio",
                short_name = "LoRAMancer",
                description = "Remote AI LoRA Training, Lab, Chop-Shop & ComfyUI Studio",
                start_url = "/",
                scope = "/",
                display = "standalone",
                background_color = "#11111b",
                theme_color = "#cba6f7",
                icons = new[] {
                    new {
                        src = "/icon.svg",
                        sizes = "512x512",
                        type = "image/svg+xml",
                        purpose = "any maskable"
                    }
                }
            };
            return Results.Json(manifest);
        });

        // 2. Application SVG Icon (Protected)
        app.MapGet("/icon.svg", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            string svg = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="100%" height="100%">
                    <defs>
                        <linearGradient id="grad" x1="0%" y1="0%" x2="100%" y2="100%">
                            <stop offset="0%" stop-color="#cba6f7" />
                            <stop offset="100%" stop-color="#89b4fa" />
                        </linearGradient>
                    </defs>
                    <rect width="512" height="512" rx="128" fill="#11111b" />
                    <circle cx="256" cy="256" r="180" fill="url(#grad)" opacity="0.15" />
                    <path d="M256 80 L320 200 L450 220 L350 310 L380 440 L256 370 L132 440 L162 310 L62 220 L192 200 Z" fill="url(#grad)" />
                    <text x="256" y="320" font-family="Arial, sans-serif" font-size="160" font-weight="900" fill="#11111b" text-anchor="middle">LM</text>
                </svg>
                """;
            return Results.Content(svg, "image/svg+xml");
        });

        // 3. PWA Service Worker (Protected)
        app.MapGet("/sw.js", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            string swScript = """
                const CACHE_NAME = 'loramancer-shell-v2';
                const SHELL_ASSETS = ['/', '/manifest.json', '/icon.svg'];

                self.addEventListener('install', (event) => {
                    event.waitUntil(
                        caches.open(CACHE_NAME).then((cache) => {
                            return cache.addAll(SHELL_ASSETS);
                        }).then(() => self.skipWaiting())
                    );
                });

                self.addEventListener('activate', (event) => {
                    event.waitUntil(
                        caches.keys().then((keys) => {
                            return Promise.all(
                                keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
                            );
                        }).then(() => self.clients.claim())
                    );
                });

                self.addEventListener('fetch', (event) => {
                    const url = new URL(event.request.url);
                    if (url.pathname.startsWith('/api/')) {
                        return;
                    }
                    event.respondWith(
                        fetch(event.request).catch(() => {
                            return caches.match(event.request).then((res) => res || caches.match('/'));
                        })
                    );
                });
                """;
            return Results.Content(swScript, "application/javascript");
        });

        // 4. Authentication Login (PIN or Token)
        app.MapPost("/api/v1/auth/login", (HttpRequest request, AuthLoginDto loginDto) => {
            string expectedToken = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : _settingsService.Current.ServerAccessToken;
            bool requireAuth = _settingsService.Current.RequireAuthForWebAccess || !string.IsNullOrWhiteSpace(expectedToken);

            string inputToken = loginDto.PinOrToken?.Trim() ?? string.Empty;
            bool isAuthorized = false;
            string effectiveToken = "authorized";

            if (!requireAuth) {
                isAuthorized = true;
            } else if (!string.IsNullOrWhiteSpace(inputToken)) {
                if (_authTokenManager != null && _authTokenManager.ValidateToken(inputToken, out var matched)) {
                    isAuthorized = true;
                    effectiveToken = matched!.Token;
                } else if (!string.IsNullOrWhiteSpace(expectedToken) && string.Equals(inputToken, expectedToken.Trim(), StringComparison.Ordinal)) {
                    isAuthorized = true;
                    effectiveToken = expectedToken;
                }
            }

            if (isAuthorized) {
                request.HttpContext.Response.Cookies.Append("loramancer_auth", effectiveToken, new CookieOptions {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = request.IsHttps,
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });
                return Results.Ok(new { success = true, token = effectiveToken });
            }

            return Results.Unauthorized();
        });

        // 5. Health & Status
        app.MapGet("/api/v1/health", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            ServerHealthDto health = new() {
                Status = _trainingRunner.IsRunning ? "Training" : "Ready",
                IsTraining = _trainingRunner.IsRunning,
                ActiveRunName = _trainingRunner.IsRunning ? "Active Run" : string.Empty,
                CurrentStep = _trainingRunner.CurrentProgress.CurrentStep,
                TotalSteps = _trainingRunner.CurrentProgress.TotalSteps,
                CurrentLoss = (float)_trainingRunner.CurrentProgress.CurrentLoss,
                PyTorchVersion = _settingsService.Current.PyTorchVersion,
                GpuInfo = "AMD ROCm / NVIDIA CUDA Host"
            };

            return Results.Json(health);
        });

        // 6. Upload Dataset ZIP Archive
        app.MapPost("/api/v1/datasets/upload", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (!request.HasFormContentType || request.Form.Files.Count == 0) {
                return Results.BadRequest(new DatasetUploadResultDto {
                    Success = false,
                    Message = "No ZIP file uploaded in request form."
                });
            }

            IFormFile file = request.Form.Files[0];
            string tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "uploads");
            Directory.CreateDirectory(tempDir);
            string destinationZip = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{file.FileName}");

            await using (FileStream stream = new(destinationZip, FileMode.Create, FileAccess.Write)) {
                await file.CopyToAsync(stream);
            }

            DatasetHealthReport report = await _datasetInspector.InspectDatasetAsync(destinationZip);

            return Results.Json(new DatasetUploadResultDto {
                Success = report.IsValid,
                ExtractedPath = report.DatasetDirectory,
                ImageCount = report.TotalImages,
                CaptionCount = report.TotalCaptions,
                Message = report.IsValid ? "Dataset uploaded and auto-extracted successfully." : "Dataset contains warnings or no valid images."
            });
        });

        // 7. Start Training Job
        app.MapPost("/api/v1/training/start", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (_trainingRunner.IsRunning) {
                return Results.Conflict(new { error = "Training runner is currently busy with another job." });
            }

            StartRemoteTrainingRequest? jobRequest = await JsonSerializer.DeserializeAsync<StartRemoteTrainingRequest>(request.Body);
            if (jobRequest == null) {
                return Results.BadRequest(new { error = "Invalid training request body." });
            }

            string runsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "remote_runs");
            Directory.CreateDirectory(runsDir);
            string configPath = Path.Combine(runsDir, $"{jobRequest.RunName}_config.yaml");

            await File.WriteAllTextAsync(configPath, jobRequest.ConfigYaml);

            string venvPath = Path.Combine(Directory.GetCurrentDirectory(), ".venv");
            if (!Directory.Exists(venvPath)) {
                venvPath = Path.Combine(AppContext.BaseDirectory, ".venv");
            }

            _ = Task.Run(async () => {
                try {
                    await _trainingRunner.StartTrainingAsync(venvPath, null, configPath);
                } catch (Exception ex) {
                    BroadcastTelemetry(new TrainingTelemetryDto {
                        EventType = "failed",
                        Message = $"Remote training launch failed: {ex.Message}"
                    });
                }
            });

            return Results.Accepted("/api/v1/health", new {
                message = "Training job queued and initiated successfully.",
                runName = jobRequest.RunName
            });
        });

        // 8. Stop Training Job
        app.MapPost("/api/v1/training/stop", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            _trainingRunner.CancelTraining();
            return Results.Ok(new { message = "Training job cancelled." });
        });

        // 9. SSE Live Telemetry Stream
        app.MapGet("/api/v1/training/stream", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                context.Response.StatusCode = 401;
                return;
            }

            context.Response.Headers.Append("Content-Type", "text/event-stream");
            context.Response.Headers.Append("Cache-Control", "no-cache");
            context.Response.Headers.Append("Connection", "keep-alive");

            _sseClients.Add(context.Response);

            try {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            } catch (OperationCanceledException) {
                // Client disconnected
            }
        });

        // 10. Training History Records
        app.MapGet("/api/v1/history", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            if (_historyService == null) {
                return Results.Json(Array.Empty<LoraHistoryRecord>());
            }

            var records = await _historyService.GetHistoryAsync();
            return Results.Json(records);
        });

        // 11. Retry Training Run From History
        app.MapPost("/api/v1/history/retry/{id}", async (string id, HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            if (_historyService == null) {
                return Results.NotFound(new { error = "History service not available on host." });
            }

            var record = await _historyService.GetRecordByIdAsync(id);
            if (record == null) {
                return Results.NotFound(new { error = "History record not found." });
            }

            if (string.IsNullOrWhiteSpace(record.ConfigYamlPath) || !File.Exists(record.ConfigYamlPath)) {
                return Results.BadRequest(new { error = "Training configuration YAML not found on host." });
            }

            if (_trainingRunner.IsRunning) {
                return Results.Conflict(new { error = "A training job is already active on the host." });
            }

            string venvPath = Path.Combine(Directory.GetCurrentDirectory(), ".venv");
            if (!Directory.Exists(venvPath)) {
                venvPath = Path.Combine(AppContext.BaseDirectory, ".venv");
            }

            _ = Task.Run(async () => {
                try {
                    await _trainingRunner.StartTrainingAsync(venvPath, null, record.ConfigYamlPath);
                } catch (Exception ex) {
                    BroadcastTelemetry(new TrainingTelemetryDto {
                        EventType = "failed",
                        Message = $"Retried training launch failed: {ex.Message}"
                    });
                }
            });

            return Results.Accepted($"/api/v1/history/retry/{id}", new {
                message = $"Training job '{record.Name}' resubmitted successfully.",
                runName = record.Name
            });
        });

        // 12. Vault / Server LoRA Models Discovery
        app.MapGet("/api/v1/vault/loras", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            var foundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchPaths = new List<string> {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "outputs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "remote_runs"),
                Path.Combine(AppContext.BaseDirectory, "outputs")
            };

            if (!string.IsNullOrWhiteSpace(_settingsService.Current.ComfyUiLorasDirectory) && Directory.Exists(_settingsService.Current.ComfyUiLorasDirectory)) {
                searchPaths.Add(_settingsService.Current.ComfyUiLorasDirectory);
            }

            if (_libraryService != null) {
                foreach (var item in _libraryService.Items) {
                    if (!string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath)) {
                        foundFiles.Add(item.FilePath);
                    }
                }
            }

            foreach (var path in searchPaths) {
                if (Directory.Exists(path)) {
                    try {
                        foreach (var f in Directory.GetFiles(path, "*.safetensors", SearchOption.AllDirectories)) {
                            foundFiles.Add(f);
                        }
                    } catch { }
                }
            }

            var list = foundFiles.Select(f => {
                var fi = new FileInfo(f);
                return new {
                    fileName = Path.GetFileName(f),
                    filePath = f,
                    fileSize = fi.Length,
                    formattedSize = $"{fi.Length / (1024.0 * 1024.0):F1} MB",
                    lastModified = fi.LastWriteTimeUtc
                };
            }).OrderByDescending(x => x.lastModified).ToList();

            return Results.Json(list);
        });

        // 13. Diagnostic Lab: Overbake & Spectral Analysis
        app.MapPost("/api/v1/lab/inspect", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (_overbakeRadar == null) {
                return Results.BadRequest(new { error = "Overbake Radar service not initialized." });
            }

            var req = await JsonSerializer.DeserializeAsync<LabInspectRequestDto>(request.Body);
            if (req == null || string.IsNullOrWhiteSpace(req.FilePath) || !File.Exists(req.FilePath)) {
                return Results.BadRequest(new { error = "Valid LoRA file path is required." });
            }

            try {
                var report = await _overbakeRadar.AnalyzeLoraOverbakeAsync(req.FilePath);
                return Results.Json(new {
                    success = true,
                    modelName = report.ModelName,
                    score = report.OverbakeScore,
                    status = report.OverallStatus.ToString(),
                    averageFrobeniusNorm = report.AverageFrobeniusNorm,
                    layerCount = report.TotalLoRALayers,
                    verdict = report.Verdict,
                    recommendation = report.Recommendation
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 14. Diagnostic Lab: Layer Rescale & SVD Rank Pruning
        app.MapPost("/api/v1/lab/rescale", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (_surgeryService == null) {
                return Results.BadRequest(new { error = "Surgery service not initialized." });
            }

            var req = await JsonSerializer.DeserializeAsync<LabRescaleRequestDto>(request.Body);
            if (req == null || string.IsNullOrWhiteSpace(req.FilePath) || !File.Exists(req.FilePath)) {
                return Results.BadRequest(new { error = "Valid LoRA file path is required." });
            }

            try {
                string dir = Path.GetDirectoryName(req.FilePath) ?? "";
                string baseName = Path.GetFileNameWithoutExtension(req.FilePath);
                string outPath = Path.Combine(dir, $"{baseName}_Rescaled.safetensors");

                var result = await _surgeryService.ResizeLoraAsync(req.FilePath, outPath, req.TargetRank);
                return Results.Json(result);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 15. LoRA Chop-Shop: Inspect Donor Model
        app.MapPost("/api/v1/chop-shop/inspect-donor", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (_chopShopService == null) {
                return Results.BadRequest(new { error = "Chop-Shop service not initialized." });
            }

            var req = await JsonSerializer.DeserializeAsync<ChopDonorInspectDto>(request.Body);
            if (req == null || string.IsNullOrWhiteSpace(req.FilePath) || !File.Exists(req.FilePath)) {
                return Results.BadRequest(new { error = "Valid donor file path is required." });
            }

            try {
                var donor = await _chopShopService.InspectDonorAsync(req.FilePath);
                return Results.Json(donor);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 16. LoRA Chop-Shop: Assemble & Bake Franken-LoRA
        app.MapPost("/api/v1/chop-shop/bake", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (_chopShopService == null) {
                return Results.BadRequest(new { error = "Chop-Shop service not initialized." });
            }

            var recipe = await JsonSerializer.DeserializeAsync<ChopShopRecipe>(request.Body);
            if (recipe == null || recipe.Donors.Count == 0) {
                return Results.BadRequest(new { error = "Valid recipe with at least one donor is required." });
            }

            try {
                var result = await _chopShopService.AssembleChopShopLoraAsync(
                    recipe,
                    onLog: line => BroadcastTelemetry(new TrainingTelemetryDto {
                        EventType = "chop_log",
                        Message = line
                    })
                );
                return Results.Json(result);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 17. ComfyUI Status
        app.MapGet("/api/v1/comfyui/status", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            if (_comfyUiService == null) {
                return Results.Json(new { isOnline = false, message = "ComfyUI service unavailable." });
            }

            var status = await _comfyUiService.CheckConnectionAsync();
            return Results.Json(new {
                isOnline = status.IsConnected,
                endpoint = status.Endpoint,
                os = status.Os,
                device = status.DeviceName,
                checkpoints = status.AvailableCheckpoints,
                loras = status.AvailableLoras,
                message = status.ErrorMessage
            });
        });

        // 18. ComfyUI Interactive Test Generation
        app.MapPost("/api/v1/comfyui/test", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) {
                return Results.Unauthorized();
            }

            if (_comfyUiService == null) {
                return Results.BadRequest(new { error = "ComfyUI service unavailable." });
            }

            var req = await JsonSerializer.DeserializeAsync<ComfyUiTestRequestDto>(request.Body);
            if (req == null || string.IsNullOrWhiteSpace(req.Prompt)) {
                return Results.BadRequest(new { error = "Prompt is required." });
            }

            try {
                string loraName = req.LoraName ?? "none";
                float weight = req.LoraWeight;
                string prompt = req.Prompt;
                string neg = req.NegativePrompt ?? "blurry, low quality, artifacts";
                int steps = req.Steps > 0 ? req.Steps : 20;
                float cfg = req.Cfg > 0 ? req.Cfg : 7.0f;
                long seed = req.Seed ?? Random.Shared.NextInt64(1, 999999999);

                var status = await _comfyUiService.CheckConnectionAsync();
                if (!status.IsConnected) {
                    return Results.BadRequest(new { error = "ComfyUI server is offline at configured endpoint." });
                }

                string ckpt = status.AvailableCheckpoints.FirstOrDefault() ?? "v1-5-pruned-emaonly.safetensors";
                JsonObject graph = req.Architecture?.ToLowerInvariant() == "flux"
                    ? _comfyUiService.GenerateFluxPromptGraph(ckpt, loraName, weight, prompt, 1024, 1024, steps, seed)
                    : _comfyUiService.GenerateSdxlPromptGraph(ckpt, loraName, weight, prompt, neg, 1024, 1024, steps, cfg, seed);

                byte[]? imgBytes = await _comfyUiService.QueuePromptAndRenderAsync(graph);
                if (imgBytes == null || imgBytes.Length == 0) {
                    return Results.BadRequest(new { error = "ComfyUI generation completed but produced no output image." });
                }

                return Results.Json(new {
                    success = true,
                    imageBase64 = Convert.ToBase64String(imgBytes),
                    mimeType = "image/png",
                    seed = seed
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 19. Embedded Responsive Web Interface & PWA App Shell (Strictly Protected)
        app.MapGet("/", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(GetUnauthorizedAccessHtml());
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetEmbeddedWebInterfaceHtml());
        });

        // 20. Token Management API (Protected)
        app.MapGet("/api/v1/tokens", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }
            if (_authTokenManager == null) {
                return Results.Ok(Array.Empty<AuthToken>());
            }
            return Results.Json(_authTokenManager.GetAllTokens());
        });

        app.MapPost("/api/v1/tokens", (HttpContext context, CreateTokenDto dto) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }
            if (_authTokenManager == null) {
                return Results.BadRequest("AuthTokenManagerService is not available.");
            }
            try {
                var created = _authTokenManager.CreateToken(dto.Name, dto.CustomToken, dto.Role ?? "Admin", dto.Description ?? string.Empty, dto.ExpiresAt);
                return Results.Ok(created);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/tokens/{id}/block", (HttpContext context, string id) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }
            bool success = _authTokenManager?.BlockToken(id) ?? false;
            return success ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        app.MapPost("/api/v1/tokens/{id}/unblock", (HttpContext context, string id) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }
            bool success = _authTokenManager?.UnblockToken(id) ?? false;
            return success ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        app.MapDelete("/api/v1/tokens/{id}", (HttpContext context, string id) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }
            bool success = _authTokenManager?.DeleteToken(id) ?? false;
            return success ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        _webApp = app;
        await _webApp.StartAsync(_serverCts.Token);

        IsRunning = true;
        ListeningUrl = $"http://{(bindAddress == "0.0.0.0" ? "localhost" : bindAddress)}:{port}";

        if (_settingsService.Current.EnablePublicInternetTunnel && _publicTunnelService != null) {
            _ = Task.Run(async () => {
                try {
                    await _publicTunnelService.StartTunnelAsync(
                        port,
                        _settingsService.Current.PublicTunnelType,
                        _settingsService.Current.PublicCustomDomainUrl,
                        _serverCts.Token
                    );
                    OnServerStateChanged?.Invoke(true, ListeningUrl);
                } catch {
                    // Local server remains active if public tunnel creation fails
                }
            });
        }

        OnServerStateChanged?.Invoke(true, ListeningUrl);
    }

    public async Task StopServerAsync() {
        if (!IsRunning || _webApp == null) {
            return;
        }

        try {
            _serverCts?.Cancel();
            if (_publicTunnelService != null) {
                try {
                    await _publicTunnelService.StopTunnelAsync();
                } catch {
                    // Ignore
                }
            }
            await _webApp.StopAsync();
            await _webApp.DisposeAsync();
        } finally {
            _webApp = null;
            IsRunning = false;
            ListeningUrl = string.Empty;
            OnServerStateChanged?.Invoke(false, string.Empty);
        }
    }

    private bool IsAuthorized(HttpContext context, string? accessToken) {
        string effectiveToken = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : _settingsService.Current.ServerAccessToken;
        if (!_settingsService.Current.RequireAuthForWebAccess && string.IsNullOrWhiteSpace(effectiveToken)) {
            return true;
        }

        string authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            string token = authHeader["Bearer ".Length..].Trim();
            if (CheckTokenValid(token, effectiveToken)) {
                return true;
            }
        }

        if (context.Request.Query.TryGetValue("token", out var queryToken)) {
            string token = queryToken.ToString().Trim();
            if (CheckTokenValid(token, effectiveToken)) {
                context.Response.Cookies.Append("loramancer_auth", token, new CookieOptions {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });
                return true;
            }
        }

        if (context.Request.Cookies.TryGetValue("loramancer_auth", out var cookieToken)) {
            string token = cookieToken.Trim();
            if (CheckTokenValid(token, effectiveToken)) {
                return true;
            }
        }

        return false;
    }

    private bool CheckTokenValid(string token, string? expectedFallbackToken) {
        if (string.IsNullOrWhiteSpace(token)) {
            return false;
        }

        if (_authTokenManager != null && _authTokenManager.ValidateToken(token, out _)) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(expectedFallbackToken) &&
            string.Equals(token, expectedFallbackToken.Trim(), StringComparison.Ordinal)) {
            return true;
        }

        return false;
    }

    private void HandleProgressUpdated(TrainingProgress progress) {
        BroadcastTelemetry(new TrainingTelemetryDto {
            EventType = "step",
            Step = progress.CurrentStep,
            TotalSteps = progress.TotalSteps,
            Loss = (float)progress.CurrentLoss
        });
    }

    private void HandleLogReceived(string log) {
        BroadcastTelemetry(new TrainingTelemetryDto {
            EventType = "log",
            Message = log
        });
    }

    private void BroadcastTelemetry(TrainingTelemetryDto telemetry) {
        string json = JsonSerializer.Serialize(telemetry);
        byte[] payload = System.Text.Encoding.UTF8.GetBytes($"data: {json}\n\n");

        foreach (HttpResponse client in _sseClients) {
            try {
                client.Body.WriteAsync(payload, 0, payload.Length);
                client.Body.FlushAsync();
            } catch {
                // Client dropped connection
            }
        }
    }

    private string GetUnauthorizedAccessHtml() {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>LoRAMancer Access Locked</title>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;600;700&display=swap" rel="stylesheet" />
    <style>
        :root { --bg: #11111b; --card: #181825; --border: #313244; --accent: #cba6f7; --red: #f38ba8; --text: #cdd6f4; }
        body { background-color: var(--bg); color: var(--text); font-family: 'Inter', sans-serif; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
        .card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 32px; width: 100%; max-width: 420px; box-shadow: 0 8px 30px rgba(0,0,0,0.5); text-align: center; }
        .icon { font-size: 3rem; margin-bottom: 12px; }
        h1 { font-size: 1.4rem; color: #fff; margin-bottom: 8px; }
        p { color: #a6adc8; font-size: 0.9rem; margin-bottom: 24px; line-height: 1.4; }
        input { width: 100%; box-sizing: border-box; background: #11111b; border: 1px solid var(--border); color: #fff; padding: 12px; border-radius: 8px; font-size: 1rem; margin-bottom: 16px; outline: none; }
        input:focus { border-color: var(--accent); }
        button { width: 100%; background: linear-gradient(135deg, #cba6f7, #89b4fa); border: none; padding: 12px; border-radius: 8px; font-weight: 700; color: #11111b; font-size: 1rem; cursor: pointer; }
        button:hover { opacity: 0.9; }
        .error { color: var(--red); font-size: 0.85rem; margin-top: 12px; display: none; }
    </style>
</head>
<body>
    <div class="card">
        <div class="icon">🔒</div>
        <h1>Authentication Required</h1>
        <p>Access to this remote LoRAMancer instance is secured. Enter your PIN or Access Token to continue.</p>
        <input id="tokenInput" type="password" placeholder="Enter Access PIN or Token" autofocus />
        <button onclick="login()">Authorize Session</button>
        <div id="errMsg" class="error">Invalid token or PIN. Access denied.</div>
    </div>
    <script>
        async function login() {
            const token = document.getElementById('tokenInput').value.trim();
            const err = document.getElementById('errMsg');
            err.style.display = 'none';
            if (!token) return;

            try {
                const res = await fetch('/api/v1/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ PinOrToken: token })
                });

                if (res.ok) {
                    localStorage.setItem('loramancer_auth_token', token);
                    window.location.reload();
                } else {
                    err.style.display = 'block';
                }
            } catch(e) {
                err.textContent = 'Server unreachable: ' + e.message;
                err.style.display = 'block';
            }
        }
        document.getElementById('tokenInput').addEventListener('keypress', function(e) {
            if (e.key === 'Enter') login();
        });
    </script>
</body>
</html>
""";
    }

    private string GetEmbeddedWebInterfaceHtml() {
        string publicUrl = !string.IsNullOrWhiteSpace(_publicTunnelService?.ActivePublicUrl)
            ? _publicTunnelService.ActivePublicUrl
            : _settingsService.Current.PublicCustomDomainUrl;

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
    <title>LoRAMancer Remote Studio</title>
    
    <!-- PWA & Mobile Web App Meta Tags -->
    <link rel="manifest" href="/manifest.json" />
    <meta name="theme-color" content="#cba6f7" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
    <meta name="apple-mobile-web-app-title" content="LoRAMancer" />
    <link rel="icon" type="image/svg+xml" href="/icon.svg" />
    <link rel="apple-touch-icon" href="/icon.svg" />

    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500;700&display=swap" rel="stylesheet" />
    <style>
        :root {
            --bg-base: #11111b;
            --bg-surface: #1e1e2e;
            --bg-card: #252538;
            --border: #313244;
            --accent: #cba6f7;
            --accent-glow: rgba(203, 166, 247, 0.25);
            --text-main: #cdd6f4;
            --text-muted: #a6adc8;
            --green: #a6e3a1;
            --red: #f38ba8;
            --amber: #f9e2af;
            --blue: #89b4fa;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            background-color: var(--bg-base);
            color: var(--text-main);
            font-family: 'Inter', sans-serif;
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }
        header {
            background-color: var(--bg-surface);
            border-bottom: 1px solid var(--border);
            padding: 12px 24px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 16px;
            flex-wrap: wrap;
        }
        .brand { display: flex; align-items: center; gap: 12px; }
        .logo-circle {
            width: 36px; height: 36px; border-radius: 50%;
            background: linear-gradient(135deg, #cba6f7, #89b4fa);
            display: flex; align-items: center; justify-content: center;
            font-weight: 700; color: #11111b; font-size: 1.1rem;
            user-select: none;
        }
        .brand h1 { font-size: 1.2rem; font-weight: 700; color: #fff; }
        .badge {
            font-size: 0.75rem; padding: 4px 10px; border-radius: 12px;
            font-weight: 600; letter-spacing: 0.5px; display: inline-flex; align-items: center; gap: 4px;
        }
        .badge-live { background-color: rgba(166, 227, 161, 0.15); color: var(--green); border: 1px solid var(--green); }
        .badge-busy { background-color: rgba(249, 226, 175, 0.15); color: var(--amber); border: 1px solid var(--amber); }
        .badge-public { background-color: rgba(203, 166, 247, 0.15); color: var(--accent); border: 1px solid var(--accent); cursor: pointer; }
        
        /* Modern App Navigation Bar */
        nav.tabs {
            background: #181825;
            border-bottom: 1px solid var(--border);
            display: flex;
            gap: 4px;
            padding: 4px 24px;
            overflow-x: auto;
            white-space: nowrap;
        }
        nav.tabs button {
            background: transparent;
            border: none;
            color: var(--text-muted);
            padding: 10px 18px;
            font-size: 0.88rem;
            font-weight: 600;
            border-radius: 6px;
            cursor: pointer;
            transition: all 0.2s ease;
            display: inline-flex;
            align-items: center;
            gap: 8px;
        }
        nav.tabs button:hover {
            color: #fff;
            background: rgba(203, 166, 247, 0.08);
        }
        nav.tabs button.active {
            color: #fff;
            background: var(--bg-surface);
            border: 1px solid var(--border);
            border-bottom: 2px solid var(--accent);
        }

        main { flex: 1; padding: 24px; max-width: 1400px; margin: 0 auto; width: 100%; }
        .tab-content { display: none; }
        .tab-content.active { display: block; }

        .grid-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; }
        .grid-3 { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; }
        .grid-4 { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; }
        @media(max-width: 950px) {
            .grid-2, .grid-3, .grid-4 { grid-template-columns: 1fr; }
            main { padding: 14px; }
            nav.tabs { padding: 4px 10px; }
        }

        .card {
            background-color: var(--bg-surface);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 20px;
            display: flex;
            flex-direction: column;
            gap: 14px;
            margin-bottom: 20px;
        }
        .card-title {
            font-size: 1.05rem; font-weight: 600; color: #fff;
            display: flex; align-items: center; justify-content: space-between;
        }
        .input-group { display: flex; flex-direction: column; gap: 6px; }
        label { font-size: 0.82rem; color: var(--text-muted); font-weight: 500; }
        input, select, textarea {
            background-color: var(--bg-card);
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 10px 14px;
            border-radius: 8px;
            font-family: inherit;
            font-size: 0.92rem;
        }
        input:focus, select:focus, textarea:focus {
            outline: none; border-color: var(--accent);
            box-shadow: 0 0 0 3px var(--accent-glow);
        }
        .btn {
            background: linear-gradient(135deg, #cba6f7, #b4befe);
            color: #11111b; font-weight: 600;
            padding: 10px 18px; border: none; border-radius: 8px;
            cursor: pointer; font-size: 0.92rem; transition: opacity 0.2s, transform 0.1s;
            display: inline-flex; align-items: center; justify-content: center; gap: 6px;
        }
        .btn:hover { opacity: 0.92; }
        .btn:active { transform: scale(0.98); }
        .btn-install {
            background: linear-gradient(135deg, #a6e3a1, #89b4fa);
            color: #11111b; padding: 6px 14px; font-size: 0.8rem; border-radius: 20px;
        }
        .btn-secondary { background: #313244; color: #cdd6f4; border: 1px solid #45475a; }
        .btn-danger { background: var(--red); color: #fff; }
        .progress-bar-container {
            background-color: var(--bg-card);
            border-radius: 8px;
            height: 12px;
            overflow: hidden;
            width: 100%;
        }
        .progress-bar-fill {
            background: linear-gradient(90deg, #cba6f7, #89b4fa);
            height: 100%; width: 0%; transition: width 0.3s ease;
        }
        .terminal {
            background-color: #0b0b10;
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 14px;
            height: 340px;
            overflow-y: auto;
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.8rem;
            color: #a6adc8;
            white-space: pre-wrap;
            line-height: 1.5;
        }
        .stat-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; }
        .stat-box {
            background-color: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 12px; text-align: center;
        }
        .stat-val { font-size: 1.25rem; font-weight: 700; color: #fff; margin-top: 4px; }
        .history-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
        .history-table th, .history-table td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border); }
        .history-table th { color: var(--text-muted); font-weight: 600; text-transform: uppercase; font-size: 0.72rem; letter-spacing: 0.5px; }
        .history-table tr:hover { background-color: var(--bg-card); }
        .btn-sm { padding: 4px 10px; font-size: 0.75rem; border-radius: 6px; }
    </style>
</head>
<body>
    <header>
        <div class="brand">
            <div class="logo-circle">LM</div>
            <div>
                <h1>LoRAMancer</h1>
                <p style="font-size:0.75rem; color:var(--text-muted)">Remote Studio Engine (Linux / Web Direct)</p>
            </div>
        </div>
        <div style="display:flex; align-items:center; gap:10px; flex-wrap:wrap;">
            <button id="installPwaBtn" class="btn btn-install" style="display:none;" onclick="installPwa()">📲 Install App</button>
            <span id="publicUrlBadge" class="badge badge-public" style="display:none;" onclick="copyPublicUrl()" title="Click to copy host URL">🌐 Host Active</span>
            <span id="hostStatus" class="badge badge-live">Ready</span>
            <span id="gpuBadge" class="badge" style="background:#313244; color:#cdd6f4;">ROCm / CUDA Active</span>
        </div>
    </header>

    <nav class="tabs">
        <button id="tabBtn-train" class="active" onclick="switchTab('train')">🏋️ Train &amp; Telemetry</button>
        <button id="tabBtn-vault" onclick="switchTab('vault')">📚 LoRA Vault</button>
        <button id="tabBtn-lab" onclick="switchTab('lab')">🔬 Diagnostic Lab</button>
        <button id="tabBtn-chop" onclick="switchTab('chop')">🛠️ LoRA Chop-Shop</button>
        <button id="tabBtn-comfy" onclick="switchTab('comfy')">🎨 ComfyUI Studio</button>
        <button id="tabBtn-history" onclick="switchTab('history')">📜 History</button>
    </nav>

    <main>
        <!-- 1. TRAIN & TELEMETRY TAB -->
        <section id="tab-train" class="tab-content active">
            <div class="grid-2">
                <div class="card">
                    <div class="card-title">
                        <span>Launch Training on Host GPU</span>
                        <span style="font-size:0.8rem; color:var(--accent);">AI-Toolkit Engine</span>
                    </div>

                    <div class="input-group">
                        <label>Run Identifier</label>
                        <input id="runName" type="text" value="flux_lora_run_1" placeholder="e.g. style_lora_v1" />
                    </div>

                    <div class="input-group">
                        <label>Dataset Archive (.zip file)</label>
                        <input id="datasetZip" type="file" accept=".zip" />
                        <span id="uploadInfo" style="font-size:0.75rem; color:var(--text-muted);">ZIP will be auto-extracted on host PC</span>
                    </div>

                    <div class="input-group">
                        <label>Trigger Word / Concept</label>
                        <input id="triggerWord" type="text" placeholder="e.g. ohwx portrait style" />
                    </div>

                    <div class="input-group">
                        <label>Base Architecture</label>
                        <select id="architecture">
                            <option value="flux1">FLUX.1 (Dev / Schnell)</option>
                            <option value="sdxl">SDXL 1.0 / Pony / Illustrious</option>
                            <option value="chroma">Chroma / Wan / Custom</option>
                            <option value="sd15">Stable Diffusion 1.5</option>
                        </select>
                    </div>

                    <div class="input-group">
                        <label>Training Steps</label>
                        <input id="steps" type="number" value="1500" min="100" max="20000" />
                    </div>

                    <div style="display:flex; gap:12px; margin-top:8px;">
                        <button class="btn" style="flex:1;" onclick="startTraining()">🚀 Launch Remote Training</button>
                        <button class="btn btn-danger" onclick="stopTraining()">⏹ Stop</button>
                    </div>
                </div>

                <div class="card">
                    <div class="card-title">
                        <span>Workstation Telemetry &amp; Loss</span>
                        <span id="stepCounter" style="font-size:0.85rem; color:var(--text-muted);">Step 0 / 0</span>
                    </div>

                    <div class="stat-grid">
                        <div class="stat-box">
                            <span style="font-size:0.75rem; color:var(--text-muted);">CURRENT LOSS</span>
                            <div id="lossVal" class="stat-val">0.0000</div>
                        </div>
                        <div class="stat-box">
                            <span style="font-size:0.75rem; color:var(--text-muted);">PROGRESS</span>
                            <div id="percentVal" class="stat-val">0%</div>
                        </div>
                        <div class="stat-box">
                            <span style="font-size:0.75rem; color:var(--text-muted);">WORKER</span>
                            <div id="workerState" class="stat-val" style="font-size:1rem; color:var(--green);">IDLE</div>
                        </div>
                    </div>

                    <div class="progress-bar-container">
                        <div id="progressBar" class="progress-bar-fill"></div>
                    </div>

                    <div id="terminal" class="terminal">Connecting to LoRAMancer telemetry stream...</div>
                </div>
            </div>
        </section>

        <!-- 2. LORA VAULT TAB -->
        <section id="tab-vault" class="tab-content">
            <div class="card">
                <div class="card-title">
                    <span>Host Server LoRA Models &amp; Vault</span>
                    <button class="btn btn-secondary btn-sm" onclick="loadVaultLoras()">🔄 Refresh Vault</button>
                </div>
                <div style="display:flex; gap:12px; align-items:center; margin-bottom:8px;">
                    <input id="vaultSearch" type="text" placeholder="Search models by filename..." style="flex:1;" oninput="filterVault()" />
                </div>
                <div id="vaultList" class="grid-3" style="max-height:650px; overflow-y:auto; padding-top:4px;">
                    <div style="color:var(--text-muted); font-size:0.88rem;">Scanning host LoRA directories...</div>
                </div>
            </div>
        </section>

        <!-- 3. DIAGNOSTIC LAB TAB -->
        <section id="tab-lab" class="tab-content">
            <div class="grid-2">
                <div class="card">
                    <div class="card-title">
                        <span>Target Model SVD &amp; Overbake Radar</span>
                        <span style="font-size:0.8rem; color:var(--blue);">Layer Diagnostics</span>
                    </div>

                    <div class="input-group">
                        <label>Target Safetensors Path on Host</label>
                        <input id="labModelPath" type="text" placeholder="Select from Vault or paste path..." />
                    </div>

                    <button class="btn" onclick="runLabInspection()">🔬 Run Overbake &amp; Spectral Analysis</button>

                    <div id="labReportCard" style="display:none; background:#181825; border:1px solid var(--border); border-radius:8px; padding:14px;">
                        <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:8px;">
                            <span style="font-weight:700;" id="labReportName">-</span>
                            <span class="badge" id="labReportBadge">-</span>
                        </div>
                        <div class="stat-grid" style="margin-bottom:10px;">
                            <div class="stat-box">
                                <span style="font-size:0.75rem; color:var(--text-muted);">OVERBAKE SCORE</span>
                                <div id="labScoreVal" class="stat-val" style="color:var(--accent);">0%</div>
                            </div>
                            <div class="stat-box">
                                <span style="font-size:0.75rem; color:var(--text-muted);">ENERGY NORM</span>
                                <div id="labNormVal" class="stat-val" style="color:var(--green);">0.00</div>
                            </div>
                            <div class="stat-box">
                                <span style="font-size:0.75rem; color:var(--text-muted);">LAYERS</span>
                                <div id="labLayersVal" class="stat-val">0</div>
                            </div>
                        </div>
                        <div id="labVerdict" style="font-size:0.85rem; color:#bac2de; line-height:1.4;"></div>
                    </div>
                </div>

                <div class="card">
                    <div class="card-title">
                        <span>Layer Surgery &amp; Rescaling</span>
                        <span style="font-size:0.8rem; color:var(--accent);">Block Multipliers</span>
                    </div>

                    <div class="input-group">
                        <label>Text Encoder Multiplier (<span id="teVal">1.00</span>x)</label>
                        <input id="teSlider" type="range" min="0" max="2" step="0.05" value="1.0" oninput="document.getElementById('teVal').textContent = parseFloat(this.value).toFixed(2)" />
                    </div>

                    <div class="input-group">
                        <label>UNet / DiT Backbone Multiplier (<span id="unetVal">1.00</span>x)</label>
                        <input id="unetSlider" type="range" min="0" max="2" step="0.05" value="1.0" oninput="document.getElementById('unetVal').textContent = parseFloat(this.value).toFixed(2)" />
                    </div>

                    <div class="input-group">
                        <label>SVD Output Rank</label>
                        <select id="labRank">
                            <option value="8">Rank 8 (~16MB Compact)</option>
                            <option value="16" selected>Rank 16 (~32MB Standard)</option>
                            <option value="32">Rank 32 (~64MB High Fidelity)</option>
                            <option value="64">Rank 64 (~128MB Full)</option>
                        </select>
                    </div>

                    <button class="btn" onclick="applyLabRescale()">🛠️ Apply Layer Rescale &amp; Save on Host</button>
                    <div id="labRescaleStatus" style="font-size:0.85rem; color:var(--green); display:none;"></div>
                </div>
            </div>
        </section>

        <!-- 4. LORA CHOP-SHOP TAB -->
        <section id="tab-chop" class="tab-content">
            <div class="card">
                <div class="card-title">
                    <span>LoRA Vehicle Chop-Shop: Multi-Model Anatomical Grafting</span>
                    <button class="btn btn-secondary btn-sm" onclick="clearChopGarage()">🧹 Reset Garage</button>
                </div>
                <p style="font-size:0.85rem; color:var(--text-muted);">
                    Select donor LoRAs residing on the host machine, assign visual components (Chassis, Eyes, Hair, Armor, Glow), and bake a unified Franken-LoRA using SVD matrix compression without retraining.
                </p>

                <div class="input-group">
                    <label>Select Server LoRA to Add to Garage</label>
                    <div style="display:flex; gap:10px;">
                        <select id="chopAddSelect" style="flex:1;">
                            <option value="">Choose a model from host vault...</option>
                        </select>
                        <button class="btn btn-secondary" onclick="addSelectedDonorToGarage()">➕ Add Donor</button>
                    </div>
                </div>

                <div id="chopDonorsShelf" class="grid-3" style="margin-top:6px;">
                    <div style="color:var(--text-muted); font-size:0.85rem; font-style:italic;">No donors added yet. Select a model above to begin.</div>
                </div>
            </div>

            <div class="card">
                <div class="card-title">
                    <span>Anatomical Part Assignment &amp; Multipliers</span>
                    <button class="btn btn-sm btn-secondary" onclick="autoAssignChop()">✨ Auto-Assign Donors</button>
                </div>

                <div class="grid-2">
                    <div class="input-group">
                        <label>👤 Chassis &amp; Face (Mid-Block Anatomy)</label>
                        <select id="part-face" class="chop-part-select"><option value="">Base / None</option></select>
                    </div>
                    <div class="input-group">
                        <label>👁️ Headlights &amp; Eyes (Iris &amp; Catchlights)</label>
                        <select id="part-eyes" class="chop-part-select"><option value="">Base / None</option></select>
                    </div>
                    <div class="input-group">
                        <label>💇 Custom Paint &amp; Hair (Style &amp; Flow)</label>
                        <select id="part-hair" class="chop-part-select"><option value="">Base / None</option></select>
                    </div>
                    <div class="input-group">
                        <label>👗 Upholstery &amp; Armor (Garments &amp; Costumes)</label>
                        <select id="part-armor" class="chop-part-select"><option value="">Base / None</option></select>
                    </div>
                    <div class="input-group">
                        <label>💡 Engine &amp; Glow (Lighting &amp; Ambiance)</label>
                        <select id="part-glow" class="chop-part-select"><option value="">Base / None</option></select>
                    </div>
                    <div class="input-group">
                        <label>⚡ Detail Polish (Skin Pores &amp; Micro-Clarity)</label>
                        <select id="part-detail" class="chop-part-select"><option value="">Base / None</option></select>
                    </div>
                </div>

                <div class="grid-2" style="margin-top:8px;">
                    <div class="input-group">
                        <label>Franken-LoRA Output Name</label>
                        <input id="chopOutputName" type="text" value="Franken_Chop_v1" />
                    </div>
                    <div class="input-group">
                        <label>Output SVD Rank</label>
                        <select id="chopRank">
                            <option value="8">Rank 8 (~16MB)</option>
                            <option value="16" selected>Rank 16 (~32MB)</option>
                            <option value="32">Rank 32 (~64MB)</option>
                            <option value="64">Rank 64 (~128MB)</option>
                        </select>
                    </div>
                </div>

                <div style="margin-top:12px;">
                    <button class="btn" style="width:100%;" onclick="bakeChopShop()">🔥 Bake &amp; Synthesize Franken-LoRA on Server</button>
                    <div id="chopStatus" style="margin-top:8px; font-size:0.85rem; color:var(--accent); display:none;"></div>
                </div>
            </div>
        </section>

        <!-- 5. COMFYUI STUDIO TAB -->
        <section id="tab-comfy" class="tab-content">
            <div class="grid-2">
                <div class="card">
                    <div class="card-title">
                        <span>Remote ComfyUI Inference Engine</span>
                        <span id="comfyStatusBadge" class="badge">Checking...</span>
                    </div>

                    <div class="input-group">
                        <label>Prompt</label>
                        <textarea id="comfyPrompt" rows="3" placeholder="masterpiece, 1girl, highly detailed, expressive eyes, dynamic lighting"></textarea>
                    </div>

                    <div class="input-group">
                        <label>Negative Prompt</label>
                        <input id="comfyNeg" type="text" value="blurry, bad anatomy, low quality, artifacts, watermark" />
                    </div>

                    <div class="input-group">
                        <label>Active LoRA</label>
                        <select id="comfyLoraSelect">
                            <option value="none">None (Base Checkpoint)</option>
                        </select>
                    </div>

                    <div class="grid-3">
                        <div class="input-group">
                            <label>LoRA Weight (<span id="comfyWeightVal">1.0</span>)</label>
                            <input id="comfyWeight" type="range" min="0" max="2" step="0.05" value="1.0" oninput="document.getElementById('comfyWeightVal').textContent = parseFloat(this.value).toFixed(2)" />
                        </div>
                        <div class="input-group">
                            <label>Steps</label>
                            <input id="comfySteps" type="number" value="20" min="10" max="60" />
                        </div>
                        <div class="input-group">
                            <label>CFG Scale</label>
                            <input id="comfyCfg" type="number" value="7.0" step="0.5" min="1" max="20" />
                        </div>
                    </div>

                    <button class="btn" onclick="runComfyTest()">🎨 Generate Test Image on Host GPU</button>
                    <div id="comfyGenStatus" style="font-size:0.85rem; color:var(--text-muted); display:none;">Queued in ComfyUI...</div>
                </div>

                <div class="card" style="align-items:center; justify-content:center; min-height:420px; background:#181825;">
                    <div id="comfyPlaceholder" style="text-align:center; color:var(--text-muted);">
                        <div style="font-size:3rem; margin-bottom:8px;">🖼️</div>
                        <div>Generated ComfyUI image will appear here</div>
                    </div>
                    <img id="comfyPreview" style="display:none; max-width:100%; max-height:500px; border-radius:8px; border:1px solid var(--border);" />
                    <a id="comfyDownloadBtn" class="btn btn-secondary btn-sm" style="display:none; margin-top:10px;" download="loramancer_sample.png">💾 Download Image</a>
                </div>
            </div>
        </section>

        <!-- 6. TRAINING HISTORY TAB -->
        <section id="tab-history" class="tab-content">
            <div class="card">
                <div class="card-title">
                    <span>LoRA Training History &amp; Records</span>
                    <button class="btn btn-install btn-sm" onclick="loadHistory()">🔄 Refresh History</button>
                </div>
                <div id="historyTableContainer" style="overflow-x:auto;">
                    <div style="color:var(--text-muted); font-size:0.88rem; padding: 12px 0;">Loading training history...</div>
                </div>
            </div>
        </section>
    </main>

    <script>
        const terminal = document.getElementById('terminal');
        const progressBar = document.getElementById('progressBar');
        const stepCounter = document.getElementById('stepCounter');
        const lossVal = document.getElementById('lossVal');
        const percentVal = document.getElementById('percentVal');
        const hostStatus = document.getElementById('hostStatus');
        const workerState = document.getElementById('workerState');
        const publicBadge = document.getElementById('publicUrlBadge');

        const activePublicUrl = '{{publicUrl}}';
        if (activePublicUrl) {
            publicBadge.style.display = 'inline-flex';
            publicBadge.textContent = '🌐 ' + activePublicUrl.replace(/^https?:\/\//, '');
        }

        function copyPublicUrl() {
            if (activePublicUrl) {
                navigator.clipboard.writeText(activePublicUrl).then(() => {
                    alert('Copied URL to clipboard:\n' + activePublicUrl);
                });
            }
        }

        function appendLog(msg) {
            terminal.textContent += msg + '\n';
            terminal.scrollTop = terminal.scrollHeight;
        }

        function switchTab(tabId) {
            document.querySelectorAll('.tab-content').forEach(el => el.classList.remove('active'));
            document.querySelectorAll('nav.tabs button').forEach(el => el.classList.remove('active'));

            const content = document.getElementById('tab-' + tabId);
            const btn = document.getElementById('tabBtn-' + tabId);
            if (content) content.classList.add('active');
            if (btn) btn.classList.add('active');

            if (tabId === 'vault') loadVaultLoras();
            if (tabId === 'comfy') checkComfyStatus();
            if (tabId === 'history') loadHistory();
        }

        // PWA Service Worker Registration
        if ('serviceWorker' in navigator) {
            window.addEventListener('load', () => {
                navigator.serviceWorker.register('/sw.js').catch(err => {
                    console.log('Service Worker registration:', err);
                });
            });
        }

        let deferredPrompt;
        window.addEventListener('beforeinstallprompt', (e) => {
            e.preventDefault();
            deferredPrompt = e;
            const btn = document.getElementById('installPwaBtn');
            if (btn) btn.style.display = 'inline-flex';
        });

        async function installPwa() {
            if (!deferredPrompt) return;
            deferredPrompt.prompt();
            const { outcome } = await deferredPrompt.userChoice;
            if (outcome === 'accepted') {
                const btn = document.getElementById('installPwaBtn');
                if (btn) btn.style.display = 'none';
            }
            deferredPrompt = null;
        }

        function getAuthToken() {
            return localStorage.getItem('loramancer_auth_token') || '';
        }

        function authHeaders() {
            const token = getAuthToken();
            const headers = {};
            if (token) {
                headers['Authorization'] = 'Bearer ' + token;
            }
            return headers;
        }

        async function checkHealth() {
            try {
                const res = await fetch('/api/v1/health', { headers: authHeaders() });
                if (res.status === 401) {
                    window.location.reload();
                    return;
                }
                if (res.ok) {
                    const health = await res.json();
                    hostStatus.textContent = health.Status;
                    hostStatus.className = health.IsTraining ? 'badge badge-busy' : 'badge badge-live';
                    workerState.textContent = health.IsTraining ? 'BUSY' : 'IDLE';
                    workerState.style.color = health.IsTraining ? 'var(--amber)' : 'var(--green)';
                }
            } catch(err) {
                appendLog('[Health check error]: ' + err.message);
            }
        }

        let evtSource = null;
        function initTelemetryStream() {
            if (evtSource) evtSource.close();
            const token = getAuthToken();
            const streamUrl = token ? '/api/v1/training/stream?token=' + encodeURIComponent(token) : '/api/v1/training/stream';
            evtSource = new EventSource(streamUrl);
            evtSource.onmessage = function(e) {
                try {
                    const data = JSON.parse(e.data);
                    if (data.EventType === 'step') {
                        stepCounter.textContent = 'Step ' + data.Step + ' / ' + data.TotalSteps;
                        lossVal.textContent = data.Loss.toFixed(4);
                        const pct = data.TotalSteps > 0 ? Math.round((data.Step / data.TotalSteps) * 100) : 0;
                        progressBar.style.width = pct + '%';
                        percentVal.textContent = pct + '%';
                        hostStatus.textContent = 'Training';
                        hostStatus.className = 'badge badge-busy';
                        workerState.textContent = 'TRAINING';
                        workerState.style.color = 'var(--amber)';
                    }
                    if (data.Message) appendLog(data.Message);
                } catch(err) {
                    appendLog(e.data);
                }
            };
        }

        checkHealth();
        initTelemetryStream();
        loadVaultLoras();

        // --- VAULT LOGIC ---
        let allVaultLoras = [];
        async function loadVaultLoras() {
            const listEl = document.getElementById('vaultList');
            const chopSelect = document.getElementById('chopAddSelect');
            const comfySelect = document.getElementById('comfyLoraSelect');
            try {
                const res = await fetch('/api/v1/vault/loras', { headers: authHeaders() });
                if (res.status === 401) return window.location.reload();
                if (!res.ok) throw new Error('Failed to fetch LoRAs');
                allVaultLoras = await res.json();
                renderVaultList(allVaultLoras);

                if (chopSelect) {
                    chopSelect.innerHTML = '<option value="">Choose a model from host vault...</option>';
                    allVaultLoras.forEach(m => {
                        chopSelect.innerHTML += `<option value="${m.filePath}">${m.fileName} (${m.formattedSize})</option>`;
                    });
                }
                if (comfySelect) {
                    comfySelect.innerHTML = '<option value="none">None (Base Checkpoint)</option>';
                    allVaultLoras.forEach(m => {
                        comfySelect.innerHTML += `<option value="${m.fileName}">${m.fileName}</option>`;
                    });
                }
            } catch(e) {
                if (listEl) listEl.innerHTML = `<div style="color:var(--red);">Error loading LoRAs: ${e.message}</div>`;
            }
        }

        function renderVaultList(items) {
            const listEl = document.getElementById('vaultList');
            if (!listEl) return;
            if (items.length === 0) {
                listEl.innerHTML = '<div style="color:var(--text-muted); font-style:italic;">No LoRA models found in host outputs.</div>';
                return;
            }
            listEl.innerHTML = items.map(item => `
                <div style="background:#181825; border:1px solid var(--border); border-radius:8px; padding:12px; display:flex; flex-direction:column; gap:8px;">
                    <div style="font-weight:700; color:#89b4fa; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" title="${item.fileName}">
                        ${item.fileName}
                    </div>
                    <div style="display:flex; justify-content:space-between; font-size:0.75rem; color:var(--text-muted);">
                        <span>Size: ${item.formattedSize}</span>
                        <span>${new Date(item.lastModified).toLocaleDateString()}</span>
                    </div>
                    <div style="display:flex; gap:6px; margin-top:4px;">
                        <button class="btn btn-secondary btn-sm" style="flex:1;" onclick="sendToLab('${item.filePath.replace(/\\/g, '\\\\')}')">🔬 Lab</button>
                        <button class="btn btn-secondary btn-sm" style="flex:1;" onclick="sendToChop('${item.filePath.replace(/\\/g, '\\\\')}')">🛠️ Chop</button>
                        <button class="btn btn-secondary btn-sm" style="flex:1;" onclick="sendToComfy('${item.fileName}')">🎨 Test</button>
                    </div>
                </div>
            `).join('');
        }

        function filterVault() {
            const q = document.getElementById('vaultSearch').value.toLowerCase();
            const filtered = allVaultLoras.filter(x => x.fileName.toLowerCase().includes(q));
            renderVaultList(filtered);
        }

        function sendToLab(path) {
            document.getElementById('labModelPath').value = path;
            switchTab('lab');
            runLabInspection();
        }

        function sendToChop(path) {
            switchTab('chop');
            addDonorByPath(path);
        }

        function sendToComfy(name) {
            document.getElementById('comfyLoraSelect').value = name;
            switchTab('comfy');
        }

        // --- DIAGNOSTIC LAB LOGIC ---
        async function runLabInspection() {
            const path = document.getElementById('labModelPath').value.trim();
            if (!path) return alert('Please enter a target LoRA file path');
            const card = document.getElementById('labReportCard');
            card.style.display = 'block';
            document.getElementById('labReportName').textContent = 'Analyzing ' + path.split(/[\\\\/]/).pop() + '...';

            try {
                const headers = authHeaders();
                headers['Content-Type'] = 'application/json';
                const res = await fetch('/api/v1/lab/inspect', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({ FilePath: path })
                });
                const data = await res.json();
                if (res.ok && data.success) {
                    document.getElementById('labReportName').textContent = data.modelName;
                    const badge = document.getElementById('labReportBadge');
                    badge.textContent = data.status;
                    badge.className = 'badge ' + (data.status.includes('Optimal') ? 'badge-live' : 'badge-busy');
                    document.getElementById('labScoreVal').textContent = data.score + '%';
                    document.getElementById('labNormVal').textContent = data.averageFrobeniusNorm.toFixed(3);
                    document.getElementById('labLayersVal').textContent = data.layerCount;
                    document.getElementById('labVerdict').textContent = data.verdict + ' Recommendation: ' + data.recommendation;
                } else {
                    document.getElementById('labVerdict').textContent = 'Inspection failed: ' + (data.error || 'Unknown error');
                }
            } catch(e) {
                document.getElementById('labVerdict').textContent = 'Error: ' + e.message;
            }
        }

        async function applyLabRescale() {
            const path = document.getElementById('labModelPath').value.trim();
            if (!path) return alert('Please specify a target LoRA path');
            const te = parseFloat(document.getElementById('teSlider').value);
            const unet = parseFloat(document.getElementById('unetSlider').value);
            const rank = parseInt(document.getElementById('labRank').value);
            const status = document.getElementById('labRescaleStatus');
            status.style.display = 'block';
            status.textContent = 'Processing rescale on host GPU...';

            try {
                const headers = authHeaders();
                headers['Content-Type'] = 'application/json';
                const res = await fetch('/api/v1/lab/rescale', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({ FilePath: path, TeScale: te, UnetScale: unet, TargetRank: rank })
                });
                const data = await res.json();
                if (res.ok) {
                    status.textContent = 'Rescale complete! Saved as ' + data.outputLoraPath;
                    loadVaultLoras();
                } else {
                    status.textContent = 'Error: ' + (data.error || 'Rescale failed');
                }
            } catch(e) {
                status.textContent = 'Network error: ' + e.message;
            }
        }

        // --- LORA CHOP-SHOP LOGIC ---
        const chopDonors = [];
        async function addSelectedDonorToGarage() {
            const path = document.getElementById('chopAddSelect').value;
            if (!path) return;
            addDonorByPath(path);
        }

        async function addDonorByPath(path) {
            if (chopDonors.some(d => d.filePath === path)) return alert('Model already added to garage.');
            const headers = authHeaders();
            headers['Content-Type'] = 'application/json';
            try {
                const res = await fetch('/api/v1/chop-shop/inspect-donor', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({ FilePath: path })
                });
                const donor = await res.json();
                if (res.ok) {
                    chopDonors.push(donor);
                    renderChopGarage();
                    updateChopPartSelects();
                } else {
                    alert('Failed to inspect donor: ' + (donor.error || 'Server error'));
                }
            } catch(e) {
                alert('Error: ' + e.message);
            }
        }

        function clearChopGarage() {
            chopDonors.length = 0;
            renderChopGarage();
            updateChopPartSelects();
        }

        function renderChopGarage() {
            const shelf = document.getElementById('chopDonorsShelf');
            if (chopDonors.length === 0) {
                shelf.innerHTML = '<div style="color:var(--text-muted); font-size:0.85rem; font-style:italic;">No donors added yet. Select a model above to begin.</div>';
                return;
            }
            shelf.innerHTML = chopDonors.map((d, i) => `
                <div style="background:#181825; border:1px solid var(--border); border-radius:8px; padding:12px;">
                    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                        <span class="badge" style="background:#313244; color:#89b4fa; font-size:0.65rem;">DONOR #${i + 1}</span>
                        <span style="font-size:0.75rem; color:var(--green); font-weight:700;">Rank ${d.rank}</span>
                    </div>
                    <div style="font-weight:700; color:#fff; font-size:0.85rem; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${d.fileName}</div>
                    <div class="badge badge-public" style="margin-top:6px; font-size:0.7rem;">${d.dominantFeature}</div>
                </div>
            `).join('');
        }

        function updateChopPartSelects() {
            document.querySelectorAll('.chop-part-select').forEach(sel => {
                const prev = sel.value;
                sel.innerHTML = '<option value="">None (Base)</option>' + chopDonors.map(d => `
                    <option value="${d.id}">${d.fileName} (${d.dominantFeature})</option>
                `).join('');
                if (chopDonors.some(d => d.id === prev)) sel.value = prev;
            });
        }

        function autoAssignChop() {
            if (chopDonors.length === 0) return alert('Add at least 1 donor first');
            document.querySelectorAll('.chop-part-select').forEach((sel, idx) => {
                const d = chopDonors[idx % chopDonors.length];
                if (d) sel.value = d.id;
            });
        }

        async function bakeChopShop() {
            if (chopDonors.length === 0) return alert('Please add at least 1 donor model to the garage.');
            const name = document.getElementById('chopOutputName').value.trim() || 'Franken_Chop';
            const rank = parseInt(document.getElementById('chopRank').value);
            const status = document.getElementById('chopStatus');
            status.style.display = 'block';
            status.textContent = 'Grafting tensors and synthesizing SVD on host server...';

            const parts = [
                { PartName: 'Chassis & Face', SelectedDonorId: document.getElementById('part-face').value, Weight: 1.0 },
                { PartName: 'Headlights & Eyes', SelectedDonorId: document.getElementById('part-eyes').value, Weight: 1.0 },
                { PartName: 'Custom Paint & Hair', SelectedDonorId: document.getElementById('part-hair').value, Weight: 1.0 },
                { PartName: 'Upholstery & Armor', SelectedDonorId: document.getElementById('part-armor').value, Weight: 1.0 },
                { PartName: 'Engine & Glow', SelectedDonorId: document.getElementById('part-glow').value, Weight: 1.0 },
                { PartName: 'Detail Polish', SelectedDonorId: document.getElementById('part-detail').value, Weight: 1.0 }
            ];

            const outDir = chopDonors[0].filePath ? chopDonors[0].filePath.substring(0, chopDonors[0].filePath.lastIndexOf(/[\\\\/]/)) : '';
            const outPath = outDir + '/' + name + '.safetensors';

            try {
                const headers = authHeaders();
                headers['Content-Type'] = 'application/json';
                const res = await fetch('/api/v1/chop-shop/bake', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({
                        RecipeName: name,
                        OutputPath: outPath,
                        TargetRank: rank,
                        Donors: chopDonors,
                        PartAssignments: parts
                    })
                });
                const result = await res.json();
                if (res.ok && result.success) {
                    status.textContent = 'Franken-LoRA successfully baked on server! Created: ' + result.outputPath;
                    loadVaultLoras();
                } else {
                    status.textContent = 'Bake failed: ' + (result.message || 'Error');
                }
            } catch(e) {
                status.textContent = 'Network error: ' + e.message;
            }
        }

        // --- COMFYUI STUDIO LOGIC ---
        async function checkComfyStatus() {
            const badge = document.getElementById('comfyStatusBadge');
            try {
                const res = await fetch('/api/v1/comfyui/status', { headers: authHeaders() });
                const data = await res.json();
                if (data.isOnline) {
                    badge.className = 'badge badge-live';
                    badge.textContent = 'Online (' + (data.device || 'GPU') + ')';
                } else {
                    badge.className = 'badge badge-busy';
                    badge.textContent = 'Offline';
                }
            } catch(e) {
                badge.className = 'badge badge-busy';
                badge.textContent = 'Unavailable';
            }
        }

        async function runComfyTest() {
            const prompt = document.getElementById('comfyPrompt').value.trim();
            if (!prompt) return alert('Please enter a prompt.');
            const lora = document.getElementById('comfyLoraSelect').value;
            const weight = parseFloat(document.getElementById('comfyWeight').value);
            const steps = parseInt(document.getElementById('comfySteps').value);
            const cfg = parseFloat(document.getElementById('comfyCfg').value);
            const neg = document.getElementById('comfyNeg').value.trim();
            const status = document.getElementById('comfyGenStatus');
            const preview = document.getElementById('comfyPreview');
            const placeholder = document.getElementById('comfyPlaceholder');
            const downloadBtn = document.getElementById('comfyDownloadBtn');

            status.style.display = 'block';
            status.textContent = 'Rendering prompt graph on host ComfyUI instance...';

            try {
                const headers = authHeaders();
                headers['Content-Type'] = 'application/json';
                const res = await fetch('/api/v1/comfyui/test', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({
                        Prompt: prompt,
                        NegativePrompt: neg,
                        LoraName: lora,
                        LoraWeight: weight,
                        Steps: steps,
                        Cfg: cfg
                    })
                });
                const data = await res.json();
                if (res.ok && data.success) {
                    status.textContent = 'Render completed successfully!';
                    placeholder.style.display = 'none';
                    preview.style.display = 'block';
                    preview.src = 'data:' + data.mimeType + ';base64,' + data.imageBase64;
                    downloadBtn.style.display = 'inline-flex';
                    downloadBtn.href = preview.src;
                } else {
                    status.textContent = 'Generation error: ' + (data.error || 'Failed');
                }
            } catch(e) {
                status.textContent = 'Network error: ' + e.message;
            }
        }

        // --- HISTORY LOGIC ---
        async function loadHistory() {
            const container = document.getElementById('historyTableContainer');
            try {
                const res = await fetch('/api/v1/history', { headers: authHeaders() });
                if (res.status === 401) return window.location.reload();
                if (!res.ok) {
                    container.innerHTML = '<div style="color:var(--red); padding:10px 0;">Failed to load history from host.</div>';
                    return;
                }
                const list = await res.json();
                if (!list || list.length === 0) {
                    container.innerHTML = '<div style="color:var(--text-muted); font-style:italic; padding:12px 0;">No training records found in vault.</div>';
                    return;
                }
                let html = '<table class="history-table"><thead><tr>' +
                    '<th>Name</th><th>Architecture</th><th>Status</th><th>Steps</th><th>Loss</th><th>Completed</th><th>Action</th>' +
                    '</tr></thead><tbody>';
                for (const item of list) {
                    const status = item.Status || 'Unknown';
                    const isSuccess = status === 'Completed';
                    const isFailed = status === 'Failed' || status === 'Cancelled';
                    const badgeClass = isSuccess ? 'badge-live' : (isFailed ? 'badge-busy' : '');
                    const btnLabel = isFailed ? '🔁 Retry' : '▶️ Retrain';
                    const dateStr = item.CompletedAt ? new Date(item.CompletedAt).toLocaleDateString() : '-';
                    const arch = (item.BaseArchitecture || 'FLUX').toUpperCase();
                    const loss = item.FinalLoss ? item.FinalLoss.toFixed(4) : '-';
                    html += '<tr>' +
                        '<td><b>' + (item.Name || 'Unnamed') + '</b><div style="font-size:0.75rem; color:var(--text-muted); font-family:monospace;">' + (item.TriggerWord || '') + '</div></td>' +
                        '<td><span class="badge" style="background:#313244; color:#cdd6f4;">' + arch + '</span></td>' +
                        '<td><span class="badge ' + badgeClass + '">' + status + '</span></td>' +
                        '<td>' + (item.Steps || 0) + '</td>' +
                        '<td style="color:var(--green); font-weight:600;">' + loss + '</td>' +
                        '<td>' + dateStr + '</td>' +
                        '<td><button class="btn btn-secondary btn-sm" onclick="retryTraining(\'' + item.Id + '\', \'' + (item.Name || 'Job').replace(/'/g, "\\'") + '\')">' + btnLabel + '</button></td>' +
                    '</tr>';
                }
                html += '</tbody></table>';
                container.innerHTML = html;
            } catch(e) {
                container.innerHTML = '<div style="color:var(--red); padding:10px 0;">Error loading history: ' + e.message + '</div>';
            }
        }

        async function retryTraining(id, name) {
            if (!confirm('Resubmit and start training for "' + name + '" on host?')) return;
            try {
                const headers = authHeaders();
                headers['Content-Type'] = 'application/json';
                const res = await fetch('/api/v1/history/retry/' + encodeURIComponent(id), {
                    method: 'POST',
                    headers
                });
                if (res.status === 401) return window.location.reload();
                const data = await res.json();
                if (res.ok) {
                    appendLog('[RETRY] Training successfully queued for: ' + name);
                    switchTab('train');
                    checkHealth();
                } else {
                    alert('Failed to retry training: ' + (data.error || 'Server error'));
                }
            } catch(e) {
                alert('Network error: ' + e.message);
            }
        }

        async function startTraining() {
            const runName = document.getElementById('runName').value.trim() || 'remote_run';
            const triggerWord = document.getElementById('triggerWord').value.trim();
            const steps = parseInt(document.getElementById('steps').value) || 1500;
            const arch = document.getElementById('architecture').value;
            const fileInput = document.getElementById('datasetZip');

            let datasetPath = '';
            if (fileInput.files.length > 0) {
                appendLog('Uploading dataset ZIP archive to host AI PC...');
                const formData = new FormData();
                formData.append('file', fileInput.files[0]);
                const uploadHeaders = authHeaders();
                const uploadRes = await fetch('/api/v1/datasets/upload', {
                    method: 'POST',
                    headers: uploadHeaders,
                    body: formData
                });
                if (uploadRes.status === 401) return window.location.reload();
                const uploadJson = await uploadRes.json();
                if (!uploadJson.Success) appendLog('Upload warning: ' + uploadJson.Message);
                datasetPath = uploadJson.ExtractedPath;
                appendLog('Dataset uncompressed on host: ' + datasetPath);
            }

            appendLog('Submitting training job to AI PC...');
            const yamlDummy = 'name: ' + runName + '\ntarget_steps: ' + steps + '\ndataset: ' + datasetPath;
            const startHeaders = authHeaders();
            startHeaders['Content-Type'] = 'application/json';

            const res = await fetch('/api/v1/training/start', {
                method: 'POST',
                headers: startHeaders,
                body: JSON.stringify({
                    RunName: runName,
                    BaseArchitecture: arch,
                    ConfigYaml: yamlDummy,
                    TriggerWord: triggerWord,
                    Steps: steps
                })
            });

            if (res.status === 401) return window.location.reload();
            if (res.ok) {
                appendLog('Job successfully accepted by host! Training initiated...');
            } else {
                appendLog('Error starting job: ' + res.statusText);
            }
        }

        async function stopTraining() {
            appendLog('Sending stop signal to host...');
            const res = await fetch('/api/v1/training/stop', {
                method: 'POST',
                headers: authHeaders()
            });
            if (res.status === 401) return window.location.reload();
            hostStatus.textContent = 'Ready';
            hostStatus.className = 'badge badge-live';
            workerState.textContent = 'IDLE';
            workerState.style.color = 'var(--green)';
        }
    </script>
</body>
</html>
""";
    }

    public async ValueTask DisposeAsync() {
        await StopServerAsync();
        _trainingRunner.OnProgressUpdated -= HandleProgressUpdated;
        _trainingRunner.OnLogReceived -= HandleLogReceived;
    }
}
