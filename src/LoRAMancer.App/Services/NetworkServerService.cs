using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LoRAMancer.App.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LoRAMancer.App.Services;

public sealed class NetworkServerService : IAsyncDisposable {
    private readonly SettingsService _settingsService;
    private readonly TrainingRunnerService _trainingRunner;
    private readonly DatasetInspectorService _datasetInspector;
    private readonly AmdVenvProvisioner _venvProvisioner;
    private readonly ConcurrentBag<HttpResponse> _sseClients = new();

    private WebApplication? _webApp;
    private CancellationTokenSource? _serverCts;

    public bool IsRunning { get; private set; }
    public string ListeningUrl { get; private set; } = string.Empty;
    public int ActiveClientCount => _sseClients.Count;

    public event Action<bool, string>? OnServerStateChanged;

    public NetworkServerService(
        SettingsService settingsService,
        TrainingRunnerService trainingRunner,
        DatasetInspectorService datasetInspector,
        AmdVenvProvisioner venvProvisioner
    ) {
        _settingsService = settingsService;
        _trainingRunner = trainingRunner;
        _datasetInspector = datasetInspector;
        _venvProvisioner = venvProvisioner;

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

        // 1. Health & Status
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

        // 2. Upload Dataset ZIP Archive
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

        // 3. Start Training Job
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

        // 4. Stop Training Job
        app.MapPost("/api/v1/training/stop", (HttpContext context) => {
            if (!IsAuthorized(context, accessToken)) {
                return Results.Unauthorized();
            }

            _trainingRunner.CancelTraining();
            return Results.Ok(new { message = "Training job cancelled." });
        });

        // 5. SSE Live Telemetry Stream
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

        // 6. Embedded Responsive Web Interface (for Linux, Mac, Browser access)
        app.MapGet("/", async (HttpContext context) => {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetEmbeddedWebInterfaceHtml());
        });

        _webApp = app;
        await _webApp.StartAsync(_serverCts.Token);

