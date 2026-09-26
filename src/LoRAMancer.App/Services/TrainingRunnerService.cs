using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class TrainingRunnerService {
    private Process? _currentProcess;
    private CancellationTokenSource? _trainingCts;
    private readonly Stopwatch _stopwatch = new();
    private readonly SettingsService? _settingsService;
    private readonly AiToolkitSetupService? _toolkitSetupService;
    private readonly LoraHistoryService? _historyService;

    private readonly List<string> _recentLogs = new();
    private readonly object _logsLock = new();
    private const int MaxLogHistory = 2000;
    private DateTime _lastProcessExitTime = DateTime.MinValue;

    private readonly List<TrainingJob> _queue = new();
    private readonly object _queueLock = new();
    private bool _isProcessingQueue;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _completionSources = new();

    public TrainingProgress CurrentProgress { get; } = new();
    public TrainingJob? CurrentJob { get; private set; }

    public IReadOnlyList<TrainingJob> Queue {
        get {
            lock (_queueLock) {
                return _queue.ToList();
            }
        }
    }

    public int QueueCount {
        get {
            lock (_queueLock) {
                return _queue.Count;
            }
        }
    }

    public event Action<TrainingProgress>? OnProgressUpdated;
    public event Action<string>? OnLogReceived;
    public event Action? OnQueueUpdated;

    public bool IsRunning => _currentProcess != null && !_currentProcess.HasExited && (CurrentProgress.Status == TrainingStatus.Training || CurrentProgress.Status == TrainingStatus.Initializing);

    public TrainingRunnerService(
        SettingsService? settingsService = null,
        AiToolkitSetupService? toolkitSetupService = null,
        LoraHistoryService? historyService = null
    ) {
        _settingsService = settingsService;
        _toolkitSetupService = toolkitSetupService;
        _historyService = historyService;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            KillCurrentProcess();
        };
    }

    public IReadOnlyList<string> GetRecentLogs() {
        lock (_logsLock) {
            return _recentLogs.ToList();
        }
    }

    public void ClearLogs() {
        lock (_logsLock) {
            _recentLogs.Clear();
        }
    }

    public TrainingJob EnqueueJob(string configYamlPath, string? venvPath = null, string? scriptPath = null, string? name = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(configYamlPath);

        string effectiveVenv = venvPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effectiveVenv)) {
            effectiveVenv = AiToolkitSetupService.GetDefaultVenvPath();
        }

        string effectiveName = name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effectiveName)) {
            effectiveName = Path.GetFileNameWithoutExtension(configYamlPath).Replace("_aitoolkit", string.Empty);
        }

        TrainingJob job = new() {
            ConfigYamlPath = configYamlPath,
            VenvPath = effectiveVenv,
            ScriptPath = scriptPath,
            Name = effectiveName,
            Status = TrainingStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        lock (_queueLock) {
            _queue.Add(job);
        }

        OnLogReceived?.Invoke($"[QUEUE] Added job '{job.Name}' to training queue ({QueueCount} pending).");
        OnQueueUpdated?.Invoke();

        EnsureQueueWorker();
        return job;
    }

    public bool RemoveJob(string jobId) {
        bool removed = false;
        lock (_queueLock) {
            int idx = _queue.FindIndex(j => j.Id == jobId);
            if (idx >= 0) {
                var job = _queue[idx];
                _queue.RemoveAt(idx);
                removed = true;
                if (_completionSources.TryRemove(jobId, out var tcs)) {
                    tcs.TrySetCanceled();
                }
                OnLogReceived?.Invoke($"[QUEUE] Removed job '{job.Name}' from queue.");
            }
        }

        if (removed) {
            OnQueueUpdated?.Invoke();
        }
        return removed;
    }

    public bool MoveJobUp(string jobId) {
        bool moved = false;
        lock (_queueLock) {
            int idx = _queue.FindIndex(j => j.Id == jobId);
            if (idx > 0) {
                (_queue[idx - 1], _queue[idx]) = (_queue[idx], _queue[idx - 1]);
                moved = true;
            }
        }
        if (moved) {
            OnQueueUpdated?.Invoke();
        }
        return moved;
    }

    public bool MoveJobDown(string jobId) {
        bool moved = false;
        lock (_queueLock) {
            int idx = _queue.FindIndex(j => j.Id == jobId);
            if (idx >= 0 && idx < _queue.Count - 1) {
                (_queue[idx + 1], _queue[idx]) = (_queue[idx], _queue[idx + 1]);
                moved = true;
            }
        }
        if (moved) {
            OnQueueUpdated?.Invoke();
        }
        return moved;
    }

    public void ClearQueue() {
        lock (_queueLock) {
            foreach (var job in _queue) {
                if (_completionSources.TryRemove(job.Id, out var tcs)) {
                    tcs.TrySetCanceled();
                }
            }
            _queue.Clear();
        }
        OnLogReceived?.Invoke("[QUEUE] Cleared all pending training jobs from queue.");
        OnQueueUpdated?.Invoke();
    }

    public void CancelAll() {
        ClearQueue();
        CancelTraining();
    }

    public async Task StartTrainingAsync(
        string venvPath,
        string? toolkitScriptPath,
        string configYamlPath,
        CancellationToken cancellationToken = default
    ) {
        var job = EnqueueJob(configYamlPath, venvPath, toolkitScriptPath);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completionSources[job.Id] = tcs;

        using (cancellationToken.Register(() => {
            if (CurrentJob?.Id == job.Id) {
                CancelTraining();
            } else {
                RemoveJob(job.Id);
            }
            tcs.TrySetCanceled(cancellationToken);
        })) {
            await tcs.Task;
        }
    }

    private void EnsureQueueWorker() {
        lock (_queueLock) {
            if (_isProcessingQueue) {
                return;
            }
            _isProcessingQueue = true;
        }

        _ = Task.Run(async () => {
            while (true) {
                TrainingJob? job = null;
                lock (_queueLock) {
                    if (_queue.Count > 0) {
                        job = _queue[0];
                        _queue.RemoveAt(0);
                    } else {
                        _isProcessingQueue = false;
                        CurrentJob = null;
                        OnQueueUpdated?.Invoke();
                        break;
                    }
                }

                if (job != null) {
                    CurrentJob = job;
                    OnQueueUpdated?.Invoke();

                    try {
                        await RunJobAsync(job);
                    } catch (Exception ex) {
                        job.Status = TrainingStatus.Failed;
                        job.ErrorMessage = ex.Message;
                        OnLogReceived?.Invoke($"[QUEUE] Job '{job.Name}' finished with error: {ex.Message}");
                    } finally {
                        job.CompletedAt = DateTime.UtcNow;
                        if (_completionSources.TryRemove(job.Id, out var tcs)) {
                            if (job.Status == TrainingStatus.Completed) {
                                tcs.TrySetResult(true);
                            } else if (job.Status == TrainingStatus.Cancelled) {
                                tcs.TrySetCanceled();
                            } else {
                                tcs.TrySetException(new InvalidOperationException(job.ErrorMessage ?? "Training job failed"));
                            }
                        }
                        OnQueueUpdated?.Invoke();
                    }

                    // Brief cooldown between runs to allow GPU driver and memory cleanup
                    await Task.Delay(2000, CancellationToken.None);
                }
            }
        });
    }

    private async Task RunJobAsync(TrainingJob job) {
        string venvPath = job.VenvPath;
        string configYamlPath = job.ConfigYamlPath;
        string? toolkitScriptPath = job.ScriptPath;

        ArgumentException.ThrowIfNullOrWhiteSpace(venvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configYamlPath);

        if (_currentProcess != null && !_currentProcess.HasExited) {
            KillCurrentProcess();
            await Task.Delay(1500, CancellationToken.None);
        }

        var elapsedSinceLastRun = DateTime.UtcNow - _lastProcessExitTime;
        if (elapsedSinceLastRun < TimeSpan.FromSeconds(2)) {
            var waitTime = TimeSpan.FromSeconds(2) - elapsedSinceLastRun;
            OnLogReceived?.Invoke($"[HOST] Waiting {waitTime.TotalSeconds:F1}s for GPU VRAM and driver resources to clear...");
            await Task.Delay(waitTime, CancellationToken.None);
        }

        string effectiveScriptPath = toolkitScriptPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effectiveScriptPath)) {
            effectiveScriptPath = _toolkitSetupService?.GetRunScriptPath() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(effectiveScriptPath) || !File.Exists(effectiveScriptPath)) {
            string localRun = Path.Combine(AppContext.BaseDirectory, "tools", "ai-toolkit", "run.py");
            if (File.Exists(localRun)) {
                effectiveScriptPath = localRun;
            } else {
                effectiveScriptPath = "run.py";
            }
        }

        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            pythonExe = "python.exe";
        }

        DateTime startedAt = DateTime.UtcNow;
        job.StartedAt = startedAt;
        _trainingCts?.Dispose();
        _trainingCts = new CancellationTokenSource();
        CurrentProgress.Status = TrainingStatus.Initializing;
        CurrentProgress.CurrentStep = 0;
        CurrentProgress.TotalSteps = 1000;
        CurrentProgress.CurrentLoss = 0.0;
        CurrentProgress.Elapsed = TimeSpan.Zero;
        job.Status = TrainingStatus.Initializing;
        OnProgressUpdated?.Invoke(CurrentProgress);

        _stopwatch.Restart();

        ProcessStartInfo startInfo = new() {
            FileName = pythonExe,
            Arguments = $"\"{effectiveScriptPath}\" \"{configYamlPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(configYamlPath) ?? Directory.GetCurrentDirectory()
        };

        startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        var envInfo = new AmdEnvironmentInfo();
        new AmdVenvProvisioner(new Engines.ProcessRunner()).DetectGpuHardware(envInfo);

        if (envInfo.DetectedVendor == HardwareVendor.Amd) {
            startInfo.EnvironmentVariables["PYTORCH_ROCM_ARCH"] = "native";
            startInfo.EnvironmentVariables["MIOPEN_FIND_MODE"] = "FAST";
            startInfo.EnvironmentVariables["HSA_OVERRIDE_GFX_VERSION"] = "11.0.0";
            AmdVenvProvisioner.PatchRocmSdkDistInfo(venvPath, msg => OnLogReceived?.Invoke(msg));
            AmdVenvProvisioner.PatchTorchaoDistributedUtils(venvPath, msg => OnLogReceived?.Invoke(msg));
        } else if (envInfo.DetectedVendor == HardwareVendor.Nvidia) {
            startInfo.EnvironmentVariables["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";
        } else if (envInfo.DetectedVendor == HardwareVendor.Intel) {
            startInfo.EnvironmentVariables["ZE_AFFINITY_MASK"] = "0";
        }

        if (_settingsService != null) {
            string hfToken = _settingsService.Current.HuggingFaceToken?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(hfToken)) {
                startInfo.EnvironmentVariables["HF_TOKEN"] = hfToken;
                startInfo.EnvironmentVariables["HUGGING_FACE_HUB_TOKEN"] = hfToken;
            }

            string hfHome = _settingsService.Current.HfHomeCachePath?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(hfHome)) {
                startInfo.EnvironmentVariables["HF_HOME"] = hfHome;
            }
        }

        _currentProcess = new Process { StartInfo = startInfo };

        _currentProcess.OutputDataReceived += (_, e) => {
            if (e.Data != null) {
                ParseLogLine(e.Data);
            }
        };

        _currentProcess.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                ParseLogLine(e.Data);
            }
        };

        try {
            if (!_currentProcess.Start()) {
                CurrentProgress.Status = TrainingStatus.Failed;
                job.Status = TrainingStatus.Failed;
                OnProgressUpdated?.Invoke(CurrentProgress);
                throw new InvalidOperationException("Failed to launch training process.");
            }

            CurrentProgress.Status = TrainingStatus.Training;
            job.Status = TrainingStatus.Training;
            OnProgressUpdated?.Invoke(CurrentProgress);

            OnLogReceived?.Invoke($"[HOST] Job '{job.Name}' started (PID: {_currentProcess.Id}). If running this architecture for the first time, foundation model weights are currently downloading to your HuggingFace cache...");

            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();

            await _currentProcess.WaitForExitAsync(_trainingCts.Token);

            if (_trainingCts.Token.IsCancellationRequested) {
                CurrentProgress.Status = TrainingStatus.Cancelled;
                job.Status = TrainingStatus.Cancelled;
            } else if (_currentProcess.ExitCode == 0) {
                CurrentProgress.Status = TrainingStatus.Completed;
                job.Status = TrainingStatus.Completed;
            } else {
                CurrentProgress.Status = TrainingStatus.Failed;
                job.Status = TrainingStatus.Failed;
            }
        } catch (OperationCanceledException) {
            CurrentProgress.Status = TrainingStatus.Cancelled;
            job.Status = TrainingStatus.Cancelled;
            KillCurrentProcess();
        } catch (Exception ex) {
            CurrentProgress.Status = TrainingStatus.Failed;
            job.Status = TrainingStatus.Failed;
            job.ErrorMessage = ex.Message;
            OnLogReceived?.Invoke($"[ERROR] Training runner failed: {ex.Message}");
        } finally {
            _lastProcessExitTime = DateTime.UtcNow;
            _stopwatch.Stop();
            CurrentProgress.Elapsed = _stopwatch.Elapsed;
            job.Elapsed = _stopwatch.Elapsed;
            OnProgressUpdated?.Invoke(CurrentProgress);

            if (_historyService != null) {
                try {
                    await _historyService.AddOrUpdateRecordAsync(new LoraHistoryRecord {
                        Name = job.Name,
                        ConfigYamlPath = configYamlPath,
                        Steps = CurrentProgress.CurrentStep > 0 ? CurrentProgress.CurrentStep : CurrentProgress.TotalSteps,
                        FinalLoss = CurrentProgress.CurrentLoss,
                        StartedAt = startedAt,
                        CompletedAt = DateTime.UtcNow,
                        Status = CurrentProgress.Status.ToString()
                    });
                } catch {
                    // Suppress history logging errors
                }
            }

            _currentProcess?.Dispose();
            _currentProcess = null;
        }
    }

    public void CancelTraining() {
        try {
            if (_trainingCts != null && !_trainingCts.IsCancellationRequested) {
                _trainingCts.Cancel();
            }
        } catch { }

        KillCurrentProcess();
        CurrentProgress.Status = TrainingStatus.Cancelled;
        if (CurrentJob != null) {
            CurrentJob.Status = TrainingStatus.Cancelled;
        }
        OnProgressUpdated?.Invoke(CurrentProgress);
    }

    private void KillCurrentProcess() {
        try {
            if (_currentProcess != null && !_currentProcess.HasExited) {
                _currentProcess.Kill(entireProcessTree: true);
                _currentProcess.WaitForExit(3000);
            }
        } catch {
            // Suppress process kill errors during cleanup
        }
    }

    private void ParseLogLine(string? line) {
        if (string.IsNullOrEmpty(line)) {
            return;
        }

        lock (_logsLock) {
            _recentLogs.Add(line);
            if (_recentLogs.Count > MaxLogHistory) {
                _recentLogs.RemoveRange(0, 100);
            }
        }

        OnLogReceived?.Invoke(line);

        Match stepMatch = Regex.Match(line, @"(?:step|Step|\b)(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
        if (stepMatch.Success) {
            if (int.TryParse(stepMatch.Groups[1].Value, out int current) && int.TryParse(stepMatch.Groups[2].Value, out int total)) {
                CurrentProgress.CurrentStep = current;
                CurrentProgress.TotalSteps = total;
            }
        }

        Match lossMatch = Regex.Match(line, @"(?:loss|Loss)\s*[:=]\s*([0-9\.]+)", RegexOptions.IgnoreCase);
        if (lossMatch.Success && double.TryParse(lossMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double loss)) {
            CurrentProgress.CurrentLoss = loss;
        }

        Match lrMatch = Regex.Match(line, @"(?:lr|LR|learning_rate)\s*[:=]\s*([0-9\.eE\-]+)", RegexOptions.IgnoreCase);
        if (lrMatch.Success && double.TryParse(lrMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double lr)) {
            CurrentProgress.CurrentLearningRate = lr;
        }

        CurrentProgress.Elapsed = _stopwatch.Elapsed;
        if (CurrentProgress.CurrentStep > 0 && CurrentProgress.TotalSteps > 0) {
            double msPerStep = _stopwatch.Elapsed.TotalMilliseconds / CurrentProgress.CurrentStep;
            int remainingSteps = Math.Max(0, CurrentProgress.TotalSteps - CurrentProgress.CurrentStep);
            CurrentProgress.EstimatedRemaining = TimeSpan.FromMilliseconds(msPerStep * remainingSteps);
        }

        if (CurrentJob != null) {
            CurrentJob.CurrentStep = CurrentProgress.CurrentStep;
            CurrentJob.TotalSteps = CurrentProgress.TotalSteps;
            CurrentJob.CurrentLoss = CurrentProgress.CurrentLoss;
            CurrentJob.CurrentLearningRate = CurrentProgress.CurrentLearningRate;
            CurrentJob.Elapsed = CurrentProgress.Elapsed;
            CurrentJob.EstimatedRemaining = CurrentProgress.EstimatedRemaining;
            CurrentJob.Status = CurrentProgress.Status;
        }

        OnProgressUpdated?.Invoke(CurrentProgress);
    }
}
