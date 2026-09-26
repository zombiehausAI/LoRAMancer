using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LoRAMancer.App.Engines;
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
    public string? Checkpoint { get; set; }
    public string? Architecture { get; set; }
}

public sealed class WebTrainingRequestDto {
    public string RunName { get; set; } = "lora_run";
    public string BaseArchitecture { get; set; } = "flux1";
    public string? DatasetPath { get; set; }
    public string? TriggerWord { get; set; }
    public int Steps { get; set; } = 1500;
    public int BatchSize { get; set; } = 1;
    public double LearningRate { get; set; } = 0.0001;
    public string Optimizer { get; set; } = "adamw8bit";
    public int NetworkDim { get; set; } = 16;
    public int NetworkAlpha { get; set; } = 16;
    public string Precision { get; set; } = "bf16";
    public int Resolution { get; set; } = 1024;
    public int Epochs { get; set; } = 10;
    public int GradientAccumulation { get; set; } = 1;
    public string? SamplePrompt { get; set; }
    public string? RawConfigYaml { get; set; }
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
    private readonly AiToolkitConfigBuilder? _configBuilder;
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
        ComfyUiService? comfyUiService = null,
        AiToolkitConfigBuilder? configBuilder = null
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
        _configBuilder = configBuilder;

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

        // 1. PWA Web App Manifest
        app.MapGet("/manifest.json", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

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

        // 2. Application SVG Icon
        app.MapGet("/icon.svg", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

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

        // 3. PWA Service Worker
        app.MapGet("/sw.js", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

            string swScript = """
                const CACHE_NAME = 'loramancer-shell-v3';
                const SHELL_ASSETS = ['/', '/manifest.json', '/icon.svg'];

                self.addEventListener('install', (event) => {
                    event.waitUntil(
                        caches.open(CACHE_NAME).then((cache) => cache.addAll(SHELL_ASSETS)).then(() => self.skipWaiting())
                    );
                });

                self.addEventListener('activate', (event) => {
                    event.waitUntil(
                        caches.keys().then((keys) => Promise.all(
                            keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
                        )).then(() => self.clients.claim())
                    );
                });

                self.addEventListener('fetch', (event) => {
                    const url = new URL(event.request.url);
                    if (url.pathname.startsWith('/api/')) return;
                    event.respondWith(
                        fetch(event.request).catch(() => caches.match(event.request).then((res) => res || caches.match('/')))
                    );
                });
                """;
            return Results.Content(swScript, "application/javascript");
        });

        // 4. Authentication Login
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
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

            ServerHealthDto health = new() {
                Status = _trainingRunner.IsRunning ? "Training" : "Ready",
                IsTraining = _trainingRunner.IsRunning,
                ActiveRunName = _trainingRunner.CurrentJob?.Name ?? (_trainingRunner.IsRunning ? "Active Run" : string.Empty),
                CurrentStep = _trainingRunner.CurrentProgress.CurrentStep,
                TotalSteps = _trainingRunner.CurrentProgress.TotalSteps,
                CurrentLoss = (float)_trainingRunner.CurrentProgress.CurrentLoss,
                PyTorchVersion = _settingsService.Current.PyTorchVersion,
                GpuInfo = "AMD ROCm / NVIDIA CUDA Host"
            };

            return Results.Json(health);
        });

        // 6. Environment Telemetry
        app.MapGet("/api/v1/environment", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

            var env = await _venvProvisioner.DetectEnvironmentAsync();
            return Results.Json(new {
                detectedVendor = env.DetectedVendor.ToString(),
                gpuName = string.IsNullOrWhiteSpace(env.GpuName) ? "AMD Radeon Graphics" : env.GpuName,
                rocmFound = env.RocmDriverFound,
                rocmVersion = env.RocmVersion,
                pythonFound = !string.IsNullOrWhiteSpace(env.PythonExecutable),
                pythonVersion = env.PythonVersion,
                vramTotalMb = 16384,
                vramAvailableMb = 12288,
                torchVersion = _settingsService.Current.PyTorchVersion,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                machineName = Environment.MachineName,
                isRemote = true
            });
        });

        // 7. Upload Dataset ZIP Archive
        app.MapPost("/api/v1/datasets/upload", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();

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
                Message = report.IsValid ? "Dataset uploaded and auto-extracted successfully on host." : "Dataset contains warnings or no valid images."
            });
        });

        // 8. Robust Training Launch / Enqueue
        app.MapPost("/api/v1/training/start", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();

            var req = await JsonSerializer.DeserializeAsync<WebTrainingRequestDto>(request.Body);
            if (req == null) return Results.BadRequest(new { error = "Invalid training parameters." });

            string runsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "remote_runs");
            Directory.CreateDirectory(runsDir);
            string configPath = Path.Combine(runsDir, $"{req.RunName}_config.yaml");

            string finalYaml = req.RawConfigYaml ?? string.Empty;
            if (string.IsNullOrWhiteSpace(finalYaml) && _configBuilder != null) {
                string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "outputs", req.RunName);
                Directory.CreateDirectory(outDir);

                var tc = new TrainingConfig {
                    RunName = req.RunName,
                    TargetBaseModel = req.BaseArchitecture,
                    DatasetDirectory = req.DatasetPath ?? "",
                    OutputDirectory = outDir,
                    TriggerWord = req.TriggerWord ?? "",
                    TotalSteps = req.Steps,
                    BatchSize = req.BatchSize > 0 ? req.BatchSize : 1,
                    LearningRate = req.LearningRate > 0 ? req.LearningRate : 1e-4,
                    Optimizer = !string.IsNullOrWhiteSpace(req.Optimizer) ? req.Optimizer : "adamw8bit",
                    NetworkDim = req.NetworkDim > 0 ? req.NetworkDim : 16,
                    NetworkAlpha = req.NetworkAlpha > 0 ? req.NetworkAlpha : 16,
                    Precision = !string.IsNullOrWhiteSpace(req.Precision) ? req.Precision : "bf16",
                    MaxTrainEpochs = req.Epochs > 0 ? req.Epochs : 10,
                    GradientAccumulationSteps = req.GradientAccumulation > 0 ? req.GradientAccumulation : 1
                };
                if (!string.IsNullOrWhiteSpace(req.SamplePrompt)) {
                    tc.SamplePrompts = new List<string> { req.SamplePrompt };
                }
                finalYaml = _configBuilder.BuildAiToolkitYaml(tc);
            }

            if (string.IsNullOrWhiteSpace(finalYaml)) {
                finalYaml = $"name: {req.RunName}\ntarget_steps: {req.Steps}\ndataset: {req.DatasetPath}";
            }

            await File.WriteAllTextAsync(configPath, finalYaml);

            string venvPath = Path.Combine(Directory.GetCurrentDirectory(), ".venv");
            if (!Directory.Exists(venvPath)) {
                venvPath = Path.Combine(AppContext.BaseDirectory, ".venv");
            }

            var job = _trainingRunner.EnqueueJob(configPath, venvPath, null, req.RunName);

            return Results.Accepted("/api/v1/health", new {
                message = "Training job registered and queued on host.",
                runName = req.RunName,
                jobId = job.Id,
                queuePosition = _trainingRunner.QueueCount
            });
        });

        // 9. Stop Active Run
        app.MapPost("/api/v1/training/stop", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            _trainingRunner.CancelTraining();
            return Results.Ok(new { message = "Active training job stopped." });
        });

        // 10. Cancel All & Clear Queue
        app.MapPost("/api/v1/training/cancel-all", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            _trainingRunner.CancelAll();
            return Results.Ok(new { message = "All queued and active training jobs cancelled." });
        });

        // 11. Clear Logs
        app.MapPost("/api/v1/training/clear-logs", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            _trainingRunner.ClearLogs();
            return Results.Ok(new { message = "Console logs cleared." });
        });

        // 12. Training Queue Status
        app.MapGet("/api/v1/training/queue", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            return Results.Json(new {
                isRunning = _trainingRunner.IsRunning,
                currentJob = _trainingRunner.CurrentJob,
                queue = _trainingRunner.Queue,
                queueCount = _trainingRunner.QueueCount
            });
        });

        // 13. SSE Live Telemetry Stream
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
            } catch (OperationCanceledException) { }
        });

        // 14. Training History Records
        app.MapGet("/api/v1/history", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            if (_historyService == null) return Results.Json(Array.Empty<LoraHistoryRecord>());
            var records = await _historyService.GetHistoryAsync();
            return Results.Json(records);
        });

        // 15. Retry Training Run From History
        app.MapPost("/api/v1/history/retry/{id}", async (string id, HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            if (_historyService == null) return Results.NotFound(new { error = "History service not available on host." });

            var record = await _historyService.GetRecordByIdAsync(id);
            if (record == null) return Results.NotFound(new { error = "History record not found." });
            if (string.IsNullOrWhiteSpace(record.ConfigYamlPath) || !File.Exists(record.ConfigYamlPath)) {
                return Results.BadRequest(new { error = "Training configuration YAML not found on host." });
            }

            string venvPath = Path.Combine(Directory.GetCurrentDirectory(), ".venv");
            if (!Directory.Exists(venvPath)) venvPath = Path.Combine(AppContext.BaseDirectory, ".venv");

            var job = _trainingRunner.EnqueueJob(record.ConfigYamlPath, venvPath, null, record.Name);
            return Results.Accepted($"/api/v1/history/retry/{id}", new {
                message = $"Training job '{record.Name}' resubmitted to queue.",
                runName = record.Name,
                jobId = job.Id
            });
        });

        // 16. Vault / Server LoRA Models Discovery
        app.MapGet("/api/v1/vault/loras", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

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

        // 17. Diagnostic Lab: Overbake & Spectral Analysis
        app.MapPost("/api/v1/lab/inspect", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_overbakeRadar == null) return Results.BadRequest(new { error = "Overbake Radar service not initialized." });

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

        // 18. Diagnostic Lab: Layer Rescale & SVD Rank Pruning
        app.MapPost("/api/v1/lab/rescale", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_surgeryService == null) return Results.BadRequest(new { error = "Surgery service not initialized." });

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

        // 19. LoRA Chop-Shop: Inspect Donor Model
        app.MapPost("/api/v1/chop-shop/inspect-donor", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_chopShopService == null) return Results.BadRequest(new { error = "Chop-Shop service not initialized." });

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

        // 20. LoRA Chop-Shop: Assemble & Bake Franken-LoRA
        app.MapPost("/api/v1/chop-shop/bake", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_chopShopService == null) return Results.BadRequest(new { error = "Chop-Shop service not initialized." });

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

        // 21. ComfyUI Status
        app.MapGet("/api/v1/comfyui/status", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            if (_comfyUiService == null) return Results.Json(new { isOnline = false, message = "ComfyUI service unavailable." });

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

        // 22. ComfyUI Interactive Test Generation
        app.MapPost("/api/v1/comfyui/test", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_comfyUiService == null) return Results.BadRequest(new { error = "ComfyUI service unavailable." });

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

        // 23. Embedded Desktop-Mirrored Web Application (Protected)
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

        // 24. Token Management API
        app.MapGet("/api/v1/tokens", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            if (_authTokenManager == null) return Results.Ok(Array.Empty<AuthToken>());
            return Results.Json(_authTokenManager.GetAllTokens());
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
                } catch { }
            });
        }

        OnServerStateChanged?.Invoke(true, ListeningUrl);
    }

    public async Task StopServerAsync() {
        if (!IsRunning || _webApp == null) return;

        try {
            _serverCts?.Cancel();
            if (_publicTunnelService != null) {
                try {
                    await _publicTunnelService.StopTunnelAsync();
                } catch { }
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
            if (CheckTokenValid(token, effectiveToken)) return true;
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
            if (CheckTokenValid(token, effectiveToken)) return true;
        }

        return false;
    }

    private bool CheckTokenValid(string token, string? expectedFallbackToken) {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (_authTokenManager != null && _authTokenManager.ValidateToken(token, out _)) return true;
        if (!string.IsNullOrWhiteSpace(expectedFallbackToken) &&
            string.Equals(token, expectedFallbackToken.Trim(), StringComparison.Ordinal)) return true;
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
            } catch { }
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
    <title>LoRAMancer Studio</title>
    
    <!-- PWA & Mobile Web App Meta Tags -->
    <link rel="manifest" href="/manifest.json" />
    <meta name="theme-color" content="#11111b" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
    <meta name="apple-mobile-web-app-title" content="LoRAMancer" />
    <link rel="icon" type="image/svg+xml" href="/icon.svg" />
    <link rel="apple-touch-icon" href="/icon.svg" />

    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500;600;700&display=swap" rel="stylesheet" />
    <style>
        :root {
            --bg-base: #11111b;
            --bg-surface: #181825;
            --bg-overlay: #1e1e2e;
            --bg-card: #252538;
            --border-dark: #313244;
            --border-focus: #cba6f7;
            --accent-purple: #cba6f7;
            --accent-blue: #89b4fa;
            --accent-green: #a6e3a1;
            --accent-red: #f38ba8;
            --accent-amber: #f9e2af;
            --text-primary: #cdd6f4;
            --text-secondary: #a6adc8;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            background-color: var(--bg-base);
            color: var(--text-primary);
            font-family: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            min-height: 100vh;
            display: flex;
            overflow-x: hidden;
        }

        /* 1. Left Sidebar (Matching Desktop LoRAMancer NavMenu) */
        aside.sidebar {
            width: 260px;
            background-color: var(--bg-surface);
            border-right: 1px solid var(--border-dark);
            display: flex;
            flex-direction: column;
            flex-shrink: 0;
            height: 100vh;
            position: sticky;
            top: 0;
            overflow-y: auto;
            z-index: 100;
            transition: transform 0.25s ease;
        }
        .sidebar-brand {
            padding: 16px 20px;
            display: flex;
            align-items: center;
            gap: 12px;
            border-bottom: 1px solid var(--border-dark);
        }
        .logo-box {
            width: 38px;
            height: 38px;
            border-radius: 10px;
            background: linear-gradient(135deg, var(--accent-purple), var(--accent-blue));
            display: flex;
            align-items: center;
            justify-content: center;
            font-weight: 800;
            font-size: 1.15rem;
            color: #11111b;
            box-shadow: 0 4px 14px rgba(203, 166, 247, 0.25);
        }
        .sidebar-nav {
            padding: 12px 8px;
            display: flex;
            flex-direction: column;
            gap: 2px;
            flex: 1;
        }
        .nav-section-title {
            font-size: 0.72rem;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.6px;
            color: var(--text-secondary);
            padding: 12px 14px 4px 14px;
        }
        .nav-item {
            display: flex;
            align-items: center;
            gap: 10px;
            padding: 9px 14px;
            color: var(--text-secondary);
            background: transparent;
            border: none;
            border-radius: 6px;
            font-size: 0.85rem;
            font-weight: 500;
            cursor: pointer;
            text-align: left;
            width: 100%;
            transition: all 0.15s ease;
        }
        .nav-item:hover {
            color: #fff;
            background-color: rgba(255, 255, 255, 0.04);
        }
        .nav-item.active {
            color: #fff;
            background-color: rgba(203, 166, 247, 0.12);
            font-weight: 600;
        }
        .nav-item.active .nav-icon {
            color: var(--accent-purple);
        }
        .nav-icon {
            font-size: 1.1rem;
            width: 22px;
            display: inline-flex;
            justify-content: center;
        }
        .sidebar-divider {
            height: 1px;
            background-color: var(--border-dark);
            margin: 8px 12px;
        }
        .sidebar-footer {
            padding: 14px 18px;
            border-top: 1px solid var(--border-dark);
            background-color: rgba(0, 0, 0, 0.2);
            font-size: 0.75rem;
            display: flex;
            flex-direction: column;
            gap: 6px;
        }

        /* 2. Top Header Bar (Matching Desktop MainLayout) */
        .app-shell {
            flex: 1;
            display: flex;
            flex-direction: column;
            min-width: 0;
            height: 100vh;
            overflow-y: auto;
        }
        header.top-header {
            background-color: var(--bg-surface);
            border-bottom: 1px solid var(--border-dark);
            padding: 10px 24px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 16px;
            position: sticky;
            top: 0;
            z-index: 90;
        }
        .top-left {
            display: flex;
            align-items: center;
            gap: 12px;
            flex-wrap: wrap;
        }
        .mobile-toggle {
            display: none;
            background: transparent;
            border: 1px solid var(--border-dark);
            color: var(--text-primary);
            padding: 6px 10px;
            border-radius: 6px;
            cursor: pointer;
        }
        .chip {
            font-size: 0.75rem;
            padding: 3px 10px;
            border-radius: 12px;
            font-weight: 600;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            border: 1px solid transparent;
        }
        .chip-stage { background: #1e1e2e; border-color: var(--border-dark); color: var(--text-secondary); cursor: pointer; }
        .chip-stage.active { background: rgba(203, 166, 247, 0.2); border-color: var(--accent-purple); color: #fff; }
        .chip-gpu { background: rgba(166, 227, 161, 0.12); border-color: var(--accent-green); color: var(--accent-green); }
        .chip-live { background: rgba(166, 227, 161, 0.15); color: var(--accent-green); }
        .chip-busy { background: rgba(249, 226, 175, 0.15); color: var(--accent-amber); }

        /* 3. Main Views */
        main.content-area {
            flex: 1;
            padding: 24px 32px;
            max-width: 1500px;
            width: 100%;
            margin: 0 auto;
        }
        .view-panel { display: none; }
        .view-panel.active { display: block; animation: fadeIn 0.15s ease-in-out; }
        @keyframes fadeIn { from { opacity: 0; transform: translateY(4px); } to { opacity: 1; transform: translateY(0); } }

        /* Cards & Grids */
        .card {
            background-color: var(--bg-surface);
            border: 1px solid var(--border-dark);
            border-radius: 8px;
            padding: 18px 20px;
            margin-bottom: 20px;
        }
        .card-header-bar {
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 14px;
            flex-wrap: wrap;
            gap: 10px;
        }
        .card-title {
            font-size: 1.05rem;
            font-weight: 700;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .grid-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; }
        .grid-3 { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; }
        .grid-4 { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }

        /* Form Inputs */
        .input-group {
            display: flex;
            flex-direction: column;
            gap: 6px;
            margin-bottom: 12px;
        }
        label {
            font-size: 0.8rem;
            color: var(--text-secondary);
            font-weight: 600;
        }
        input, select, textarea {
            background-color: var(--bg-card);
            border: 1px solid var(--border-dark);
            color: var(--text-primary);
            padding: 9px 12px;
            border-radius: 6px;
            font-family: inherit;
            font-size: 0.9rem;
        }
        input:focus, select:focus, textarea:focus {
            outline: none;
            border-color: var(--border-focus);
            box-shadow: 0 0 0 2px rgba(203, 166, 247, 0.2);
        }

        /* Buttons */
        .btn {
            background: linear-gradient(135deg, var(--accent-purple), #b4befe);
            color: #11111b;
            font-weight: 700;
            padding: 9px 16px;
            border: none;
            border-radius: 6px;
            cursor: pointer;
            font-size: 0.88rem;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 6px;
            transition: opacity 0.15s, transform 0.1s;
        }
        .btn:hover { opacity: 0.92; }
        .btn:active { transform: scale(0.98); }
        .btn-outline { background: transparent; border: 1px solid var(--border-dark); color: var(--text-primary); }
        .btn-outline:hover { background: rgba(255, 255, 255, 0.05); }
        .btn-secondary { background: var(--bg-card); color: var(--text-primary); border: 1px solid var(--border-dark); }
        .btn-danger { background: var(--accent-red); color: #11111b; }
        .btn-sm { padding: 5px 10px; font-size: 0.78rem; border-radius: 4px; }

        /* Telemetry & Progress */
        .stat-box {
            background-color: rgba(255, 255, 255, 0.02);
            border: 1px solid var(--border-dark);
            border-radius: 6px;
            padding: 12px;
            text-align: center;
        }
        .stat-label { font-size: 0.72rem; color: var(--text-secondary); text-transform: uppercase; font-weight: 600; }
        .stat-val { font-size: 1.25rem; font-weight: 700; color: #fff; margin-top: 4px; }
        .progress-bar-container {
            background-color: var(--bg-card);
            border-radius: 6px;
            height: 10px;
            overflow: hidden;
            width: 100%;
            margin: 12px 0;
        }
        .progress-bar-fill {
            background: linear-gradient(90deg, var(--accent-purple), var(--accent-blue));
            height: 100%;
            width: 0%;
            transition: width 0.3s ease;
        }
        .terminal {
            background-color: #0b0b10;
            border: 1px solid var(--border-dark);
            border-radius: 6px;
            padding: 12px;
            height: 360px;
            overflow-y: auto;
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.78rem;
            color: #a6adc8;
            white-space: pre-wrap;
            line-height: 1.5;
        }
        .history-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
        .history-table th, .history-table td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border-dark); }
        .history-table th { color: var(--text-secondary); font-weight: 600; text-transform: uppercase; font-size: 0.72rem; }
        .history-table tr:hover { background-color: rgba(255, 255, 255, 0.02); }

        @media(max-width: 900px) {
            aside.sidebar { position: fixed; transform: translateX(-100%); }
            aside.sidebar.open { transform: translateX(0); }
            .mobile-toggle { display: inline-flex; }
            .grid-2, .grid-3, .grid-4 { grid-template-columns: 1fr; }
            main.content-area { padding: 14px; }
        }
    </style>
</head>
<body>
    <!-- 1. LEFT SIDEBAR -->
    <aside id="appSidebar" class="sidebar">
        <div class="sidebar-brand">
            <div class="logo-box">LM</div>
            <div>
                <div style="font-weight:700; font-size:1.15rem; color:#fff;">LoRAMancer</div>
                <div style="font-size:0.7rem; color:var(--text-secondary);">Remote Studio Engine</div>
            </div>
        </div>

        <nav class="sidebar-nav">
            <div class="nav-section-title">Studio Pipeline</div>
            <button class="nav-item" onclick="switchStage('curate')">
                <span class="nav-icon">📁</span> 1. Curate &amp; Caption
            </button>
            <button class="nav-item active" onclick="switchStage('train')">
                <span class="nav-icon">⚡</span> 2. Train &amp; Forge
            </button>
            <button class="nav-item" onclick="switchStage('lab')">
                <span class="nav-icon">🔬</span> 3. Diagnostic Lab
            </button>
            <button class="nav-item" onclick="switchStage('comfy')">
                <span class="nav-icon">🎨</span> 4. ComfyUI Test
            </button>
            <button class="nav-item" onclick="switchStage('vault')">
                <span class="nav-icon">📚</span> 5. Library &amp; Vault
            </button>
            <button class="nav-item" onclick="switchStage('history')">
                <span class="nav-icon">📜</span> Training History
            </button>
            <button class="nav-item" onclick="switchStage('chop')">
                <span class="nav-icon">🛠️</span> LoRA Chop-Shop
            </button>

            <div class="sidebar-divider"></div>

            <div class="nav-section-title">Subsystems</div>
            <button class="nav-item" onclick="switchStage('env')">
                <span class="nav-icon">💻</span> Compute Environment
            </button>
            <button class="nav-item" onclick="switchStage('settings')">
                <span class="nav-icon">⚙️</span> Settings &amp; Admin
            </button>
        </nav>

        <div class="sidebar-footer">
            <div style="display:flex; justify-content:space-between; align-items:center;">
                <span style="font-weight:600; color:#fff;">Host Worker</span>
                <span id="sidebarStatusChip" class="chip chip-live" style="font-size:0.65rem;">Ready</span>
            </div>
            <div id="sidebarGpuLabel" style="color:var(--text-secondary); font-family:monospace; font-size:0.7rem;">Detecting GPU...</div>
        </div>
    </aside>

    <!-- 2. APP SHELL -->
    <div class="app-shell">
        <header class="top-header">
            <div class="top-left">
                <button class="mobile-toggle" onclick="toggleSidebar()">☰</button>
                <div class="chip chip-stage" style="background:transparent; border-color:transparent; font-weight:700; color:#fff;">
                    Concept: <span style="color:var(--accent-purple); margin-left:4px;">Remote Studio</span>
                </div>
                <div style="display:flex; gap:4px; flex-wrap:wrap;">
                    <span id="chip-curate" class="chip chip-stage" onclick="switchStage('curate')">1. Curate</span>
                    <span id="chip-train" class="chip chip-stage active" onclick="switchStage('train')">2. Train</span>
                    <span id="chip-lab" class="chip chip-stage" onclick="switchStage('lab')">3. Lab</span>
                    <span id="chip-comfy" class="chip chip-stage" onclick="switchStage('comfy')">4. Test</span>
                    <span id="chip-vault" class="chip chip-stage" onclick="switchStage('vault')">5. Vault</span>
                </div>
            </div>

            <div style="display:flex; align-items:center; gap:10px; flex-wrap:wrap;">
                <button id="installPwaBtn" class="btn btn-sm" style="display:none;" onclick="installPwa()">📲 Install App</button>
                <span id="publicUrlBadge" class="chip chip-stage" style="display:none; cursor:pointer;" onclick="copyPublicUrl()">🌐 Host Active</span>
                <span id="hostAcceleratorChip" class="chip chip-gpu">AMD ROCm Active</span>
                <span id="hostStatusPill" class="chip chip-live">Ready</span>
            </div>
        </header>

        <main class="content-area">
            <!-- VIEW: TRAIN & FORGE -->
            <div id="view-train" class="view-panel active">
                <div style="display:flex; align-items:center; justify-content:space-between; margin-bottom:16px; flex-wrap:wrap; gap:10px;">
                    <div>
                        <h2 style="font-size:1.3rem; font-weight:700; color:#fff;">Training Orchestrator &amp; Live Console</h2>
                        <p style="font-size:0.82rem; color:var(--text-secondary);">Execute AI-Toolkit runs on host GPU with live hardware acceleration telemetry and log streaming</p>
                    </div>
                    <div style="display:flex; gap:8px; flex-wrap:wrap;">
                        <button class="btn" onclick="startTraining()">🚀 Launch Training</button>
                        <button class="btn btn-outline" onclick="startTraining()">📥 Queue Run</button>
                        <button class="btn btn-danger" onclick="stopTraining()">⏹ Stop Current</button>
                        <button class="btn btn-outline" onclick="cancelAllJobs()">🗑️ Cancel All</button>
                        <button class="btn btn-secondary" onclick="clearConsoleLogs()">🧹 Clear Logs</button>
                    </div>
                </div>

                <!-- Active Queue Banner -->
                <div id="queueCard" class="card" style="padding:12px 18px; margin-bottom:16px;">
                    <div style="display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px;">
                        <div style="display:flex; align-items:center; gap:10px;">
                            <span style="font-size:1.1rem;">📥</span>
                            <span style="font-weight:700;">Studio Training Queue:</span>
                            <span id="queueBadge" class="chip chip-stage">0 Queued</span>
                            <span id="activeJobName" style="color:var(--accent-green); font-weight:600;">Idle</span>
                        </div>
                        <div style="font-size:0.78rem; color:var(--text-secondary);">Automatic single GPU sequential queue with VRAM cooldown</div>
                    </div>
                </div>

                <!-- 4 Metrics Cards -->
                <div class="grid-4" style="margin-bottom:16px;">
                    <div class="stat-box">
                        <div class="stat-label">Status</div>
                        <div id="trainStatusVal" class="stat-val" style="color:var(--accent-green);">Ready</div>
                    </div>
                    <div class="stat-box">
                        <div class="stat-label">Step Progress</div>
                        <div id="stepCounter" class="stat-val" style="color:var(--accent-blue);">0 / 0</div>
                    </div>
                    <div class="stat-box">
                        <div class="stat-label">Current Loss</div>
                        <div id="lossVal" class="stat-val" style="color:var(--accent-green);">-</div>
                    </div>
                    <div class="stat-box">
                        <div class="stat-label">Host GPU Engine</div>
                        <div id="engineStatusVal" class="stat-val" style="font-size:1rem; color:var(--accent-purple);">ROCm / CUDA</div>
                    </div>
                </div>

                <div class="progress-bar-container">
                    <div id="progressBar" class="progress-bar-fill"></div>
                </div>

                <div class="grid-2">
                    <!-- Training Parameters Form -->
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">⚙️ Training Job Configuration</div>
                            <span class="chip chip-stage" style="font-size:0.7rem;">AI-Toolkit Automated</span>
                        </div>

                        <div class="input-group">
                            <label>Run Identifier</label>
                            <input id="runName" type="text" value="flux_lora_run_1" placeholder="e.g. anime_portrait_v1" />
                        </div>

                        <div class="input-group">
                            <label>Dataset Archive (.zip file upload) or Host Directory</label>
                            <input id="datasetZip" type="file" accept=".zip" />
                            <input id="datasetDir" type="text" placeholder="Or enter existing folder path on host..." style="margin-top:4px;" />
                            <span id="uploadInfo" style="font-size:0.75rem; color:var(--text-secondary);">ZIP archives are auto-extracted on host PC</span>
                        </div>

                        <div class="grid-2">
                            <div class="input-group">
                                <label>Base Model Architecture</label>
                                <select id="architecture">
                                    <option value="flux1">FLUX.1 (Dev / Schnell)</option>
                                    <option value="sdxl">SDXL 1.0 / Pony / Illustrious</option>
                                    <option value="chroma">Chroma / Wan</option>
                                    <option value="sd15">Stable Diffusion 1.5</option>
                                </select>
                            </div>
                            <div class="input-group">
                                <label>Target Steps</label>
                                <input id="steps" type="number" value="1500" min="100" max="25000" />
                            </div>
                        </div>

                        <div class="input-group">
                            <label>Concept Trigger Word</label>
                            <input id="triggerWord" type="text" placeholder="e.g. ohwx style" />
                        </div>

                        <!-- Advanced Hyperparameters Accordion -->
                        <details style="margin-top:8px;">
                            <summary style="cursor:pointer; font-size:0.85rem; font-weight:700; color:var(--accent-purple); padding:6px 0;">
                                🔧 Advanced Hyperparameters (Rank, Optimizer, LR, Precision)
                            </summary>
                            <div style="background:var(--bg-overlay); border:1px solid var(--border-dark); border-radius:6px; padding:12px; margin-top:8px;">
                                <div class="grid-2">
                                    <div class="input-group">
                                        <label>Optimizer</label>
                                        <select id="optimizer">
                                            <option value="adamw8bit">AdamW 8-bit (Low VRAM)</option>
                                            <option value="prodigy">Prodigy (Adaptive LR)</option>
                                            <option value="lion8bit">Lion 8-bit</option>
                                            <option value="adamw">AdamW Full (32-bit)</option>
                                        </select>
                                    </div>
                                    <div class="input-group">
                                        <label>Learning Rate</label>
                                        <input id="lr" type="text" value="0.0001" />
                                    </div>
                                </div>
                                <div class="grid-3">
                                    <div class="input-group">
                                        <label>Network Dim (Rank)</label>
                                        <input id="networkDim" type="number" value="16" min="4" max="256" />
                                    </div>
                                    <div class="input-group">
                                        <label>Network Alpha</label>
                                        <input id="networkAlpha" type="number" value="16" min="4" max="256" />
                                    </div>
                                    <div class="input-group">
                                        <label>Precision (Dtype)</label>
                                        <select id="precision">
                                            <option value="bf16">bfloat16 (Recommended)</option>
                                            <option value="fp16">float16</option>
                                            <option value="fp8">fp8 (Ultra-Low VRAM)</option>
                                        </select>
                                    </div>
                                </div>
                                <div class="grid-2">
                                    <div class="input-group">
                                        <label>Batch Size</label>
                                        <input id="batchSize" type="number" value="1" min="1" max="16" />
                                    </div>
                                    <div class="input-group">
                                        <label>Resolution</label>
                                        <select id="resolution">
                                            <option value="1024">1024 x 1024</option>
                                            <option value="768">768 x 768</option>
                                            <option value="512">512 x 512</option>
                                        </select>
                                    </div>
                                </div>
                                <div class="input-group">
                                    <label>Validation Sample Prompt</label>
                                    <input id="samplePrompt" type="text" placeholder="e.g. photo of ohwx in cinematic lighting" />
                                </div>
                            </div>
                        </details>
                    </div>

                    <!-- Live Terminal Console -->
                    <div class="card" style="display:flex; flex-direction:column;">
                        <div class="card-header-bar">
                            <div class="card-title">🖥️ Live Execution Console</div>
                            <div style="display:flex; gap:6px;">
                                <button class="btn btn-secondary btn-sm" onclick="toggleAutoScroll()">Auto-Scroll: <span id="autoScrollStatus">ON</span></button>
                            </div>
                        </div>
                        <div id="terminal" class="terminal" style="flex:1;">Connecting to LoRAMancer live telemetry stream...</div>
                    </div>
                </div>
            </div>

            <!-- VIEW: CURATE & CAPTION -->
            <div id="view-curate" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">📁 Stage 1: Dataset Curator &amp; Caption Studio</div>
                        <button class="btn btn-secondary btn-sm" onclick="alert('Select images or a ZIP archive to upload to the host.')">Upload Images</button>
                    </div>
                    <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:12px;">
                        Manage training image pairs and text captions remotely. Upload images, clean tags, or auto-caption on the host workstation.
                    </p>
                    <div class="stat-box" style="padding:40px 20px; border:1px dashed var(--border-dark);">
                        <div style="font-size:2.5rem; margin-bottom:8px;">🖼️</div>
                        <h3 style="color:#fff; margin-bottom:6px;">Upload or Inspect Dataset Images</h3>
                        <p style="font-size:0.85rem; color:var(--text-secondary); max-width:480px; margin:0 auto 16px auto;">Upload raw photos and captions to build your training dataset on the host workstation.</p>
                        <input type="file" id="curateFileInput" multiple accept="image/*,.txt" style="display:none;" />
                        <button class="btn" onclick="document.getElementById('curateFileInput').click()">Browse Images...</button>
                    </div>
                </div>
            </div>

            <!-- VIEW: DIAGNOSTIC LAB -->
            <div id="view-lab" class="view-panel">
                <div class="grid-2">
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">🔬 Stage 3: LoRA Diagnostic Lab &amp; SVD Overbake Radar</div>
                        </div>
                        <div class="input-group">
                            <label>Target Safetensors Model on Host</label>
                            <input id="labModelPath" type="text" placeholder="Select from Vault or enter host path..." />
                        </div>
                        <button class="btn" onclick="runLabInspection()">🔬 Run Overbake &amp; Spectral Analysis</button>

                        <div id="labReportCard" style="display:none; background:var(--bg-overlay); border:1px solid var(--border-dark); border-radius:8px; padding:14px; margin-top:14px;">
                            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:8px;">
                                <span style="font-weight:700;" id="labReportName">-</span>
                                <span class="chip" id="labReportBadge">-</span>
                            </div>
                            <div class="grid-3" style="margin-bottom:10px;">
                                <div class="stat-box">
                                    <div class="stat-label">Overbake Score</div>
                                    <div id="labScoreVal" class="stat-val" style="color:var(--accent-purple);">0%</div>
                                </div>
                                <div class="stat-box">
                                    <div class="stat-label">Energy Norm</div>
                                    <div id="labNormVal" class="stat-val" style="color:var(--accent-green);">0.00</div>
                                </div>
                                <div class="stat-box">
                                    <div class="stat-label">Layers</div>
                                    <div id="labLayersVal" class="stat-val">0</div>
                                </div>
                            </div>
                            <div id="labVerdict" style="font-size:0.85rem; color:#bac2de; line-height:1.4;"></div>
                        </div>
                    </div>

                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">🛠️ Layer Surgery &amp; Rescaling</div>
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
                            <label>Output Rank</label>
                            <select id="labRank">
                                <option value="8">Rank 8 (~16MB)</option>
                                <option value="16" selected>Rank 16 (~32MB)</option>
                                <option value="32">Rank 32 (~64MB)</option>
                                <option value="64">Rank 64 (~128MB)</option>
                            </select>
                        </div>
                        <button class="btn" onclick="applyLabRescale()">🛠️ Apply Layer Rescale &amp; Save on Host</button>
                        <div id="labRescaleStatus" style="font-size:0.85rem; color:var(--accent-green); margin-top:8px; display:none;"></div>
                    </div>
                </div>
            </div>

            <!-- VIEW: COMFYUI TEST -->
            <div id="view-comfy" class="view-panel">
                <div class="grid-2">
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">🎨 Stage 4: ComfyUI Interactive Test Studio</div>
                            <span id="comfyStatusBadge" class="chip chip-stage">Checking...</span>
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
                            <label>LoRA Model</label>
                            <select id="comfyLoraSelect">
                                <option value="none">None (Base Checkpoint)</option>
                            </select>
                        </div>
                        <div class="grid-3">
                            <div class="input-group">
                                <label>Weight (<span id="comfyWeightVal">1.0</span>)</label>
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
                        <button class="btn" onclick="runComfyTest()">🎨 Generate Test Sample on Host GPU</button>
                        <div id="comfyGenStatus" style="font-size:0.85rem; color:var(--text-secondary); margin-top:8px; display:none;">Queued in ComfyUI...</div>
                    </div>

                    <div class="card" style="align-items:center; justify-content:center; min-height:420px; background:var(--bg-overlay);">
                        <div id="comfyPlaceholder" style="text-align:center; color:var(--text-secondary);">
                            <div style="font-size:3rem; margin-bottom:8px;">🖼️</div>
                            <div>Rendered ComfyUI sample will display here</div>
                        </div>
                        <img id="comfyPreview" style="display:none; max-width:100%; max-height:480px; border-radius:6px; border:1px solid var(--border-dark);" />
                        <a id="comfyDownloadBtn" class="btn btn-secondary btn-sm" style="display:none; margin-top:10px;" download="loramancer_sample.png">💾 Download Full Image</a>
                    </div>
                </div>
            </div>

            <!-- VIEW: LIBRARY & VAULT -->
            <div id="view-vault" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">📚 Stage 5: LoRA Library &amp; Vault</div>
                        <button class="btn btn-secondary btn-sm" onclick="loadVaultLoras()">🔄 Refresh Vault</button>
                    </div>
                    <div class="input-group">
                        <input id="vaultSearch" type="text" placeholder="Search models by filename..." oninput="filterVault()" />
                    </div>
                    <div id="vaultList" class="grid-3" style="max-height:650px; overflow-y:auto; padding-top:6px;">
                        <div style="color:var(--text-secondary); font-size:0.88rem;">Scanning host LoRA directories...</div>
                    </div>
                </div>
            </div>

            <!-- VIEW: TRAINING HISTORY -->
            <div id="view-history" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">📜 LoRA Training History</div>
                        <button class="btn btn-secondary btn-sm" onclick="loadHistory()">🔄 Refresh History</button>
                    </div>
                    <div id="historyTableContainer" style="overflow-x:auto;">
                        <div style="color:var(--text-secondary); font-size:0.88rem; padding:12px 0;">Loading training records from host...</div>
                    </div>
                </div>
            </div>

            <!-- VIEW: LORA CHOP-SHOP -->
            <div id="view-chop" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">🛠️ LoRA Vehicle Chop-Shop: Multi-Model Anatomical Grafting</div>
                        <button class="btn btn-secondary btn-sm" onclick="clearChopGarage()">🧹 Reset Garage</button>
                    </div>
                    <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:12px;">
                        Selectively harvest visual components across donor models (face, eyes, hair, clothing, lighting, textures) and forge a unified Franken-LoRA using SVD matrix compression without retraining.
                    </p>
                    <div class="input-group">
                        <label>Select Server LoRA to Add to Garage Shelf</label>
                        <div style="display:flex; gap:10px;">
                            <select id="chopAddSelect" style="flex:1;"><option value="">Choose a model from host vault...</option></select>
                            <button class="btn btn-secondary" onclick="addSelectedDonorToGarage()">➕ Add Donor</button>
                        </div>
                    </div>
                    <div id="chopDonorsShelf" class="grid-3" style="margin-top:6px;">
                        <div style="color:var(--text-secondary); font-size:0.85rem; font-style:italic;">No donors added yet. Select a model above to begin.</div>
                    </div>
                </div>

                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">🧬 Anatomical &amp; Style Component Grafting</div>
                        <button class="btn btn-sm btn-secondary" onclick="autoAssignChop()">✨ Auto-Craft Recipe</button>
                    </div>
                    <div class="grid-2">
                        <div class="input-group">
                            <label>👤 Chassis &amp; Face (Head Shape &amp; Jawline)</label>
                            <select id="part-face" class="chop-part-select"><option value="">Base / None</option></select>
                        </div>
                        <div class="input-group">
                            <label>👁️ Headlights &amp; Eyes (Iris &amp; Catchlights)</label>
                            <select id="part-eyes" class="chop-part-select"><option value="">Base / None</option></select>
                        </div>
                        <div class="input-group">
                            <label>💇 Custom Paint &amp; Hair (Hairstyle &amp; Flow)</label>
                            <select id="part-hair" class="chop-part-select"><option value="">Base / None</option></select>
                        </div>
                        <div class="input-group">
                            <label>👗 Upholstery &amp; Armor (Garments &amp; Uniforms)</label>
                            <select id="part-armor" class="chop-part-select"><option value="">Base / None</option></select>
                        </div>
                        <div class="input-group">
                            <label>💡 Engine &amp; Glow (Volumetric Lighting &amp; Tone)</label>
                            <select id="part-glow" class="chop-part-select"><option value="">Base / None</option></select>
                        </div>
                        <div class="input-group">
                            <label>⚡ Detail Polish (Skin Pores &amp; Micro-Clarity)</label>
                            <select id="part-detail" class="chop-part-select"><option value="">Base / None</option></select>
                        </div>
                    </div>

                    <div class="grid-2" style="margin-top:10px;">
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
                        <div id="chopStatus" style="margin-top:8px; font-size:0.85rem; color:var(--accent-purple); display:none;"></div>
                    </div>
                </div>
            </div>

            <!-- VIEW: COMPUTE ENVIRONMENT -->
            <div id="view-env" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">💻 Compute Environment &amp; Hardware Acceleration</div>
                        <button class="btn btn-secondary btn-sm" onclick="loadEnvironment()">🔄 Refresh Specs</button>
                    </div>
                    <div id="envDetails" class="grid-2" style="margin-top:8px;">
                        <div class="stat-box"><div class="stat-label">GPU Model</div><div id="envGpuName" class="stat-val" style="font-size:1rem;">-</div></div>
                        <div class="stat-box"><div class="stat-label">Driver / Platform</div><div id="envDriver" class="stat-val" style="font-size:1rem;">-</div></div>
                        <div class="stat-box"><div class="stat-label">PyTorch Wheels</div><div id="envTorch" class="stat-val" style="font-size:1rem;">-</div></div>
                        <div class="stat-box"><div class="stat-label">Python Runtime</div><div id="envPython" class="stat-val" style="font-size:1rem;">-</div></div>
                    </div>
                </div>
            </div>

            <!-- VIEW: SETTINGS & ADMIN -->
            <div id="view-settings" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">⚙️ Remote Studio Settings &amp; Host Configuration</div>
                    </div>
                    <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:14px;">
                        Configure network connection parameters and manage authorization tokens for remote access.
                    </p>
                    <div class="input-group">
                        <label>Session Security Token</label>
                        <input id="clientTokenDisplay" type="text" readonly style="font-family:monospace;" />
                    </div>
                    <button class="btn btn-secondary" onclick="localStorage.removeItem('loramancer_auth_token'); window.location.reload();">🔒 Log Out / Clear Session</button>
                </div>
            </div>
        </main>
    </div>

    <script>
        const terminal = document.getElementById('terminal');
        const progressBar = document.getElementById('progressBar');
        const stepCounter = document.getElementById('stepCounter');
        const lossVal = document.getElementById('lossVal');
        const hostStatusPill = document.getElementById('hostStatusPill');
        const sidebarStatusChip = document.getElementById('sidebarStatusChip');
        const publicBadge = document.getElementById('publicUrlBadge');
        let autoScroll = true;

        const activePublicUrl = '{{publicUrl}}';
        if (activePublicUrl) {
            publicBadge.style.display = 'inline-flex';
            publicBadge.textContent = '🌐 ' + activePublicUrl.replace(/^https?:\/\//, '');
        }

        function copyPublicUrl() {
            if (activePublicUrl) {
                navigator.clipboard.writeText(activePublicUrl).then(() => alert('Copied URL:\n' + activePublicUrl));
            }
        }

        function toggleSidebar() {
            document.getElementById('appSidebar').classList.toggle('open');
        }

        function toggleAutoScroll() {
            autoScroll = !autoScroll;
            document.getElementById('autoScrollStatus').textContent = autoScroll ? 'ON' : 'OFF';
        }

        function appendLog(msg) {
            terminal.textContent += msg + '\n';
            if (autoScroll) terminal.scrollTop = terminal.scrollHeight;
        }

        function switchStage(stageId) {
            document.querySelectorAll('.view-panel').forEach(p => p.classList.remove('active'));
            document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
            document.querySelectorAll('.chip-stage').forEach(c => c.classList.remove('active'));

            const view = document.getElementById('view-' + stageId);
            if (view) view.classList.add('active');

            // Find matching sidebar nav item
            const navBtn = Array.from(document.querySelectorAll('.nav-item')).find(b => b.getAttribute('onclick')?.includes("'" + stageId + "'"));
            if (navBtn) navBtn.classList.add('active');

            // Find matching top chip
            const chip = document.getElementById('chip-' + stageId);
            if (chip) chip.classList.add('active');

            // Mobile sidebar auto-close
            document.getElementById('appSidebar').classList.remove('open');

            if (stageId === 'vault') loadVaultLoras();
            if (stageId === 'comfy') checkComfyStatus();
            if (stageId === 'history') loadHistory();
            if (stageId === 'env') loadEnvironment();
            if (stageId === 'train') refreshQueue();
        }

        // PWA Installation
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
            if (token) headers['Authorization'] = 'Bearer ' + token;
            return headers;
        }

        async function checkHealth() {
            try {
                const res = await fetch('/api/v1/health', { headers: authHeaders() });
                if (res.status === 401) return window.location.reload();
                if (res.ok) {
                    const h = await res.json();
                    hostStatusPill.textContent = h.Status;
                    hostStatusPill.className = 'chip ' + (h.IsTraining ? 'chip-busy' : 'chip-live');
                    sidebarStatusChip.textContent = h.Status;
                    sidebarStatusChip.className = 'chip ' + (h.IsTraining ? 'chip-busy' : 'chip-live');
                    document.getElementById('trainStatusVal').textContent = h.Status;
                    document.getElementById('trainStatusVal').style.color = h.IsTraining ? 'var(--accent-amber)' : 'var(--accent-green)';
                }
            } catch(e) { }
        }

        async function loadEnvironment() {
            try {
                const res = await fetch('/api/v1/environment', { headers: authHeaders() });
                if (res.ok) {
                    const env = await res.json();
                    document.getElementById('sidebarGpuLabel').textContent = env.gpuName || 'GPU Hardware';
                    document.getElementById('hostAcceleratorChip').textContent = (env.detectedVendor === 'Amd' ? 'AMD ROCm ' + env.rocmVersion : 'NVIDIA CUDA') + ' Active';
                    document.getElementById('envGpuName').textContent = env.gpuName || 'Discrete Accelerator';
                    document.getElementById('envDriver').textContent = (env.detectedVendor === 'Amd' ? 'ROCm ' + env.rocmVersion : 'CUDA') + ' on ' + env.os;
                    document.getElementById('envTorch').textContent = env.torchVersion || 'PyTorch';
                    document.getElementById('envPython').textContent = env.pythonVersion || 'Python 3.12';
                }
            } catch(e) { }
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
                        stepCounter.textContent = data.Step + ' / ' + data.TotalSteps;
                        lossVal.textContent = data.Loss.toFixed(4);
                        const pct = data.TotalSteps > 0 ? Math.round((data.Step / data.TotalSteps) * 100) : 0;
                        progressBar.style.width = pct + '%';
                        hostStatusPill.textContent = 'Training';
                        hostStatusPill.className = 'chip chip-busy';
                    }
                    if (data.Message) appendLog(data.Message);
                } catch(err) {
                    appendLog(e.data);
                }
            };
        }

        checkHealth();
        loadEnvironment();
        initTelemetryStream();
        loadVaultLoras();
        refreshQueue();

        const tokenDisplay = document.getElementById('clientTokenDisplay');
        if (tokenDisplay) tokenDisplay.value = getAuthToken() || '(None / Open LAN)';

        // --- QUEUE MANAGEMENT ---
        async function refreshQueue() {
            try {
                const res = await fetch('/api/v1/training/queue', { headers: authHeaders() });
                if (res.ok) {
                    const q = await res.json();
                    document.getElementById('queueBadge').textContent = q.queueCount + ' Queued';
                    document.getElementById('activeJobName').textContent = q.currentJob ? ('Running: ' + q.currentJob.Name) : (q.isRunning ? 'Active Run' : 'Idle');
                }
            } catch(e) { }
        }

        async function cancelAllJobs() {
            if (!confirm('Cancel active training and clear all queued runs on host?')) return;
            try {
                await fetch('/api/v1/training/cancel-all', { method: 'POST', headers: authHeaders() });
                refreshQueue();
                checkHealth();
            } catch(e) { }
        }

        function clearConsoleLogs() {
            terminal.textContent = '';
            fetch('/api/v1/training/clear-logs', { method: 'POST', headers: authHeaders() }).catch(() => {});
        }

        // --- LAUNCH TRAINING ---
        async function startTraining() {
            const runName = document.getElementById('runName').value.trim() || 'lora_run';
            const triggerWord = document.getElementById('triggerWord').value.trim();
            const steps = parseInt(document.getElementById('steps').value) || 1500;
            const arch = document.getElementById('architecture').value;
            const dirInput = document.getElementById('datasetDir').value.trim();
            const fileInput = document.getElementById('datasetZip');

            let datasetPath = dirInput;
            if (fileInput.files.length > 0) {
                appendLog('[HOST] Uploading dataset ZIP archive to host workstation...');
                const formData = new FormData();
                formData.append('file', fileInput.files[0]);
                const uploadRes = await fetch('/api/v1/datasets/upload', {
                    method: 'POST',
                    headers: authHeaders(),
                    body: formData
                });
                if (uploadRes.status === 401) return window.location.reload();
                const uploadJson = await uploadRes.json();
                datasetPath = uploadJson.ExtractedPath;
                appendLog('[HOST] Dataset extracted successfully: ' + datasetPath);
            }

            appendLog('[HOST] Submitting training job to queue...');
            const headers = authHeaders();
            headers['Content-Type'] = 'application/json';

            const payload = {
                RunName: runName,
                BaseArchitecture: arch,
                DatasetPath: datasetPath,
                TriggerWord: triggerWord,
                Steps: steps,
                BatchSize: parseInt(document.getElementById('batchSize').value) || 1,
                LearningRate: parseFloat(document.getElementById('lr').value) || 0.0001,
                Optimizer: document.getElementById('optimizer').value,
                NetworkDim: parseInt(document.getElementById('networkDim').value) || 16,
                NetworkAlpha: parseInt(document.getElementById('networkAlpha').value) || 16,
                Precision: document.getElementById('precision').value,
                Resolution: parseInt(document.getElementById('resolution').value) || 1024,
                SamplePrompt: document.getElementById('samplePrompt').value.trim()
            };

            const res = await fetch('/api/v1/training/start', {
                method: 'POST',
                headers,
                body: JSON.stringify(payload)
            });

            if (res.ok) {
                appendLog('[HOST] Training run queued and scheduled successfully!');
                refreshQueue();
                checkHealth();
            } else {
                appendLog('[HOST ERROR] Failed to start run: ' + res.statusText);
            }
        }

        async function stopTraining() {
            appendLog('[HOST] Stopping active run...');
            await fetch('/api/v1/training/stop', { method: 'POST', headers: authHeaders() });
            checkHealth();
            refreshQueue();
        }

        // --- VAULT LOGIC ---
        let allVaultLoras = [];
        async function loadVaultLoras() {
            const listEl = document.getElementById('vaultList');
            const chopSelect = document.getElementById('chopAddSelect');
            const comfySelect = document.getElementById('comfyLoraSelect');
            try {
                const res = await fetch('/api/v1/vault/loras', { headers: authHeaders() });
                if (res.status === 401) return window.location.reload();
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
                if (listEl) listEl.innerHTML = `<div style="color:var(--accent-red);">Error loading LoRAs: ${e.message}</div>`;
            }
        }

        function renderVaultList(items) {
            const listEl = document.getElementById('vaultList');
            if (!listEl) return;
            if (items.length === 0) {
                listEl.innerHTML = '<div style="color:var(--text-secondary); font-style:italic;">No LoRA models found in host outputs.</div>';
                return;
            }
            listEl.innerHTML = items.map(item => `
                <div style="background:var(--bg-overlay); border:1px solid var(--border-dark); border-radius:6px; padding:12px; display:flex; flex-direction:column; gap:8px;">
                    <div style="font-weight:700; color:#89b4fa; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" title="${item.fileName}">
                        ${item.fileName}
                    </div>
                    <div style="display:flex; justify-content:space-between; font-size:0.75rem; color:var(--text-secondary);">
                        <span>${item.formattedSize}</span>
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
            renderVaultList(allVaultLoras.filter(x => x.fileName.toLowerCase().includes(q)));
        }

        function sendToLab(path) {
            document.getElementById('labModelPath').value = path;
            switchStage('lab');
            runLabInspection();
        }

        function sendToChop(path) {
            switchStage('chop');
            addDonorByPath(path);
        }

        function sendToComfy(name) {
            document.getElementById('comfyLoraSelect').value = name;
            switchStage('comfy');
        }

        // --- DIAGNOSTIC LAB ---
        async function runLabInspection() {
            const path = document.getElementById('labModelPath').value.trim();
            if (!path) return alert('Enter target LoRA path');
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
                    badge.className = 'chip ' + (data.status.includes('Optimal') ? 'chip-live' : 'chip-busy');
                    document.getElementById('labScoreVal').textContent = data.score + '%';
                    document.getElementById('labNormVal').textContent = data.averageFrobeniusNorm.toFixed(3);
                    document.getElementById('labLayersVal').textContent = data.layerCount;
                    document.getElementById('labVerdict').textContent = data.verdict + ' Recommendation: ' + data.recommendation;
                } else {
                    document.getElementById('labVerdict').textContent = 'Inspection failed: ' + (data.error || 'Error');
                }
            } catch(e) {
                document.getElementById('labVerdict').textContent = 'Error: ' + e.message;
            }
        }

        async function applyLabRescale() {
            const path = document.getElementById('labModelPath').value.trim();
            if (!path) return alert('Specify target LoRA path');
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

        // --- LORA CHOP-SHOP ---
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
                }
            } catch(e) { }
        }

        function clearChopGarage() {
            chopDonors.length = 0;
            renderChopGarage();
            updateChopPartSelects();
        }

        function renderChopGarage() {
            const shelf = document.getElementById('chopDonorsShelf');
            if (chopDonors.length === 0) {
                shelf.innerHTML = '<div style="color:var(--text-secondary); font-size:0.85rem; font-style:italic;">No donors added yet. Select a model above to begin.</div>';
                return;
            }
            shelf.innerHTML = chopDonors.map((d, i) => `
                <div style="background:var(--bg-overlay); border:1px solid var(--border-dark); border-radius:6px; padding:12px;">
                    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                        <span class="chip chip-stage" style="font-size:0.65rem;">DONOR #${i + 1}</span>
                        <span style="font-size:0.75rem; color:var(--accent-green); font-weight:700;">Rank ${d.rank}</span>
                    </div>
                    <div style="font-weight:700; color:#fff; font-size:0.85rem; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${d.fileName}</div>
                    <div class="chip chip-stage" style="margin-top:6px; font-size:0.7rem;">${d.dominantFeature}</div>
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
            if (chopDonors.length === 0) return alert('Add at least 1 donor model to garage.');
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

        // --- COMFYUI STUDIO ---
        async function checkComfyStatus() {
            const badge = document.getElementById('comfyStatusBadge');
            try {
                const res = await fetch('/api/v1/comfyui/status', { headers: authHeaders() });
                const data = await res.json();
                if (data.isOnline) {
                    badge.className = 'chip chip-live';
                    badge.textContent = 'Online (' + (data.device || 'GPU') + ')';
                } else {
                    badge.className = 'chip chip-busy';
                    badge.textContent = 'Offline';
                }
            } catch(e) {
                badge.className = 'chip chip-busy';
                badge.textContent = 'Unavailable';
            }
        }

        async function runComfyTest() {
            const prompt = document.getElementById('comfyPrompt').value.trim();
            if (!prompt) return alert('Enter prompt');
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

        // --- HISTORY ---
        async function loadHistory() {
            const container = document.getElementById('historyTableContainer');
            try {
                const res = await fetch('/api/v1/history', { headers: authHeaders() });
                if (res.status === 401) return window.location.reload();
                const list = await res.json();
                if (!list || list.length === 0) {
                    container.innerHTML = '<div style="color:var(--text-secondary); font-style:italic; padding:12px 0;">No training records found in vault.</div>';
                    return;
                }
                let html = '<table class="history-table"><thead><tr>' +
                    '<th>Name</th><th>Architecture</th><th>Status</th><th>Steps</th><th>Loss</th><th>Completed</th><th>Action</th>' +
                    '</tr></thead><tbody>';
                for (const item of list) {
                    const status = item.Status || 'Unknown';
                    const isSuccess = status === 'Completed';
                    const isFailed = status === 'Failed' || status === 'Cancelled';
                    const badgeClass = isSuccess ? 'chip-live' : (isFailed ? 'chip-busy' : '');
                    const btnLabel = isFailed ? '🔁 Retry' : '▶️ Retrain';
                    const dateStr = item.CompletedAt ? new Date(item.CompletedAt).toLocaleDateString() : '-';
                    const arch = (item.BaseArchitecture || 'FLUX').toUpperCase();
                    const loss = item.FinalLoss ? item.FinalLoss.toFixed(4) : '-';
                    html += '<tr>' +
                        '<td><b>' + (item.Name || 'Unnamed') + '</b><div style="font-size:0.75rem; color:var(--text-secondary); font-family:monospace;">' + (item.TriggerWord || '') + '</div></td>' +
                        '<td><span class="chip chip-stage">' + arch + '</span></td>' +
                        '<td><span class="chip ' + badgeClass + '">' + status + '</span></td>' +
                        '<td>' + (item.Steps || 0) + '</td>' +
                        '<td style="color:var(--accent-green); font-weight:600;">' + loss + '</td>' +
                        '<td>' + dateStr + '</td>' +
                        '<td><button class="btn btn-secondary btn-sm" onclick="retryTraining(\'' + item.Id + '\', \'' + (item.Name || 'Job').replace(/'/g, "\\'") + '\')">' + btnLabel + '</button></td>' +
                    '</tr>';
                }
                html += '</tbody></table>';
                container.innerHTML = html;
            } catch(e) {
                container.innerHTML = '<div style="color:var(--accent-red); padding:10px 0;">Error loading history: ' + e.message + '</div>';
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
                    appendLog('[HOST] Training successfully resubmitted to queue: ' + name);
                    switchStage('train');
                    checkHealth();
                }
            } catch(e) { }
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
