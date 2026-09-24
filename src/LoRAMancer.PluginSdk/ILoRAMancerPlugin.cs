namespace LoRAMancer.PluginSdk;

public interface ILoRAMancerPlugin {
    PluginMetadata Metadata { get; }
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
    Task<PluginResult> ExecuteAsync(string command, IDictionary<string, object?> parameters, CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}
