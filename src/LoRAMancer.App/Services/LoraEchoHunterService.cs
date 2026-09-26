using System.Diagnostics;
using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public enum EchoHunterMode {
    OrthogonalRepulsion,
    DirectVectorSubtraction,
    ExportNegativeCleanseLora
}

public sealed record EchoHunterResult(
    bool Success,
    string OutputPath,
    string Message,
    int ModifiedLayersCount,
    double RepelledEnergyPct,
    TimeSpan Duration
);

public sealed record StyleDecoupleResult(
    bool Success,
    string OutputPath,
    string Message,
    int IdentityLayersKept,
    int StyleLayersDamped,
    TimeSpan Duration
);

public sealed class LoraEchoHunterService {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService? _settingsService;
    private readonly AiToolkitSetupService? _toolkitSetup;

    public LoraEchoHunterService(
        ProcessRunner processRunner,
        SettingsService? settingsService = null,
        AiToolkitSetupService? toolkitSetup = null
    ) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _settingsService = settingsService;
        _toolkitSetup = toolkitSetup;
    }

    public string ResolvePythonExecutable() {
        string defaultVenv = AiToolkitSetupService.GetDefaultVenvPath();
        string venvPython = Path.Combine(defaultVenv, "Scripts", "python.exe");
        if (File.Exists(venvPython)) return venvPython;

        string preferred = _settingsService?.Current.PreferredPythonPath ?? "python.exe";
        if (File.Exists(preferred)) return preferred;

        return "python.exe";
    }

    public async Task<EchoHunterResult> HuntAndRepelGhostVectorAsync(
        string targetLoraPath,
        string ghostLoraPath,
        string outputPath,
        EchoHunterMode mode = EchoHunterMode.OrthogonalRepulsion,
        float repulsionStrength = 0.85f,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(targetLoraPath)) {
            return new EchoHunterResult(false, outputPath, $"Target LoRA '{targetLoraPath}' not found.", 0, 0, TimeSpan.Zero);
        }
        if (!File.Exists(ghostLoraPath)) {
            return new EchoHunterResult(false, outputPath, $"Ghost/Negative donor LoRA '{ghostLoraPath}' not found.", 0, 0, TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_ghost_{Guid.NewGuid():N}.py");
        string jsonOutPath = Path.Combine(Path.GetTempPath(), $"loramancer_ghost_out_{Guid.NewGuid():N}.json");

        string pythonCode = $$"""
import sys, os, json
import torch
from safetensors.torch import load_file, save_file

def hunt():
    target_path = r"{{targetLoraPath}}"
    ghost_path = r"{{ghostLoraPath}}"
    out_path = r"{{outputPath}}"
    out_json = r"{{jsonOutPath}}"
    mode = "{{mode}}"
    strength = float({{repulsionStrength}})

    target = load_file(target_path)
    ghost = load_file(ghost_path)
    result = {}

    modified = 0
    total_energy_damped = 0.0

    for k, v in target.items():
        if k in ghost and ghost[k].shape == v.shape:
            t_w = v.float()
            g_w = ghost[k].float()

            if mode == "OrthogonalRepulsion":
                # Project target onto ghost direction and subtract projection scaled by strength
                g_norm_sq = torch.sum(g_w * g_w) + 1e-12
                dot = torch.sum(t_w * g_w)
                projection = (dot / g_norm_sq) * g_w
                cleaned = t_w - (strength * projection)
                result[k] = cleaned.to(v.dtype)
                modified += 1
                total_energy_damped += float(torch.norm(projection).item())
            elif mode == "DirectVectorSubtraction":
                cleaned = t_w - (strength * g_w)
                result[k] = cleaned.to(v.dtype)
                modified += 1
            elif mode == "ExportNegativeCleanseLora":
                # Invert ghost vector direction
                result[k] = (-1.0 * strength * g_w).to(v.dtype)
                modified += 1
        else:
            result[k] = v

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    save_file(result, out_path)

    with open(out_json, "w", encoding="utf-8") as f:
        json.dump({"modified": modified, "energy": round(total_energy_damped, 4)}, f)

if __name__ == "__main__":
    hunt()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(targetLoraPath) ?? Path.GetTempPath(),
                null,
                output => onLog?.Invoke(output),
                null,
                cancellationToken: cancellationToken
            );

            int modified = 0;
            double energy = 0.0;
            if (File.Exists(jsonOutPath)) {
                string json = await File.ReadAllTextAsync(jsonOutPath, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("modified", out var m)) modified = m.GetInt32();
                if (doc.RootElement.TryGetProperty("energy", out var e)) energy = e.GetDouble();
            }

            sw.Stop();
            return new EchoHunterResult(
                true,
                outputPath,
                $"Successfully repelled ghost vectors across {modified} layers.",
                modified,
                energy,
                sw.Elapsed
            );
        } catch (Exception ex) {
            sw.Stop();
            return new EchoHunterResult(false, outputPath, $"Ghost Hunter failed: {ex.Message}", 0, 0, sw.Elapsed);
        } finally {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            try { if (File.Exists(jsonOutPath)) File.Delete(jsonOutPath); } catch { }
        }
    }

    public async Task<StyleDecoupleResult> DecoupleStyleAndIdentityAsync(
        string sourceLoraPath,
        string outputPath,
        float identityRetention = 1.0f,
        float styleBleedDamping = 0.25f,
        bool pureStyleMode = false,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(sourceLoraPath)) {
            return new StyleDecoupleResult(false, outputPath, "Source LoRA file not found.", 0, 0, TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_decouple_{Guid.NewGuid():N}.py");
        string jsonOutPath = Path.Combine(Path.GetTempPath(), $"loramancer_decouple_out_{Guid.NewGuid():N}.json");

        string pythonCode = $$"""
import sys, os, json
import torch
from safetensors.torch import load_file, save_file

def decouple():
    src_path = r"{{sourceLoraPath}}"
    out_path = r"{{outputPath}}"
    out_json = r"{{jsonOutPath}}"
    id_retention = float({{identityRetention}})
    style_damping = float({{styleBleedDamping}})
    pure_style = {{pureStyleMode.ToString().ToLowerInvariant()}}

    tensors = load_file(src_path)
    result = {}
    id_count = 0
    style_count = 0

    # Cross-attention (attn2) and Text-Encoder layers carry semantic identity
    # Self-attention (attn1), feedforward (ff), and transformer image blocks carry style/texture/brushstrokes
    for k, v in tensors.items():
        is_identity_layer = any(term in k for term in [
            "lora_te", "text_model", "lora_clip", "attn2", "cross_attn", "context"
        ])

        tensor_f = v.float()
        if is_identity_layer:
            multiplier = style_damping if pure_style else id_retention
            result[k] = (tensor_f * multiplier).to(v.dtype)
            id_count += 1
        else:
            multiplier = id_retention if pure_style else style_damping
            result[k] = (tensor_f * multiplier).to(v.dtype)
            style_count += 1

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    save_file(result, out_path)

    with open(out_json, "w", encoding="utf-8") as f:
        json.dump({"id_count": id_count, "style_count": style_count}, f)

if __name__ == "__main__":
    decouple()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(sourceLoraPath) ?? Path.GetTempPath(),
                null,
                output => onLog?.Invoke(output),
                null,
                cancellationToken: cancellationToken
            );

            int idCount = 0, styleCount = 0;
            if (File.Exists(jsonOutPath)) {
                string json = await File.ReadAllTextAsync(jsonOutPath, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id_count", out var ic)) idCount = ic.GetInt32();
                if (doc.RootElement.TryGetProperty("style_count", out var sc)) styleCount = sc.GetInt32();
            }

            sw.Stop();
            string modeName = pureStyleMode ? "Pure Style Extraction" : "Pure Identity Isolation";
            return new StyleDecoupleResult(
                true,
                outputPath,
                $"{modeName} complete. Scaled {idCount} identity layers and damped {styleCount} style layers.",
                idCount,
                styleCount,
                sw.Elapsed
            );
        } catch (Exception ex) {
            sw.Stop();
            return new StyleDecoupleResult(false, outputPath, $"Decoupling failed: {ex.Message}", 0, 0, sw.Elapsed);
        } finally {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            try { if (File.Exists(jsonOutPath)) File.Delete(jsonOutPath); } catch { }
        }
    }
}
