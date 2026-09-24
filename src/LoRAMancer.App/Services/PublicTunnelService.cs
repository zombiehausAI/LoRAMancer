using System.Diagnostics;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class PublicTunnelService : IAsyncDisposable {
    private readonly SettingsService _settingsService;
    private Process? _tunnelProcess;
    private CancellationTokenSource? _tunnelCts;

    public bool IsTunnelRunning { get; private set; }
    public string ActivePublicUrl { get; private set; } = string.Empty;
    public string TunnelType { get; private set; } = "cloudflare";

    public event Action<bool, string>? OnTunnelStateChanged;

    public PublicTunnelService(SettingsService settingsService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task<string> StartTunnelAsync(int localPort = 8420, string? tunnelType = "cloudflare", string? customDomain = null, CancellationToken cancellationToken = default) {
        if (IsTunnelRunning) {
            return ActivePublicUrl;
        }

        TunnelType = tunnelType ?? "cloudflare";

        if (string.Equals(TunnelType, "custom", StringComparison.OrdinalIgnoreCase)) {
            ActivePublicUrl = !string.IsNullOrWhiteSpace(customDomain) ? customDomain : _settingsService.Current.PublicCustomDomainUrl;
            IsTunnelRunning = !string.IsNullOrWhiteSpace(ActivePublicUrl);
            OnTunnelStateChanged?.Invoke(IsTunnelRunning, ActivePublicUrl);
            return ActivePublicUrl;
        }

        _tunnelCts = new CancellationTokenSource();
        TaskCompletionSource<string> urlTcs = new();

        try {
            string cloudflaredPath = FindCloudflaredExecutable();

            ProcessStartInfo psi = new() {
                FileName = cloudflaredPath,
                Arguments = $"tunnel --url http://127.0.0.1:{localPort}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _tunnelProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            DataReceivedEventHandler handler = (sender, e) => {
                if (string.IsNullOrWhiteSpace(e.Data)) {
                    return;
                }

                Match match = Regex.Match(e.Data, @"https://[a-zA-Z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
                if (match.Success && !urlTcs.Task.IsCompleted) {
                    string foundUrl = match.Value;
                    ActivePublicUrl = foundUrl;
                    IsTunnelRunning = true;
                    urlTcs.TrySetResult(foundUrl);
                    OnTunnelStateChanged?.Invoke(true, foundUrl);
                }
            };

            _tunnelProcess.OutputDataReceived += handler;
            _tunnelProcess.ErrorDataReceived += handler;

            _tunnelProcess.Start();
            _tunnelProcess.BeginOutputReadLine();
            _tunnelProcess.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var completedTask = await Task.WhenAny(urlTcs.Task, Task.Delay(Timeout.Infinite, linked.Token));
            if (completedTask == urlTcs.Task) {
                return await urlTcs.Task;
            }

            throw new TimeoutException("Cloudflare tunnel failed to establish within 20 seconds.");
        } catch (Exception) {
            await StopTunnelAsync();
            throw;
        }
    }

    public async Task StopTunnelAsync() {
        if (!IsTunnelRunning && _tunnelProcess == null) {
            return;
        }

        try {
            _tunnelCts?.Cancel();
            if (_tunnelProcess != null && !_tunnelProcess.HasExited) {
                _tunnelProcess.Kill(entireProcessTree: true);
                await _tunnelProcess.WaitForExitAsync();
            }
        } catch {
            // Suppress process kill errors during cleanup
        } finally {
            _tunnelProcess?.Dispose();
            _tunnelProcess = null;
            IsTunnelRunning = false;
            ActivePublicUrl = string.Empty;
            OnTunnelStateChanged?.Invoke(false, string.Empty);
        }
    }

    private static string FindCloudflaredExecutable() {
        string[] candidates = {
            "cloudflared.exe",
            Path.Combine(AppContext.BaseDirectory, "tools", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "bin", "cloudflared.exe")
        };

        foreach (string candidate in candidates) {
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return "cloudflared";
    }

    public async ValueTask DisposeAsync() {
        await StopTunnelAsync();
    }
}
