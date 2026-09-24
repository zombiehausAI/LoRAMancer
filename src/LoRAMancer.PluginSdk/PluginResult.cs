namespace LoRAMancer.PluginSdk;

public sealed class PluginResult {
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public object? Data { get; init; }

    public static PluginResult Ok(string message = "Success", object? data = null) {
        return new PluginResult {
            Success = true,
            Message = message,
            Data = data
        };
    }

    public static PluginResult Fail(string message) {
        return new PluginResult {
            Success = false,
            Message = message,
            Data = null
        };
    }
}