        IsRunning = true;
        ListeningUrl = $"http://{(bindAddress == "0.0.0.0" ? "localhost" : bindAddress)}:{port}";
        OnServerStateChanged?.Invoke(true, ListeningUrl);
    }

    public async Task StopServerAsync() {
        if (!IsRunning || _webApp == null) {
            return;
        }

        try {
            _serverCts?.Cancel();
            await _webApp.StopAsync();
            await _webApp.DisposeAsync();
        } finally {
            _webApp = null;
            IsRunning = false;
            ListeningUrl = string.Empty;
            OnServerStateChanged?.Invoke(false, string.Empty);
        }
    }

    private static bool IsAuthorized(HttpContext context, string? accessToken) {
        if (string.IsNullOrWhiteSpace(accessToken)) {
            return true;
        }

        string authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            string token = authHeader["Bearer ".Length..].Trim();
            if (string.Equals(token, accessToken, StringComparison.Ordinal)) {
                return true;
            }
        }

        if (context.Request.Query.TryGetValue("token", out var queryToken)) {
            if (string.Equals(queryToken.ToString(), accessToken, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private void HandleProgressUpdated(TrainingProgress progress) {
        BroadcastTelemetry(new TrainingTelemetryDto {
            EventType = "step",
            Step = progress.CurrentStep,
            TotalSteps = progress.TotalSteps,
            Loss = (float)progress.CurrentLoss,
            Message = $"Step {progress.CurrentStep}/{progress.TotalSteps} (Loss: {progress.CurrentLoss:F4})"
        });
    }

    private void HandleLogReceived(string log) {
        BroadcastTelemetry(new TrainingTelemetryDto {
            EventType = "log",
            Message = log
        });
    }

    private void BroadcastTelemetry(TrainingTelemetryDto telemetry) {
        if (_sseClients.IsEmpty) {
            return;
        }

        string json = JsonSerializer.Serialize(telemetry);
        string payload = $"data: {json}\n\n";

        foreach (HttpResponse client in _sseClients) {
            try {
                client.WriteAsync(payload);
            } catch {
                // Ignore broken pipe
            }
        }
    }

    public async ValueTask DisposeAsync() {
        _trainingRunner.OnProgressUpdated -= HandleProgressUpdated;
        _trainingRunner.OnLogReceived -= HandleLogReceived;
        await StopServerAsync();
    }

    private static string GetEmbeddedWebInterfaceHtml() {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>LoRAMancer Remote Web Control</title>
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
            padding: 16px 32px;
            display: flex;
            align-items: center;
            justify-content: space-between;
        }
        .brand { display: flex; align-items: center; gap: 12px; }
        .logo-circle {
            width: 38px; height: 38px; border-radius: 50%;
            background: linear-gradient(135deg, #cba6f7, #89b4fa);
            display: flex; align-items: center; justify-content: center;
            font-weight: 700; color: #11111b; font-size: 1.2rem;
        }
        .brand h1 { font-size: 1.3rem; font-weight: 700; color: #fff; }
        .badge {
            font-size: 0.75rem; padding: 4px 10px; border-radius: 12px;
            font-weight: 600; letter-spacing: 0.5px;
        }
        .badge-live { background-color: rgba(166, 227, 161, 0.15); color: var(--green); border: 1px solid var(--green); }
        .badge-busy { background-color: rgba(249, 226, 175, 0.15); color: var(--amber); border: 1px solid var(--amber); }
        main { flex: 1; padding: 32px; max-width: 1300px; margin: 0 auto; width: 100%; display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }
        @media(max-width: 900px) { main { grid-template-columns: 1fr; } }
        .card {
            background-color: var(--bg-surface);
            border: 1px solid var(--border);
            border-radius: 14px;
            padding: 24px;
            display: flex;
            flex-direction: column;
            gap: 16px;
        }
        .card-title {
            font-size: 1.1rem; font-weight: 600; color: #fff;
            display: flex; align-items: center; justify-content: space-between;
        }
        .input-group { display: flex; flex-direction: column; gap: 6px; }
        label { font-size: 0.85rem; color: var(--text-muted); font-weight: 500; }
        input, select, textarea {
            background-color: var(--bg-card);
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 10px 14px;
            border-radius: 8px;
            font-family: inherit;
            font-size: 0.95rem;
        }
        input:focus, select:focus, textarea:focus {
            outline: none; border-color: var(--accent);
            box-shadow: 0 0 0 3px var(--accent-glow);
        }
        .btn {
            background: linear-gradient(135deg, #cba6f7, #b4befe);
            color: #11111b; font-weight: 600;
            padding: 12px 20px; border: none; border-radius: 8px;
            cursor: pointer; font-size: 0.95rem; transition: opacity 0.2s;
        }
        .btn:hover { opacity: 0.9; }
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
            height: 380px;
            overflow-y: auto;
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.82rem;
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
    </style>
</head>
<body>
    <header>
        <div class="brand">
            <div class="logo-circle">LM</div>
            <div>
                <h1>LoRAMancer</h1>
                <p style="font-size:0.8rem; color:var(--text-muted)">Remote Host Engine (AI PC)</p>
            </div>
        </div>
        <div style="display:flex; align-items:center; gap:12px;">
            <span id="hostStatus" class="badge badge-live">Ready</span>
            <span id="gpuBadge" class="badge" style="background:#313244; color:#cdd6f4;">ROCm / CUDA Active</span>
        </div>
    </header>

    <main>
        <section class="card">
            <div class="card-title">
                <span>Start Remote Training Run</span>
                <span style="font-size:0.8rem; color:var(--accent);">Web Direct</span>
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
                <label>Trigger Word / Expression</label>
                <input id="triggerWord" type="text" placeholder="e.g. ohwx portrait style" />
            </div>

            <div class="input-group">
                <label>Target Architecture</label>
                <select id="architecture">
                    <option value="flux1">FLUX.1 (Dev / Schnell)</option>
                    <option value="sdxl">SDXL 1.0 / Pony / Illustrious</option>
                    <option value="chroma">Chroma / Wan / Custom</option>
                </select>
            </div>

            <div class="input-group">
                <label>Max Training Steps</label>
                <input id="steps" type="number" value="1500" />
            </div>

            <div style="display:flex; gap:12px; margin-top:8px;">
                <button id="startBtn" class="btn" onclick="startTraining()" style="flex:1;">Launch Training on Host</button>
                <button id="stopBtn" class="btn btn-danger" onclick="stopTraining()">Stop</button>
            </div>
        </section>

        <section class="card">
            <div class="card-title">
                <span>Live Telemetry & Logs</span>
                <span id="stepCounter" style="font-size:0.85rem; color:var(--accent);">Step 0 / 0</span>
            </div>

            <div class="progress-bar-container">
                <div id="progressBar" class="progress-bar-fill"></div>
            </div>

            <div class="stat-grid">
                <div class="stat-box">
                    <div style="font-size:0.75rem; color:var(--text-muted);">LOSS</div>
                    <div id="lossVal" class="stat-val">0.0000</div>
                </div>
                <div class="stat-box">
                    <div style="font-size:0.75rem; color:var(--text-muted);">PROGRESS</div>
                    <div id="percentVal" class="stat-val">0%</div>
                </div>
                <div class="stat-box">
                    <div style="font-size:0.75rem; color:var(--text-muted);">CLIENTS</div>
                    <div id="clientsVal" class="stat-val">1</div>
                </div>
            </div>

            <div id="terminal" class="terminal">Connecting to LoRAMancer telemetry stream...</div>
        </section>
    </main>

    <script>
        const terminal = document.getElementById('terminal');
        const progressBar = document.getElementById('progressBar');
        const stepCounter = document.getElementById('stepCounter');
        const lossVal = document.getElementById('lossVal');
        const percentVal = document.getElementById('percentVal');
        const hostStatus = document.getElementById('hostStatus');

        function appendLog(msg) {
            terminal.textContent += msg + '\n';
            terminal.scrollTop = terminal.scrollHeight;
        }

        const evtSource = new EventSource('/api/v1/training/stream');
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
                }
                if (data.Message) {
                    appendLog(data.Message);
                }
            } catch(err) {
                appendLog(e.data);
            }
        };

        evtSource.onerror = function() {
            appendLog('[Stream Disconnected - Reconnecting...]');
        };

        async function startTraining() {
            const runName = document.getElementById('runName').value.trim() || 'remote_run';
            const triggerWord = document.getElementById('triggerWord').value.trim();
            const steps = parseInt(document.getElementById('steps').value) || 1500;
            const arch = document.getElementById('architecture').value;
            const fileInput = document.getElementById('datasetZip');

            let datasetPath = '';
            if (fileInput.files.length > 0) {
                appendLog('Uploading dataset ZIP archive to host...');
                const formData = new FormData();
                formData.append('file', fileInput.files[0]);
                const uploadRes = await fetch('/api/v1/datasets/upload', { method: 'POST', body: formData });
                const uploadJson = await uploadRes.json();
                if (!uploadJson.Success) {
                    appendLog('Upload warning: ' + uploadJson.Message);
                }
                datasetPath = uploadJson.ExtractedPath;
                appendLog('Dataset uncompressed to host path: ' + datasetPath);
            }

            appendLog('Submitting training job to AI PC...');
            const yamlDummy = 'name: ' + runName + '\ntarget_steps: ' + steps + '\ndataset: ' + datasetPath;
            const res = await fetch('/api/v1/training/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    RunName: runName,
                    BaseArchitecture: arch,
                    ConfigYaml: yamlDummy,
                    TriggerWord: triggerWord,
                    Steps: steps
                })
            });

            if (res.ok) {
                appendLog('Job successfully received by host! Training starting...');
            } else {
                appendLog('Error starting job: ' + res.statusText);
            }
        }

        async function stopTraining() {
            appendLog('Sending stop signal to host...');
            await fetch('/api/v1/training/stop', { method: 'POST' });
            hostStatus.textContent = 'Ready';
            hostStatus.className = 'badge badge-live';
        }
    </script>
</body>
</html>
""";
    }
}
