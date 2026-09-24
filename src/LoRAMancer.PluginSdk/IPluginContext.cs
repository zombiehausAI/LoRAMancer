namespace LoRAMancer.PluginSdk;

public interface IPluginContext {
    string AppDirectory { get; }
    string PluginDirectory { get; }
    string PythonVenvDirectory { get; }
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}
