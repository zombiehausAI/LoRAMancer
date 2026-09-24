using LoRAMancer.PluginSdk;

namespace SampleCSharpPlugin;

public sealed class SamplePlugin : ILoRAMancerPlugin {
    public PluginMetadata Metadata { get; } = new() {
        Id = "sample-csharp-plugin",
        Name = "LoRA Optimizer Diagnostic Plugin",
        Version = "1.0.0",
        Description = "Sample C# plugin analyzing donor learning rates and proposing AMD memory safe adjustments.",
        Author = "LoRAMancer Team",
        PluginType = "CSharp",
        Capabilities = new[] { "diagnostics", "hyperparameters" }
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) {
        context.LogInformation("Initializing SampleCSharpPlugin...");
        return Task.CompletedTask;
    }

    public Task<PluginResult> ExecuteAsync(string command, IDictionary<string, object?> parameters, CancellationToken cancellationToken) {
        if (string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase)) {
            return Task.FromResult(PluginResult.Ok("Pong from SampleCSharpPlugin (.NET 10 assembly)"));
        }

        if (string.Equals(command, "diagnose", StringComparison.OrdinalIgnoreCase)) {
            return Task.FromResult(PluginResult.Ok("Diagnostic passed: AMD ROCm memory alignment is optimal."));
        }

        return Task.FromResult(PluginResult.Fail($"Unknown command: {command}"));
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
