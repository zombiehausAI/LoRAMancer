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

public sealed class NetworkServerService : IAsyncDisposable {
    private readonly SettingsService _settingsService;
    private readonly TrainingRunnerService _trainingRunner;
    private readonly DatasetInspectorService _datasetInspector;
    private readonly AmdVenvProvisioner _venvProvisioner;
    private readonly PublicTunnelService? _publicTunnelService;
    private readonly AuthTokenManagerService? _authTokenManager;
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
        AuthTokenManagerService? authTokenManager = null
    ) {
        _settingsService = settingsService;
        _trainingRunner = trainingRunner;
        _datasetInspector = datasetInspector;
        _venvProvisioner = venvProvisioner;
        _publicTunnelService = publicTunnelService;
        _authTokenManager = authTokenManager;

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
                name = "LoRAMancer Remote Trainer",
                short_name = "LoRAMancer",
                description = "Remote AI LoRA Training & Live Monitoring Engine",
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
                const CACHE_NAME = 'loramancer-shell-v1';
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

        // 10. Embedded Responsive Web Interface & PWA App Shell (Strictly Protected)
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

        // Token Management API (Protected)
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

        if (!string.IsNullOrWhiteSpace(expectedFallbackToken) && string.Equals(token, expectedFallbackToken.Trim(), StringComparison.Ordinal)) {
            return true;
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

    private static string GetUnauthorizedAccessHtml() {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>401 Unauthorized - LoRAMancer Workstation</title>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet" />
    <style>
        :root {
            --bg-base: #11111b;
            --bg-surface: #1e1e2e;
            --border: #313244;
            --accent: #cba6f7;
            --text-main: #cdd6f4;
            --text-muted: #a6adc8;
            --red: #f38ba8;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            background-color: var(--bg-base);
            color: var(--text-main);
            font-family: 'Inter', sans-serif;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }
        .gate-card {
            background-color: var(--bg-surface);
            border: 1px solid var(--border);
            border-radius: 16px;
            padding: 36px;
            max-width: 440px;
            width: 100%;
            text-align: center;
            box-shadow: 0 16px 40px rgba(0, 0, 0, 0.6);
        }
        .icon {
            font-size: 2.8rem;
            margin-bottom: 16px;
            display: inline-block;
        }
        h1 {
            font-size: 1.4rem;
            font-weight: 700;
            color: #fff;
            margin-bottom: 8px;
        }
        p {
            font-size: 0.88rem;
            color: var(--text-muted);
            line-height: 1.5;
            margin-bottom: 24px;
        }
        input {
            width: 100%;
            background-color: #252538;
            border: 1px solid var(--border);
            color: #fff;
            padding: 12px 16px;
            border-radius: 8px;
            font-size: 1rem;
            text-align: center;
            letter-spacing: 2px;
            margin-bottom: 16px;
            outline: none;
        }
        input:focus {
            border-color: var(--accent);
            box-shadow: 0 0 0 3px rgba(203, 166, 247, 0.25);
        }
        button {
            width: 100%;
            background: linear-gradient(135deg, #cba6f7, #89b4fa);
            color: #11111b;
            font-weight: 700;
            padding: 12px;
            border: none;
            border-radius: 8px;
            cursor: pointer;
            font-size: 0.95rem;
            transition: opacity 0.2s;
        }
        button:hover { opacity: 0.92; }
        .error-msg {
            color: var(--red);
            font-size: 0.82rem;
            margin-top: 12px;
            display: none;
        }
    </style>
</head>
<body>
    <div class="gate-card">
        <div class="icon">🔒</div>
        <h1>401 - Access Denied</h1>
        <p>This LoRAMancer workstation is private. A valid Auth Token is required to access the training engine, web UI, and telemetry stream.</p>
        <input type="password" id="tokenInput" placeholder="Enter Auth Token" onkeydown="if(event.key==='Enter')authenticate()" />
        <button onclick="authenticate()">Authenticate Session</button>
        <div id="errorMsg" class="error-msg">Invalid authentication token. Access denied.</div>
    </div>
    <script>
        async function authenticate() {
            const token = document.getElementById('tokenInput').value.trim();
            if (!token) return;
            try {
                const res = await fetch('/api/v1/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ pinOrToken: token })
                });
                if (res.ok) {
                    const data = await res.json();
                    localStorage.setItem('loramancer_auth_token', data.token);
                    window.location.reload();
                } else {
                    document.getElementById('errorMsg').style.display = 'block';
                }
            } catch(e) {
                document.getElementById('errorMsg').style.display = 'block';
            }
        }
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
    <title>LoRAMancer Remote Trainer</title>
    
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
            gap: 16px;
            flex-wrap: wrap;
        }
        .brand { display: flex; align-items: center; gap: 12px; }
        .logo-circle {
            width: 38px; height: 38px; border-radius: 50%;
            background: linear-gradient(135deg, #cba6f7, #89b4fa);
            display: flex; align-items: center; justify-content: center;
            font-weight: 700; color: #11111b; font-size: 1.2rem;
            user-select: none;
        }
        .brand h1 { font-size: 1.3rem; font-weight: 700; color: #fff; }
        .badge {
            font-size: 0.75rem; padding: 4px 10px; border-radius: 12px;
            font-weight: 600; letter-spacing: 0.5px; display: inline-flex; align-items: center; gap: 4px;
        }
        .badge-live { background-color: rgba(166, 227, 161, 0.15); color: var(--green); border: 1px solid var(--green); }
        .badge-busy { background-color: rgba(249, 226, 175, 0.15); color: var(--amber); border: 1px solid var(--amber); }
        .badge-public { background-color: rgba(203, 166, 247, 0.15); color: var(--accent); border: 1px solid var(--accent); cursor: pointer; }
        main { flex: 1; padding: 32px; max-width: 1300px; margin: 0 auto; width: 100%; display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }
        @media(max-width: 900px) {
            header { padding: 12px 16px; }
            main { grid-template-columns: 1fr; padding: 16px; }
        }
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
            cursor: pointer; font-size: 0.95rem; transition: opacity 0.2s, transform 0.1s;
            display: inline-flex; align-items: center; justify-content: center; gap: 6px;
        }
        .btn:hover { opacity: 0.92; }
        .btn:active { transform: scale(0.98); }
        .btn-install {
            background: linear-gradient(135deg, #a6e3a1, #89b4fa);
            color: #11111b; padding: 6px 14px; font-size: 0.8rem; border-radius: 20px;
        }
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
        <div style="display:flex; align-items:center; gap:10px; flex-wrap:wrap;">
            <button id="installPwaBtn" class="btn btn-install" style="display:none;" onclick="installPwa()">📲 Install App</button>
            <span id="publicUrlBadge" class="badge badge-public" style="display:none;" onclick="copyPublicUrl()" title="Click to copy host URL">
                🌐 Host Active
            </span>
            <span id="hostStatus" class="badge badge-live">Ready</span>
            <span id="gpuBadge" class="badge" style="background:#313244; color:#cdd6f4;">ROCm / CUDA Active</span>
        </div>
    </header>

    <main>
        <section class="card">
            <div class="card-title">
                <span>Start Remote Training Run</span>
                <span style="font-size:0.8rem; color:var(--accent);">Web Direct / PWA</span>
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
        </section>

        <section class="card">
            <div class="card-title">
                <span>Workstation Telemetry & Loss</span>
                <span id="stepCounter" style="font-size:0.85rem; color:var(--text-muted);">Step 0 / 0</span>
            </div>

            <div class="stat-grid">
                <div class="stat-box">
                    <span style="font-size:0.75rem; color:var(--text-muted);">CURRENT LOSS</span>
                    <div id="lossVal" class="stat-val">0.0000</div>
                </div>
                <div class="stat-box">
                    <span style="font-size:0.75rem; color:var(--text-muted);">COMPLETION</span>
                    <div id="percentVal" class="stat-val">0%</div>
                </div>
                <div class="stat-box">
                    <span style="font-size:0.75rem; color:var(--text-muted);">WORKER STATE</span>
                    <div id="workerState" class="stat-val" style="font-size:1rem; color:var(--green);">IDLE</div>
                </div>
            </div>

            <div class="progress-bar-container">
                <div id="progressBar" class="progress-bar-fill"></div>
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

        // PWA Service Worker Registration
        if ('serviceWorker' in navigator) {
            window.addEventListener('load', () => {
                navigator.serviceWorker.register('/sw.js').catch(err => {
                    console.log('Service Worker registration:', err);
                });
            });
        }

        // PWA Installation Trigger
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
            if (evtSource) {
                evtSource.close();
            }
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
        }

        checkHealth();
        initTelemetryStream();

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
                if (uploadRes.status === 401) {
                    window.location.reload();
                    return;
                }
                const uploadJson = await uploadRes.json();
                if (!uploadJson.Success) {
                    appendLog('Upload warning: ' + uploadJson.Message);
                }
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

            if (res.status === 401) {
                window.location.reload();
                return;
            }

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
            if (res.status === 401) {
                window.location.reload();
                return;
            }
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
}
