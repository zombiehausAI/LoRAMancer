using System.Diagnostics;

namespace LoRAMancer.App.Engines;

public sealed class ProcessRunner {
    public async Task<int> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        IDictionary<string, string>? environmentVariables,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        ProcessStartInfo startInfo = new() {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environmentVariables != null) {
            foreach (KeyValuePair<string, string> kvp in environmentVariables) {
                startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        using Process process = new() { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) => {
            if (e.Data != null && onOutputLine != null) {
                try {
                    onOutputLine(e.Data);
                } catch {
                    // Protect ThreadPool worker thread from crashing host process
                }
            }
        };

        process.ErrorDataReceived += (_, e) => {
            if (e.Data != null && onErrorLine != null) {
                try {
                    onErrorLine(e.Data);
                } catch {
                    // Protect ThreadPool worker thread from crashing host process
                }
            }
        };

        if (!process.Start()) {
            throw new InvalidOperationException($"Failed to start process: {executable}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        } catch (OperationCanceledException) {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            } catch {
                // Ignore errors during emergency kill
            }
            throw;
        }
    }
}
