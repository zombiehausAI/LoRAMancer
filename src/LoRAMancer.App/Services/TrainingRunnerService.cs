using System.Diagnostics;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class TrainingRunnerService {
    private Process? _currentProcess;
    private CancellationTokenSource? _trainingCts;
    private readonly Stopwatch _stopwatch = new();

    public TrainingProgress CurrentProgress { get; } = new();
    public event Action<TrainingProgress>? OnProgressUpdated;
    public event Action<string>? OnLogReceived;

    public bool IsRunning => CurrentProgress.Status == TrainingStatus.Training || CurrentProgress.Status == TrainingStatus.Initializing;

    public async Task StartTrainingAsync(
        string venvPath,
        string toolkitScriptPath,
        string configYamlPath,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(venvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configYamlPath);

        if (IsRunning) {
            throw new InvalidOperationException("A training job is already running.");
        }

        string pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) {
            pythonExe = "python.exe";
        }

        _trainingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CurrentProgress.Status = TrainingStatus.Initializing;
        CurrentProgress.CurrentStep = 0;
        CurrentProgress.TotalSteps = 1000;
        CurrentProgress.CurrentLoss = 0.0;
        CurrentProgress.Elapsed = TimeSpan.Zero;
        OnProgressUpdated?.Invoke(CurrentProgress);

        _stopwatch.Restart();

        ProcessStartInfo startInfo = new() {
            FileName = pythonExe,
            Arguments = $"\"{toolkitScriptPath}\" \"{configYamlPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(configYamlPath) ?? Directory.GetCurrentDirectory()
        };

        // Inject AMD ROCm optimization flags
        startInfo.EnvironmentVariables["PYTORCH_ROCM_ARCH"] = "native";
        startInfo.EnvironmentVariables["MIOPEN_FIND_MODE"] = "FAST";
        startInfo.EnvironmentVariables["HSA_OVERRIDE_GFX_VERSION"] = "11.0.0";

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
                OnProgressUpdated?.Invoke(CurrentProgress);
                throw new InvalidOperationException("Failed to launch training process.");
            }

            CurrentProgress.Status = TrainingStatus.Training;
            OnProgressUpdated?.Invoke(CurrentProgress);

            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();

            await _currentProcess.WaitForExitAsync(_trainingCts.Token);

            if (_currentProcess.ExitCode == 0) {
                CurrentProgress.Status = TrainingStatus.Completed;
            } else {
                CurrentProgress.Status = TrainingStatus.Failed;
            }
        } catch (OperationCanceledException) {
            CurrentProgress.Status = TrainingStatus.Cancelled;
            KillCurrentProcess();
        } catch (Exception ex) {
            CurrentProgress.Status = TrainingStatus.Failed;
            OnLogReceived?.Invoke($"[ERROR] Training runner failed: {ex.Message}");
        } finally {
            _stopwatch.Stop();
            CurrentProgress.Elapsed = _stopwatch.Elapsed;
            OnProgressUpdated?.Invoke(CurrentProgress);
            _currentProcess?.Dispose();
            _currentProcess = null;
        }
    }

    public void CancelTraining() {
        if (_trainingCts != null && !_trainingCts.IsCancellationRequested) {
            _trainingCts.Cancel();
            KillCurrentProcess();
        }
    }

    private void KillCurrentProcess() {
        try {
            if (_currentProcess != null && !_currentProcess.HasExited) {
                _currentProcess.Kill(entireProcessTree: true);
            }
        } catch {
            // Suppress process kill errors during cleanup
        }
    }

    private void ParseLogLine(string line) {
        OnLogReceived?.Invoke(line);

        // Parse patterns like: "Step: 120/1000, Loss: 0.0842, LR: 1.00e-04" or "[120/1000] loss=0.0842"
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

        OnProgressUpdated?.Invoke(CurrentProgress);
    }
}
