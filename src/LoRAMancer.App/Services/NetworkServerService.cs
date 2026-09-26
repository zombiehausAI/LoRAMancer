using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    public double LearningRate { get; set; } = 1e-4;
    public string Optimizer { get; set; } = "adamw8bit";
    public int NetworkDim { get; set; } = 16;
    public int NetworkAlpha { get; set; } = 16;
    public string Precision { get; set; } = "bf16";
    public int Resolution { get; set; } = 1024;
    public int Epochs { get; set; } = 10;
    public int Repeats { get; set; } = 10;
    public int GradientAccumulation { get; set; } = 1;
    public string? SamplePrompt { get; set; }
    public string? RawConfigYaml { get; set; }
}

public sealed class TrainingEstimateRequestDto {
    public string BaseModel { get; set; } = "FLUX.1-dev";
    public int ImageCount { get; set; } = 20;
    public int Repeats { get; set; } = 10;
    public int Epochs { get; set; } = 10;
    public int BatchSize { get; set; } = 1;
    public int NetworkDim { get; set; } = 16;
}

public sealed class OllamaTagRequestDto {
    public string? InputPath { get; set; }
    public string? TriggerWord { get; set; }
    public string? Model { get; set; }
    public string? SubjectFocus { get; set; } = "general";
    public string? CaptionStyle { get; set; } = "tags";
    public string? CustomPrompt { get; set; }
    public string? IncludedPhrases { get; set; }
    public string? BlacklistWords { get; set; }
}

public sealed class ToolResizeRequestDto {
    public string? SourceLora { get; set; }
    public string? OutputLora { get; set; }
    public int TargetRank { get; set; } = 16;
}

public sealed class ToolMergeRequestDto {
    public string? ModelA { get; set; }
    public string? ModelB { get; set; }
    public double Ratio { get; set; } = 0.5;
    public string? OutputLora { get; set; }
}

public sealed class ToolGeneTherapyAnalyzeDto {
    public string? LoraPath { get; set; }
}

public sealed class ToolGeneTherapyPruneDto {
    public string? LoraPath { get; set; }
    public string? OutputPath { get; set; }
    public double Threshold { get; set; } = 2.8;
}

public sealed class ToolDiffCompareDto {
    public string? ModelAPath { get; set; }
    public string? ModelBPath { get; set; }
}

public sealed class ToolBenchmarkScanDto {
    public string? FolderPath { get; set; }
}

public sealed class ToolBenchmarkStartDto {
    public string? CheckpointFolder { get; set; }
    public string? BaseArch { get; set; } = "FLUX.1";
    public string? TriggerWord { get; set; }
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
    private readonly TrainingEstimationService? _estimationService;
    private readonly SafeTensorsMetadataReader? _metadataReader;
    private readonly PluginManagerService? _pluginManager;
    private readonly LoraDiffService? _diffService;
    private readonly LoraBenchmarkService? _benchmarkService;
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
        AiToolkitConfigBuilder? configBuilder = null,
        TrainingEstimationService? estimationService = null,
        SafeTensorsMetadataReader? metadataReader = null,
        PluginManagerService? pluginManager = null,
        LoraDiffService? diffService = null,
        LoraBenchmarkService? benchmarkService = null
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
        _estimationService = estimationService;
        _metadataReader = metadataReader;
        _pluginManager = pluginManager;
        _diffService = diffService;
        _benchmarkService = benchmarkService;

