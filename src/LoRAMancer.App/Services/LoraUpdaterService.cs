using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class LoraUpdaterService : IDisposable {
    private readonly PluginManagerService _pluginManager;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public string CurrentOperation { get; private set; } = "Idle";
    public string CurrentStatus { get; private set; } = "Ready";
    public string TargetFolder { get; private set; } = string.Empty;
    public int TotalFiles { get; private set; }
    public int ProcessedFiles { get; private set; }
    public int UpdatesAvailable { get; private set; }
    public int UpdatedCount { get; private set; }
    public string? LastResultJson { get; private set; }

    private readonly List<string> _liveLogs = new();
    public IReadOnlyList<string> LiveLogs {
        get {
            lock (_lock) {
                return _liveLogs.ToList();
            }
        }
    }

    public event Action? OnStateChanged;

    public LoraUpdaterService(PluginManagerService pluginManager) {
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
    }

    public Task StartCheckUpdatesAsync(string folder, bool recursive, string apiKey) {
        if (IsRunning) {
            throw new InvalidOperationException("Lora Updater is already running an operation.");
        }

        ResetState(folder, "Checking for updates...");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () => {
            try {
                AddLog($"Starting background update check on: {folder} (Recursive: {recursive})");

                var parameters = new Dictionary<string, object?> {
                    ["folder"] = folder,
                    ["recursive"] = recursive,
                    ["api_key"] = apiKey
                };

                var result = await _pluginManager.ExecutePluginAsync(
                    "lora-updater",
                    "check_updates",
                    parameters,
                    onOutputLine: HandleProcessLine,
                    onErrorLine: err => AddLog($"[stderr] {err}"),
                    cancellationToken: token
                );

                if (result.Success) {
                    LastResultJson = result.Data?.ToString();
                    ParseSummaryFromJson(LastResultJson);
                    CurrentStatus = $"Check complete. {UpdatesAvailable} update(s) available.";
                    AddLog($"[Success] Check finished. {UpdatesAvailable} updates available.");
                } else {
                    CurrentStatus = $"Check failed: {result.Message}";
                    AddLog($"[Error] {result.Message}");
                }
            } catch (OperationCanceledException) {
                CurrentStatus = "Check cancelled by user.";
                AddLog("[Cancelled] Background check stopped.");
            } catch (Exception ex) {
                CurrentStatus = $"Error: {ex.Message}";
                AddLog($"[Exception] {ex.Message}");
            } finally {
                IsRunning = false;
                CurrentOperation = "Idle";
                NotifyStateChanged();
            }
        }, token);

        return Task.CompletedTask;
    }

    public Task StartScanAndUpdateAsync(string folder, bool recursive, string backupFolder, string apiKey, bool allowMismatch) {
        if (IsRunning) {
            throw new InvalidOperationException("Lora Updater is already running an operation.");
        }

        ResetState(folder, "Updating LoRAs...");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () => {
            try {
                AddLog($"Starting background update on: {folder}");

                var parameters = new Dictionary<string, object?> {
                    ["folder"] = folder,
                    ["recursive"] = recursive,
                    ["backup_folder"] = backupFolder,
                    ["api_key"] = apiKey,
                    ["allow_base_mismatch"] = allowMismatch
                };

                var result = await _pluginManager.ExecutePluginAsync(
                    "lora-updater",
                    "scan_and_update",
                    parameters,
                    onOutputLine: HandleProcessLine,
                    onErrorLine: err => AddLog($"[stderr] {err}"),
                    cancellationToken: token
                );

                if (result.Success) {
                    LastResultJson = result.Data?.ToString();
                    ParseSummaryFromJson(LastResultJson);
                    CurrentStatus = $"Update complete! {UpdatedCount} model(s) updated.";
                    AddLog($"[Success] Update finished. Updated {UpdatedCount} model(s).");
                } else {
                    CurrentStatus = $"Update failed: {result.Message}";
                    AddLog($"[Error] {result.Message}");
                }
            } catch (OperationCanceledException) {
                CurrentStatus = "Update cancelled by user.";
                AddLog("[Cancelled] Background update stopped.");
            } catch (Exception ex) {
                CurrentStatus = $"Error: {ex.Message}";
                AddLog($"[Exception] {ex.Message}");
            } finally {
                IsRunning = false;
                CurrentOperation = "Idle";
                NotifyStateChanged();
            }
        }, token);

        return Task.CompletedTask;
    }

    public Task StartUpdateSingleAsync(string filePath, string backupFolder, string apiKey) {
        if (IsRunning) {
            throw new InvalidOperationException("Lora Updater is already running an operation.");
        }

        string folder = Path.GetDirectoryName(filePath) ?? string.Empty;
        ResetState(folder, "Updating single model...");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () => {
            try {
                AddLog($"Updating single LoRA file: {Path.GetFileName(filePath)}");

                var parameters = new Dictionary<string, object?> {
                    ["file_path"] = filePath,
                    ["backup_folder"] = backupFolder,
                    ["api_key"] = apiKey
                };

                var result = await _pluginManager.ExecutePluginAsync(
                    "lora-updater",
                    "update_single",
                    parameters,
                    onOutputLine: HandleProcessLine,
                    onErrorLine: err => AddLog($"[stderr] {err}"),
                    cancellationToken: token
                );

                if (result.Success) {
                    UpdatedCount++;
                    CurrentStatus = $"Updated {Path.GetFileName(filePath)} successfully.";
                    AddLog($"[Success] {CurrentStatus}");
                } else {
                    CurrentStatus = $"Failed: {result.Message}";
                    AddLog($"[Error] {result.Message}");
                }
            } catch (OperationCanceledException) {
                CurrentStatus = "Update cancelled.";
                AddLog("[Cancelled] Single update stopped.");
            } catch (Exception ex) {
                CurrentStatus = $"Error: {ex.Message}";
                AddLog($"[Exception] {ex.Message}");
            } finally {
                IsRunning = false;
                CurrentOperation = "Idle";
                NotifyStateChanged();
            }
        }, token);

        return Task.CompletedTask;
    }

    public void Cancel() {
        if (_cts != null && !_cts.IsCancellationRequested) {
            AddLog("Requesting cancellation of Lora Updater...");
            _cts.Cancel();
        }
    }

    public void ClearLogs() {
        lock (_lock) {
            _liveLogs.Clear();
        }
        NotifyStateChanged();
    }

    private void ResetState(string folder, string operation) {
        IsRunning = true;
        CurrentOperation = operation;
        CurrentStatus = "Starting...";
        TargetFolder = folder;
        TotalFiles = 0;
        ProcessedFiles = 0;
        UpdatesAvailable = 0;
        UpdatedCount = 0;
        lock (_lock) {
            _liveLogs.Clear();
        }
        NotifyStateChanged();
    }

    private void HandleProcessLine(string line) {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (line.StartsWith("[STATUS] ")) {
            string status = line.Substring("[STATUS] ".Length).Trim();
            CurrentStatus = status;

            // Extract (X/Y) if present
            int openParen = status.LastIndexOf('(');
            int closeParen = status.LastIndexOf(')');
            if (openParen >= 0 && closeParen > openParen) {
                string inside = status.Substring(openParen + 1, closeParen - openParen - 1);
                string[] parts = inside.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out int cur) && int.TryParse(parts[1], out int tot)) {
                    ProcessedFiles = cur;
                    TotalFiles = tot;
                }
            }

            AddLog(status);
        } else if (line.StartsWith("[UPDATE_FOUND] ")) {
            UpdatesAvailable++;
            AddLog(line);
        } else if (line.StartsWith("[UPDATED] ")) {
            UpdatedCount++;
            AddLog(line);
        } else if (!line.StartsWith("{")) {
            AddLog(line);
        }

        NotifyStateChanged();
    }

    private void ParseSummaryFromJson(string? json) {
        if (string.IsNullOrWhiteSpace(json)) return;

        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("summary", out var sumEl)) {
                if (sumEl.TryGetProperty("total_files", out var tf)) TotalFiles = tf.GetInt32();
                if (sumEl.TryGetProperty("updates_available", out var ua)) UpdatesAvailable = ua.GetInt32();
                if (sumEl.TryGetProperty("updated", out var up)) UpdatedCount = up.GetInt32();
            }
        } catch {
            // Ignore parse errors
        }
    }

    private void AddLog(string log) {
        lock (_lock) {
            _liveLogs.Add($"[{DateTime.Now:T}] {log}");
            if (_liveLogs.Count > 500) {
                _liveLogs.RemoveAt(0);
            }
        }
    }

    private void NotifyStateChanged() {
        OnStateChanged?.Invoke();
    }

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