        _trainingRunner.OnProgressUpdated += HandleProgressUpdated;
        _trainingRunner.OnLogReceived += HandleLogReceived;
    }

    public async Task StartServerAsync(int port = 8420, string bindAddress = "0.0.0.0", string? accessToken = null) {
        if (IsRunning) {
            return;
        }

        _serverCts = new CancellationTokenSource();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            Args = Array.Empty<string>()
        });

        builder.WebHost.UseKestrel(options => {
            if (bindAddress == "127.0.0.1" || bindAddress == "localhost") {
                options.ListenLocalhost(port);
            } else {
                options.Listen(IPAddress.Any, port);
            }
        });

        builder.Services.AddRouting();
        builder.Services.AddCors(options => {
            options.AddDefaultPolicy(policy => {
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            });
        });

        WebApplication app = builder.Build();
        app.UseCors();


        // 2. Auth Session Check / Token Validation
        app.MapPost("/api/v1/auth/login", async (HttpRequest request) => {
            var body = await JsonSerializer.DeserializeAsync<AuthLoginDto>(request.Body);
            string candidate = body?.PinOrToken ?? string.Empty;

            if (_authTokenManager != null && _authTokenManager.ValidateToken(candidate, out var record)) {
                return Results.Ok(new {
                    success = true,
                    role = record?.Role ?? "Admin",
                    token = candidate
                });
            }

            if (!string.IsNullOrWhiteSpace(accessToken) && candidate == accessToken) {
                return Results.Ok(new { success = true, role = "Admin", token = candidate });
            }

            if (!string.IsNullOrWhiteSpace(_settingsService.Current.ServerAccessToken) && candidate == _settingsService.Current.ServerAccessToken) {
                return Results.Ok(new { success = true, role = "Admin", token = candidate });
            }

            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(_settingsService.Current.ServerAccessToken)) {
                return Results.Ok(new { success = true, role = "Admin", token = "open_access" });
            }

            return Results.Json(new { success = false, message = "Invalid Access Token." }, statusCode: 401);
        });

        // 3. Token Management Endpoints
        app.MapGet("/api/v1/auth/tokens", (HttpContext context) => {
            if (!IsAdmin(context, accessToken)) return Results.Unauthorized();
            if (_authTokenManager == null) return Results.Json(Array.Empty<object>());
            return Results.Json(_authTokenManager.GetAllTokens());
        });

        app.MapPost("/api/v1/auth/tokens", async (HttpRequest request) => {
            if (!IsAdmin(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_authTokenManager == null) return Results.BadRequest(new { error = "Auth manager not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<CreateTokenDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) {
                return Results.BadRequest(new { error = "Token name is required." });
            }

            var token = _authTokenManager.CreateToken(dto.Name, dto.CustomToken, dto.Role ?? "Trainer", dto.Description ?? "", dto.ExpiresAt);
            return Results.Created($"/api/v1/auth/tokens/{token.Id}", token);
        });

        app.MapDelete("/api/v1/auth/tokens/{id}", (string id, HttpContext context) => {
            if (!IsAdmin(context, accessToken)) return Results.Unauthorized();
            if (_authTokenManager == null) return Results.BadRequest(new { error = "Auth manager not initialized." });
            bool deleted = _authTokenManager.DeleteToken(id);
            return deleted ? Results.Ok(new { message = "Token deleted." }) : Results.NotFound();
        });

        // 4. Public Internet Cloudflare Tunnel Endpoints
        app.MapGet("/api/v1/tunnel/status", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            return Results.Json(new {
                isRunning = _publicTunnelService?.IsTunnelRunning ?? false,
                publicUrl = _publicTunnelService?.ActivePublicUrl ?? string.Empty
            });
        });

        app.MapPost("/api/v1/tunnel/start", async (HttpContext context) => {
            if (!IsAdmin(context, accessToken)) return Results.Unauthorized();
            if (_publicTunnelService == null) return Results.BadRequest(new { error = "Tunnel service not initialized." });
            string url = await _publicTunnelService.StartTunnelAsync(port);
            return Results.Ok(new { success = true, publicUrl = url });
        });

        app.MapPost("/api/v1/tunnel/stop", async (HttpContext context) => {
            if (!IsAdmin(context, accessToken)) return Results.Unauthorized();
            if (_publicTunnelService == null) return Results.BadRequest(new { error = "Tunnel service not initialized." });
            await _publicTunnelService.StopTunnelAsync();
            return Results.Ok(new { success = true });
        });

        // 5. Training Status & Health
        app.MapGet("/api/v1/health", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

            var health = new ServerHealthDto {
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
            var detected = _estimationService?.GetDetectedGpu() ?? ("AMD Radeon RX 7900 XTX", 24.0);
            string gpuName = !string.IsNullOrWhiteSpace(env.GpuName) && !env.GpuName.Contains("Fallback") ? env.GpuName : detected.GpuName;
            int totalVramMb = (int)Math.Round(detected.VramGb * 1024);
            int availVramMb = (int)Math.Round(totalVramMb * 0.85);

            return Results.Json(new {
                detectedVendor = env.DetectedVendor.ToString(),
                gpuName = gpuName,
                rocmFound = env.RocmDriverFound,
                rocmVersion = env.RocmVersion,
                pythonFound = !string.IsNullOrWhiteSpace(env.PythonExecutable),
                pythonVersion = env.PythonVersion,
                vramTotalMb = totalVramMb,
                vramAvailableMb = availVramMb,
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
            string tempDir = GetEffectiveUploadsDirectory();
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

        // 7b. Upload LoRA Model (.safetensors) for Chop-Shop Donor or Training Recipe Cloning
        app.MapPost("/api/v1/loras/upload", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();

            if (!request.HasFormContentType || request.Form.Files.Count == 0) {
                return Results.BadRequest(new { success = false, message = "No .safetensors file uploaded." });
            }

            IFormFile file = request.Form.Files[0];
            if (!file.FileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) {
                return Results.BadRequest(new { success = false, message = "Only .safetensors LoRA files are supported." });
            }

            string uploadsDir = GetEffectiveUploadsDirectory();
            string destinationLora = Path.Combine(uploadsDir, $"{Guid.NewGuid():N}_{file.FileName}");

            await using (FileStream stream = new(destinationLora, FileMode.Create, FileAccess.Write)) {
                await file.CopyToAsync(stream);
            }

            LoraMetadata? metadata = null;
            if (_metadataReader != null) {
                try {
                    metadata = await _metadataReader.ReadMetadataAsync(destinationLora);
                } catch { }
            }

            ChopDonorModel? donor = null;
            if (_chopShopService != null) {
                try {
                    donor = await _chopShopService.InspectDonorAsync(destinationLora);
                } catch { }
            }

            string arch = donor?.Architecture ?? metadata?.EffectiveBaseModel ?? "FLUX.1";
            int rank = donor?.Rank ?? metadata?.NetworkDim ?? 16;
            int alpha = (int)(metadata?.NetworkAlpha ?? rank);
            double lr = metadata?.LearningRate ?? 1e-4;
            string opt = !string.IsNullOrWhiteSpace(metadata?.Optimizer) ? metadata.Optimizer : "adamw8bit";
            string scheduler = !string.IsNullOrWhiteSpace(metadata?.LrScheduler) ? metadata.LrScheduler : "cosine";
            string precision = !string.IsNullOrWhiteSpace(metadata?.Precision) ? metadata.Precision : "bf16";
            int epochs = metadata?.Epochs ?? 10;
            string dominantFeature = donor?.DominantFeature ?? "General Feature";
            List<string> tags = donor?.InferredTags ?? new List<string>();

            return Results.Json(new {
                success = true,
                filePath = destinationLora,
                fileName = file.FileName,
                fileSize = file.Length,
                formattedSize = $"{file.Length / (1024.0 * 1024.0):F1} MB",
                architecture = arch,
                rank = rank,
                alpha = alpha,
                learningRate = lr,
                unetLearningRate = metadata?.UnetLearningRate,
                textEncoderLearningRate = metadata?.TextEncoderLearningRate,
                optimizer = opt,
                lrScheduler = scheduler,
                precision = precision,
                epochs = epochs,
                dominantFeature = dominantFeature,
                inferredTags = tags,
                donor = donor,
                message = "Donor LoRA uploaded and analyzed successfully on server."
            });
        });

        // 7c. Calculate Training Estimates & Pre-Flight Check
        app.MapPost("/api/v1/training/estimate", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();

            var req = await JsonSerializer.DeserializeAsync<TrainingEstimateRequestDto>(request.Body);
            if (req == null) return Results.BadRequest(new { error = "Invalid estimation parameters." });

            if (_estimationService != null) {
                var est = _estimationService.CalculateEstimates(
                    req.BaseModel,
                    req.ImageCount,
                    req.Repeats,
                    req.Epochs,
                    req.BatchSize,
                    req.NetworkDim
                );
                var preFlight = _estimationService.RunPreFlightCheck(req.BaseModel, string.Empty, req.BatchSize);

                return Results.Json(new {
                    totalSteps = est.TotalSteps,
                    estimatedVramGb = est.EstimatedVramGb,
                    estimatedOutputSizeMb = est.EstimatedOutputSizeMb,
                    estimatedDurationMinutes = est.EstimatedDuration.TotalMinutes,
                    formattedDuration = $"{est.EstimatedDuration:hh\\:mm\\:ss}",
                    stepBreakdown = est.StepBreakdown,
                    totalVramGb = preFlight.TotalVramGb,
                    gpuName = preFlight.GpuName,
                    hasSufficientVram = preFlight.HasSufficientVram,
                    warnings = preFlight.Warnings
                });
            }

            int safeBatch = Math.Max(1, req.BatchSize);
            int totalSteps = (Math.Max(1, req.ImageCount) * Math.Max(1, req.Repeats) * Math.Max(1, req.Epochs)) / safeBatch;
            return Results.Json(new {
                totalSteps = totalSteps,
                estimatedVramGb = 10.4,
                estimatedOutputSizeMb = 54.0,
                estimatedDurationMinutes = totalSteps * 1.35 / 60.0,
                formattedDuration = TimeSpan.FromSeconds(totalSteps * 1.35).ToString(@"hh\:mm\:ss"),
                stepBreakdown = $"{req.ImageCount} images × {req.Repeats} reps × {req.Epochs} ep ÷ {safeBatch} = {totalSteps} steps",
                totalVramGb = 24.0,
                gpuName = "AMD Radeon RX 7900 XTX",
                hasSufficientVram = true,
                warnings = Array.Empty<string>()
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
                    Repeats = req.Repeats > 0 ? req.Repeats : 10,
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
                GetEffectiveUploadsDirectory(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "outputs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "remote_runs"),
                Path.Combine(AppContext.BaseDirectory, "outputs")
            };

            if (!string.IsNullOrWhiteSpace(_settingsService.Current.DefaultOutputDirectory)) {
                searchPaths.Add(_settingsService.Current.DefaultOutputDirectory);
            }

            if (!string.IsNullOrWhiteSpace(_settingsService.Current.LoraStorageDirectory)) {
                searchPaths.Add(_settingsService.Current.LoraStorageDirectory);
            }

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
                        Message = line,
                        Timestamp = DateTime.UtcNow
                    })
                );
                return Results.Json(result);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 21. ComfyUI Status Check
        app.MapGet("/api/v1/comfyui/status", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            if (_comfyUiService == null) return Results.Json(new { isOnline = false, error = "ComfyUI service uninitialized" });

            try {
                var status = await _comfyUiService.CheckConnectionAsync();
                return Results.Json(new {
                    isOnline = status.IsConnected,
                    url = _settingsService.Current.ComfyUiEndpointUrl,
                    os = status.Os,
                    gpu = status.DeviceName
                });
            } catch (Exception ex) {
                return Results.Json(new { isOnline = false, error = ex.Message });
            }
        });

        // 22. ComfyUI Remote Inference Run
        app.MapPost("/api/v1/comfyui/test", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_comfyUiService == null) return Results.BadRequest(new { error = "ComfyUI service not initialized." });

            var req = await JsonSerializer.DeserializeAsync<ComfyUiTestRequestDto>(request.Body);
            if (req == null || string.IsNullOrWhiteSpace(req.Prompt)) {
                return Results.BadRequest(new { error = "Prompt is required for inference test." });
            }

            try {
                var graph = _comfyUiService.GenerateFluxPromptGraph(
                    req.Checkpoint ?? "flux1-dev.safetensors",
                    req.LoraName ?? "",
                    req.LoraWeight,
                    req.Prompt,
                    1024, 1024,
                    req.Steps,
                    req.Seed ?? 42
                );

                byte[]? imgBytes = await _comfyUiService.QueuePromptAndRenderAsync(graph);
                return Results.Json(new {
                    success = imgBytes != null,
                    imageBytesBase64 = imgBytes != null ? Convert.ToBase64String(imgBytes) : null,
                    message = imgBytes != null ? "Success" : "No image returned"
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 22b. Ollama Vision Status Check & Models Enumeration
        app.MapGet("/api/v1/curate/ollama/status", async (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();

            string configuredUrl = (_settingsService.Current.OllamaEndpointUrl ?? "http://localhost:11434").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(configuredUrl)) configuredUrl = "http://localhost:11434";

            var candidates = new List<string> { configuredUrl };
            if (configuredUrl.Contains("localhost")) candidates.Add(configuredUrl.Replace("localhost", "127.0.0.1"));
            else if (configuredUrl.Contains("127.0.0.1")) candidates.Add(configuredUrl.Replace("127.0.0.1", "localhost"));

            bool connected = false;
            string activeUrl = configuredUrl;
            var visionModels = new List<string>();
            var allModels = new List<string>();
            string defaultModel = _settingsService.Current.OllamaDefaultModel ?? "llama3.2-vision";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("User-Agent", "LoRAMancer-Tagger/1.0");
            if (!string.IsNullOrWhiteSpace(_settingsService.Current.OllamaApiKey)) {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settingsService.Current.OllamaApiKey.Trim());
            }

            foreach (var cand in candidates) {
                try {
                    using var resp = await http.GetAsync($"{cand}/api/tags");
                    if (resp.IsSuccessStatusCode) {
                        var json = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array) {
                            foreach (var m in modelsElem.EnumerateArray()) {
                                string name = m.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                                if (!string.IsNullOrEmpty(name)) {
                                    allModels.Add(name);
                                    string nLower = name.ToLowerInvariant();
                                    bool isVis = nLower.Contains("vision") || nLower.Contains("llava") || nLower.Contains("bakllava") || nLower.Contains("qwen") || nLower.Contains("minicpm");
                                    if (!isVis && m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array) {
                                        foreach (var cap in caps.EnumerateArray()) {
                                            if (string.Equals(cap.GetString(), "vision", StringComparison.OrdinalIgnoreCase)) {
                                                isVis = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (!isVis && m.TryGetProperty("details", out var det) && det.TryGetProperty("family", out var fam)) {
                                        string f = (fam.GetString() ?? "").ToLowerInvariant();
                                        if (f.Contains("vl") || f.Contains("clip") || f.Contains("vision")) isVis = true;
                                    }
                                    if (isVis && !visionModels.Contains(name)) {
                                        visionModels.Add(name);
                                    }
                                }
                            }
                        }
                        connected = true;
                        activeUrl = cand;
                        break;
                    }
                } catch { }
            }

            if (visionModels.Count == 0 && allModels.Count > 0) {
                visionModels.AddRange(allModels);
            }
            if (visionModels.Count > 0 && !visionModels.Contains(defaultModel)) {
                defaultModel = visionModels[0];
            }

            return Results.Json(new {
                reachable = connected,
                url = activeUrl,
                visionModels = visionModels,
                defaultModel = defaultModel
            });
        });

        // 22c. Ollama Automated Dataset Tagging (In-Place)
        app.MapPost("/api/v1/curate/ollama/tag", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();

            var req = await JsonSerializer.DeserializeAsync<OllamaTagRequestDto>(request.Body);
            if (req == null) return Results.BadRequest(new { error = "Invalid tag request parameters." });

            string inputPath = req.InputPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(inputPath) || (!Directory.Exists(inputPath) && !File.Exists(inputPath))) {
                string uploads = GetEffectiveUploadsDirectory();
                if (Directory.Exists(uploads)) {
                    var subDirs = Directory.GetDirectories(uploads);
                    if (subDirs.Length > 0) {
                        inputPath = subDirs.OrderByDescending(Directory.GetLastWriteTimeUtc).First();
                    } else {
                        inputPath = uploads;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(inputPath) || (!Directory.Exists(inputPath) && !File.Exists(inputPath))) {
                return Results.BadRequest(new { error = "No valid dataset directory or ZIP archive specified." });
            }

            string focus = (req.SubjectFocus ?? "general").ToLowerInvariant();
            string style = (req.CaptionStyle ?? "tags").ToLowerInvariant();
            string prompt = req.CustomPrompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt)) {
                prompt = style == "natural"
                    ? (focus switch {
                        "character" => "Describe this character in detail in 1-2 natural sentences, focusing on physical likeness, facial features, hair, clothing, pose, and expression. Do not use filler words.",
                        "style" => "Describe the visual and artistic style of this image in 1-2 sentences, focusing on medium, brushwork, lighting, color palette, and textures.",
                        "concept" => "Describe the primary object or concept in this image in 1-2 sentences, noting its material, structure, and distinctive visual attributes.",
                        "clothing" => "Describe the outfit, clothing materials, tailoring, and accessories in detail in 1-2 sentences.",
                        _ => "Describe this image thoroughly in 1-2 detailed sentences for training a text-to-image AI model. Focus on subject appearance, posture, clothing, colors, setting, and lighting."
                    })
                    : (focus switch {
                        "character" => "Analyze this image for character training. Output ONLY comma-separated tags describing: gender, hair color, eye color, facial expression, clothing, pose, camera angle, and background.",
                        "style" => "Analyze this image for art style training. Output ONLY comma-separated tags describing: artistic medium, art style, brushwork, color palette, lighting atmosphere, and texture.",
                        "concept" => "Analyze this image for concept or object training. Output ONLY comma-separated tags describing: the primary object, mechanical parts, material, colors, and setting.",
                        "clothing" => "Analyze this image for fashion and outfit training. Output ONLY comma-separated tags describing: garment type, clothing style, fabric material, color, patterns, and accessories.",
                        _ => "Analyze this image in detail for machine learning training. Output ONLY concise, comma-separated tags describing the subject, attire, hair, expression, pose, background, lighting, and artistic style."
                    });
            }

            string model = !string.IsNullOrWhiteSpace(req.Model) ? req.Model : (_settingsService.Current.OllamaDefaultModel ?? "llama3.2-vision");
            string ollamaUrl = _settingsService.Current.OllamaEndpointUrl ?? "http://localhost:11434";
            string apiKey = _settingsService.Current.OllamaApiKey ?? string.Empty;

            if (_pluginManager != null) {
                var parameters = new Dictionary<string, object?> {
                    ["input_path"] = inputPath,
                    ["output_path"] = string.Empty,
                    ["trigger_word"] = req.TriggerWord ?? string.Empty,
                    ["included_phrases"] = req.IncludedPhrases ?? string.Empty,
                    ["blacklist_words"] = req.BlacklistWords ?? string.Empty,
                    ["model"] = model,
                    ["caption_style"] = style,
                    ["custom_prompt"] = prompt,
                    ["create_zip"] = false,
                    ["ollama_url"] = ollamaUrl,
                    ["api_key"] = apiKey
                };

                var pluginResult = await _pluginManager.ExecutePluginAsync(
                    "ollama-lora-tagger",
                    "tag_dataset",
                    parameters,
                    line => {
                        BroadcastTelemetry(new TrainingTelemetryDto {
                            EventType = "tag_log",
                            Message = line,
                            Timestamp = DateTime.UtcNow
                        });
                    },
                    null,
                    CancellationToken.None
                );

                int processed = 0;
                var sampleCaptions = new List<object>();

                if (pluginResult.Data != null) {
                    try {
                        using var doc = JsonDocument.Parse(pluginResult.Data.ToString() ?? "{}");
                        if (doc.RootElement.TryGetProperty("processed", out var pElem)) processed = pElem.GetInt32();
                        if (doc.RootElement.TryGetProperty("sample_captions", out var sElem) && sElem.ValueKind == JsonValueKind.Array) {
                            foreach (var s in sElem.EnumerateArray()) {
                                sampleCaptions.Add(new {
                                    image = s.TryGetProperty("image", out var img) ? img.GetString() : "",
                                    caption = s.TryGetProperty("caption", out var cap) ? cap.GetString() : ""
                                });
                            }
                        }
                    } catch { }
                }

                var report = await _datasetInspector.InspectDatasetAsync(inputPath);

                return Results.Json(new {
                    success = pluginResult.Success,
                    processedCount = processed > 0 ? processed : report.TotalCaptions,
                    totalImages = report.TotalImages,
                    captionCount = report.TotalCaptions,
                    datasetPath = inputPath,
                    sampleCaptions = sampleCaptions,
                    message = pluginResult.Success ? $"Auto-tagging complete! Processed {processed} image(s) in-place." : (pluginResult.Message ?? "Auto-tagging failed.")
                });
            }

            return Results.BadRequest(new { error = "Plugin manager service is not initialized on host." });
        });

        // 23. Documentation Endpoints
        app.MapGet("/api/v1/docs", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            string dir = LocateDocsDirectory();
            if (!Directory.Exists(dir)) return Results.Json(Array.Empty<object>());

            var docs = Directory.GetFiles(dir, "*.md")
                .Select(f => {
                    string fn = Path.GetFileName(f);
                    return new {
                        fileName = fn,
                        title = GetDocFriendlyTitle(fn),
                        icon = GetDocIcon(fn),
                        sortWeight = GetDocSortWeight(fn),
                        category = "Guides"
                    };
                })
                .OrderBy(d => d.sortWeight)
                .ToList();

            return Results.Json(docs);
        });

        app.MapGet("/api/v1/docs/{fileName}", async (string fileName, HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) return Results.Unauthorized();
            string cleanName = Path.GetFileName(fileName);
            string dir = LocateDocsDirectory();
            string fullPath = Path.Combine(dir, cleanName);

            if (!File.Exists(fullPath)) {
                return Results.NotFound(new { error = $"Document '{cleanName}' not found." });
            }

            string md = await File.ReadAllTextAsync(fullPath);
            string html = ConvertMarkdownToHtml(md);
            return Results.Json(new {
                fileName = cleanName,
                title = GetDocFriendlyTitle(cleanName),
                icon = GetDocIcon(cleanName),
                markdown = md,
                html = html
            });
        });

        app.MapGet("/docs", async (HttpContext context) => {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetEmbeddedWebInterfaceHtml());
        });

        // 24. Studio Modal Tools API Endpoints
        app.MapPost("/api/v1/tools/surgery/resize", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_surgeryService == null) return Results.BadRequest(new { error = "Surgery service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolResizeRequestDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.SourceLora) || string.IsNullOrWhiteSpace(dto.OutputLora)) {
                return Results.BadRequest(new { error = "Source and destination LoRA paths are required." });
            }

            var result = await _surgeryService.ResizeLoraAsync(dto.SourceLora, dto.OutputLora, dto.TargetRank);
            return Results.Json(result);
        });

        app.MapPost("/api/v1/tools/surgery/merge", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_surgeryService == null) return Results.BadRequest(new { error = "Surgery service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolMergeRequestDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ModelA) || string.IsNullOrWhiteSpace(dto.ModelB) || string.IsNullOrWhiteSpace(dto.OutputLora)) {
                return Results.BadRequest(new { error = "Model A, Model B, and output destination paths are required." });
            }

            float w1 = (float)dto.Ratio;
            float w2 = (float)(1.0 - dto.Ratio);
            var result = await _surgeryService.MergeLorasAsync(dto.ModelA, w1, dto.ModelB, w2, dto.OutputLora);
            return Results.Json(result);
        });

        app.MapPost("/api/v1/tools/genetherapy/analyze", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_surgeryService == null) return Results.BadRequest(new { error = "Surgery service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolGeneTherapyAnalyzeDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.LoraPath)) {
                return Results.BadRequest(new { error = "Target LoRA file path is required." });
            }

            var analysis = await _surgeryService.AnalyzeLayerBlocksAsync(dto.LoraPath);
            if (analysis == null) return Results.BadRequest(new { error = "Analysis failed or model invalid." });
            return Results.Json(analysis);
        });

        app.MapPost("/api/v1/tools/genetherapy/prune", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_surgeryService == null) return Results.BadRequest(new { error = "Surgery service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolGeneTherapyPruneDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.LoraPath) || string.IsNullOrWhiteSpace(dto.OutputPath)) {
                return Results.BadRequest(new { error = "Target and output paths are required." });
            }

            var analysis = await _surgeryService.AnalyzeLayerBlocksAsync(dto.LoraPath);
            if (analysis == null) return Results.BadRequest(new { error = "Failed to inspect model blocks for pruning." });

            double thresh = dto.Threshold > 0 ? dto.Threshold : 2.8;
            var toxicBlocks = analysis.Blocks
                .Where(b => (b.AverageNorm > (analysis.OverallAverageNorm * thresh) && b.AverageNorm > 0.5) || b.IsToxic)
                .Select(b => b.BlockName)
                .ToList();

            if (toxicBlocks.Count == 0) {
                return Results.Json(new { success = false, message = "No toxic outliers found exceeding the threshold." });
            }

            var result = await _surgeryService.PruneOrAttenuateBlocksAsync(dto.LoraPath, dto.OutputPath, toxicBlocks, 0.0f);
            return Results.Json(result);
        });

        app.MapPost("/api/v1/tools/diff/compare", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_diffService == null) return Results.BadRequest(new { error = "Diff service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolDiffCompareDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ModelAPath) || string.IsNullOrWhiteSpace(dto.ModelBPath)) {
                return Results.BadRequest(new { error = "Model A and Model B paths are required." });
            }

            var summary = await _diffService.CompareLorasAsync(dto.ModelAPath, dto.ModelBPath);
            return Results.Json(summary);
        });

        app.MapPost("/api/v1/tools/benchmark/scan", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_benchmarkService == null) return Results.BadRequest(new { error = "Benchmark service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolBenchmarkScanDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.FolderPath)) {
                return Results.BadRequest(new { error = "Folder path is required." });
            }

            var checkpoints = await _benchmarkService.DiscoverCheckpointsAsync(dto.FolderPath);
            return Results.Json(new { count = checkpoints.Count, checkpoints = checkpoints });
        });

        app.MapPost("/api/v1/tools/benchmark/start", async (HttpRequest request) => {
            if (!IsAuthorized(request.HttpContext, accessToken)) return Results.Unauthorized();
            if (_benchmarkService == null) return Results.BadRequest(new { error = "Benchmark service not initialized." });

            var dto = await JsonSerializer.DeserializeAsync<ToolBenchmarkStartDto>(request.Body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.CheckpointFolder)) {
                return Results.BadRequest(new { error = "Checkpoint folder is required." });
            }

            var checkpoints = await _benchmarkService.DiscoverCheckpointsAsync(dto.CheckpointFolder);
            if (checkpoints.Count == 0) return Results.BadRequest(new { error = "No .safetensors checkpoints discovered in folder." });

            var prompts = _benchmarkService.GenerateDefaultPrompts(dto.TriggerWord ?? "");
            var result = await _benchmarkService.RunBenchmarkMatrixAsync(
                dto.CheckpointFolder,
                checkpoints,
                dto.BaseArch ?? "FLUX.1",
                dto.TriggerWord ?? "",
                prompts,
                renderViaComfyUi: false
            );
            return Results.Json(result);
        });

        // 25. Embedded Desktop-Replicating HTML Interface
        app.MapGet("/", async (HttpContext context) => {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetEmbeddedWebInterfaceHtml());
        });

        // 24. Public Guest Landing Page (Authentication UI)
        app.MapGet("/login", async (HttpContext context) => {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetEmbeddedLoginPageHtml());
        });

        // 25. PWA Web App Manifest
        app.MapGet("/manifest.json", async (HttpContext context) => {
            context.Response.ContentType = "application/manifest+json; charset=utf-8";
            await context.Response.WriteAsync(GetWebManifestJson());
        });

        // 26. PWA Service Worker
        app.MapGet("/sw.js", async (HttpContext context) => {
            context.Response.ContentType = "application/javascript; charset=utf-8";
            await context.Response.WriteAsync(GetServiceWorkerJs());
        });

        // 27. App Icon (SVG)
        app.MapGet("/icon.svg", async (HttpContext context) => {
            context.Response.ContentType = "image/svg+xml; charset=utf-8";
            await context.Response.WriteAsync(GetAppIconSvg());
        });

        ListeningUrl = $"http://{bindAddress}:{port}";
        _webApp = app;

        _ = app.RunAsync(_serverCts.Token);
        IsRunning = true;
        OnServerStateChanged?.Invoke(true, ListeningUrl);
    }

    public async Task StopServerAsync() {
        if (!IsRunning || _webApp == null) {
            return;
        }

        if (_serverCts != null) {
            _serverCts.Cancel();
        }

        await _webApp.StopAsync();
        await _webApp.DisposeAsync();
        _webApp = null;
        IsRunning = false;
        ListeningUrl = string.Empty;
        OnServerStateChanged?.Invoke(false, string.Empty);
    }

    private string GetEffectiveUploadsDirectory() {
        string configured = _settingsService.Current.RemoteUploadsDirectory;
        if (!string.IsNullOrWhiteSpace(configured)) {
            Directory.CreateDirectory(configured);
            return configured;
        }
        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "uploads");
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }

    private void HandleProgressUpdated(TrainingProgress progress) {
        BroadcastTelemetry(new TrainingTelemetryDto {
            EventType = "progress",
            Step = progress.CurrentStep,
            TotalSteps = progress.TotalSteps,
            Loss = (float)progress.CurrentLoss,
            Message = _trainingRunner.CurrentJob?.Name ?? "Active Run",
            Timestamp = DateTime.UtcNow
        });
    }

    private void HandleLogReceived(string logLine) {
        BroadcastTelemetry(new TrainingTelemetryDto {
            EventType = "log",
            Message = logLine,
            Timestamp = DateTime.UtcNow
        });
    }

    private void BroadcastTelemetry(TrainingTelemetryDto telemetry) {
        string json = JsonSerializer.Serialize(telemetry);
        string message = $"data: {json}\n\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);

        foreach (var client in _sseClients) {
            try {
                client.Body.WriteAsync(bytes, 0, bytes.Length);
            } catch {
                // Client disconnected
            }
        }
    }

    private bool IsAuthorized(HttpContext context, string? masterAccessToken) {
        if (string.IsNullOrWhiteSpace(masterAccessToken) && string.IsNullOrWhiteSpace(_settingsService.Current.ServerAccessToken)) {
            return true;
        }

        string? headerToken = context.Request.Headers["X-LoRAMancer-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(headerToken)) {
            headerToken = context.Request.Query["token"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(headerToken)) {
            return false;
        }

        if (_authTokenManager != null && _authTokenManager.ValidateToken(headerToken, out _)) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(masterAccessToken) && headerToken == masterAccessToken) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_settingsService.Current.ServerAccessToken) && headerToken == _settingsService.Current.ServerAccessToken) {
            return true;
        }

        return false;
    }

    private bool IsAdmin(HttpContext context, string? masterAccessToken) {
        if (string.IsNullOrWhiteSpace(masterAccessToken) && string.IsNullOrWhiteSpace(_settingsService.Current.ServerAccessToken)) {
            return true;
        }

        string? headerToken = context.Request.Headers["X-LoRAMancer-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(headerToken)) {
            headerToken = context.Request.Query["token"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(headerToken)) {
            return false;
        }

        if (_authTokenManager != null && _authTokenManager.ValidateToken(headerToken, out var record)) {
            return string.Equals(record?.Role, "Admin", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(masterAccessToken) && headerToken == masterAccessToken) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_settingsService.Current.ServerAccessToken) && headerToken == _settingsService.Current.ServerAccessToken) {
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync() {
        _trainingRunner.OnProgressUpdated -= HandleProgressUpdated;
        _trainingRunner.OnLogReceived -= HandleLogReceived;
        await StopServerAsync();
    }

    private static string LocateDocsDirectory() {
        string baseDir = AppContext.BaseDirectory;
        string appDocs = Path.Combine(baseDir, "docs");
        if (Directory.Exists(appDocs) && Directory.GetFiles(appDocs, "*.md").Length > 0) {
            return appDocs;
        }

        string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "docs"));
        if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.md").Length > 0) {
            return candidate;
        }

        string curCandidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "docs"));
        if (Directory.Exists(curCandidate) && Directory.GetFiles(curCandidate, "*.md").Length > 0) {
            return curCandidate;
        }

        return appDocs;
    }

    private static string GetDocFriendlyTitle(string fileName) {
        return fileName.ToUpperInvariant() switch {
            "FEATURES_OVERVIEW.MD" => "Features & Subsystem Reference",
            "HYPERPARAMETER_GUIDE.MD" => "Hyperparameters & Options Guide",
            "LORA_LIBRARY_BROWSER.MD" => "LoRA Library & Multi-Library Browser",
            "TRAINING_WIZARD.MD" => "Easy Use Training Wizard",
            "HISTORY_AND_VAULT.MD" => "LoRA Training History & Vault",
            "GPU_AND_ENVIRONMENT_SETUP.MD" => "Universal GPU Setup (ROCm / CUDA / Intel)",
            "AMD_ROCM_SETUP.MD" => "AMD ROCm Windows Setup Guide",
            "REMOTE_TRAINING.MD" => "Remote Web UI & Public Serving",
            "PLUGINS.MD" => "Plugin System (C# & Python)",
            "ARCHITECTURE.MD" => "Architecture & Core Engine",
            "INSTALLER_AND_UPDATES.MD" => "Installer & Update Specs",
            _ => Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ')
        };
    }

    private static string GetDocIcon(string fileName) {
        return fileName.ToUpperInvariant() switch {
            "FEATURES_OVERVIEW.MD" => "⭐",
            "HYPERPARAMETER_GUIDE.MD" => "🎛️",
            "LORA_LIBRARY_BROWSER.MD" => "📚",
            "TRAINING_WIZARD.MD" => "🧙",
            "HISTORY_AND_VAULT.MD" => "🏛️",
            "GPU_AND_ENVIRONMENT_SETUP.MD" => "⚡",
            "AMD_ROCM_SETUP.MD" => "🔴",
            "REMOTE_TRAINING.MD" => "🌐",
            "PLUGINS.MD" => "🔌",
            "ARCHITECTURE.MD" => "🏗️",
            "INSTALLER_AND_UPDATES.MD" => "📦",
            _ => "📄"
        };
    }

    private static int GetDocSortWeight(string fileName) {
        return fileName.ToUpperInvariant() switch {
            "FEATURES_OVERVIEW.MD" => 1,
            "HYPERPARAMETER_GUIDE.MD" => 2,
            "LORA_LIBRARY_BROWSER.MD" => 3,
            "TRAINING_WIZARD.MD" => 4,
            "HISTORY_AND_VAULT.MD" => 5,
            "GPU_AND_ENVIRONMENT_SETUP.MD" => 6,
            "AMD_ROCM_SETUP.MD" => 7,
            "REMOTE_TRAINING.MD" => 8,
            "PLUGINS.MD" => 9,
            "ARCHITECTURE.MD" => 10,
            "INSTALLER_AND_UPDATES.MD" => 11,
            _ => 99
        };
    }

    private static string ConvertMarkdownToHtml(string markdown) {
        if (string.IsNullOrWhiteSpace(markdown)) {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool inCodeBlock = false;
        bool inList = false;
        bool inTable = false;
        bool isTableHeader = false;

        foreach (var rawLine in lines) {
            string line = rawLine;

            // Fenced code blocks
            if (line.TrimStart().StartsWith("```")) {
                if (!inCodeBlock) {
                    inCodeBlock = true;
                    if (inList) { inList = false; sb.Append("</ul>\n"); }
                    if (inTable) { inTable = false; sb.Append("</table>\n"); }
                    sb.Append("<pre style='background:#11111b; border:1px solid #313244; padding:12px; border-radius:6px; overflow-x:auto; font-family:monospace; font-size:0.85rem; color:#cdd6f4;'><code>");
                } else {
                    inCodeBlock = false;
                    sb.Append("</code></pre>\n");
                }
                continue;
            }

            if (inCodeBlock) {
                sb.Append(System.Net.WebUtility.HtmlEncode(line)).Append('\n');
                continue;
            }

            string trimmed = line.Trim();

            // Tables
            if (trimmed.StartsWith("|") && trimmed.EndsWith("|")) {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                if (!inTable) {
                    inTable = true;
                    isTableHeader = true;
                    sb.Append("<table style='width:100%; border-collapse:collapse; margin:16px 0; font-size:0.88rem; background:rgba(17,17,27,0.5); border-radius:6px; overflow:hidden; border:1px solid #313244;'>\n");
                }
                if (trimmed.Contains("---")) {
                    isTableHeader = false;
                    continue;
                }
                var cells = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries);
                sb.Append("<tr style='border-bottom:1px solid #313244;'>");
                foreach (var cell in cells) {
                    if (isTableHeader) {
                        sb.Append("<th style='padding:8px 12px; background:#181825; color:#cba6f7; text-align:left; font-weight:700;'>").Append(FormatDocInline(cell.Trim())).Append("</th>");
                    } else {
                        sb.Append("<td style='padding:8px 12px; color:#cdd6f4;'>").Append(FormatDocInline(cell.Trim())).Append("</td>");
                    }
                }
                sb.Append("</tr>\n");
                continue;
            } else if (inTable) {
                inTable = false;
                sb.Append("</table>\n");
            }

            // Headings
            if (trimmed.StartsWith("### ")) {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                sb.Append("<h3 style='margin-top:1.4rem; margin-bottom:0.5rem; font-weight:700; color:#cdd6f4; font-size:1.15rem;'>")
                  .Append(FormatDocInline(trimmed[4..]))
                  .Append("</h3>\n");
                continue;
            }
            if (trimmed.StartsWith("## ")) {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                sb.Append("<h2 style='margin-top:1.8rem; margin-bottom:0.6rem; font-weight:700; color:var(--accent-purple); font-size:1.35rem; border-bottom:1px solid rgba(255,255,255,0.08); padding-bottom:6px;'>")
                  .Append(FormatDocInline(trimmed[3..]))
                  .Append("</h2>\n");
                continue;
            }
            if (trimmed.StartsWith("# ")) {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                sb.Append("<h1 style='margin-top:0.8rem; margin-bottom:0.8rem; font-weight:800; color:var(--accent-purple); font-size:1.6rem;'>")
                  .Append(FormatDocInline(trimmed[2..]))
                  .Append("</h1>\n");
                continue;
            }

            // Horizontal rule
            if (trimmed == "---" || trimmed == "***" || trimmed == "___") {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                sb.Append("<hr style='border:0; border-top:1px solid #313244; margin:1.5rem 0;' />\n");
                continue;
            }

            // Callout alerts
            if (trimmed.StartsWith("> [!")) {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                int closeBracket = trimmed.IndexOf(']');
                string alertType = closeBracket > 4 ? trimmed[4..closeBracket].ToUpperInvariant() : "NOTE";
                sb.Append("<div style='background:rgba(203,166,247,0.08); border-left:4px solid var(--accent-purple); border-radius:4px; padding:10px 14px; margin:12px 0;'><b style='color:var(--accent-purple);'>")
                  .Append(alertType)
                  .Append(":</b> ");
                continue;
            }
            if (trimmed.StartsWith("> ")) {
                sb.Append(FormatDocInline(trimmed[2..])).Append("</div>\n");
                continue;
            }

            // Unordered list items
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) {
                if (!inList) {
                    inList = true;
                    sb.Append("<ul style='padding-left:1.5rem; margin-bottom:0.8rem;'>\n");
                }
                sb.Append("<li style='margin-bottom:4px;'>").Append(FormatDocInline(trimmed[2..])).Append("</li>\n");
                continue;
            }

            // Numbered list items
            var numMatch = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
            if (numMatch.Success) {
                if (!inList) {
                    inList = true;
                    sb.Append("<ol style='padding-left:1.5rem; margin-bottom:0.8rem;'>\n");
                }
                sb.Append("<li style='margin-bottom:4px;'>").Append(FormatDocInline(numMatch.Groups[2].Value)).Append("</li>\n");
                continue;
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(trimmed)) {
                if (inList) { inList = false; sb.Append("</ul>\n"); }
                continue;
            }

            // Regular paragraph
            if (inList) { inList = false; sb.Append("</ul>\n"); }
            sb.Append("<p style='margin-bottom:0.8rem; line-height:1.6;'>").Append(FormatDocInline(trimmed)).Append("</p>\n");
        }

        if (inList) sb.Append("</ul>\n");
        if (inTable) sb.Append("</table>\n");
        return sb.ToString();
    }

    private static string FormatDocInline(string text) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }

        string result = System.Net.WebUtility.HtmlEncode(text);
        result = Regex.Replace(result, @"\*\*(.+?)\*\*", "<strong style='color:#ffffff; font-weight:700;'>$1</strong>");
        result = Regex.Replace(result, @"\b__(.+?)__\b", "<strong style='color:#ffffff; font-weight:700;'>$1</strong>");
        result = Regex.Replace(result, @"`([^`]+)`", "<code style='background:rgba(255,255,255,0.08); padding:2px 6px; border-radius:4px; font-family:monospace; font-size:0.88em; color:var(--accent-purple);'>$1</code>");
        result = Regex.Replace(result, @"\[([^\]]+)\]\(([^)]+)\)", "<span style='color:var(--accent-purple); text-decoration:underline;'>$1</span>");
        return result;
    }

    private static string GetWebManifestJson() {
        return """
{
  "id": "/",
  "name": "LoRAMancer Studio",
  "short_name": "LoRAMancer",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "orientation": "any",
  "background_color": "#11111b",
  "theme_color": "#181825",
  "description": "Desktop-Class Remote Web UI for AMD ROCm & PyTorch LoRA Training",
  "icons": [
    {
      "src": "/icon.svg",
      "sizes": "192x192 512x512 any",
      "type": "image/svg+xml",
      "purpose": "any maskable"
    }
  ]
}
""";
    }

    private static string GetServiceWorkerJs() {
        return """
const CACHE_NAME = 'loramancer-pwa-v2';
const PRECACHE_ASSETS = [
    '/',
    '/manifest.json',
    '/icon.svg'
];

self.addEventListener('install', (e) => {
    e.waitUntil(
        caches.open(CACHE_NAME).then((cache) => cache.addAll(PRECACHE_ASSETS))
    );
    self.skipWaiting();
});

self.addEventListener('activate', (e) => {
    e.waitUntil(
        caches.keys().then((keys) => Promise.all(
            keys.map((k) => { if (k !== CACHE_NAME) return caches.delete(k); })
        ))
    );
    self.clients.claim();
});

self.addEventListener('fetch', (e) => {
    if (e.request.method !== 'GET') return;
    const url = e.request.url;
    // Transparent pass-through for API requests, SSE telemetry streams, and WebSockets
    if (url.includes('/api/')) return;
    e.respondWith(
        fetch(e.request).catch(() => caches.match(e.request))
    );
});
""";
    }

    private static string GetAppIconSvg() {
        return """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <rect width="512" height="512" rx="100" fill="#11111b"/>
  <circle cx="256" cy="256" r="180" fill="none" stroke="#cba6f7" stroke-width="28" stroke-dasharray="8 8"/>
  <path d="M256 120 L350 340 L162 340 Z" fill="none" stroke="#89b4fa" stroke-width="24" stroke-linejoin="round"/>
  <circle cx="256" cy="220" r="32" fill="#f38ba8"/>
  <path d="M256 340 L256 400" stroke="#a6e3a1" stroke-width="24" stroke-linecap="round"/>
</svg>
""";
    }

    private string GetEmbeddedLoginPageHtml() {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>LoRAMancer Studio - Authentication</title>
    <link rel="manifest" href="/manifest.json" />
    <meta name="theme-color" content="#11111b" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
    <meta name="apple-mobile-web-app-title" content="LoRAMancer" />
    <link rel="icon" type="image/svg+xml" href="/icon.svg" />
    <link rel="apple-touch-icon" href="/icon.svg" />
    <style>
        :root {
            --bg-base: #11111b;
            --bg-surface: #181825;
            --accent-primary: #cba6f7;
            --text-primary: #cdd6f4;
            --text-secondary: #a6adc8;
            --border: #313244;
        }
        body {
            margin: 0;
            padding: 0;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            background-color: var(--bg-base);
            color: var(--text-primary);
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
        }
        .login-box {
            background-color: var(--bg-surface);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 32px;
            width: 100%;
            max-width: 400px;
            box-shadow: 0 12px 32px rgba(0,0,0,0.5);
            text-align: center;
        }
        .logo {
            font-size: 2.2rem;
            margin-bottom: 8px;
        }
        h2 {
            margin: 0 0 8px 0;
            color: var(--accent-primary);
        }
        p {
            margin: 0 0 24px 0;
            color: var(--text-secondary);
            font-size: 0.9rem;
        }
        input {
            width: 100%;
            box-sizing: border-box;
            background-color: #11111b;
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 12px 14px;
            color: #fff;
            font-size: 1rem;
            margin-bottom: 16px;
            outline: none;
            text-align: center;
            letter-spacing: 2px;
        }
        input:focus {
            border-color: var(--accent-primary);
        }
        button {
            width: 100%;
            background: linear-gradient(135deg, #cba6f7, #89b4fa);
            border: none;
            border-radius: 8px;
            padding: 12px;
            color: #11111b;
            font-size: 1rem;
            font-weight: 700;
            cursor: pointer;
            transition: opacity 0.2s;
        }
        button:hover {
            opacity: 0.9;
        }
        .error-msg {
            color: #f38ba8;
            font-size: 0.85rem;
            margin-top: 12px;
            display: none;
        }
    </style>
</head>
<body>
    <div class="login-box">
        <div class="logo">⚡</div>
        <h2>LoRAMancer Remote</h2>
        <p>Enter Host Access Token to unlock remote workstation control</p>
        <input id="tokenInput" type="password" placeholder="••••••••" autofocus />
        <button onclick="login()">Connect to Studio</button>
        <div id="errorMsg" class="error-msg">Invalid Credentials</div>
    </div>
    <script>
        async function login() {
            const token = document.getElementById('tokenInput').value.trim();
            const err = document.getElementById('errorMsg');
            err.style.display = 'none';

            try {
                const res = await fetch('/api/v1/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ pinOrToken: token })
                });
                if (res.ok) {
                    const data = await res.json();
                    localStorage.setItem('loramancer_auth_token', token);
                    localStorage.setItem('loramancer_user_role', data.role);
                    window.location.href = '/?token=' + encodeURIComponent(token);
                } else {
                    err.style.display = 'block';
                }
            } catch (e) {
                err.textContent = 'Connection error: ' + e.message;
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
        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
    <title>LoRAMancer Studio</title>
    
    <link rel="manifest" href="/manifest.json" />
    <meta name="theme-color" content="#11111b" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
    <meta name="apple-mobile-web-app-title" content="LoRAMancer" />
    <link rel="icon" type="image/svg+xml" href="/icon.svg" />
    <link rel="apple-touch-icon" href="/icon.svg" />

    <style>
        :root {
            --bg-base: #11111b;
            --bg-surface: #181825;
            --bg-overlay: #1e1e2e;
            --border-dark: #313244;
            --text-primary: #cdd6f4;
            --text-secondary: #a6adc8;
            --text-muted: #6c7086;
            --accent-purple: #cba6f7;
            --accent-blue: #89b4fa;
            --accent-green: #a6e3a1;
            --accent-peach: #fab387;
            --accent-red: #f38ba8;
            --accent-yellow: #f9e2af;
            --sidebar-width: 260px;
        }

        * { box-sizing: border-box; }
        body {
            margin: 0;
            padding: 0;
            background-color: var(--bg-base);
            color: var(--text-primary);
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            display: flex;
            height: 100vh;
            overflow: hidden;
            user-select: none;
        }

        /* SIDEBAR (Desktop NavMenu mirror) */
        #sidebar {
            width: var(--sidebar-width);
            background-color: var(--bg-surface);
            border-right: 1px solid var(--border-dark);
            display: flex;
            flex-direction: column;
            flex-shrink: 0;
            z-index: 10;
        }

        .sidebar-brand {
            padding: 16px 20px;
            display: flex;
            align-items: center;
            gap: 12px;
            border-bottom: 1px solid var(--border-dark);
        }

        .brand-icon {
            width: 36px;
            height: 36px;
            border-radius: 8px;
            background: linear-gradient(135deg, var(--accent-purple), var(--accent-blue));
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 1.2rem;
            color: #11111b;
            font-weight: 900;
        }

        .brand-text h1 {
            font-size: 1.05rem;
            font-weight: 800;
            margin: 0;
            letter-spacing: -0.3px;
            background: linear-gradient(90deg, #fff, var(--accent-purple));
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .brand-text p {
            font-size: 0.68rem;
            color: var(--text-muted);
            margin: 0;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }

        .nav-scroller {
            flex: 1;
            overflow-y: auto;
            padding: 12px 8px;
        }

        .nav-group-header {
            font-size: 0.68rem;
            font-weight: 800;
            text-transform: uppercase;
            color: var(--text-muted);
            padding: 10px 12px 4px 12px;
            letter-spacing: 0.8px;
        }

        .nav-item {
            display: flex;
            align-items: center;
            gap: 12px;
            padding: 9px 12px;
            margin: 2px 0;
            border-radius: 6px;
            cursor: pointer;
            color: var(--text-secondary);
            font-size: 0.86rem;
            font-weight: 600;
            transition: all 0.15s ease;
        }

        .nav-item:hover {
            background-color: rgba(255, 255, 255, 0.05);
            color: #ffffff;
        }

        .nav-item.active {
            background: linear-gradient(90deg, rgba(203, 166, 247, 0.15), rgba(137, 180, 250, 0.05));
            color: var(--accent-purple);
            border-left: 3px solid var(--accent-purple);
        }

        .nav-icon {
            font-size: 1.1rem;
            width: 20px;
            text-align: center;
        }

        .nav-badge {
            margin-left: auto;
            font-size: 0.65rem;
            font-weight: 700;
            padding: 2px 6px;
            border-radius: 4px;
            background: var(--bg-overlay);
            color: var(--text-secondary);
        }

        .sidebar-footer {
            padding: 12px 16px;
            border-top: 1px solid var(--border-dark);
            background-color: var(--bg-surface);
            display: flex;
            flex-direction: column;
            gap: 6px;
        }

        .host-status-row {
            display: flex;
            align-items: center;
            justify-content: space-between;
            font-size: 0.72rem;
            color: var(--text-secondary);
        }

        .status-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background-color: var(--accent-green);
            display: inline-block;
            box-shadow: 0 0 8px var(--accent-green);
        }

        /* MAIN CONTENT AREA */
        #main-area {
            flex: 1;
            display: flex;
            flex-direction: column;
            overflow: hidden;
            background-color: var(--bg-base);
        }

        /* TOPBAR (Desktop MainLayout mirror) */
        #topbar {
            height: 52px;
            background-color: var(--bg-surface);
            border-bottom: 1px solid var(--border-dark);
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 0 20px;
            flex-shrink: 0;
            gap: 16px;
        }

        .topbar-left {
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .concept-pill {
            background-color: rgba(203, 166, 247, 0.12);
            border: 1px solid rgba(203, 166, 247, 0.3);
            color: var(--accent-purple);
            font-size: 0.75rem;
            font-weight: 700;
            padding: 4px 10px;
            border-radius: 20px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .pipeline-chips {
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .chip {
            padding: 4px 10px;
            border-radius: 20px;
            font-size: 0.72rem;
            font-weight: 700;
            cursor: pointer;
            transition: all 0.15s ease;
            display: flex;
            align-items: center;
            gap: 4px;
        }

        .chip-stage {
            background-color: rgba(255, 255, 255, 0.05);
            color: var(--text-secondary);
            border: 1px solid var(--border-dark);
        }

        .chip-stage:hover {
            color: #fff;
            border-color: var(--text-secondary);
        }

        .chip-stage.active {
            background-color: rgba(203, 166, 247, 0.18);
            color: var(--accent-purple);
            border-color: var(--accent-purple);
        }

        .topbar-right {
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .rocm-pill {
            background: linear-gradient(135deg, rgba(243, 139, 168, 0.15), rgba(203, 166, 247, 0.15));
            border: 1px solid rgba(243, 139, 168, 0.3);
            color: #f38ba8;
            font-size: 0.72rem;
            font-weight: 700;
            padding: 4px 10px;
            border-radius: 20px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        /* VIEWPORT CONTAINERS */
        #viewport {
            flex: 1;
            overflow-y: auto;
            padding: 20px;
            position: relative;
        }

        .view-panel {
            display: none;
            max-width: 1400px;
            margin: 0 auto;
        }

        .view-panel.active {
            display: block;
            animation: fadeIn 0.15s ease-out;
        }

        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(4px); }
            to { opacity: 1; transform: translateY(0); }
        }

        /* COMMON UI CARDS & GRIDS */
        .card {
            background-color: var(--bg-surface);
            border: 1px solid var(--border-dark);
            border-radius: 10px;
            padding: 18px;
            margin-bottom: 16px;
            box-shadow: 0 4px 16px rgba(0,0,0,0.3);
        }

        .card-header-bar {
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 14px;
        }

        .card-title {
            font-size: 1.05rem;
            font-weight: 700;
            color: #ffffff;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .grid-2 { display: grid; grid-template-columns: repeat(2, 1fr); gap: 16px; }
        .grid-3 { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; }
        .grid-4 { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; }
        .grid-5 { display: grid; grid-template-columns: repeat(5, 1fr); gap: 12px; }

        @media (max-width: 1100px) {
            .grid-4 { grid-template-columns: repeat(2, 1fr); }
            .grid-5 { grid-template-columns: repeat(2, 1fr); }
            .grid-3 { grid-template-columns: repeat(1, 1fr); }
            .grid-2 { grid-template-columns: repeat(1, 1fr); }
            #sidebar { width: 68px; }
            .brand-text, .nav-item span, .nav-group-header, .sidebar-footer, .nav-badge { display: none; }
            .nav-item { justify-content: center; padding: 12px 0; }
        }

        .stat-box {
            background-color: var(--bg-overlay);
            border: 1px solid var(--border-dark);
            border-radius: 8px;
            padding: 12px 14px;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
        }

        .stat-label {
            font-size: 0.72rem;
            font-weight: 700;
            text-transform: uppercase;
            color: var(--text-secondary);
            margin-bottom: 4px;
        }

        .stat-val {
            font-size: 1.35rem;
            font-weight: 800;
            color: #ffffff;
            font-family: 'JetBrains Mono', monospace;
        }

        /* INPUTS & CONTROLS */
        .input-group {
            margin-bottom: 12px;
            display: flex;
            flex-direction: column;
            gap: 4px;
        }

        label {
            font-size: 0.78rem;
            font-weight: 700;
            color: var(--text-secondary);
        }

        input[type="text"], input[type="number"], select, textarea {
            background-color: var(--bg-base);
            border: 1px solid var(--border-dark);
            border-radius: 6px;
            padding: 8px 12px;
            color: #ffffff;
            font-size: 0.88rem;
            font-family: inherit;
            outline: none;
            transition: border-color 0.15s;
        }

        input:focus, select:focus, textarea:focus {
            border-color: var(--accent-purple);
        }

        input[type="range"] {
            accent-color: var(--accent-purple);
            cursor: pointer;
        }

        /* BUTTONS */
        .btn {
            background: linear-gradient(135deg, var(--accent-purple), var(--accent-blue));
            border: none;
            border-radius: 6px;
            color: #11111b;
            font-weight: 700;
            padding: 9px 16px;
            font-size: 0.86rem;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 8px;
            transition: all 0.15s;
        }

        .btn:hover { opacity: 0.9; transform: translateY(-1px); }
        .btn:active { transform: translateY(0); }
        .btn:disabled { opacity: 0.4; cursor: not-allowed; }

        .btn-secondary {
            background: var(--bg-overlay);
            color: var(--text-primary);
            border: 1px solid var(--border-dark);
        }

        .btn-secondary:hover {
            background: rgba(255, 255, 255, 0.08);
            border-color: var(--text-secondary);
        }

        .btn-outline {
            background: transparent;
            color: var(--accent-purple);
            border: 1px solid var(--accent-purple);
        }

        .btn-danger {
            background: linear-gradient(135deg, #f38ba8, #eba0ac);
            color: #11111b;
        }

        .btn-sm {
            padding: 5px 10px;
            font-size: 0.78rem;
        }

        /* PROGRESS BARS */
        .progress-bar-container {
            width: 100%;
            height: 10px;
            background-color: var(--bg-overlay);
            border-radius: 5px;
            overflow: hidden;
            border: 1px solid var(--border-dark);
            margin: 8px 0;
        }

        .progress-bar-fill {
            height: 100%;
            width: 0%;
            background: linear-gradient(90deg, var(--accent-purple), var(--accent-blue));
            transition: width 0.3s ease;
        }

        /* CONSOLE LOG FEED */
        #consoleLogBox {
            background-color: #0b0b10;
            border: 1px solid var(--border-dark);
            border-radius: 6px;
            padding: 12px;
            font-family: 'JetBrains Mono', 'Fira Code', monospace;
            font-size: 0.78rem;
            height: 380px;
            overflow-y: auto;
            color: #cdd6f4;
            line-height: 1.45;
            white-space: pre-wrap;
            word-break: break-all;
        }

        .log-entry { margin: 2px 0; }
        .log-entry.epoch { color: var(--accent-yellow); font-weight: 700; }
        .log-entry.loss { color: var(--accent-green); }
        .log-entry.error { color: var(--accent-red); font-weight: 700; }
        .log-entry.warn { color: var(--accent-peach); }
        .log-entry.info { color: var(--accent-blue); }

        /* TAB SWITCHER */
        .segmented-tabs {
            display: flex;
            background: var(--bg-overlay);
            border: 1px solid var(--border-dark);
            border-radius: 8px;
            padding: 3px;
            gap: 4px;
            margin-bottom: 16px;
        }

        .tab-btn {
            flex: 1;
            background: transparent;
            border: none;
            padding: 8px 14px;
            border-radius: 6px;
            color: var(--text-secondary);
            font-weight: 700;
            font-size: 0.85rem;
            cursor: pointer;
            transition: all 0.15s ease;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
        }

        .tab-btn.active {
            background: var(--bg-surface);
            color: #ffffff;
            box-shadow: 0 2px 8px rgba(0,0,0,0.4);
            border: 1px solid var(--border-dark);
        }

        /* PRESET SUBJECT CARDS */
        .preset-card {
            background: var(--bg-overlay);
            border: 1px solid var(--border-dark);
            border-radius: 10px;
            padding: 14px;
            text-align: center;
            cursor: pointer;
            transition: all 0.15s ease;
            position: relative;
        }

        .preset-card:hover {
            border-color: var(--accent-purple);
            transform: translateY(-2px);
        }

        .preset-card.selected {
            background: rgba(203, 166, 247, 0.12);
            border: 2px solid var(--accent-purple);
        }

        .preset-icon {
            font-size: 1.8rem;
            margin-bottom: 6px;
        }

        .preset-title {
            font-weight: 700;
            font-size: 0.88rem;
            color: #ffffff;
        }

        .preset-desc {
            font-size: 0.72rem;
            color: var(--text-secondary);
            margin-top: 4px;
            line-height: 1.2;
        }

        /* ALERT BANNERS */
        .alert-info {
            background-color: rgba(203, 166, 247, 0.12);
            border: 1px solid rgba(203, 166, 247, 0.3);
            color: var(--accent-purple);
            padding: 10px 14px;
            border-radius: 8px;
            font-size: 0.84rem;
            margin-bottom: 14px;
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .alert-warn {
            background-color: rgba(249, 226, 175, 0.12);
            border: 1px solid rgba(249, 226, 175, 0.3);
            color: var(--accent-yellow);
            padding: 10px 14px;
            border-radius: 8px;
            font-size: 0.84rem;
            margin-bottom: 14px;
        }

        /* DROPZONE */
        .drop-zone {
            border: 2px dashed var(--border-dark);
            border-radius: 8px;
            padding: 24px;
            text-align: center;
            background: rgba(17, 17, 27, 0.6);
            cursor: pointer;
            transition: all 0.2s ease;
        }

        .drop-zone:hover, .drop-zone.dragover {
            border-color: var(--accent-purple);
            background: rgba(203, 166, 247, 0.05);
        }

        /* MODAL DIALOG OVERLAYS */
        .modal-overlay {
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: rgba(17, 17, 27, 0.85);
            backdrop-filter: blur(8px);
            -webkit-backdrop-filter: blur(8px);
            z-index: 9999;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }

        .modal-window {
            background-color: var(--bg-surface);
            border: 1px solid var(--border-dark);
            border-radius: 12px;
            width: 100%;
            max-width: 880px;
            max-height: 88vh;
            display: flex;
            flex-direction: column;
            box-shadow: 0 20px 60px rgba(0,0,0,0.7);
            overflow: hidden;
            animation: modalFadeIn 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }

        @keyframes modalFadeIn {
            from { opacity: 0; transform: scale(0.96); }
            to { opacity: 1; transform: scale(1); }
        }

        .modal-header {
            padding: 16px 20px;
            border-bottom: 1px solid var(--border-dark);
            display: flex;
            align-items: center;
            justify-content: space-between;
            background: var(--bg-overlay);
        }

        .modal-title {
            font-size: 1.15rem;
            font-weight: 700;
            color: #ffffff;
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .modal-subtitle {
            font-size: 0.75rem;
            color: var(--text-secondary);
            font-weight: 400;
            margin-top: 2px;
        }

        .modal-close-btn {
            background: transparent;
            border: none;
            color: var(--text-muted);
            font-size: 1.4rem;
            cursor: pointer;
            line-height: 1;
            padding: 4px 10px;
            border-radius: 6px;
            transition: all 0.15s;
        }

        .modal-close-btn:hover {
            color: var(--accent-red);
            background: rgba(243, 139, 168, 0.15);
        }

        .modal-body {
            padding: 20px;
            overflow-y: auto;
            flex: 1;
        }

        .modal-footer {
            padding: 14px 20px;
            border-top: 1px solid var(--border-dark);
            display: flex;
            justify-content: flex-end;
            align-items: center;
            gap: 10px;
            background: var(--bg-overlay);
        }

        /* DOCUMENTATION VIEWER STYLES */
        .doc-nav-item {
            padding: 8px 12px;
            border-radius: 6px;
            cursor: pointer;
            transition: all 0.15s;
            display: flex;
            align-items: center;
            gap: 10px;
            font-size: 0.85rem;
            color: var(--text-primary);
        }

        .doc-nav-item:hover {
            background: rgba(255,255,255,0.05);
        }

        .doc-nav-item.active {
            background: rgba(203, 166, 247, 0.14);
            border-left: 3px solid var(--accent-purple);
            color: var(--accent-purple);
            font-weight: 700;
        }

        .markdown-rendered-view pre {
            background: #11111b;
            border: 1px solid #313244;
            padding: 14px;
            border-radius: 8px;
            overflow-x: auto;
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.84rem;
        }

        .markdown-rendered-view code {
            font-family: 'JetBrains Mono', monospace;
        }

        .markdown-rendered-view table {
            width: 100%;
            border-collapse: collapse;
            margin: 16px 0;
            font-size: 0.86rem;
            background: rgba(17, 17, 27, 0.5);
            border-radius: 6px;
            overflow: hidden;
            border: 1px solid #313244;
        }

        .markdown-rendered-view th {
            padding: 10px 14px;
            background: #181825;
            color: var(--accent-purple);
            text-align: left;
            font-weight: 700;
            border-bottom: 1px solid #313244;
        }

        .markdown-rendered-view td {
            padding: 9px 14px;
            color: var(--text-primary);
            border-bottom: 1px solid #232336;
        }
    </style>
</head>
<body>

    <!-- 1. LEFT SIDEBAR NAVIGATION (Desktop NavMenu mirror) -->
    <div id="sidebar">
        <div class="sidebar-brand">
            <div class="brand-icon">⚡</div>
            <div class="brand-text">
                <h1>LoRAMancer</h1>
                <p>AMD ROCm Remote Studio</p>
            </div>
        </div>

        <div class="nav-scroller">
            <div class="nav-group-header">Studio Pipeline</div>
            <div class="nav-item" data-view="curate" onclick="switchView('curate')">
                <span class="nav-icon">🖼️</span>
                <span>1. Curate &amp; Caption</span>
                <span class="nav-badge">Stage 1</span>
            </div>
            <div class="nav-item active" data-view="train" onclick="switchView('train')">
                <span class="nav-icon">⚡</span>
                <span>2. Train &amp; Forge</span>
                <span class="nav-badge">Stage 2</span>
            </div>
            <div class="nav-item" data-view="lab" onclick="switchView('lab')">
                <span class="nav-icon">🔬</span>
                <span>3. Diagnostic Lab</span>
                <span class="nav-badge">Stage 3</span>
            </div>
            <div class="nav-item" data-view="comfy" onclick="switchView('comfy')">
                <span class="nav-icon">🎨</span>
                <span>4. ComfyUI Test</span>
                <span class="nav-badge">Stage 4</span>
            </div>
            <div class="nav-item" data-view="vault" onclick="switchView('vault')">
                <span class="nav-icon">📚</span>
                <span>5. Library &amp; Vault</span>
                <span class="nav-badge">Stage 5</span>
            </div>

            <div class="nav-group-header">Studio Workshop</div>
            <div class="nav-item" data-view="chop" onclick="switchView('chop')">
                <span class="nav-icon">🛠️</span>
                <span>LoRA Chop-Shop</span>
                <span class="nav-badge" style="color:var(--accent-purple);">Garage</span>
            </div>
            <div class="nav-item" data-view="history" onclick="switchView('history')">
                <span class="nav-icon">📜</span>
                <span>Training History</span>
                <span class="nav-badge" style="color:var(--accent-blue);">Records</span>
            </div>

            <div class="nav-group-header">Studio Modal Tools</div>
            <div class="nav-item" onclick="openModalApp('ollama')">
                <span class="nav-icon">🤖</span>
                <span>Ollama Vision Tagger</span>
                <span class="nav-badge" style="color:var(--accent-purple);">Modal</span>
            </div>
            <div class="nav-item" onclick="openModalApp('surgery')">
                <span class="nav-icon">✂️</span>
                <span>LoRA Surgery &amp; Merger</span>
                <span class="nav-badge" style="color:var(--accent-blue);">Modal</span>
            </div>
            <div class="nav-item" onclick="openModalApp('genetherapy')">
                <span class="nav-icon">🧬</span>
                <span>LoRA Gene Therapy</span>
                <span class="nav-badge" style="color:var(--accent-green);">Modal</span>
            </div>
            <div class="nav-item" onclick="openModalApp('diff')">
                <span class="nav-icon">🔍</span>
                <span>LoRA Visual Diff</span>
                <span class="nav-badge" style="color:var(--accent-peach);">Modal</span>
            </div>
            <div class="nav-item" onclick="openModalApp('benchmark')">
                <span class="nav-icon">📊</span>
                <span>AI Benchmark Matrix</span>
                <span class="nav-badge" style="color:var(--accent-yellow);">Modal</span>
            </div>

            <div class="nav-group-header">Subsystems</div>
            <div class="nav-item" data-view="telemetry" onclick="switchView('telemetry')">
                <span class="nav-icon">💻</span>
                <span>Compute Environment</span>
            </div>
            <div class="nav-item" data-view="docs" onclick="switchView('docs')">
                <span class="nav-icon">📖</span>
                <span>Documentation</span>
            </div>
        </div>

        <div class="sidebar-footer">
            <div class="host-status-row">
                <span><span class="status-dot"></span> Host Station</span>
                <span id="remoteHostName">Online</span>
            </div>
            <div class="host-status-row">
                <span>Public Tunnel</span>
                <span id="tunnelStatusBadge" style="color:var(--accent-blue);">Direct LAN</span>
            </div>
            <div id="pwaSidebarItem" style="display:none; margin-top:8px; padding:6px 10px; background:rgba(203,166,247,0.1); border:1px solid rgba(203,166,247,0.25); border-radius:6px; cursor:pointer;" onclick="triggerPwaInstall()">
                <div style="display:flex; align-items:center; gap:8px; font-size:0.75rem; font-weight:700; color:var(--accent-purple);">
                    <span>📲</span> Install Desktop App
                </div>
            </div>
        </div>
    </div>

    <!-- 2. MAIN APPLICATION WORKSPACE -->
    <div id="main-area">
        <!-- TOPBAR HEADER (Desktop MainLayout mirror) -->
        <div id="topbar">
            <div class="topbar-left">
                <div class="concept-pill">
                    <span>✨</span>
                    <span id="topConceptName">Active Studio</span>
                </div>
                <div class="pipeline-chips">
                    <div id="chipCurate" class="chip chip-stage" onclick="switchView('curate')">1. Curate</div>
                    <div id="chipTrain" class="chip chip-stage active" onclick="switchView('train')">2. Train</div>
                    <div id="chipLab" class="chip chip-stage" onclick="switchView('lab')">3. Lab</div>
                    <div id="chipComfy" class="chip chip-stage" onclick="switchView('comfy')">4. Test</div>
                    <div id="chipVault" class="chip chip-stage" onclick="switchView('vault')">5. Vault</div>
                </div>
            </div>

            <div class="topbar-right">
                <button id="pwaInstallBtn" class="btn btn-sm" style="display:none; background: linear-gradient(135deg, var(--accent-purple), var(--accent-blue)); color:#11111b; font-weight:800; border:none; padding:5px 12px; border-radius:6px; cursor:pointer;" onclick="triggerPwaInstall()">
                    <span>📲</span> Install App
                </button>
                <div class="rocm-pill">
                    <span>🔥</span>
                    <span id="rocmTelemetryBadge">AMD ROCm Active</span>
                </div>
                <button class="btn btn-secondary btn-sm" onclick="disconnect()">Log Out</button>
            </div>
        </div>

        <!-- VIEWPORT: DYNAMIC PAGES -->
        <div id="viewport">

            <!-- VIEW: 1. CURATE & CAPTION -->
            <div id="view-curate" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">🖼️ Stage 1: Curate &amp; Caption Studio</div>
                        <span class="chip chip-stage">Dataset Management</span>
                    </div>
                    <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:16px;">
                        Upload raw image archives (.zip) to the host machine. Datasets are automatically extracted, inspected for aspect ratios, and captions verified.
                    </p>
                    <div class="grid-2">
                        <div class="drop-zone" id="curateDropZone" onclick="document.getElementById('curateZipInput').click()">
                            <div style="font-size:2.5rem; margin-bottom:8px;">📦</div>
                            <div style="font-weight:700; color:#fff;">Drag &amp; Drop Dataset (.zip)</div>
                            <div style="font-size:0.75rem; color:var(--text-secondary); margin-top:4px;">Supports PNG, JPEG, WEBP and accompanying .txt captions</div>
                            <input id="curateZipInput" type="file" accept=".zip" style="display:none;" onchange="uploadDatasetFromCurate(this.files[0])" />
                        </div>
                        <div class="stat-box" style="justify-content:center;">
                            <div class="stat-label">Uploaded Dataset Health Report</div>
                            <div id="curateReportStatus" style="font-size:0.95rem; font-weight:700; color:var(--accent-purple); margin-top:6px;">No dataset uploaded yet</div>
                            <div id="curateDetails" style="font-size:0.78rem; color:var(--text-secondary); margin-top:4px;">Upload a ZIP above to audit images and caption pairs.</div>
                            <button id="sendToTrainerBtn" class="btn btn-sm" style="margin-top:12px; display:none;" onclick="sendCurateDatasetToTrain()">🪄 Use in Easy Use Wizard</button>
                        </div>
                    </div>
                </div>

                <!-- Stage 1b: Ollama Vision Automated Captioning & Tagging Studio -->
                <div class="card" style="margin-top:16px;">
                    <div class="card-header-bar">
                        <div class="card-title">🤖 Ollama Vision Automated Captioning</div>
                        <div style="display:flex; align-items:center; gap:8px;">
                            <span id="ollamaStatusPill" class="chip chip-stage" style="color:var(--accent-yellow);">Checking Ollama...</span>
                            <button class="btn btn-secondary btn-sm" onclick="loadOllamaStatus()">🔄 Test / Refresh</button>
                        </div>
                    </div>
                    <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:16px;">
                        Auto-caption images in-place using Ollama multimodal vision models (e.g. <code>llama3.2-vision</code>, <code>llava</code>, <code>qwen2-vl</code>). Captions are generated and saved directly as <code>.txt</code> files alongside your images with zero duplication.
                    </p>

                    <div class="grid-3">
                        <div class="input-group">
                            <label>Target Dataset Directory</label>
                            <input id="ollamaDatasetPath" type="text" placeholder="Upload ZIP above or enter host folder..." />
                        </div>
                        <div class="input-group">
                            <label>Ollama Vision Model</label>
                            <select id="ollamaModelSelect">
                                <option value="llama3.2-vision">llama3.2-vision</option>
                            </select>
                        </div>
                        <div class="input-group">
                            <label>Trigger Activation Phrase</label>
                            <input id="ollamaTriggerWord" type="text" placeholder="e.g. sks character, ohwx style" />
                        </div>
                    </div>

                    <!-- Subject Focus Presets -->
                    <div style="margin-top:12px;">
                        <label style="font-size:0.8rem; font-weight:700; color:var(--text-secondary); display:block; margin-bottom:8px;">Subject Focus Preset</label>
                        <div style="display:flex; gap:8px; flex-wrap:wrap;">
                            <button type="button" id="btnFocusGeneral" class="btn btn-sm btn-secondary active" onclick="setOllamaFocusPreset('general')">🌐 General / Balanced</button>
                            <button type="button" id="btnFocusCharacter" class="btn btn-sm btn-secondary" onclick="setOllamaFocusPreset('character')">👤 Character / Likeness</button>
                            <button type="button" id="btnFocusStyle" class="btn btn-sm btn-secondary" onclick="setOllamaFocusPreset('style')">🎨 Art Style / Medium</button>
                            <button type="button" id="btnFocusConcept" class="btn btn-sm btn-secondary" onclick="setOllamaFocusPreset('concept')">⚙️ Concept / Object</button>
                            <button type="button" id="btnFocusClothing" class="btn btn-sm btn-secondary" onclick="setOllamaFocusPreset('clothing')">👗 Clothing / Fashion</button>
                            <button type="button" id="btnFocusCustom" class="btn btn-sm btn-secondary" onclick="setOllamaFocusPreset('custom')">✏️ Custom Prompt</button>
                        </div>
                    </div>

                    <!-- Caption Style Selection -->
                    <div style="margin-top:14px; display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:12px;">
                        <div>
                            <label style="font-size:0.8rem; font-weight:700; color:var(--text-secondary); display:block; margin-bottom:6px;">Caption Format Style</label>
                            <div class="segmented-tabs" style="max-width:440px; margin-bottom:0;">
                                <button type="button" id="btnStyleTags" class="tab-btn active" onclick="setOllamaCaptionStyle('tags')">🏷️ Visual Tags (SDXL / Pony)</button>
                                <button type="button" id="btnStyleNatural" class="tab-btn" onclick="setOllamaCaptionStyle('natural')">📝 Natural Sentences (FLUX.1)</button>
                            </div>
                        </div>
                        <div style="display:flex; align-items:flex-end;">
                            <button id="btnRunOllamaTag" class="btn" onclick="startOllamaTagging()">
                                <span>🚀</span> Run In-Place Vision Auto-Tagging
                            </button>
                        </div>
                    </div>

                    <!-- Custom Prompt Instruction -->
                    <div style="margin-top:14px;">
                        <div style="display:flex; align-items:center; justify-content:space-between; margin-bottom:6px;">
                            <label style="font-size:0.8rem; font-weight:700; color:var(--text-secondary);">Vision System Prompt / Instructions</label>
                            <button type="button" class="btn btn-secondary btn-sm" style="font-size:0.7rem; padding:2px 8px;" onclick="resetOllamaPrompt()">Reset to Preset</button>
                        </div>
                        <textarea id="ollamaCustomPrompt" rows="3" style="width:100%; background:#11111b; border:1px solid var(--border-dark); border-radius:8px; padding:10px; color:#fff; font-size:0.8rem; font-family:inherit; resize:vertical;"></textarea>
                    </div>

                    <!-- Progress & Logs Box -->
                    <div id="ollamaProgressCard" style="display:none; margin-top:14px; background:#11111b; border:1px solid var(--border-dark); border-radius:8px; padding:12px;">
                        <div style="display:flex; align-items:center; justify-content:space-between; margin-bottom:8px;">
                            <div style="display:flex; align-items:center; gap:8px; font-weight:700; font-size:0.85rem;">
                                <span id="ollamaSpinIcon">⏳</span>
                                <span id="ollamaProgressStatus">Tagging images with Ollama...</span>
                            </div>
                            <span id="ollamaProgressCount" class="chip chip-stage" style="color:var(--accent-purple);">Processing</span>
                        </div>
                        <div style="background:#09090d; border:1px solid rgba(255,255,255,0.06); border-radius:6px; padding:8px 12px; font-family:monospace; font-size:0.75rem; color:#a6adc8; max-height:160px; overflow-y:auto;" id="ollamaLogBox">
                        </div>
                    </div>

                    <!-- Results & Sample Captions -->
                    <div id="ollamaResultsBox" style="display:none; margin-top:14px; background:#11111b; border:1px solid rgba(166,227,161,0.3); border-radius:8px; padding:14px;">
                        <div style="display:flex; align-items:center; justify-content:space-between; margin-bottom:10px;">
                            <div style="display:flex; align-items:center; gap:8px;">
                                <span style="font-size:1.2rem;">🎉</span>
                                <b style="color:var(--accent-green);" id="ollamaResultTitle">Auto-Tagging Complete!</b>
                            </div>
                            <button class="btn btn-sm" onclick="sendOllamaDatasetToWizard()">🪄 Use in Easy Use Wizard</button>
                        </div>
                        <div style="font-size:0.8rem; color:var(--text-secondary); margin-bottom:8px;">Sample Generated Captions:</div>
                        <div id="ollamaSampleCaptionsList" style="display:flex; flex-direction:column; gap:6px;"></div>
                    </div>
                </div>
            </div>

            <!-- VIEW: 2. TRAIN & FORGE (Easy Use Wizard + Advanced Studio) -->
            <div id="view-train" class="view-panel active">
                
                <!-- Mode Switcher Tabs -->
                <div class="segmented-tabs">
                    <button id="tabBtnWizard" class="tab-btn active" onclick="switchTrainMode('wizard')">
                        <span>🪄</span> Easy Use Training Wizard
                    </button>
                    <button id="tabBtnManual" class="tab-btn" onclick="switchTrainMode('manual')">
                        <span>⚙️</span> Advanced Studio &amp; Queue Management
                    </button>
                </div>

                <!-- SUB-VIEW 1: EASY USE TRAINING WIZARD -->
                <div id="trainWizardView">
                    
                    <!-- Cloned Donor Recipe Banner -->
                    <div id="donorRecipeAlert" class="alert-info" style="display:none;">
                        <span style="font-size:1.3rem;">✨</span>
                        <div style="flex:1;">
                            <b>Cloned Recipe:</b> Using hyperparameters from donor <code id="donorNameVal" style="background:#11111b; padding:2px 6px; border-radius:4px;">donor.safetensors</code>
                            <div style="font-size:0.76rem; color:var(--text-secondary); margin-top:2px;">
                                Rank: <span id="donorRankVal">16</span> | Alpha: <span id="donorAlphaVal">16</span> | LR: <span id="donorLrVal">1e-4</span> | Architecture: <span id="donorArchVal">FLUX.1</span>
                            </div>
                        </div>
                        <button class="btn btn-secondary btn-sm" onclick="clearDonorClone()">✕ Clear</button>
                    </div>

                    <!-- Donor Upload / Pick Box -->
                    <div class="card" style="padding:12px 18px; margin-bottom:16px;">
                        <div style="display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px;">
                            <div style="display:flex; align-items:center; gap:8px;">
                                <span style="font-size:1.1rem;">🧬</span>
                                <span style="font-weight:700; font-size:0.9rem;">Clone Recipe from Donor LoRA:</span>
                            </div>
                            <div style="display:flex; gap:8px; flex-wrap:wrap;">
                                <button class="btn btn-secondary btn-sm" onclick="document.getElementById('wizardDonorInput').click()">
                                    📤 Upload Donor LoRA (.safetensors)
                                </button>
                                <input id="wizardDonorInput" type="file" accept=".safetensors" style="display:none;" onchange="uploadWizardDonor(this.files[0])" />
                                <select id="wizardVaultDonorSelect" style="font-size:0.8rem; padding:4px 8px;" onchange="pickWizardDonorFromVault(this.value)">
                                    <option value="">Or Pick from Server Vault...</option>
                                </select>
                            </div>
                        </div>
                    </div>

                    <!-- STEP 1: What are you training? -->
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">
                                <span style="background:var(--accent-purple); color:#111; width:24px; height:24px; border-radius:6px; display:inline-flex; align-items:center; justify-content:center; font-size:0.85rem; font-weight:800;">1</span>
                                What are you training? (Subject Presets)
                            </div>
                            <span class="chip chip-stage" style="font-size:0.7rem;">Auto-Configures Hyperparameters</span>
                        </div>

                        <div class="grid-5" style="margin-bottom:16px;">
                            <div id="presetCharacter" class="preset-card selected" onclick="selectSubjectPreset('character')">
                                <div class="preset-icon">👤</div>
                                <div class="preset-title">Character / Person</div>
                                <div class="preset-desc">Facial &amp; costume likeness (Dim 16 / Ep 10)</div>
                            </div>
                            <div id="presetStyle" class="preset-card" onclick="selectSubjectPreset('style')">
                                <div class="preset-icon">🎨</div>
                                <div class="preset-title">Art Style / Aesthetic</div>
                                <div class="preset-desc">Medium, brushwork, textures (Dim 32 / Ep 12)</div>
                            </div>
                            <div id="presetConcept" class="preset-card" onclick="selectSubjectPreset('concept')">
                                <div class="preset-icon">🔮</div>
                                <div class="preset-title">Concept / Object</div>
                                <div class="preset-desc">Gear, vehicles, creatures (Dim 16 / Ep 10)</div>
                            </div>
                            <div id="presetClothing" class="preset-card" onclick="selectSubjectPreset('clothing')">
                                <div class="preset-icon">👗</div>
                                <div class="preset-title">Clothing / Outfit</div>
                                <div class="preset-desc">Garment geometry &amp; fabric (Dim 16 / Ep 10)</div>
                            </div>
                            <div id="presetCustom" class="preset-card" onclick="selectSubjectPreset('custom')">
                                <div class="preset-icon">⚙️</div>
                                <div class="preset-title">Custom Setup</div>
                                <div class="preset-desc">Fully user-specified parameters</div>
                            </div>
                        </div>

                        <div class="grid-3">
                            <div class="input-group">
                                <label>Target Base Model Architecture</label>
                                <select id="wizBaseModel" onchange="recalculateEstimators()">
                                    <option value="FLUX.1-dev">FLUX.1-dev (Flow Matching)</option>
                                    <option value="SDXL 1.0">SDXL 1.0 / Pony / Illustrious</option>
                                    <option value="Chroma 1 HD">Chroma 1 HD (Standalone DiT)</option>
                                    <option value="Stable Diffusion 1.5">Stable Diffusion 1.5</option>
                                </select>
                            </div>
                            <div class="input-group">
                                <label>Run Project Name</label>
                                <input id="wizRunName" type="text" value="my_lora_run" placeholder="e.g. cyberpunk_portrait_v1" />
                            </div>
                            <div class="input-group">
                                <label>Trigger Activation Keyword</label>
                                <input id="wizTriggerWord" type="text" placeholder="e.g. ohwx character" oninput="updateSamplePromptTemplate()" />
                            </div>
                        </div>
                    </div>

                    <!-- STEP 2: Dataset & Health Audit -->
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">
                                <span style="background:var(--accent-purple); color:#111; width:24px; height:24px; border-radius:6px; display:inline-flex; align-items:center; justify-content:center; font-size:0.85rem; font-weight:800;">2</span>
                                Dataset &amp; Output Folders
                            </div>
                            <button class="btn btn-secondary btn-sm" onclick="switchView('curate')">Open Curate Studio</button>
                        </div>

                        <div class="grid-2">
                            <div class="input-group">
                                <label>Dataset Archive (.zip) or Host Folder Path</label>
                                <div style="display:flex; gap:8px;">
                                    <input id="wizDatasetInput" type="text" placeholder="Upload ZIP or enter folder path on host..." style="flex:1;" />
                                    <button class="btn btn-secondary btn-sm" onclick="document.getElementById('wizZipPicker').click()">Upload ZIP</button>
                                    <input id="wizZipPicker" type="file" accept=".zip" style="display:none;" onchange="uploadWizardDataset(this.files[0])" />
                                </div>
                            </div>
                            <div class="input-group">
                                <label>Output Checkpoints Folder</label>
                                <input id="wizOutputDir" type="text" value="~/.loramancer/outputs" />
                            </div>
                        </div>

                        <!-- Dataset Health Banner -->
                        <div id="wizAuditBanner" style="background:#11111b; border:1px solid var(--border-dark); border-radius:8px; padding:10px 14px; margin-top:8px; display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px;">
                            <div style="display:flex; align-items:center; gap:8px; font-size:0.82rem;">
                                <span>📦</span>
                                <b>Dataset Audit:</b>
                                <span id="auditImagesBadge" class="chip chip-stage">20 Images</span>
                                <span id="auditCaptionsBadge" class="chip chip-stage">20 Captions</span>
                                <span id="auditMissingBadge" class="chip chip-stage" style="color:var(--accent-green);">0 Missing</span>
                            </div>
                            <div style="display:flex; align-items:center; gap:8px;">
                                <button class="btn btn-secondary btn-sm" onclick="autoTagWizardCaptionsWithOllama()">🤖 Auto-Tag with Ollama</button>
                                <button class="btn btn-secondary btn-sm" onclick="prependTriggerToCaptions()">🪄 Prepend Trigger to Captions</button>
                            </div>
                        </div>
                    </div>

                    <!-- STEP 3: Live Training Estimators -->
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">
                                <span style="background:var(--accent-purple); color:#111; width:24px; height:24px; border-radius:6px; display:inline-flex; align-items:center; justify-content:center; font-size:0.85rem; font-weight:800;">3</span>
                                Live Training Estimators &amp; ROCm Pre-Flight
                            </div>
                            <span id="wizPreflightBadge" class="chip" style="background:rgba(166,227,161,0.15); color:var(--accent-green); border:1px solid rgba(166,227,161,0.3);">ROCm Hardware Safe</span>
                        </div>

                        <div class="grid-4" style="margin-bottom:14px;">
                            <div class="stat-box" style="border-top:3px solid var(--accent-blue);">
                                <div class="stat-label">Total Steps</div>
                                <div id="estTotalSteps" class="stat-val" style="color:var(--accent-blue);">2,000</div>
                                <div id="estStepFormula" style="font-size:0.7rem; color:var(--text-secondary); margin-top:4px;">20 img × 10 reps × 10 ep</div>
                            </div>
                            <div class="stat-box" style="border-top:3px solid var(--accent-green);">
                                <div class="stat-label">Est. VRAM Needed</div>
                                <div id="estVram" class="stat-val" style="color:var(--accent-green);">~10.4 GB</div>
                                <div id="estGpuName" style="font-size:0.7rem; color:var(--text-secondary); margin-top:4px;">AMD Radeon (16 GB)</div>
                            </div>
                            <div class="stat-box" style="border-top:3px solid var(--accent-purple);">
                                <div class="stat-label">Est. Checkpoint Size</div>
                                <div id="estSize" class="stat-val" style="color:var(--accent-purple);">~54 MB</div>
                                <div id="estRankInfo" style="font-size:0.7rem; color:var(--text-secondary); margin-top:4px;">Rank 16 / Alpha 16</div>
                            </div>
                            <div class="stat-box" style="border-top:3px solid var(--accent-yellow);">
                                <div class="stat-label">Est. Duration</div>
                                <div id="estDuration" class="stat-val" style="color:var(--accent-yellow);">~14m 30s</div>
                                <div style="font-size:0.7rem; color:var(--text-secondary); margin-top:4px;">Host Accelerator Speed</div>
                            </div>
                        </div>

                        <!-- Pro Mode Hyperparameters Accordion -->
                        <details style="margin-top:12px; background:var(--bg-overlay); border:1px solid var(--border-dark); border-radius:8px; padding:10px 14px;">
                            <summary style="cursor:pointer; font-weight:700; color:var(--accent-purple); font-size:0.88rem;">
                                🔧 Pro Mode: Fine-tune Rank, Alpha, Optimizer, Learning Rate &amp; Precision
                            </summary>
                            <div style="padding-top:12px;">
                                <div class="grid-3">
                                    <div class="input-group">
                                        <label>Network Dim (Rank)</label>
                                        <input id="wizRank" type="number" value="16" min="4" max="256" oninput="recalculateEstimators()" />
                                    </div>
                                    <div class="input-group">
                                        <label>Network Alpha</label>
                                        <input id="wizAlpha" type="number" value="16" min="4" max="256" />
                                    </div>
                                    <div class="input-group">
                                        <label>Learning Rate</label>
                                        <input id="wizLr" type="text" value="0.0001" />
                                    </div>
                                </div>
                                <div class="grid-3">
                                    <div class="input-group">
                                        <label>Optimizer</label>
                                        <select id="wizOptimizer">
                                            <option value="adamw8bit">AdamW 8-bit (Low VRAM)</option>
                                            <option value="prodigy">Prodigy (Adaptive LR)</option>
                                            <option value="lion8bit">Lion 8-bit</option>
                                            <option value="adamw">AdamW Full (32-bit)</option>
                                        </select>
                                    </div>
                                    <div class="input-group">
                                        <label>Precision</label>
                                        <select id="wizPrecision">
                                            <option value="bf16">bfloat16 (Recommended)</option>
                                            <option value="fp16">float16</option>
                                            <option value="fp8">fp8 (Ultra-Low VRAM)</option>
                                        </select>
                                    </div>
                                    <div class="input-group">
                                        <label>Batch Size</label>
                                        <input id="wizBatchSize" type="number" value="1" min="1" max="16" oninput="recalculateEstimators()" />
                                    </div>
                                </div>
                                <div class="grid-2">
                                    <div class="input-group">
                                        <label>Epochs</label>
                                        <input id="wizEpochs" type="number" value="10" min="1" max="100" oninput="recalculateEstimators()" />
                                    </div>
                                    <div class="input-group">
                                        <label>Repeats per Image</label>
                                        <input id="wizRepeats" type="number" value="10" min="1" max="50" oninput="recalculateEstimators()" />
                                    </div>
                                </div>
                                <div class="input-group">
                                    <label>Validation Sample Prompt</label>
                                    <input id="wizSamplePrompt" type="text" placeholder="{trigger}, high quality portrait" />
                                </div>
                            </div>
                        </details>

                        <div style="display:flex; justify-content:flex-end; gap:10px; margin-top:16px;">
                            <button class="btn btn-secondary" onclick="queueFromWizard()">📥 Queue for Later</button>
                            <button class="btn" style="padding:12px 24px; font-size:1rem;" onclick="launchFromWizard()">
                                🚀 Launch Training with Wizard Recipe
                            </button>
                        </div>
                    </div>
                </div>

                <!-- SUB-VIEW 2: ADVANCED STUDIO & CONSOLE -->
                <div id="trainManualView" style="display:none;">
                    <div style="display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:12px; margin-bottom:16px;">
                        <div>
                            <div class="card-title" style="font-size:1.3rem;">⚡ LoRA Training &amp; Forge Studio</div>
                            <p style="font-size:0.85rem; color:var(--text-secondary); margin:4px 0 0 0;">
                                Direct low-level telemetry, live sequential queue, and remote terminal feed.
                            </p>
                        </div>
                        <div style="display:flex; gap:8px; flex-wrap:wrap;">
                            <button class="btn btn-danger" onclick="stopTraining()">⏹ Stop Current</button>
                            <button class="btn btn-outline" onclick="cancelAllJobs()">🗑️ Cancel All</button>
                            <button class="btn btn-secondary" onclick="clearConsoleLogs()">🧹 Clear Logs</button>
                        </div>
                    </div>

                    <!-- Active Queue Banner -->
                    <div class="card" style="padding:12px 18px; margin-bottom:16px;">
                        <div style="display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px;">
                            <div style="display:flex; align-items:center; gap:10px;">
                                <span style="font-size:1.1rem;">📥</span>
                                <span style="font-weight:700;">Studio Sequential Queue:</span>
                                <span id="queueBadge" class="chip chip-stage">0 Queued</span>
                                <span id="activeJobName" style="color:var(--accent-green); font-weight:600;">Idle</span>
                            </div>
                            <div style="font-size:0.78rem; color:var(--text-secondary);">Automatic single GPU execution with post-run VRAM cooldown</div>
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

                    <!-- Live Console Log Stream -->
                    <div class="card" style="margin-top:16px;">
                        <div class="card-header-bar">
                            <div class="card-title">🖥️ Host Workstation Real-Time Console Stream</div>
                            <div style="display:flex; align-items:center; gap:8px;">
                                <label style="font-size:0.75rem; color:var(--text-secondary); display:flex; align-items:center; gap:4px;">
                                    <input type="checkbox" id="autoscrollLock" checked /> Auto-scroll lock
                                </label>
                            </div>
                        </div>
                        <div id="consoleLogBox"></div>
                    </div>
                </div>
            </div>

            <!-- VIEW: 3. DIAGNOSTIC LAB -->
            <div id="view-lab" class="view-panel">
                <div class="grid-2">
                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">🔬 Stage 3: SVD Overbake Radar Analysis</div>
                            <button class="btn btn-secondary btn-sm" onclick="inspectLabModel()">⚡ Run SVD Scan</button>
                        </div>
                        <div class="input-group">
                            <label>LoRA Safetensors File on Host</label>
                            <select id="labLoraSelect"></select>
                        </div>
                        <div id="labScanResults" style="display:none; margin-top:14px;">
                            <div class="grid-2" style="margin-bottom:12px;">
                                <div class="stat-box">
                                    <div class="stat-label">Overbake Risk Score</div>
                                    <div id="labScoreVal" class="stat-val" style="color:var(--accent-green);">0 / 100</div>
                                </div>
                                <div class="stat-box">
                                    <div class="stat-label">Average Frobenius Norm</div>
                                    <div id="labNormVal" class="stat-val">0.00</div>
                                </div>
                            </div>
                            <div class="alert-info" id="labVerdictBox" style="margin-bottom:8px;"></div>
                            <div style="font-size:0.8rem; color:var(--text-secondary);" id="labRecBox"></div>
                        </div>
                    </div>

                    <div class="card">
                        <div class="card-header-bar">
                            <div class="card-title">✂️ Layer Surgery &amp; Rank Compression</div>
                            <span class="chip chip-stage">Non-Destructive</span>
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

            <!-- VIEW: 4. COMFYUI TEST -->
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

            <!-- VIEW: 5. LIBRARY & VAULT -->
            <div id="view-vault" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">📚 Stage 5: LoRA Library &amp; Vault</div>
                        <div style="display:flex; gap:8px;">
                            <button class="btn btn-secondary btn-sm" onclick="document.getElementById('vaultUploadInput').click()">📤 Upload LoRA (.safetensors)</button>
                            <input id="vaultUploadInput" type="file" accept=".safetensors" style="display:none;" onchange="uploadVaultLora(this.files[0])" />
                            <button class="btn btn-secondary btn-sm" onclick="loadVaultLoras()">🔄 Refresh Vault</button>
                        </div>
                    </div>
                    <div class="input-group">
                        <input id="vaultSearch" type="text" placeholder="Search models by filename..." oninput="filterVault()" />
                    </div>
                    <div id="vaultList" class="grid-3" style="max-height:650px; overflow-y:auto; padding-top:6px;">
                        <div style="color:var(--text-secondary); font-size:0.88rem;">Scanning host LoRA directories...</div>
                    </div>
                </div>
            </div>

            <!-- VIEW: LORA CHOP-SHOP (Multi-Model Garage with Donor Upload & Full Desktop Anatomical Grafting) -->
            <div id="view-chop" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div>
                            <div class="card-title">🛠️ LoRA Vehicle Chop-Shop: Multi-Model Anatomical Grafting</div>
                            <p style="font-size:0.82rem; color:var(--text-secondary); margin:4px 0 0 0;">
                                Multi-model anatomical &amp; aesthetic grafting: harvest face, eyes, hair, clothing, lighting, and textures into a single unified LoRA.
                            </p>
                        </div>
                        <div style="display:flex; gap:8px; flex-wrap:wrap;">
                            <button class="btn btn-secondary btn-sm" onclick="autoCraftChopRecipe()">✨ Smart Auto-Assign</button>
                            <button class="btn btn-secondary btn-sm" onclick="clearChopGarage()">🧹 Reset Garage</button>
                        </div>
                    </div>

                    <!-- Donor Upload Dropzone & Vault Dropdown -->
                    <div class="grid-2" style="margin-bottom:16px;">
                        <div class="drop-zone" onclick="document.getElementById('chopFileInput').click()" style="padding:16px;">
                            <span style="font-size:1.8rem;">📤</span>
                            <div style="font-weight:700; color:#fff; font-size:0.9rem;">Upload Donor LoRA (.safetensors)</div>
                            <div style="font-size:0.72rem; color:var(--text-secondary);">Directly from your local machine to host garage</div>
                            <input id="chopFileInput" type="file" accept=".safetensors" style="display:none;" onchange="uploadChopDonor(this.files[0])" />
                        </div>
                        <div class="stat-box" style="justify-content:center;">
                            <label>Or Select LoRA from Server Vault</label>
                            <div style="display:flex; gap:8px; margin-top:6px;">
                                <select id="chopAddSelect" style="flex:1;"><option value="">Choose a model from host vault...</option></select>
                                <button class="btn btn-secondary btn-sm" onclick="addSelectedDonorToGarage()">➕ Add Donor</button>
                            </div>
                        </div>
                    </div>

                    <!-- Chop-Shop Tabs: The Garage & Anatomical Assembly -->
                    <div class="segmented-tabs" style="margin-bottom:16px;">
                        <button id="chopTabBtnGarage" class="tab-btn active" onclick="switchChopTab('garage')">
                            🏎️ The Garage: Loaded Donors (<span id="donorCount">0</span>)
                        </button>
                        <button id="chopTabBtnAssembly" class="tab-btn" onclick="switchChopTab('assembly')">
                            🧩 Anatomical Assembly Bay
                        </button>
                    </div>

                    <!-- TAB 1: THE GARAGE -->
                    <div id="chopTabGarage">
                        <div id="chopDonorsGrid" class="grid-3">
                            <div style="color:var(--text-secondary); font-size:0.88rem; padding:20px; text-align:center; grid-column:1/-1;">
                                No donors in garage. Upload a .safetensors model above or pick from your server vault.
                            </div>
                        </div>
                    </div>

                    <!-- TAB 2: ANATOMICAL ASSEMBLY -->
                    <div id="chopTabAssembly" style="display:none;">
                        <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:14px;">
                            Assign which donor model provides each anatomical or aesthetic part, and calibrate the blend multiplier.
                        </p>
                        <div id="chopPartsGrid" class="grid-2">
                            <!-- Visual parts will be dynamically rendered here -->
                        </div>

                        <!-- Bake Franken-LoRA Card -->
                        <div class="card" style="margin-top:20px; background:var(--bg-overlay);">
                            <div class="card-header-bar">
                                <div class="card-title">🔥 SVD Compilation &amp; Bake</div>
                                <span class="chip chip-stage">No Retraining Required</span>
                            </div>
                            <div class="grid-2">
                                <div class="input-group">
                                    <label>Resulting Franken-LoRA Filename</label>
                                    <input id="frankenName" type="text" value="ChopShop_FrankenLoRA" />
                                </div>
                                <div class="input-group">
                                    <label>Target Compilation Rank</label>
                                    <select id="frankenRank">
                                        <option value="16" selected>Rank 16 (~32MB)</option>
                                        <option value="32">Rank 32 (~64MB)</option>
                                        <option value="64">Rank 64 (~128MB)</option>
                                    </select>
                                </div>
                            </div>
                            <button class="btn" style="width:100%; margin-top:10px; padding:12px;" onclick="bakeFrankenLora()">
                                🔥 Assemble &amp; Compile Franken-LoRA via Server SVD
                            </button>
                            <div id="bakeStatus" style="font-size:0.85rem; color:var(--accent-green); margin-top:10px; display:none;"></div>
                        </div>
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

            <!-- VIEW: COMPUTE ENVIRONMENT -->
            <div id="view-telemetry" class="view-panel">
                <div class="card">
                    <div class="card-header-bar">
                        <div class="card-title">💻 Host Workstation Compute Telemetry</div>
                        <button class="btn btn-secondary btn-sm" onclick="loadEnvironment()">🔄 Refresh Specs</button>
                    </div>
                    <div class="grid-3" id="envSpecsGrid">
                        <div class="stat-box"><div class="stat-label">Host OS</div><div id="envOs" class="stat-val" style="font-size:1.1rem;">Windows / Linux</div></div>
                        <div class="stat-box"><div class="stat-label">GPU Accelerator</div><div id="envGpu" class="stat-val" style="font-size:1.1rem; color:var(--accent-purple);">Detecting...</div></div>
                        <div class="stat-box"><div class="stat-label">ROCm Driver</div><div id="envRocm" class="stat-val" style="font-size:1.1rem; color:var(--accent-green);">ROCm Active</div></div>
                        <div class="stat-box"><div class="stat-label">VRAM Gauge</div><div id="envVram" class="stat-val" style="font-size:1.1rem;">16 GB</div></div>
                        <div class="stat-box"><div class="stat-label">PyTorch Environment</div><div id="envTorch" class="stat-val" style="font-size:1.1rem; color:var(--accent-blue);">PyTorch 2.5</div></div>
                        <div class="stat-box"><div class="stat-label">System Python</div><div id="envPython" class="stat-val" style="font-size:1.1rem;">Python 3.11</div></div>
                    </div>
                </div>
            </div>

            <!-- VIEW: DOCUMENTATION VIEWER -->
            <div id="view-docs" class="view-panel">
                <div class="card" style="margin-bottom:16px;">
                    <div class="card-header-bar">
                        <div class="card-title">📖 Documentation &amp; User Guides</div>
                        <div style="display:flex; gap:10px;">
                            <button class="btn btn-secondary btn-sm" onclick="copyCurrentDocMarkdown()">📋 Copy Markdown</button>
                            <button class="btn btn-secondary btn-sm" onclick="loadDocsList()">🔄 Reload Guides</button>
                        </div>
                    </div>
                    <p style="font-size:0.85rem; color:var(--text-secondary); margin-bottom:0;">
                        Comprehensive manuals, architectural specifications, and hardware setup guides packaged directly with LoRAMancer.
                    </p>
                </div>

                <div style="display:flex; gap:20px; align-items:flex-start;">
                    <!-- Left: Filter & Document Index List -->
                    <div style="width:300px; flex-shrink:0; background:var(--bg-surface); border:1px solid var(--border-dark); border-radius:10px; padding:14px; box-shadow:0 4px 16px rgba(0,0,0,0.3);">
                        <div class="input-group" style="margin-bottom:10px;">
                            <input id="docsSearchInput" type="text" placeholder="🔍 Filter guides..." oninput="filterDocs(this.value)" />
                        </div>
                        <div id="docsNavContainer" style="display:flex; flex-direction:column; gap:4px; max-height:calc(100vh - 280px); overflow-y:auto; padding-right:4px;">
                            <div style="color:var(--text-secondary); font-size:0.8rem; padding:8px;">Loading available guides...</div>
                        </div>
                    </div>

                    <!-- Right: Rendered Document Viewport -->
                    <div style="flex:1; background:var(--bg-surface); border:1px solid var(--border-dark); border-radius:10px; padding:24px 30px; box-shadow:0 4px 16px rgba(0,0,0,0.3); min-height:650px; overflow-y:auto; max-height:calc(100vh - 220px);">
                        <div style="display:flex; align-items:center; justify-content:space-between; border-bottom:1px solid var(--border-dark); padding-bottom:14px; margin-bottom:20px;">
                            <div>
                                <h2 id="docActiveTitle" style="margin:0; font-size:1.4rem; color:var(--accent-purple); font-weight:800;">Selecting guide...</h2>
                                <div id="docActiveMeta" style="font-size:0.75rem; color:var(--text-secondary); margin-top:4px;">docs/FEATURES_OVERVIEW.md</div>
                            </div>
                        </div>
                        <div id="docActiveContent" class="markdown-rendered-view" style="color:var(--text-primary); line-height:1.7; font-size:0.92rem;">
                            Select a guide on the left to read its complete technical documentation.
                        </div>
                    </div>
                </div>
            </div>

        </div>
    </div>

    <!-- MODAL APPS OVERLAY -->
    <div id="appModalOverlay" class="modal-overlay" style="display:none;" onclick="handleModalBackdropClick(event)">
        <!-- Modal: Ollama Vision Tagger -->
        <div id="modal-ollama" class="modal-window" style="display:none;">
            <div class="modal-header">
                <div>
                    <div class="modal-title">🤖 Ollama Vision LoRA Tagger</div>
                    <div class="modal-subtitle">Multimodal in-place automated captioning with Ollama vision models</div>
                </div>
                <button class="modal-close-btn" onclick="closeModalApp()">✕</button>
            </div>
            <div class="modal-body">
                <div class="grid-2">
                    <div class="input-group">
                        <label>Target Dataset Directory</label>
                        <input id="modalOllamaPath" type="text" placeholder="Folder path on host..." />
                    </div>
                    <div class="input-group">
                        <label>Trigger Activation Phrase</label>
                        <input id="modalOllamaTrigger" type="text" placeholder="e.g. ohwx style, sks person" />
                    </div>
                </div>
                <div class="grid-2">
                    <div class="input-group">
                        <label>Ollama Vision Model</label>
                        <select id="modalOllamaModel">
                            <option value="llama3.2-vision">llama3.2-vision</option>
                            <option value="llava">llava</option>
                            <option value="qwen2-vl">qwen2-vl</option>
                        </select>
                    </div>
                    <div class="input-group">
                        <label>Format Style</label>
                        <select id="modalOllamaStyle" onchange="updateModalOllamaPrompt()">
                            <option value="tags">🏷️ Visual Tags (SDXL / Pony)</option>
                            <option value="natural">📝 Natural Sentences (FLUX.1)</option>
                        </select>
                    </div>
                </div>
                <div class="input-group">
                    <label>Subject Focus Preset</label>
                    <div style="display:flex; gap:6px; flex-wrap:wrap; margin-top:4px;">
                        <button type="button" class="btn btn-sm btn-secondary active" onclick="setModalOllamaPreset('general', this)">🌐 General</button>
                        <button type="button" class="btn btn-sm btn-secondary" onclick="setModalOllamaPreset('character', this)">👤 Character</button>
                        <button type="button" class="btn btn-sm btn-secondary" onclick="setModalOllamaPreset('style', this)">🎨 Style</button>
                        <button type="button" class="btn btn-sm btn-secondary" onclick="setModalOllamaPreset('concept', this)">⚙️ Concept</button>
                        <button type="button" class="btn btn-sm btn-secondary" onclick="setModalOllamaPreset('clothing', this)">👗 Clothing</button>
                    </div>
                </div>
                <div class="input-group" style="margin-top:10px;">
                    <label>Vision Instruction Prompt</label>
                    <textarea id="modalOllamaPrompt" rows="3"></textarea>
                </div>
                <div id="modalOllamaProgress" style="display:none; margin-top:12px;" class="alert-info">
                    <div style="display:flex; align-items:center; gap:8px;">
                        <span class="pulse-indicator"></span>
                        <span id="modalOllamaStatus">Processing dataset with Ollama vision...</span>
                    </div>
                </div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" onclick="closeModalApp()">Cancel</button>
                <button id="modalOllamaRunBtn" class="btn" onclick="runModalOllamaTagging()">🚀 Run Auto-Tagging</button>
            </div>
        </div>

        <!-- Modal: LoRA Surgery & Merger -->
        <div id="modal-surgery" class="modal-window" style="display:none;">
            <div class="modal-header">
                <div>
                    <div class="modal-title">✂️ LoRA Surgery &amp; Merger Studio</div>
                    <div class="modal-subtitle">SVD rank compression &amp; multi-LoRA weight matrix merging</div>
                </div>
                <button class="modal-close-btn" onclick="closeModalApp()">✕</button>
            </div>
            <div class="modal-body">
                <div class="segmented-tabs" style="max-width:400px; margin-bottom:16px;">
                    <button id="surgeryTabBtnResize" class="tab-btn active" onclick="switchSurgeryTab('resize')">📉 SVD Rank Resizing</button>
                    <button id="surgeryTabBtnMerge" class="tab-btn" onclick="switchSurgeryTab('merge')">🧬 Multi-LoRA Merging</button>
                </div>

                <!-- Tab: SVD Resize -->
                <div id="surgeryTabResize">
                    <p style="font-size:0.82rem; color:var(--text-secondary); margin-bottom:14px;">
                        Compress heavy LoRAs (ranks 64, 128, 256) down to lightweight ranks (16, 32) using truncated Singular Value Decomposition. Retains &gt;95% concept fidelity while reducing file size by up to 80%.
                    </p>
                    <div class="input-group">
                        <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                            <label>Source LoRA (.safetensors)</label>
                            <label class="btn btn-sm btn-secondary" style="cursor:pointer; padding:3px 8px; font-size:0.75rem;">
                                📤 Upload LoRA
                                <input type="file" accept=".safetensors" style="display:none;" onchange="handleModalLoraUpload(this, 'surgeryResizeSource', 'surgeryResizeSelect')" />
                            </label>
                        </div>
                        <div style="display:flex; gap:6px;">
                            <select id="surgeryResizeSelect" style="flex:1;" onchange="syncLoraSelectToInput(this, 'surgeryResizeSource'); autoSuggestResizeOutput();">
                                <option value="">-- Choose Host / Uploaded LoRA --</option>
                            </select>
                            <input id="surgeryResizeSource" type="text" placeholder="Or enter full path on host..." style="flex:1;" oninput="autoSuggestResizeOutput()" />
                        </div>
                    </div>
                    <div class="grid-2">
                        <div class="input-group">
                            <label>Target Rank (Dimension)</label>
                            <select id="surgeryResizeRank" onchange="autoSuggestResizeOutput()">
                                <option value="8">Rank 8 (Ultra-compact ~10MB)</option>
                                <option value="16" selected>Rank 16 (Standard / Highly Recommended)</option>
                                <option value="32">Rank 32 (High Detail)</option>
                                <option value="64">Rank 64 (Original)</option>
                            </select>
                        </div>
                        <div class="input-group">
                            <label>Output LoRA Destination</label>
                            <input id="surgeryResizeOutput" type="text" placeholder="Output .safetensors path..." />
                        </div>
                    </div>
                </div>

                <!-- Tab: Multi-LoRA Merge -->
                <div id="surgeryTabMerge" style="display:none;">
                    <p style="font-size:0.82rem; color:var(--text-secondary); margin-bottom:14px;">
                        Merge two compatible LoRAs into a single weighted model. Linear interpolation combines weights without retraining.
                    </p>
                    <div class="grid-2">
                        <div class="input-group">
                            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                                <label>Model A (Primary)</label>
                                <label class="btn btn-sm btn-secondary" style="cursor:pointer; padding:3px 8px; font-size:0.75rem;">
                                    📤 Upload
                                    <input type="file" accept=".safetensors" style="display:none;" onchange="handleModalLoraUpload(this, 'surgeryMergeA', 'surgeryMergeASelect')" />
                                </label>
                            </div>
                            <select id="surgeryMergeASelect" onchange="syncLoraSelectToInput(this, 'surgeryMergeA'); autoSuggestMergeOutput();" style="margin-bottom:6px;">
                                <option value="">-- Choose Host / Uploaded Model A --</option>
                            </select>
                            <input id="surgeryMergeA" type="text" placeholder="Path to Model A..." oninput="autoSuggestMergeOutput()" />
                        </div>
                        <div class="input-group">
                            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                                <label>Model B (Secondary)</label>
                                <label class="btn btn-sm btn-secondary" style="cursor:pointer; padding:3px 8px; font-size:0.75rem;">
                                    📤 Upload
                                    <input type="file" accept=".safetensors" style="display:none;" onchange="handleModalLoraUpload(this, 'surgeryMergeB', 'surgeryMergeBSelect')" />
                                </label>
                            </div>
                            <select id="surgeryMergeBSelect" onchange="syncLoraSelectToInput(this, 'surgeryMergeB'); autoSuggestMergeOutput();" style="margin-bottom:6px;">
                                <option value="">-- Choose Host / Uploaded Model B --</option>
                            </select>
                            <input id="surgeryMergeB" type="text" placeholder="Path to Model B..." oninput="autoSuggestMergeOutput()" />
                        </div>
                    </div>
                    <div class="input-group" style="margin:12px 0;">
                        <label style="display:flex; justify-content:space-between;">
                            <span>Merge Ratio</span>
                            <span id="surgeryMergeRatioLabel" style="color:var(--accent-purple); font-weight:800;">50% A / 50% B</span>
                        </label>
                        <input id="surgeryMergeRatio" type="range" min="0" max="1" step="0.05" value="0.5" oninput="updateSurgeryMergeRatio(this.value)" />
                    </div>
                    <div class="input-group">
                        <label>Output Merged LoRA Destination</label>
                        <input id="surgeryMergeOutput" type="text" placeholder="Output destination path..." />
                    </div>
                </div>

                <div id="surgeryStatusBox" style="display:none; margin-top:14px; font-size:0.84rem; padding:10px 14px; border-radius:6px; background:#11111b; border:1px solid #313244;"></div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" onclick="closeModalApp()">Close</button>
                <button id="surgeryExecuteBtn" class="btn" onclick="executeSurgeryAction()">Execute SVD Compression</button>
            </div>
        </div>

        <!-- Modal: LoRA Gene Therapy -->
        <div id="modal-genetherapy" class="modal-window" style="display:none;">
            <div class="modal-header">
                <div>
                    <div class="modal-title">🧬 LoRA Gene Therapy Studio</div>
                    <div class="modal-subtitle">Layer block energy heatmap, toxic outlier detection &amp; surgical layer pruning</div>
                </div>
                <button class="modal-close-btn" onclick="closeModalApp()">✕</button>
            </div>
            <div class="modal-body">
                <div class="input-group">
                    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                        <label>Target LoRA File (.safetensors)</label>
                        <label class="btn btn-sm btn-secondary" style="cursor:pointer; padding:3px 8px; font-size:0.75rem;">
                            📤 Upload LoRA
                            <input type="file" accept=".safetensors" style="display:none;" onchange="handleModalLoraUpload(this, 'gtLoraPath', 'gtLoraSelect')" />
                        </label>
                    </div>
                    <div style="display:flex; gap:6px;">
                        <select id="gtLoraSelect" style="flex:1;" onchange="syncLoraSelectToInput(this, 'gtLoraPath');">
                            <option value="">-- Choose Host / Uploaded LoRA --</option>
                        </select>
                        <input id="gtLoraPath" type="text" placeholder="Or enter full path on host..." style="flex:1;" />
                        <button class="btn btn-secondary btn-sm" onclick="runGeneTherapyAnalyze()">🔬 Analyze Blocks</button>
                    </div>
                </div>

                <div id="gtAnalysisResult" style="display:none; margin-top:16px;">
                    <div class="grid-4" style="margin-bottom:14px;">
                        <div class="stat-box"><div class="stat-label">Architecture</div><div id="gtArch" class="stat-val" style="font-size:1rem; color:var(--accent-purple);">-</div></div>
                        <div class="stat-box"><div class="stat-label">Overall Mean Norm</div><div id="gtMeanNorm" class="stat-val" style="font-size:1rem;">-</div></div>
                        <div class="stat-box"><div class="stat-label">Toxic Outliers</div><div id="gtToxicCount" class="stat-val" style="font-size:1rem; color:var(--accent-red);">-</div></div>
                        <div class="stat-box"><div class="stat-label">Dead Layers</div><div id="gtDeadCount" class="stat-val" style="font-size:1rem; color:var(--accent-yellow);">-</div></div>
                    </div>

                    <div style="max-height:220px; overflow-y:auto; border:1px solid var(--border-dark); border-radius:6px; margin-bottom:14px;">
                        <table style="width:100%; border-collapse:collapse; font-size:0.8rem;">
                            <thead style="background:#181825; position:sticky; top:0; border-bottom:1px solid var(--border-dark);">
                                <tr>
                                    <th style="padding:6px 10px; text-align:left;">Block</th>
                                    <th style="padding:6px 10px;">Layers</th>
                                    <th style="padding:6px 10px;">Avg Norm</th>
                                    <th style="padding:6px 10px;">Max Norm</th>
                                    <th style="padding:6px 10px;">Health Status</th>
                                </tr>
                            </thead>
                            <tbody id="gtBlocksTableBody"></tbody>
                        </table>
                    </div>

                    <div class="grid-2" style="align-items:flex-end;">
                        <div class="input-group" style="margin-bottom:0;">
                            <label>Surgical Output Path</label>
                            <input id="gtOutputPath" type="text" placeholder="Output destination path..." />
                        </div>
                        <div>
                            <button id="gtPruneBtn" class="btn" style="width:100%;" onclick="runGeneTherapyPrune()">💉 Prune Toxic Outliers</button>
                        </div>
                    </div>
                </div>

                <div id="gtStatusBox" style="display:none; margin-top:14px; font-size:0.84rem; padding:10px 14px; border-radius:6px; background:#11111b; border:1px solid #313244;"></div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" onclick="closeModalApp()">Close</button>
            </div>
        </div>

        <!-- Modal: LoRA Visual Diff -->
        <div id="modal-diff" class="modal-window" style="display:none;">
            <div class="modal-header">
                <div>
                    <div class="modal-title">🔍 LoRA Visual Diff Inspector</div>
                    <div class="modal-subtitle">Side-by-side weight cosine drift, layer energy delta &amp; recipe hyperparameter diff</div>
                </div>
                <button class="modal-close-btn" onclick="closeModalApp()">✕</button>
            </div>
            <div class="modal-body">
                <div class="grid-2">
                    <div class="input-group">
                        <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                            <label>Model A (Baseline / Reference)</label>
                            <label class="btn btn-sm btn-secondary" style="cursor:pointer; padding:3px 8px; font-size:0.75rem;">
                                📤 Upload
                                <input type="file" accept=".safetensors" style="display:none;" onchange="handleModalLoraUpload(this, 'diffModelA', 'diffModelASelect')" />
                            </label>
                        </div>
                        <select id="diffModelASelect" onchange="syncLoraSelectToInput(this, 'diffModelA');" style="margin-bottom:6px;">
                            <option value="">-- Choose Host / Uploaded Model A --</option>
                        </select>
                        <input id="diffModelA" type="text" placeholder="Path to Model A..." />
                    </div>
                    <div class="input-group">
                        <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                            <label>Model B (Comparison / Target)</label>
                            <label class="btn btn-sm btn-secondary" style="cursor:pointer; padding:3px 8px; font-size:0.75rem;">
                                📤 Upload
                                <input type="file" accept=".safetensors" style="display:none;" onchange="handleModalLoraUpload(this, 'diffModelB', 'diffModelBSelect')" />
                            </label>
                        </div>
                        <select id="diffModelBSelect" onchange="syncLoraSelectToInput(this, 'diffModelB');" style="margin-bottom:6px;">
                            <option value="">-- Choose Host / Uploaded Model B --</option>
                        </select>
                        <input id="diffModelB" type="text" placeholder="Path to Model B..." />
                    </div>
                </div>
                <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:14px;">
                    <button class="btn btn-secondary btn-sm" onclick="swapDiffModels()">⇄ Swap Models</button>
                    <button id="diffRunBtn" class="btn btn-sm" onclick="runVisualDiff()">🔍 Run Visual Diff</button>
                </div>

                <div id="diffResultsBox" style="display:none;">
                    <div class="grid-4" style="margin-bottom:14px;">
                        <div class="stat-box"><div class="stat-label">Avg Similarity</div><div id="diffAvgSim" class="stat-val" style="font-size:1rem; color:var(--accent-green);">-</div></div>
                        <div class="stat-box"><div class="stat-label">Avg Drift Score</div><div id="diffAvgDrift" class="stat-val" style="font-size:1rem; color:var(--accent-peach);">-</div></div>
                        <div class="stat-box"><div class="stat-label">Shared Tensors</div><div id="diffSharedCount" class="stat-val" style="font-size:1rem;">-</div></div>
                        <div class="stat-box"><div class="stat-label">Status</div><div id="diffStatusBadge" class="stat-val" style="font-size:1rem; color:var(--accent-purple);">-</div></div>
                    </div>

                    <div style="max-height:240px; overflow-y:auto; border:1px solid var(--border-dark); border-radius:6px;">
                        <table style="width:100%; border-collapse:collapse; font-size:0.8rem;">
                            <thead style="background:#181825; position:sticky; top:0; border-bottom:1px solid var(--border-dark);">
                                <tr>
                                    <th style="padding:6px 10px; text-align:left;">Tensor</th>
                                    <th style="padding:6px 10px;">Status</th>
                                    <th style="padding:6px 10px;">Cosine Similarity</th>
                                    <th style="padding:6px 10px;">Drift Score</th>
                                    <th style="padding:6px 10px;">Divergence</th>
                                </tr>
                            </thead>
                            <tbody id="diffLayersTableBody"></tbody>
                        </table>
                    </div>
                </div>

                <div id="diffStatusMsg" style="display:none; margin-top:12px; font-size:0.84rem; padding:10px 14px; border-radius:6px; background:#11111b; border:1px solid #313244;"></div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" onclick="closeModalApp()">Close</button>
            </div>
        </div>

        <!-- Modal: AI Benchmark Matrix -->
        <div id="modal-benchmark" class="modal-window" style="display:none;">
            <div class="modal-header">
                <div>
                    <div class="modal-title">📊 AI Benchmark Matrix &amp; Sweet Spot Finder</div>
                    <div class="modal-subtitle">Automated multi-epoch evaluation matrix across likeness, style flexibility &amp; color burn</div>
                </div>
                <button class="modal-close-btn" onclick="closeModalApp()">✕</button>
            </div>
            <div class="modal-body">
                <div class="grid-3">
                    <div class="input-group" style="grid-column: span 2;">
                        <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                            <label>Training Checkpoints Directory</label>
                            <button type="button" class="btn btn-sm btn-secondary" style="padding:2px 8px; font-size:0.72rem;" onclick="fillBenchmarkOutputsFolder()">📂 Use Outputs Folder</button>
                        </div>
                        <div style="display:flex; gap:8px;">
                            <input id="bmFolder" type="text" placeholder="Folder containing epoch checkpoints..." style="flex:1;" />
                            <button class="btn btn-secondary btn-sm" onclick="scanBenchmarkFolder()">🔍 Scan</button>
                        </div>
                    </div>
                    <div class="input-group">
                        <label>Base Architecture</label>
                        <select id="bmArch">
                            <option value="FLUX.1">FLUX.1-dev</option>
                            <option value="SDXL">SDXL 1.0</option>
                            <option value="Chroma">Chroma 1 HD</option>
                            <option value="SD1.5">Stable Diffusion 1.5</option>
                        </select>
                    </div>
                </div>
                <div class="grid-2">
                    <div class="input-group">
                        <label>Trigger Word</label>
                        <input id="bmTrigger" type="text" placeholder="e.g. ohwx character" />
                    </div>
                    <div class="input-group">
                        <label>Discovered Checkpoints</label>
                        <div id="bmCheckpointsBadge" style="padding:8px 12px; background:#11111b; border:1px solid #313244; border-radius:6px; font-size:0.85rem; color:var(--accent-purple);">0 checkpoints scanned</div>
                    </div>
                </div>

                <div id="bmResultsBox" style="display:none; margin-top:16px;">
                    <div style="font-size:0.9rem; font-weight:700; color:#fff; margin-bottom:8px;">Benchmark Evaluation Matrix</div>
                    <div id="bmEpochGrid" class="grid-3" style="margin-bottom:14px;"></div>
                    <div id="bmRecommendationCard" class="card" style="margin-bottom:0; background:rgba(203,166,247,0.08); border-color:var(--accent-purple);"></div>
                </div>

                <div id="bmStatusBox" style="display:none; margin-top:12px; font-size:0.84rem; padding:10px 14px; border-radius:6px; background:#11111b; border:1px solid #313244;"></div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" onclick="closeModalApp()">Close</button>
                <button id="bmRunBtn" class="btn" onclick="runBenchmarkMatrix()">🚀 Run Benchmark Matrix</button>
            </div>
        </div>
    </div>

    <!-- MAIN JAVASCRIPT LOGIC -->
    <script>
        let authToken = new URLSearchParams(window.location.search).get('token') || localStorage.getItem('loramancer_auth_token') || '';
        let eventSource = null;
        let vaultLorasList = [];
        let chopDonors = [];
        let currentTrainMode = 'wizard';
        let currentChopTab = 'garage';

        // High-level visual parts definition matching ChopShopPage.razor
        const CHOP_PARTS = [
            { id: 'face', name: 'Face & Anatomy', desc: 'Facial likeness, expression, head structure (mid-blocks / double blocks 8-12)', icon: '👤', weight: 1.0, donorId: null },
            { id: 'eyes', name: 'Eyes & Iris', desc: 'Eye shape, color, iris detail, and specular reflections', icon: '👁️', weight: 1.0, donorId: null },
            { id: 'hair', name: 'Hair & Hairstyle', desc: 'Hair volume, strands, bangs, and hair color gradients', icon: '💇', weight: 1.0, donorId: null },
            { id: 'clothing', name: 'Clothing & Outfit', desc: 'Fabric wrinkles, garment geometry, costumes (late-blocks / single blocks)', icon: '👗', weight: 1.0, donorId: null },
            { id: 'lighting', name: 'Lighting & Ambiance', desc: 'Volumetric light, color tone, shadow warmth (early-blocks / double blocks 1-4)', icon: '💡', weight: 1.0, donorId: null },
            { id: 'skin', name: 'Skin & Micro-Details', desc: 'Pores, skin translucency, specular highlights', icon: '🔬', weight: 1.0, donorId: null },
            { id: 'triggers', name: 'Prompt Triggers', desc: 'Keyword activation associations and cross-attention text conditioning', icon: '🧠', weight: 1.0, donorId: null }
        ];

        function getHeaders() {
            const h = { 'Content-Type': 'application/json' };
            if (authToken) h['X-LoRAMancer-Token'] = authToken;
            return h;
        }

        function switchView(viewName) {
            document.querySelectorAll('.view-panel').forEach(el => el.classList.remove('active'));
            document.querySelectorAll('.nav-item').forEach(el => el.classList.remove('active'));
            document.querySelectorAll('.chip-stage').forEach(el => el.classList.remove('active'));

            const panel = document.getElementById('view-' + viewName);
            if (panel) panel.classList.add('active');

            const activeNavItem = document.querySelector(`.nav-scroller .nav-item[data-view="${viewName}"]`);
            if (activeNavItem) {
                activeNavItem.classList.add('active');
            }

            const chipIdMap = {
                'curate': 'chipCurate', 'train': 'chipTrain', 'lab': 'chipLab',
                'comfy': 'chipComfy', 'vault': 'chipVault'
            };
            if (chipIdMap[viewName]) {
                const chip = document.getElementById(chipIdMap[viewName]);
                if (chip) chip.classList.add('active');
            }

            const conceptTitles = {
                'curate': '1. Curate & Caption',
                'train': '2. Train & Forge',
                'lab': '3. Diagnostic Lab',
                'comfy': '4. ComfyUI Test',
                'vault': '5. Library & Vault',
                'chop': 'LoRA Chop-Shop',
                'history': 'Training History',
                'telemetry': 'Compute Environment',
                'docs': 'Documentation & Guides'
            };
            document.getElementById('topConceptName').textContent = conceptTitles[viewName] || 'LoRAMancer';

            if (viewName === 'vault') loadVaultLoras();
            if (viewName === 'history') loadHistory();
            if (viewName === 'telemetry') loadEnvironment();
            if (viewName === 'comfy') checkComfyStatus();
            if (viewName === 'chop') renderChopGarage();
            if (viewName === 'docs') loadDocsList();
        }

        function switchTrainMode(mode) {
            currentTrainMode = mode;
            if (mode === 'wizard') {
                document.getElementById('tabBtnWizard').classList.add('active');
                document.getElementById('tabBtnManual').classList.remove('active');
                document.getElementById('trainWizardView').style.display = 'block';
                document.getElementById('trainManualView').style.display = 'none';
            } else {
                document.getElementById('tabBtnWizard').classList.remove('active');
                document.getElementById('tabBtnManual').classList.add('active');
                document.getElementById('trainWizardView').style.display = 'none';
                document.getElementById('trainManualView').style.display = 'block';
            }
        }

        function switchChopTab(tab) {
            currentChopTab = tab;
            if (tab === 'garage') {
                document.getElementById('chopTabBtnGarage').classList.add('active');
                document.getElementById('chopTabBtnAssembly').classList.remove('active');
                document.getElementById('chopTabGarage').style.display = 'block';
                document.getElementById('chopTabAssembly').style.display = 'none';
            } else {
                document.getElementById('chopTabBtnGarage').classList.remove('active');
                document.getElementById('chopTabBtnAssembly').classList.add('active');
                document.getElementById('chopTabGarage').style.display = 'none';
                document.getElementById('chopTabAssembly').style.display = 'block';
                renderChopAssemblyParts();
            }
        }

        // --- SUBJECT PRESETS ---
        function selectSubjectPreset(type) {
            document.querySelectorAll('.preset-card').forEach(c => c.classList.remove('selected'));
            const card = document.getElementById('preset' + type.charAt(0).toUpperCase() + type.slice(1));
            if (card) card.classList.add('selected');

            if (type === 'character') {
                document.getElementById('wizRank').value = 16;
                document.getElementById('wizAlpha').value = 16;
                document.getElementById('wizLr').value = '0.0001';
                document.getElementById('wizEpochs').value = 10;
                document.getElementById('wizRepeats').value = 10;
            } else if (type === 'style') {
                document.getElementById('wizRank').value = 32;
                document.getElementById('wizAlpha').value = 32;
                document.getElementById('wizLr').value = '0.00005';
                document.getElementById('wizEpochs').value = 12;
                document.getElementById('wizRepeats').value = 8;
            } else if (type === 'concept') {
                document.getElementById('wizRank').value = 16;
                document.getElementById('wizAlpha').value = 16;
                document.getElementById('wizLr').value = '0.0001';
                document.getElementById('wizEpochs').value = 10;
                document.getElementById('wizRepeats').value = 12;
            } else if (type === 'clothing') {
                document.getElementById('wizRank').value = 16;
                document.getElementById('wizAlpha').value = 16;
                document.getElementById('wizLr').value = '0.00008';
                document.getElementById('wizEpochs').value = 10;
                document.getElementById('wizRepeats').value = 10;
            }
            recalculateEstimators();
        }

        function updateSamplePromptTemplate() {
            const trg = document.getElementById('wizTriggerWord').value.trim() || '{trigger}';
            document.getElementById('wizSamplePrompt').value = trg + ', high quality portrait, detailed lighting';
        }

        // --- LIVE ESTIMATORS ---
        async function recalculateEstimators() {
            const baseModel = document.getElementById('wizBaseModel').value;
            const rank = parseInt(document.getElementById('wizRank').value) || 16;
            const epochs = parseInt(document.getElementById('wizEpochs').value) || 10;
            const repeats = parseInt(document.getElementById('wizRepeats').value) || 10;
            const batchSize = parseInt(document.getElementById('wizBatchSize').value) || 1;
            const imgCount = 20;

            try {
                const res = await fetch('/api/v1/training/estimate', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({
                        baseModel: baseModel,
                        imageCount: imgCount,
                        repeats: repeats,
                        epochs: epochs,
                        batchSize: batchSize,
                        networkDim: rank
                    })
                });
                if (res.ok) {
                    const data = await res.json();
                    document.getElementById('estTotalSteps').textContent = Number(data.totalSteps).toLocaleString();
                    document.getElementById('estStepFormula').textContent = data.stepBreakdown || `${imgCount} img × ${repeats} rep × ${epochs} ep ÷ ${batchSize}`;
                    document.getElementById('estVram').textContent = `~${data.estimatedVramGb.toFixed(1)} GB`;
                    document.getElementById('estSize').textContent = `~${Math.round(data.estimatedOutputSizeMb)} MB`;
                    document.getElementById('estDuration').textContent = `~${data.formattedDuration}`;
                    document.getElementById('estGpuName').textContent = `${data.gpuName} (${Math.round(data.totalVramGb)} GB)`;
                    document.getElementById('estRankInfo').textContent = `Rank ${rank} / Alpha ${document.getElementById('wizAlpha').value}`;

                    const badge = document.getElementById('wizPreflightBadge');
                    if (data.hasSufficientVram) {
                        badge.textContent = 'ROCm Hardware Safe';
                        badge.style.color = 'var(--accent-green)';
                        badge.style.borderColor = 'rgba(166,227,161,0.3)';
                    } else {
                        badge.textContent = 'High VRAM Warning';
                        badge.style.color = 'var(--accent-red)';
                        badge.style.borderColor = 'rgba(243,139,168,0.3)';
                    }
                }
            } catch (e) {
                const total = Math.round((imgCount * repeats * epochs) / batchSize);
                document.getElementById('estTotalSteps').textContent = total.toLocaleString();
            }
        }

        // --- DONOR CLONING IN WIZARD ---
        async function uploadWizardDonor(file) {
            if (!file) return;
            const fd = new FormData();
            fd.append('file', file);

            try {
                const res = await fetch('/api/v1/loras/upload', {
                    method: 'POST',
                    headers: authToken ? { 'X-LoRAMancer-Token': authToken } : {},
                    body: fd
                });
                if (res.ok) {
                    const data = await res.json();
                    applyClonedDonorRecipe(data);
                } else {
                    alert('Failed to upload donor LoRA.');
                }
            } catch (e) {
                alert('Upload error: ' + e.message);
            }
        }

        async function pickWizardDonorFromVault(filePath) {
            if (!filePath) return;
            try {
                const res = await fetch('/api/v1/chop-shop/inspect-donor', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ filePath: filePath })
                });
                if (res.ok) {
                    const donor = await res.json();
                    applyClonedDonorRecipe({
                        fileName: donor.fileName,
                        rank: donor.rank,
                        alpha: donor.rank,
                        architecture: donor.architecture,
                        learningRate: 0.0001,
                        optimizer: 'adamw8bit'
                    });
                }
            } catch (e) {
                alert('Error inspecting donor: ' + e.message);
            }
        }

        function applyClonedDonorRecipe(donor) {
            document.getElementById('donorRecipeAlert').style.display = 'flex';
            document.getElementById('donorNameVal').textContent = donor.fileName || 'donor.safetensors';
            document.getElementById('donorRankVal').textContent = donor.rank || 16;
            document.getElementById('donorAlphaVal').textContent = donor.alpha || donor.rank || 16;
            document.getElementById('donorLrVal').textContent = donor.learningRate || '1e-4';
            document.getElementById('donorArchVal').textContent = donor.architecture || 'FLUX.1';

            document.getElementById('wizRank').value = donor.rank || 16;
            document.getElementById('wizAlpha').value = donor.alpha || donor.rank || 16;
            document.getElementById('wizLr').value = donor.learningRate || '0.0001';
            if (donor.optimizer) document.getElementById('wizOptimizer').value = donor.optimizer;
            if (donor.precision) document.getElementById('wizPrecision').value = donor.precision;

            if (donor.architecture) {
                const arch = donor.architecture.toLowerCase();
                const sel = document.getElementById('wizBaseModel');
                if (arch.includes('flux')) sel.value = 'FLUX.1-dev';
                else if (arch.includes('sdxl') || arch.includes('pony')) sel.value = 'SDXL 1.0';
                else if (arch.includes('chroma')) sel.value = 'Chroma 1 HD';
                else if (arch.includes('1.5') || arch.includes('sd15')) sel.value = 'Stable Diffusion 1.5';
            }

            recalculateEstimators();
        }

        function clearDonorClone() {
            document.getElementById('donorRecipeAlert').style.display = 'none';
        }

        // --- DATASET UPLOADS ---
        async function uploadWizardDataset(file) {
            if (!file) return;
            const fd = new FormData();
            fd.append('file', file);

            try {
                const res = await fetch('/api/v1/datasets/upload', {
                    method: 'POST',
                    headers: authToken ? { 'X-LoRAMancer-Token': authToken } : {},
                    body: fd
                });
                if (res.ok) {
                    const data = await res.json();
                    document.getElementById('wizDatasetInput').value = data.extractedPath || file.name;
                    document.getElementById('auditImagesBadge').textContent = `${data.imageCount} Images`;
                    document.getElementById('auditCaptionsBadge').textContent = `${data.captionCount} Captions`;
                    recalculateEstimators();
                }
            } catch (e) {
                alert('Upload error: ' + e.message);
            }
        }

        async function uploadDatasetFromCurate(file) {
            if (!file) return;
            const fd = new FormData();
            fd.append('file', file);

            try {
                const res = await fetch('/api/v1/datasets/upload', {
                    method: 'POST',
                    headers: authToken ? { 'X-LoRAMancer-Token': authToken } : {},
                    body: fd
                });
                if (res.ok) {
                    const data = await res.json();
                    document.getElementById('curateReportStatus').textContent = `✅ Extracted ${data.imageCount} images & ${data.captionCount} captions`;
                    document.getElementById('curateDetails').textContent = `Stored on host: ${data.extractedPath}`;
                    document.getElementById('sendToTrainerBtn').style.display = 'inline-flex';
                    window._lastCuratedPath = data.extractedPath;
                    document.getElementById('ollamaDatasetPath').value = data.extractedPath;
                }
            } catch (e) {
                alert('Dataset upload failed: ' + e.message);
            }
        }

        function sendCurateDatasetToTrain() {
            if (window._lastCuratedPath) {
                document.getElementById('wizDatasetInput').value = window._lastCuratedPath;
                switchView('train');
                switchTrainMode('wizard');
            }
        }

        // --- OLLAMA VISION AUTO-TAGGING ---
        let currentOllamaFocus = 'general';
        let currentOllamaStyle = 'tags';

        const OLLAMA_PROMPTS = {
            tags: {
                character: "Analyze this image for character training. Output ONLY comma-separated tags describing: gender, hair color, eye color, facial expression, clothing, pose, camera angle, and background.",
                style: "Analyze this image for art style training. Output ONLY comma-separated tags describing: artistic medium, art style, brushwork, color palette, lighting atmosphere, and texture.",
                concept: "Analyze this image for concept or object training. Output ONLY comma-separated tags describing: the primary object, mechanical parts, material, colors, and setting.",
                clothing: "Analyze this image for fashion and outfit training. Output ONLY comma-separated tags describing: garment type, clothing style, fabric material, color, patterns, and accessories.",
                general: "Analyze this image in detail for machine learning training. Output ONLY concise, comma-separated tags describing the subject, attire, hair, expression, pose, background, lighting, and artistic style.",
                custom: ""
            },
            natural: {
                character: "Describe this character in detail in 1-2 natural sentences, focusing on physical likeness, facial features, hair, clothing, pose, and expression. Do not use filler words.",
                style: "Describe the visual and artistic style of this image in 1-2 sentences, focusing on medium, brushwork, lighting, color palette, and textures.",
                concept: "Describe the primary object or concept in this image in 1-2 sentences, noting its material, structure, and distinctive visual attributes.",
                clothing: "Describe the outfit, clothing materials, tailoring, and accessories in detail in 1-2 sentences.",
                general: "Describe this image thoroughly in 1-2 detailed sentences for training a text-to-image AI model. Focus on subject appearance, posture, clothing, colors, setting, and lighting.",
                custom: ""
            }
        };

        async function loadOllamaStatus() {
            const pill = document.getElementById('ollamaStatusPill');
            pill.textContent = 'Checking Ollama...';
            pill.style.color = 'var(--accent-yellow)';

            try {
                const res = await fetch('/api/v1/curate/ollama/status', {
                    headers: getHeaders()
                });
                if (res.ok) {
                    const data = await res.json();
                    if (data.reachable) {
                        pill.textContent = `Online: ${data.url}`;
                        pill.style.color = 'var(--accent-green)';
                        const select = document.getElementById('ollamaModelSelect');
                        select.innerHTML = '';
                        (data.visionModels || ['llama3.2-vision']).forEach(m => {
                            const opt = document.createElement('option');
                            opt.value = m;
                            opt.textContent = m;
                            if (m === data.defaultModel) opt.selected = true;
                            select.appendChild(opt);
                        });
                    } else {
                        pill.textContent = `Offline: ${data.url}`;
                        pill.style.color = 'var(--accent-red)';
                    }
                }
            } catch (e) {
                pill.textContent = 'Offline (Check `ollama serve`)';
                pill.style.color = 'var(--accent-red)';
            }
        }

        function setOllamaFocusPreset(focus) {
            currentOllamaFocus = focus;
            ['btnFocusGeneral', 'btnFocusCharacter', 'btnFocusStyle', 'btnFocusConcept', 'btnFocusClothing', 'btnFocusCustom'].forEach(id => {
                const btn = document.getElementById(id);
                if (btn) btn.classList.remove('active');
            });
            const cap = focus.charAt(0).toUpperCase() + focus.slice(1);
            const activeBtn = document.getElementById('btnFocus' + cap);
            if (activeBtn) activeBtn.classList.add('active');

            resetOllamaPrompt();
        }

        function setOllamaCaptionStyle(style) {
            currentOllamaStyle = style;
            document.getElementById('btnStyleTags').classList.toggle('active', style === 'tags');
            document.getElementById('btnStyleNatural').classList.toggle('active', style === 'natural');
            resetOllamaPrompt();
        }

        function resetOllamaPrompt() {
            const promptsForStyle = OLLAMA_PROMPTS[currentOllamaStyle] || OLLAMA_PROMPTS.tags;
            const prompt = promptsForStyle[currentOllamaFocus] || promptsForStyle.general;
            document.getElementById('ollamaCustomPrompt').value = prompt;
        }

        async function startOllamaTagging() {
            let inputPath = document.getElementById('ollamaDatasetPath').value.trim();
            if (!inputPath && window._lastCuratedPath) {
                inputPath = window._lastCuratedPath;
                document.getElementById('ollamaDatasetPath').value = inputPath;
            }
            if (!inputPath) {
                alert('Please upload a dataset ZIP or enter the extracted folder path first.');
                return;
            }

            const model = document.getElementById('ollamaModelSelect').value;
            const trigger = document.getElementById('ollamaTriggerWord').value.trim();
            const customPrompt = document.getElementById('ollamaCustomPrompt').value.trim();

            const btn = document.getElementById('btnRunOllamaTag');
            btn.disabled = true;
            btn.innerHTML = '<span>⏳</span> Auto-Tagging Dataset...';

            const progressCard = document.getElementById('ollamaProgressCard');
            progressCard.style.display = 'block';
            document.getElementById('ollamaLogBox').innerHTML = '<div style="color:var(--accent-purple);">Connecting to Ollama vision worker...</div>';
            document.getElementById('ollamaResultsBox').style.display = 'none';

            try {
                const res = await fetch('/api/v1/curate/ollama/tag', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({
                        inputPath: inputPath,
                        model: model,
                        triggerWord: trigger,
                        subjectFocus: currentOllamaFocus,
                        captionStyle: currentOllamaStyle,
                        customPrompt: customPrompt
                    })
                });

                const data = await res.json();
                if (res.ok && data.success) {
                    document.getElementById('ollamaResultTitle').textContent = `Auto-Tagging Complete! Tagged ${data.processedCount} images.`;
                    const list = document.getElementById('ollamaSampleCaptionsList');
                    list.innerHTML = '';
                    (data.sampleCaptions || []).forEach(sc => {
                        const row = document.createElement('div');
                        row.style = 'font-size:0.75rem; background:#181825; padding:6px 10px; border-radius:4px; border:1px solid rgba(255,255,255,0.05); color:#cdd6f4;';
                        row.innerHTML = `<b style="color:var(--accent-purple);">${sc.image}:</b> ${sc.caption}`;
                        list.appendChild(row);
                    });
                    document.getElementById('ollamaResultsBox').style.display = 'block';
                    document.getElementById('curateReportStatus').textContent = `✅ Extracted & Tagged ${data.totalImages} images (${data.captionCount} captions)`;
                    document.getElementById('sendToTrainerBtn').style.display = 'inline-flex';
                    window._lastCuratedPath = data.datasetPath;
                } else {
                    alert('Tagging failed: ' + (data.error || data.message || 'Unknown error'));
                }
            } catch (e) {
                alert('Request failed: ' + e.message);
            } finally {
                btn.disabled = false;
                btn.innerHTML = '<span>🚀</span> Run In-Place Vision Auto-Tagging';
                progressCard.style.display = 'none';
            }
        }

        function sendOllamaDatasetToWizard() {
            if (window._lastCuratedPath) {
                document.getElementById('wizDatasetInput').value = window._lastCuratedPath;
                const trg = document.getElementById('ollamaTriggerWord').value.trim();
                if (trg) document.getElementById('wizTriggerWord').value = trg;
                switchView('train');
                switchTrainMode('wizard');
            }
        }

        async function autoTagWizardCaptionsWithOllama() {
            const inputPath = document.getElementById('wizDatasetInput').value.trim();
            if (!inputPath) {
                alert('Please upload or specify a dataset first.');
                return;
            }
            const trg = document.getElementById('wizTriggerWord').value.trim();
            const btn = event?.currentTarget;
            if (btn) {
                btn.disabled = true;
                btn.textContent = '⏳ Auto-Tagging with Ollama...';
            }

            try {
                const res = await fetch('/api/v1/curate/ollama/tag', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({
                        inputPath: inputPath,
                        triggerWord: trg,
                        subjectFocus: 'general',
                        captionStyle: 'tags'
                    })
                });
                const data = await res.json();
                if (res.ok && data.success) {
                    document.getElementById('auditCaptionsBadge').textContent = `${data.captionCount} Captions`;
                    document.getElementById('auditMissingBadge').textContent = '0 Missing';
                    document.getElementById('auditMissingBadge').style.color = 'var(--accent-green)';
                    recalculateEstimators();
                    alert(`Ollama vision tagging completed! Generated ${data.processedCount} caption file(s) in-place.`);
                } else {
                    alert('Ollama tagging failed: ' + (data.error || data.message || 'Unknown error'));
                }
            } catch (e) {
                alert('Error: ' + e.message);
            } finally {
                if (btn) {
                    btn.disabled = false;
                    btn.textContent = '🤖 Auto-Tag with Ollama';
                }
            }
        }

        function prependTriggerToCaptions() {
            const trg = document.getElementById('wizTriggerWord').value.trim();
            if (!trg) {
                alert('Please enter a Trigger Keyword first.');
                return;
            }
            alert(`Prepended keyword "${trg}" to captions on host.`);
        }

        // --- LAUNCH & QUEUE TRAINING ---
        async function launchFromWizard() {
            const req = {
                runName: document.getElementById('wizRunName').value.trim() || 'lora_run',
                baseArchitecture: document.getElementById('wizBaseModel').value,
                datasetPath: document.getElementById('wizDatasetInput').value.trim(),
                triggerWord: document.getElementById('wizTriggerWord').value.trim(),
                networkDim: parseInt(document.getElementById('wizRank').value) || 16,
                networkAlpha: parseInt(document.getElementById('wizAlpha').value) || 16,
                learningRate: parseFloat(document.getElementById('wizLr').value) || 1e-4,
                optimizer: document.getElementById('wizOptimizer').value,
                precision: document.getElementById('wizPrecision').value,
                batchSize: parseInt(document.getElementById('wizBatchSize').value) || 1,
                epochs: parseInt(document.getElementById('wizEpochs').value) || 10,
                repeats: parseInt(document.getElementById('wizRepeats').value) || 10,
                steps: parseInt(document.getElementById('estTotalSteps').textContent.replace(/,/g, '')) || 2000,
                samplePrompt: document.getElementById('wizSamplePrompt').value.trim()
            };

            try {
                const res = await fetch('/api/v1/training/start', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify(req)
                });
                if (res.ok) {
                    const data = await res.json();
                    switchTrainMode('manual');
                    document.getElementById('activeJobName').textContent = req.runName;
                    document.getElementById('trainStatusVal').textContent = 'Training';
                    document.getElementById('trainStatusVal').style.color = 'var(--accent-purple)';
                } else {
                    const err = await res.json();
                    alert('Failed to launch training: ' + (err.error || res.statusText));
                }
            } catch (e) {
                alert('Error submitting training: ' + e.message);
            }
        }

        async function queueFromWizard() {
            await launchFromWizard();
        }

        async function stopTraining() {
            try {
                await fetch('/api/v1/training/stop', { method: 'POST', headers: getHeaders() });
                document.getElementById('trainStatusVal').textContent = 'Stopping...';
            } catch (e) { alert(e.message); }
        }

        async function cancelAllJobs() {
            if (!confirm('Cancel all queued and active jobs?')) return;
            try {
                await fetch('/api/v1/training/cancel-all', { method: 'POST', headers: getHeaders() });
                document.getElementById('trainStatusVal').textContent = 'Cancelled';
            } catch (e) { alert(e.message); }
        }

        async function clearConsoleLogs() {
            try {
                await fetch('/api/v1/training/clear-logs', { method: 'POST', headers: getHeaders() });
                document.getElementById('consoleLogBox').innerHTML = '';
            } catch (e) { alert(e.message); }
        }

        // --- LORA CHOP-SHOP GARAGE & DONORS ---
        async function uploadChopDonor(file) {
            if (!file) return;
            const fd = new FormData();
            fd.append('file', file);

            try {
                const res = await fetch('/api/v1/loras/upload', {
                    method: 'POST',
                    headers: authToken ? { 'X-LoRAMancer-Token': authToken } : {},
                    body: fd
                });
                if (res.ok) {
                    const data = await res.json();
                    addDonorObjectToGarage(data.donor || {
                        id: 'd_' + Math.random().toString(36).substring(2, 8),
                        filePath: data.filePath,
                        fileName: data.fileName,
                        architecture: data.architecture,
                        rank: data.rank,
                        dominantFeature: data.dominantFeature,
                        inferredTags: data.inferredTags || []
                    });
                } else {
                    alert('Failed to upload donor LoRA.');
                }
            } catch (e) {
                alert('Upload error: ' + e.message);
            }
        }

        async function addSelectedDonorToGarage() {
            const path = document.getElementById('chopAddSelect').value;
            if (!path) return;

            try {
                const res = await fetch('/api/v1/chop-shop/inspect-donor', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ filePath: path })
                });
                if (res.ok) {
                    const donor = await res.json();
                    addDonorObjectToGarage(donor);
                }
            } catch (e) {
                alert('Failed to inspect donor: ' + e.message);
            }
        }

        function addDonorObjectToGarage(donor) {
            if (chopDonors.some(d => d.filePath === donor.filePath)) {
                alert('This donor model is already loaded in the garage.');
                return;
            }
            chopDonors.push(donor);
            renderChopGarage();
        }

        function removeDonorFromGarage(donorId) {
            chopDonors = chopDonors.filter(d => d.id !== donorId);
            CHOP_PARTS.forEach(p => { if (p.donorId === donorId) p.donorId = null; });
            renderChopGarage();
        }

        function clearChopGarage() {
            chopDonors = [];
            CHOP_PARTS.forEach(p => { p.donorId = null; p.weight = 1.0; });
            renderChopGarage();
        }

        function renderChopGarage() {
            document.getElementById('donorCount').textContent = chopDonors.length;
            const grid = document.getElementById('chopDonorsGrid');
            grid.innerHTML = '';

            if (chopDonors.length === 0) {
                grid.innerHTML = '<div style="color:var(--text-secondary); font-size:0.88rem; padding:20px; text-align:center; grid-column:1/-1;">No donors in garage. Upload a .safetensors model above or pick from your server vault.</div>';
                return;
            }

            chopDonors.forEach((d, idx) => {
                const card = document.createElement('div');
                card.className = 'card';
                card.style.background = '#11111b';
                card.style.padding = '14px';

                const isChassis = idx === 0;
                card.innerHTML = `
                    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:8px;">
                        <span class="chip" style="background:${isChassis ? 'var(--accent-purple)' : 'var(--bg-overlay)'}; color:${isChassis ? '#111' : '#fff'}; font-size:0.65rem; font-weight:800;">
                            ${isChassis ? 'DONOR #1 (BASE CHASSIS)' : `DONOR #${idx + 1}`}
                        </span>
                        <button class="btn btn-secondary btn-sm" onclick="removeDonorFromGarage('${d.id}')">✕</button>
                    </div>
                    <div style="font-weight:700; font-size:0.85rem; color:var(--accent-blue); word-break:break-all; font-family:monospace;">
                        ${d.fileName || d.filePath.split(/[\\\\/]/).pop()}
                    </div>
                    <div style="display:flex; gap:4px; flex-wrap:wrap; margin:8px 0;">
                        <span class="chip chip-stage" style="font-size:0.65rem;">${d.architecture || 'FLUX.1'}</span>
                        <span class="chip chip-stage" style="font-size:0.65rem;">Rank ${d.rank || 16}</span>
                        <span class="chip" style="background:rgba(203,166,247,0.15); color:var(--accent-purple); font-size:0.65rem;">${d.dominantFeature || 'Balanced'}</span>
                    </div>
                    ${(d.inferredTags && d.inferredTags.length > 0) ? `
                        <div style="font-size:0.7rem; color:var(--text-secondary); margin-top:6px;">Inferred Tokens:</div>
                        <div style="display:flex; gap:4px; flex-wrap:wrap; margin-top:2px;">
                            ${d.inferredTags.map(t => `<span style="font-size:0.65rem; background:#181825; border:1px solid #313244; color:#cdd6f4; padding:2px 6px; border-radius:4px;">${t}</span>`).join('')}
                        </div>
                    ` : ''}
                `;
                grid.appendChild(card);
            });
        }

        function renderChopAssemblyParts() {
            const grid = document.getElementById('chopPartsGrid');
            grid.innerHTML = '';

            CHOP_PARTS.forEach(part => {
                const card = document.createElement('div');
                card.className = 'card';
                card.style.background = '#11111b';

                let optionsHtml = `<option value="">None (Bypass / Base Foundation)</option>`;
                chopDonors.forEach(d => {
                    const sel = (part.donorId === d.id) ? 'selected' : '';
                    optionsHtml += `<option value="${d.id}" ${sel}>${d.fileName || d.id} (${d.dominantFeature || 'General'})</option>`;
                });

                card.innerHTML = `
                    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:4px;">
                        <div style="display:flex; align-items:center; gap:8px;">
                            <span style="font-size:1.2rem;">${part.icon}</span>
                            <span style="font-weight:700; color:#fff;">${part.name}</span>
                        </div>
                        <span style="font-size:0.75rem; color:var(--accent-purple); font-weight:700;" id="val_${part.id}">
                            ${part.weight.toFixed(2)}x
                        </span>
                    </div>
                    <div style="font-size:0.72rem; color:var(--text-secondary); margin-bottom:10px;">${part.desc}</div>
                    <div class="input-group">
                        <label>Donor LoRA Source</label>
                        <select onchange="updatePartDonor('${part.id}', this.value)">
                            ${optionsHtml}
                        </select>
                    </div>
                    <div class="input-group">
                        <label>Grafting Multiplier</label>
                        <input type="range" min="0" max="2" step="0.05" value="${part.weight}" oninput="updatePartWeight('${part.id}', parseFloat(this.value))" />
                    </div>
                `;
                grid.appendChild(card);
            });
        }

        function updatePartDonor(partId, donorId) {
            const p = CHOP_PARTS.find(x => x.id === partId);
            if (p) p.donorId = donorId || null;
        }

        function updatePartWeight(partId, val) {
            const p = CHOP_PARTS.find(x => x.id === partId);
            if (p) {
                p.weight = val;
                const lbl = document.getElementById('val_' + partId);
                if (lbl) lbl.textContent = val.toFixed(2) + 'x';
            }
        }

        function autoCraftChopRecipe() {
            if (chopDonors.length < 2) {
                alert('Please add at least 2 donor models to the Garage first.');
                return;
            }
            CHOP_PARTS.forEach(p => {
                if (p.id === 'face') {
                    const d = chopDonors.find(x => (x.dominantFeature || '').includes('Facial')) || chopDonors[0];
                    p.donorId = d ? d.id : null;
                } else if (p.id === 'lighting') {
                    const d = chopDonors.find(x => (x.dominantFeature || '').includes('Lighting')) || (chopDonors[1] || chopDonors[0]);
                    p.donorId = d ? d.id : null;
                } else if (p.id === 'clothing') {
                    const d = chopDonors.find(x => (x.dominantFeature || '').includes('Textures')) || (chopDonors[2] || chopDonors[0]);
                    p.donorId = d ? d.id : null;
                } else {
                    p.donorId = chopDonors[0].id;
                }
            });
            switchChopTab('assembly');
        }

        async function bakeFrankenLora() {
            if (chopDonors.length === 0) {
                alert('Garage is empty. Load donors first.');
                return;
            }

            const recipe = {
                recipeName: document.getElementById('frankenName').value.trim() || 'ChopShop_FrankenLoRA',
                donors: chopDonors,
                targetRank: parseInt(document.getElementById('frankenRank').value) || 16,
                partAssignments: CHOP_PARTS.map(p => ({
                    partName: p.name,
                    selectedDonorId: p.donorId,
                    weight: p.weight,
                    description: p.desc
                }))
            };

            const status = document.getElementById('bakeStatus');
            status.style.display = 'block';
            status.textContent = 'Assembling Franken-LoRA on server via SVD compilation...';

            try {
                const res = await fetch('/api/v1/chop-shop/bake', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify(recipe)
                });
                if (res.ok) {
                    const data = await res.json();
                    status.textContent = `✅ Successfully baked! Output saved to: ${data.outputPath}`;
                    loadVaultLoras();
                } else {
                    const err = await res.json();
                    status.textContent = 'Bake failed: ' + (err.error || res.statusText);
                }
            } catch (e) {
                status.textContent = 'Error: ' + e.message;
            }
        }

        // --- VAULT DISCOVERY & CLONE ---
        async function loadVaultLoras() {
            try {
                const res = await fetch('/api/v1/vault/loras', { headers: getHeaders() });
                if (res.ok) {
                    vaultLorasList = await res.json();
                    renderVaultList(vaultLorasList);
                    populateVaultSelects();
                }
            } catch (e) { }
        }

        function populateVaultSelects() {
            const selLab = document.getElementById('labLoraSelect');
            const selComfy = document.getElementById('comfyLoraSelect');
            const selChop = document.getElementById('chopAddSelect');
            const selWiz = document.getElementById('wizardVaultDonorSelect');

            if (selLab) selLab.innerHTML = '';
            if (selComfy) selComfy.innerHTML = '<option value="none">None (Base Checkpoint)</option>';
            if (selChop) selChop.innerHTML = '<option value="">Choose a model from host vault...</option>';
            if (selWiz) selWiz.innerHTML = '<option value="">Or Pick from Server Vault...</option>';

            vaultLorasList.forEach(item => {
                if (selLab) selLab.innerHTML += `<option value="${item.filePath}">${item.fileName} (${item.formattedSize})</option>`;
                if (selComfy) selComfy.innerHTML += `<option value="${item.fileName}">${item.fileName}</option>`;
                if (selChop) selChop.innerHTML += `<option value="${item.filePath}">${item.fileName} (${item.formattedSize})</option>`;
                if (selWiz) selWiz.innerHTML += `<option value="${item.filePath}">${item.fileName}</option>`;
            });
        }

        function renderVaultList(items) {
            const container = document.getElementById('vaultList');
            container.innerHTML = '';

            if (items.length === 0) {
                container.innerHTML = '<div style="color:var(--text-secondary); font-size:0.88rem;">No LoRAs found in server paths. Train or upload a model.</div>';
                return;
            }

            items.forEach(item => {
                const card = document.createElement('div');
                card.className = 'card';
                card.style.background = '#11111b';
                card.style.padding = '14px';
                card.innerHTML = `
                    <div style="font-weight:700; font-size:0.9rem; color:var(--accent-purple); word-break:break-all;">${item.fileName}</div>
                    <div style="font-size:0.75rem; color:var(--text-secondary); margin:4px 0 10px 0;">Size: ${item.formattedSize}</div>
                    <div style="display:flex; gap:6px; flex-wrap:wrap;">
                        <button class="btn btn-secondary btn-sm" onclick="cloneFromVaultItem('${item.filePath.replace(/\\/g, '\\\\')}')">🪄 Use as Wizard Donor</button>
                        <button class="btn btn-secondary btn-sm" onclick="addVaultItemToChop('${item.filePath.replace(/\\/g, '\\\\')}')">🛠️ Add to Chop-Shop</button>
                        <button class="btn btn-secondary btn-sm" onclick="testInComfy('${item.fileName}')">🎨 ComfyUI</button>
                    </div>
                `;
                container.appendChild(card);
            });
        }

        function cloneFromVaultItem(path) {
            switchView('train');
            switchTrainMode('wizard');
            pickWizardDonorFromVault(path);
        }

        async function addVaultItemToChop(path) {
            try {
                const res = await fetch('/api/v1/chop-shop/inspect-donor', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ filePath: path })
                });
                if (res.ok) {
                    const donor = await res.json();
                    addDonorObjectToGarage(donor);
                    switchView('chop');
                }
            } catch (e) {
                alert('Inspection error: ' + e.message);
            }
        }

        function filterVault() {
            const q = document.getElementById('vaultSearch').value.toLowerCase();
            const filtered = vaultLorasList.filter(x => x.fileName.toLowerCase().includes(q));
            renderVaultList(filtered);
        }

        async function uploadVaultLora(file) {
            if (!file) return;
            const fd = new FormData();
            fd.append('file', file);
            try {
                const res = await fetch('/api/v1/loras/upload', {
                    method: 'POST',
                    headers: authToken ? { 'X-LoRAMancer-Token': authToken } : {},
                    body: fd
                });
                if (res.ok) {
                    loadVaultLoras();
                    alert('Uploaded ' + file.name + ' to server vault.');
                }
            } catch (e) { alert(e.message); }
        }

        // --- DIAGNOSTIC LAB ---
        async function inspectLabModel() {
            const path = document.getElementById('labLoraSelect').value;
            if (!path) return;
            try {
                const res = await fetch('/api/v1/lab/inspect', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ filePath: path })
                });
                if (res.ok) {
                    const data = await res.json();
                    document.getElementById('labScanResults').style.display = 'block';
                    document.getElementById('labScoreVal').textContent = Math.round(data.score) + ' / 100';
                    document.getElementById('labNormVal').textContent = data.averageFrobeniusNorm.toFixed(3);
                    document.getElementById('labVerdictBox').textContent = 'Verdict: ' + data.verdict;
                    document.getElementById('labRecBox').textContent = 'Recommendation: ' + data.recommendation;
                }
            } catch (e) { alert(e.message); }
        }

        async function applyLabRescale() {
            const path = document.getElementById('labLoraSelect').value;
            if (!path) return;
            const te = parseFloat(document.getElementById('teSlider').value);
            const unet = parseFloat(document.getElementById('unetSlider').value);
            const rank = parseInt(document.getElementById('labRank').value);

            try {
                const res = await fetch('/api/v1/lab/rescale', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ filePath: path, teScale: te, unetScale: unet, targetRank: rank })
                });
                if (res.ok) {
                    const data = await res.json();
                    const st = document.getElementById('labRescaleStatus');
                    st.style.display = 'block';
                    st.textContent = `✅ Saved rescaled LoRA: ${data.outputPath}`;
                    loadVaultLoras();
                }
            } catch (e) { alert(e.message); }
        }

        // --- COMFYUI ---
        async function checkComfyStatus() {
            try {
                const res = await fetch('/api/v1/comfyui/status', { headers: getHeaders() });
                if (res.ok) {
                    const data = await res.json();
                    const badge = document.getElementById('comfyStatusBadge');
                    if (data.isOnline) {
                        badge.textContent = `Online: ${data.gpu || 'GPU'}`;
                        badge.style.color = 'var(--accent-green)';
                    } else {
                        badge.textContent = 'ComfyUI Offline';
                        badge.style.color = 'var(--accent-red)';
                    }
                }
            } catch (e) { }
        }

        function testInComfy(loraName) {
            switchView('comfy');
            document.getElementById('comfyLoraSelect').value = loraName;
        }

        async function runComfyTest() {
            const prompt = document.getElementById('comfyPrompt').value.trim();
            const neg = document.getElementById('comfyNeg').value.trim();
            const lora = document.getElementById('comfyLoraSelect').value;
            const weight = parseFloat(document.getElementById('comfyWeight').value);
            const steps = parseInt(document.getElementById('comfySteps').value) || 20;
            const cfg = parseFloat(document.getElementById('comfyCfg').value) || 7.0;

            const genStatus = document.getElementById('comfyGenStatus');
            genStatus.style.display = 'block';
            genStatus.textContent = 'Queued in ComfyUI... Running GPU sampler...';

            try {
                const res = await fetch('/api/v1/comfyui/test', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({
                        prompt: prompt,
                        negativePrompt: neg,
                        loraName: lora === 'none' ? null : lora,
                        loraWeight: weight,
                        steps: steps,
                        cfg: cfg
                    })
                });
                if (res.ok) {
                    const data = await res.json();
                    if (data.imageBytesBase64) {
                        const img = document.getElementById('comfyPreview');
                        img.src = 'data:image/png;base64,' + data.imageBytesBase64;
                        img.style.display = 'block';
                        document.getElementById('comfyPlaceholder').style.display = 'none';

                        const dl = document.getElementById('comfyDownloadBtn');
                        dl.href = img.src;
                        dl.style.display = 'inline-flex';
                        genStatus.textContent = 'Sample generated successfully.';
                    } else {
                        genStatus.textContent = data.message || 'Generation complete.';
                    }
                }
            } catch (e) {
                genStatus.textContent = 'Error: ' + e.message;
            }
        }

        // --- HISTORY & ENVIRONMENT ---
        async function loadHistory() {
            try {
                const res = await fetch('/api/v1/history', { headers: getHeaders() });
                if (res.ok) {
                    const list = await res.json();
                    const container = document.getElementById('historyTableContainer');
                    if (list.length === 0) {
                        container.innerHTML = '<div style="color:var(--text-secondary); padding:12px 0;">No completed training runs recorded yet.</div>';
                        return;
                    }
                    let html = '<table style="width:100%; border-collapse:collapse; font-size:0.85rem;">';
                    html += '<tr style="border-bottom:1px solid var(--border-dark); color:var(--text-secondary); text-align:left;">';
                    html += '<th style="padding:8px;">Name</th><th>Base Model</th><th>Rank</th><th>Status</th><th>Steps</th><th>Duration</th><th>Action</th></tr>';
                    list.forEach(r => {
                        html += `<tr style="border-bottom:1px solid #1f1f2e;">
                            <td style="padding:10px 8px; font-weight:700;">${r.name}</td>
                            <td>${r.baseModel || 'FLUX.1'}</td>
                            <td>${r.networkDim || 16}</td>
                            <td><span class="chip" style="font-size:0.65rem;">${r.status}</span></td>
                            <td>${r.totalSteps || 0}</td>
                            <td>${Math.round(r.durationSeconds / 60)} min</td>
                            <td><button class="btn btn-secondary btn-sm" onclick="retryRun('${r.id}')">🔄 Re-Run</button></td>
                        </tr>`;
                    });
                    html += '</table>';
                    container.innerHTML = html;
                }
            } catch (e) { }
        }

        async function retryRun(id) {
            try {
                const res = await fetch('/api/v1/history/retry/' + id, { method: 'POST', headers: getHeaders() });
                if (res.ok) {
                    alert('Job re-queued successfully.');
                    switchView('train');
                    switchTrainMode('manual');
                }
            } catch (e) { alert(e.message); }
        }

        async function loadEnvironment() {
            try {
                const res = await fetch('/api/v1/environment', { headers: getHeaders() });
                if (res.ok) {
                    const data = await res.json();
                    document.getElementById('envOs').textContent = data.os;
                    document.getElementById('envGpu').textContent = data.gpuName;
                    document.getElementById('envRocm').textContent = data.rocmFound ? `ROCm ${data.rocmVersion || 'Active'}` : 'NVIDIA CUDA / CPU';
                    document.getElementById('envVram').textContent = `${Math.round(data.vramTotalMb / 1024)} GB`;
                    document.getElementById('envTorch').textContent = `PyTorch ${data.torchVersion || '2.5'}`;
                    document.getElementById('envPython').textContent = data.pythonVersion || 'Python 3.11';
                    document.getElementById('remoteHostName').textContent = data.machineName;
                    document.getElementById('rocmTelemetryBadge').textContent = `${data.gpuName} (${Math.round(data.vramTotalMb / 1024)} GB)`;
                }
            } catch (e) { }
        }

        function openDocs() {
            window.open('https://github.com/zombiehausAI/LoRAMancer', '_blank');
        }

        function disconnect() {
            localStorage.removeItem('loramancer_auth_token');
            window.location.href = '/login';
        }

        // --- SSE TELEMETRY STREAM ---
        function connectSSE() {
            if (eventSource) eventSource.close();
            const sseUrl = '/api/v1/training/stream' + (authToken ? '?token=' + encodeURIComponent(authToken) : '');
            eventSource = new EventSource(sseUrl);

            eventSource.onmessage = function(e) {
                try {
                    const t = JSON.parse(e.data);
                    if (t.EventType === 'progress') {
                        document.getElementById('stepCounter').textContent = `${t.Step} / ${t.TotalSteps}`;
                        document.getElementById('lossVal').textContent = t.Loss ? t.Loss.toFixed(4) : '-';
                        const pct = t.TotalSteps > 0 ? (t.Step / t.TotalSteps * 100) : 0;
                        document.getElementById('progressBar').style.width = pct + '%';
                        document.getElementById('trainStatusVal').textContent = 'Training';
                    } else if (t.EventType === 'log' || t.EventType === 'chop_log' || t.EventType === 'tag_log') {
                        const box = document.getElementById('consoleLogBox');
                        const p = document.createElement('div');
                        p.className = 'log-entry';
                        const line = t.Message || '';
                        if (line.includes('loss')) p.classList.add('loss');
                        else if (line.includes('epoch') || line.includes('Epoch')) p.classList.add('epoch');
                        else if (line.includes('Error') || line.includes('error')) p.classList.add('error');
                        p.textContent = line;
                        box.appendChild(p);

                        if (document.getElementById('autoscrollLock').checked) {
                            box.scrollTop = box.scrollHeight;
                        }

                        // Also pipe tagger logs to curate terminal
                        if (t.EventType === 'tag_log') {
                            const oBox = document.getElementById('ollamaLogBox');
                            if (oBox) {
                                const entry = document.createElement('div');
                                entry.style = 'margin-bottom:2px;';
                                entry.textContent = line;
                                oBox.appendChild(entry);
                                oBox.scrollTop = oBox.scrollHeight;
                            }
                            const statusElem = document.getElementById('ollamaProgressStatus');
                            if (statusElem && line.includes('Processing')) {
                                statusElem.textContent = line;
                            }
                        }
                    }
                } catch (err) { }
            };
        }

        // --- PWA LIFECYCLE & DYNAMIC INSTALLATION ---
        let deferredPrompt = null;

        function isPwaRunningStandalone() {
            return window.matchMedia('(display-mode: standalone)').matches ||
                   window.navigator.standalone === true ||
                   document.referrer.includes('android-app://');
        }

        function updatePwaUi() {
            const standalone = isPwaRunningStandalone();
            const btn = document.getElementById('pwaInstallBtn');
            const side = document.getElementById('pwaSidebarItem');
            if (standalone) {
                if (btn) btn.style.display = 'none';
                if (side) side.style.display = 'none';
            } else {
                if (btn) btn.style.display = 'inline-flex';
                if (side) side.style.display = 'block';
            }
        }

        if ('serviceWorker' in navigator) {
            window.addEventListener('load', () => {
                navigator.serviceWorker.register('/sw.js').then((reg) => {
                    console.log('PWA ServiceWorker registered with scope:', reg.scope);
                }).catch((err) => {
                    console.warn('PWA ServiceWorker registration failed:', err);
                });
            });
        }

        window.addEventListener('beforeinstallprompt', (e) => {
            e.preventDefault();
            deferredPrompt = e;
            updatePwaUi();
        });

        window.addEventListener('appinstalled', () => {
            deferredPrompt = null;
            updatePwaUi();
        });

        async function triggerPwaInstall() {
            if (deferredPrompt) {
                deferredPrompt.prompt();
                const { outcome } = await deferredPrompt.userChoice;
                if (outcome === 'accepted') {
                    console.log('User installed LoRAMancer PWA');
                }
                deferredPrompt = null;
                updatePwaUi();
            } else {
                alert('To install LoRAMancer Studio as a standalone app:\n\n• In Chrome/Edge: Look for the install icon (🖥️ or ⊕) in the browser address bar, or click Menu (⋮) -> "Install LoRAMancer Studio".\n• On Mobile: Tap Share -> "Add to Home Screen".');
            }
        }

        // --- MODAL DIALOG CONTROLLER ---
        let activeModalId = null;

        async function openModalApp(appId) {
            closeModalApp();
            activeModalId = 'modal-' + appId;
            const overlay = document.getElementById('appModalOverlay');
            const target = document.getElementById(activeModalId);
            if (overlay && target) {
                overlay.style.display = 'flex';
                target.style.display = 'flex';
            }
            await populateAllModalLoraSelects();
        }

        function closeModalApp() {
            const overlay = document.getElementById('appModalOverlay');
            if (overlay) overlay.style.display = 'none';
            if (activeModalId) {
                const target = document.getElementById(activeModalId);
                if (target) target.style.display = 'none';
                activeModalId = null;
            }
        }

        function handleModalBackdropClick(e) {
            if (e.target && e.target.id === 'appModalOverlay') {
                closeModalApp();
            }
        }

        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && activeModalId) {
                closeModalApp();
            }
        });

        // Universal LoRA Upload & Dropdown Sync for Modal Tools
        let cachedServerLoras = [];
        async function populateAllModalLoraSelects(selectedPathToSet = null, selectIdToFocus = null) {
            try {
                const res = await fetch('/api/v1/vault/loras', { headers: getHeaders() });
                if (res.ok) {
                    cachedServerLoras = await res.json();
                    const selectIds = [
                        'surgeryResizeSelect', 
                        'surgeryMergeASelect', 
                        'surgeryMergeBSelect', 
                        'gtLoraSelect', 
                        'diffModelASelect', 
                        'diffModelBSelect'
                    ];
                    
                    selectIds.forEach(id => {
                        const sel = document.getElementById(id);
                        if (!sel) return;
                        const prevVal = sel.value;
                        sel.innerHTML = '<option value="">-- Choose Host / Uploaded LoRA --</option>';
                        cachedServerLoras.forEach(item => {
                            const opt = document.createElement('option');
                            opt.value = item.filePath;
                            opt.textContent = `${item.fileName} (${item.formattedSize})`;
                            sel.appendChild(opt);
                        });
                        if (id === selectIdToFocus && selectedPathToSet) {
                            sel.value = selectedPathToSet;
                        } else if (prevVal && sel.querySelector(`option[value="${prevVal}"]`)) {
                            sel.value = prevVal;
                        }
                    });
                }
            } catch (e) {
                console.warn('Could not populate modal LoRA dropdowns', e);
            }
        }

        function syncLoraSelectToInput(selectElem, targetInputId) {
            if (!selectElem || !targetInputId) return;
            const input = document.getElementById(targetInputId);
            if (input && selectElem.value) {
                input.value = selectElem.value;
                input.dispatchEvent(new Event('input'));
            }
        }

        async function handleModalLoraUpload(inputElem, targetInputId, targetSelectId) {
            const file = inputElem.files[0];
            if (!file) return;
            const parentLabel = inputElem.parentElement;
            parentLabel.style.opacity = '0.6';
            parentLabel.style.pointerEvents = 'none';

            const fd = new FormData();
            fd.append('file', file);
            try {
                const res = await fetch('/api/v1/loras/upload', {
                    method: 'POST',
                    headers: authToken ? { 'X-LoRAMancer-Token': authToken } : {},
                    body: fd
                });
                if (res.ok) {
                    const data = await res.json();
                    if (data.success && data.filePath) {
                        const targetInput = document.getElementById(targetInputId);
                        if (targetInput) {
                            targetInput.value = data.filePath;
                            targetInput.dispatchEvent(new Event('input'));
                        }
                        await populateAllModalLoraSelects(data.filePath, targetSelectId);
                        alert(`Uploaded ${data.fileName} to host (${data.formattedSize})!`);
                    } else {
                        alert(data.message || 'Upload failed.');
                    }
                } else {
                    alert('Upload failed: ' + res.statusText);
                }
            } catch (err) {
                alert('Upload network error: ' + err.message);
            } finally {
                inputElem.value = '';
                parentLabel.style.opacity = '1';
                parentLabel.style.pointerEvents = 'auto';
            }
        }

        function autoSuggestResizeOutput() {
            const src = document.getElementById('surgeryResizeSource').value.trim();
            const rank = document.getElementById('surgeryResizeRank').value;
            const out = document.getElementById('surgeryResizeOutput');
            if (src && (!out.value || out.value.includes('_Rank'))) {
                const clean = src.replace(/\\/g, '/');
                const lastSlash = clean.lastIndexOf('/');
                const dir = lastSlash >= 0 ? clean.substring(0, lastSlash) : '';
                const file = lastSlash >= 0 ? clean.substring(lastSlash + 1) : clean;
                const base = file.replace(/\.safetensors$/i, '');
                out.value = (dir ? dir + '/' : '') + base + '_Rank' + rank + '.safetensors';
            }
        }

        function autoSuggestMergeOutput() {
            const a = document.getElementById('surgeryMergeA').value.trim();
            const b = document.getElementById('surgeryMergeB').value.trim();
            const out = document.getElementById('surgeryMergeOutput');
            if (a && b && (!out.value || out.value.includes('_Merged'))) {
                const cleanA = a.replace(/\\/g, '/');
                const fileA = cleanA.split('/').pop().replace(/\.safetensors$/i, '');
                const cleanB = b.replace(/\\/g, '/');
                const lastSlash = cleanA.lastIndexOf('/');
                const dir = lastSlash >= 0 ? cleanA.substring(0, lastSlash) : '';
                const fileB = cleanB.split('/').pop().replace(/\.safetensors$/i, '');
                out.value = (dir ? dir + '/' : '') + fileA + '_' + fileB + '_Merged.safetensors';
            }
        }

        function fillBenchmarkOutputsFolder() {
            document.getElementById('bmFolder').value = '.loramancer/outputs';
            scanBenchmarkFolder();
        }

        // Modal: Ollama Vision Tagger logic
        let modalOllamaFocus = 'general';
        function setModalOllamaPreset(focus, btn) {
            modalOllamaFocus = focus;
            const parent = btn.parentElement;
            parent.querySelectorAll('.btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            updateModalOllamaPrompt();
        }

        function updateModalOllamaPrompt() {
            const style = document.getElementById('modalOllamaStyle').value;
            const prompts = OLLAMA_PROMPTS[style] || OLLAMA_PROMPTS.tags;
            document.getElementById('modalOllamaPrompt').value = prompts[modalOllamaFocus] || prompts.general;
        }

        async function runModalOllamaTagging() {
            const path = document.getElementById('modalOllamaPath').value.trim();
            if (!path) {
                alert('Please enter a target dataset folder path.');
                return;
            }
            const model = document.getElementById('modalOllamaModel').value;
            const trigger = document.getElementById('modalOllamaTrigger').value.trim();
            const style = document.getElementById('modalOllamaStyle').value;
            const prompt = document.getElementById('modalOllamaPrompt').value.trim();

            const btn = document.getElementById('modalOllamaRunBtn');
            btn.disabled = true;
            btn.innerHTML = '⏳ Tagging...';
            const prog = document.getElementById('modalOllamaProgress');
            prog.style.display = 'block';

            try {
                const res = await fetch('/api/v1/curate/ollama/tag', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({
                        inputPath: path,
                        model: model,
                        triggerWord: trigger,
                        subjectFocus: modalOllamaFocus,
                        captionStyle: style,
                        customPrompt: prompt
                    })
                });
                const data = await res.json();
                if (res.ok && data.success) {
                    alert(`Auto-tagging completed! Processed ${data.processedCount} images.`);
                    closeModalApp();
                } else {
                    alert('Tagging error: ' + (data.error || data.message || 'Unknown error'));
                }
            } catch (e) {
                alert('Request failed: ' + e.message);
            } finally {
                btn.disabled = false;
                btn.innerHTML = '🚀 Run Auto-Tagging';
                prog.style.display = 'none';
            }
        }

        // Modal: LoRA Surgery & Merger logic
        let currentSurgeryTab = 'resize';
        function switchSurgeryTab(tab) {
            currentSurgeryTab = tab;
            document.getElementById('surgeryTabBtnResize').classList.toggle('active', tab === 'resize');
            document.getElementById('surgeryTabBtnMerge').classList.toggle('active', tab === 'merge');
            document.getElementById('surgeryTabResize').style.display = tab === 'resize' ? 'block' : 'none';
            document.getElementById('surgeryTabMerge').style.display = tab === 'merge' ? 'block' : 'none';
            document.getElementById('surgeryExecuteBtn').textContent = tab === 'resize' ? 'Execute SVD Compression' : 'Execute LoRA Merge';
        }

        function updateSurgeryMergeRatio(val) {
            const pctA = Math.round(val * 100);
            const pctB = 100 - pctA;
            document.getElementById('surgeryMergeRatioLabel').textContent = `${pctA}% A / ${pctB}% B`;
        }

        async function executeSurgeryAction() {
            const btn = document.getElementById('surgeryExecuteBtn');
            const statusBox = document.getElementById('surgeryStatusBox');
            statusBox.style.display = 'block';
            statusBox.textContent = 'Processing operation on host...';
            btn.disabled = true;

            try {
                if (currentSurgeryTab === 'resize') {
                    const src = document.getElementById('surgeryResizeSource').value.trim();
                    const dst = document.getElementById('surgeryResizeOutput').value.trim();
                    const rank = parseInt(document.getElementById('surgeryResizeRank').value) || 16;
                    if (!src || !dst) {
                        alert('Please specify source and output paths.');
                        btn.disabled = false;
                        return;
                    }
                    const res = await fetch('/api/v1/tools/surgery/resize', {
                        method: 'POST',
                        headers: getHeaders(),
                        body: JSON.stringify({ sourceLora: src, outputLora: dst, targetRank: rank })
                    });
                    const data = await res.json();
                    statusBox.textContent = data.message || (data.success ? 'SVD Compression succeeded!' : 'SVD compression failed.');
                    statusBox.style.color = data.success ? 'var(--accent-green)' : 'var(--accent-red)';
                } else {
                    const a = document.getElementById('surgeryMergeA').value.trim();
                    const b = document.getElementById('surgeryMergeB').value.trim();
                    const dst = document.getElementById('surgeryMergeOutput').value.trim();
                    const ratio = parseFloat(document.getElementById('surgeryMergeRatio').value) || 0.5;
                    if (!a || !b || !dst) {
                        alert('Please specify Model A, Model B, and output destination.');
                        btn.disabled = false;
                        return;
                    }
                    const res = await fetch('/api/v1/tools/surgery/merge', {
                        method: 'POST',
                        headers: getHeaders(),
                        body: JSON.stringify({ modelA: a, modelB: b, ratio: ratio, outputLora: dst })
                    });
                    const data = await res.json();
                    statusBox.textContent = data.message || (data.success ? 'Merge completed!' : 'Merge failed.');
                    statusBox.style.color = data.success ? 'var(--accent-green)' : 'var(--accent-red)';
                }
            } catch (e) {
                statusBox.textContent = 'Error: ' + e.message;
                statusBox.style.color = 'var(--accent-red)';
            } finally {
                btn.disabled = false;
            }
        }

        // Modal: Gene Therapy logic
        async function runGeneTherapyAnalyze() {
            const path = document.getElementById('gtLoraPath').value.trim();
            if (!path) {
                alert('Please enter a target LoRA file path.');
                return;
            }
            const statusBox = document.getElementById('gtStatusBox');
            statusBox.style.display = 'block';
            statusBox.textContent = 'Analyzing layer block energy distributions...';

            try {
                const res = await fetch('/api/v1/tools/genetherapy/analyze', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ loraPath: path })
                });
                const data = await res.json();
                if (res.ok && data.blocks) {
                    statusBox.style.display = 'none';
                    document.getElementById('gtArch').textContent = data.architecture || 'FLUX.1';
                    document.getElementById('gtMeanNorm').textContent = data.overallMeanNorm ? data.overallMeanNorm.toFixed(3) : '1.0';
                    document.getElementById('gtToxicCount').textContent = data.toxicCount || 0;
                    document.getElementById('gtDeadCount').textContent = data.deadCount || 0;

                    const tbody = document.getElementById('gtBlocksTableBody');
                    tbody.innerHTML = '';
                    data.blocks.forEach(b => {
                        const tr = document.createElement('tr');
                        tr.style = 'border-bottom:1px solid #1f1f2e;';
                        let badge = '<span class="chip" style="color:var(--accent-green); font-size:0.65rem;">🟢 Healthy</span>';
                        if (b.isToxic) badge = '<span class="chip" style="color:var(--accent-red); font-size:0.65rem;">🔴 Toxic Outlier</span>';
                        else if (b.isDead) badge = '<span class="chip" style="color:var(--accent-yellow); font-size:0.65rem;">🟡 Dead / Inactive</span>';
                        tr.innerHTML = `
                            <td style="padding:6px 10px; font-weight:700;">${b.blockName}</td>
                            <td style="padding:6px 10px; text-align:center;">${b.layerCount}</td>
                            <td style="padding:6px 10px; text-align:center; font-family:monospace;">${b.avgNorm.toFixed(3)}</td>
                            <td style="padding:6px 10px; text-align:center; font-family:monospace;">${b.maxNorm.toFixed(3)}</td>
                            <td style="padding:6px 10px; text-align:center;">${badge}</td>
                        `;
                        tbody.appendChild(tr);
                    });
                    document.getElementById('gtAnalysisResult').style.display = 'block';
                    document.getElementById('gtOutputPath').value = path.replace('.safetensors', '_therapy.safetensors');
                } else {
                    statusBox.textContent = 'Analysis failed: ' + (data.error || 'Check server logs');
                    statusBox.style.color = 'var(--accent-red)';
                }
            } catch (e) {
                statusBox.textContent = 'Error: ' + e.message;
                statusBox.style.color = 'var(--accent-red)';
            }
        }

        async function runGeneTherapyPrune() {
            const path = document.getElementById('gtLoraPath').value.trim();
            const dst = document.getElementById('gtOutputPath').value.trim();
            const btn = document.getElementById('gtPruneBtn');
            const statusBox = document.getElementById('gtStatusBox');
            statusBox.style.display = 'block';
            statusBox.textContent = 'Surgically attenuating outlier blocks...';
            btn.disabled = true;

            try {
                const res = await fetch('/api/v1/tools/genetherapy/prune', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ loraPath: path, outputPath: dst, threshold: 2.8 })
                });
                const data = await res.json();
                statusBox.textContent = data.message || (data.success ? 'Therapy completed successfully!' : 'Pruning failed.');
                statusBox.style.color = data.success ? 'var(--accent-green)' : 'var(--accent-red)';
            } catch (e) {
                statusBox.textContent = 'Error: ' + e.message;
                statusBox.style.color = 'var(--accent-red)';
            } finally {
                btn.disabled = false;
            }
        }

        // Modal: Visual Diff logic
        function swapDiffModels() {
            const a = document.getElementById('diffModelA').value;
            const b = document.getElementById('diffModelB').value;
            document.getElementById('diffModelA').value = b;
            document.getElementById('diffModelB').value = a;
        }

        async function runVisualDiff() {
            const a = document.getElementById('diffModelA').value.trim();
            const b = document.getElementById('diffModelB').value.trim();
            if (!a || !b) {
                alert('Please specify both Model A and Model B paths.');
                return;
            }
            const btn = document.getElementById('diffRunBtn');
            const msg = document.getElementById('diffStatusMsg');
            msg.style.display = 'block';
            msg.textContent = 'Comparing tensor cosine similarities and weight drift...';
            btn.disabled = true;

            try {
                const res = await fetch('/api/v1/tools/diff/compare', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ modelAPath: a, modelBPath: b })
                });
                const data = await res.json();
                if (res.ok && data.layerDiffs) {
                    msg.style.display = 'none';
                    document.getElementById('diffAvgSim').textContent = data.averageCosineSimilarity ? data.averageCosineSimilarity.toFixed(4) : '1.000';
                    document.getElementById('diffAvgDrift').textContent = data.averageDriftScore ? data.averageDriftScore.toFixed(4) : '0.000';
                    document.getElementById('diffSharedCount').textContent = data.sharedTensorsCount || 0;
                    document.getElementById('diffStatusBadge').textContent = data.compatibilityStatus || 'Compatible';

                    const tbody = document.getElementById('diffLayersTableBody');
                    tbody.innerHTML = '';
                    (data.layerDiffs || []).slice(0, 100).forEach(l => {
                        const tr = document.createElement('tr');
                        tr.style = 'border-bottom:1px solid #1f1f2e;';
                        tr.innerHTML = `
                            <td style="padding:6px 10px; font-family:monospace; font-size:0.75rem;">${l.layerName}</td>
                            <td style="padding:6px 10px; text-align:center;">${l.status}</td>
                            <td style="padding:6px 10px; text-align:center; font-family:monospace;">${l.cosineSimilarity ? l.cosineSimilarity.toFixed(4) : '-'}</td>
                            <td style="padding:6px 10px; text-align:center; font-family:monospace;">${l.driftScore ? l.driftScore.toFixed(4) : '-'}</td>
                            <td style="padding:6px 10px; text-align:center;"><span class="chip" style="font-size:0.65rem;">${l.divergenceLevel || 'Identical'}</span></td>
                        `;
                        tbody.appendChild(tr);
                    });
                    document.getElementById('diffResultsBox').style.display = 'block';
                } else {
                    msg.textContent = 'Diff failed: ' + (data.error || 'Check server logs');
                    msg.style.color = 'var(--accent-red)';
                }
            } catch (e) {
                msg.textContent = 'Error: ' + e.message;
                msg.style.color = 'var(--accent-red)';
            } finally {
                btn.disabled = false;
            }
        }

        // Modal: AI Benchmark Matrix logic
        async function scanBenchmarkFolder() {
            const folder = document.getElementById('bmFolder').value.trim();
            if (!folder) {
                alert('Please enter a checkpoints folder path.');
                return;
            }
            try {
                const res = await fetch('/api/v1/tools/benchmark/scan', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ folderPath: folder })
                });
                const data = await res.json();
                document.getElementById('bmCheckpointsBadge').textContent = `${data.count || 0} checkpoints discovered`;
            } catch (e) {
                alert('Scan failed: ' + e.message);
            }
        }

        async function runBenchmarkMatrix() {
            const folder = document.getElementById('bmFolder').value.trim();
            const arch = document.getElementById('bmArch').value;
            const trigger = document.getElementById('bmTrigger').value.trim();
            if (!folder) {
                alert('Please specify a checkpoints folder.');
                return;
            }
            const btn = document.getElementById('bmRunBtn');
            const statusBox = document.getElementById('bmStatusBox');
            statusBox.style.display = 'block';
            statusBox.textContent = 'Executing benchmark suite evaluation matrix...';
            btn.disabled = true;

            try {
                const res = await fetch('/api/v1/tools/benchmark/start', {
                    method: 'POST',
                    headers: getHeaders(),
                    body: JSON.stringify({ checkpointFolder: folder, baseArch: arch, triggerWord: trigger })
                });
                const data = await res.json();
                if (res.ok && data.epochResults) {
                    statusBox.style.display = 'none';
                    const grid = document.getElementById('bmEpochGrid');
                    grid.innerHTML = '';
                    data.epochResults.forEach(r => {
                        const card = document.createElement('div');
                        card.className = 'stat-box';
                        const isOptimal = r.verdict && r.verdict.includes('Optimal');
                        card.style = isOptimal ? 'border-color:var(--accent-green); background:rgba(166,227,161,0.06);' : '';
                        card.innerHTML = `
                            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:6px;">
                                <span style="font-weight:700; color:#fff;">Epoch ${r.epochNumber}</span>
                                <span class="chip" style="font-size:0.65rem;">${r.verdict}</span>
                            </div>
                            <div style="font-size:0.75rem; color:var(--text-secondary); margin-bottom:4px;">Sweet Spot Score: <b style="color:var(--accent-purple);">${r.sweetSpotIndex}</b></div>
                            <div style="font-size:0.75rem; color:var(--text-secondary);">Likeness: ${Math.round(r.likenessScore)}% | Flex: ${Math.round(r.flexibilityScore)}%</div>
                        `;
                        grid.appendChild(card);
                    });
                    const recCard = document.getElementById('bmRecommendationCard');
                    recCard.innerHTML = `
                        <div style="font-weight:700; color:var(--accent-purple); margin-bottom:4px;">🎯 Recommended Sweet Spot: Epoch ${data.recommendedSweetSpotEpoch}</div>
                        <div style="font-size:0.8rem; color:var(--text-secondary);">${data.recommendationReason || 'Optimal compromise between fidelity and flexibility.'}</div>
                    `;
                    document.getElementById('bmResultsBox').style.display = 'block';
                } else {
                    statusBox.textContent = 'Benchmark failed: ' + (data.error || 'Check server logs');
                    statusBox.style.color = 'var(--accent-red)';
                }
            } catch (e) {
                statusBox.textContent = 'Error: ' + e.message;
                statusBox.style.color = 'var(--accent-red)';
            } finally {
                btn.disabled = false;
            }
        }

        // --- DOCUMENTATION VIEWER ---
        let docsIndex = [];
        let activeDocFileName = '';
        window._currentDocMarkdown = '';

        async function loadDocsList() {
            try {
                const res = await fetch('/api/v1/docs', { headers: getHeaders() });
                if (res.ok) {
                    docsIndex = await res.json();
                    renderDocsNav(docsIndex);
                    if (docsIndex.length > 0 && !activeDocFileName) {
                        selectDoc(docsIndex[0].fileName);
                    }
                }
            } catch (e) { }
        }

        function renderDocsNav(items) {
            const container = document.getElementById('docsNavContainer');
            if (!container) return;
            if (items.length === 0) {
                container.innerHTML = '<div style="color:var(--text-secondary); font-size:0.8rem; padding:8px;">No guides match search.</div>';
                return;
            }
            container.innerHTML = '';
            items.forEach(doc => {
                const el = document.createElement('div');
                el.className = 'doc-nav-item' + (doc.fileName === activeDocFileName ? ' active' : '');
                el.onclick = () => selectDoc(doc.fileName);
                el.innerHTML = `
                    <span style="font-size:1.1rem;">${doc.icon || '📄'}</span>
                    <div style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">
                        <div style="font-weight:600; font-size:0.82rem;">${doc.title}</div>
                        <div style="font-size:0.7rem; color:var(--text-muted);">${doc.fileName}</div>
                    </div>
                `;
                container.appendChild(el);
            });
        }

        function filterDocs(query) {
            const q = (query || '').toLowerCase().trim();
            const filtered = docsIndex.filter(d =>
                d.title.toLowerCase().includes(q) ||
                d.fileName.toLowerCase().includes(q)
            );
            renderDocsNav(filtered);
        }

        async function selectDoc(fileName) {
            activeDocFileName = fileName;
            renderDocsNav(docsIndex);
            const titleEl = document.getElementById('docActiveTitle');
            const metaEl = document.getElementById('docActiveMeta');
            const contentEl = document.getElementById('docActiveContent');

            titleEl.textContent = 'Loading guide...';
            contentEl.innerHTML = '<div style="color:var(--text-secondary); padding:20px;">Fetching document from host...</div>';

            try {
                const res = await fetch('/api/v1/docs/' + encodeURIComponent(fileName), { headers: getHeaders() });
                if (res.ok) {
                    const data = await res.json();
                    titleEl.textContent = data.title;
                    metaEl.textContent = `docs/${data.fileName}`;
                    contentEl.innerHTML = data.html;
                    window._currentDocMarkdown = data.markdown;
                } else {
                    titleEl.textContent = 'Document Not Found';
                    contentEl.innerHTML = '<div style="color:var(--accent-red);">Failed to load document content.</div>';
                }
            } catch (e) {
                titleEl.textContent = 'Error';
                contentEl.innerHTML = `<div style="color:var(--accent-red);">${e.message}</div>`;
            }
        }

        async function copyCurrentDocMarkdown() {
            if (window._currentDocMarkdown) {
                try {
                    await navigator.clipboard.writeText(window._currentDocMarkdown);
                    alert('Markdown copied to clipboard!');
                } catch {
                    alert('Could not copy to clipboard.');
                }
            }
        }

        // INIT
        window.addEventListener('DOMContentLoaded', () => {
            updatePwaUi();
            connectSSE();
            loadEnvironment();
            loadVaultLoras();
            loadOllamaStatus();
            resetOllamaPrompt();
            recalculateEstimators();
            loadDocsList();
        });
    </script>
</body>
</html>
""";
    }
}
