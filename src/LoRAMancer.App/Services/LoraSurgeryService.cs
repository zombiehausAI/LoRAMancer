using System.Diagnostics;
using System.Text;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public record LoraSurgeryResult(bool Success, string OutputPath, string Message, TimeSpan Duration);

public sealed class LoraSurgeryService {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService? _settingsService;
    private readonly AiToolkitSetupService? _toolkitSetup;

    public LoraSurgeryService(ProcessRunner processRunner, SettingsService? settingsService = null, AiToolkitSetupService? toolkitSetup = null) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _settingsService = settingsService;
        _toolkitSetup = toolkitSetup;
    }

    public string ResolvePythonExecutable() {
        string defaultVenv = AiToolkitSetupService.GetDefaultVenvPath();
        string venvPython = Path.Combine(defaultVenv, "Scripts", "python.exe");
        if (File.Exists(venvPython)) {
            return venvPython;
        }

        string preferred = _settingsService?.Current.PreferredPythonPath ?? "python.exe";
        if (File.Exists(preferred)) {
            return preferred;
        }

        return "python.exe";
    }

    public async Task<LoraSurgeryResult> ResizeLoraAsync(
        string sourceLoraPath,
        string outputLoraPath,
        int targetRank,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (targetRank <= 0 || targetRank > 256) {
            return new LoraSurgeryResult(false, outputLoraPath, $"Invalid target rank: {targetRank}. Rank must be between 1 and 256.", TimeSpan.Zero);
        }

        if (!File.Exists(sourceLoraPath)) {
            return new LoraSurgeryResult(false, outputLoraPath, $"Source LoRA file '{sourceLoraPath}' does not exist.", TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_resize_{Guid.NewGuid():N}.py");

        string pythonCode = $$"""
import sys, os, time
import torch
from safetensors.torch import load_file, save_file
import safetensors

def resize_lora():
    src = r"{{sourceLoraPath}}"
    dst = r"{{outputLoraPath}}"
    target_rank = {{targetRank}}
    
    print(f"[LoRAMancer SVD] Loading source LoRA: {src}")
    state_dict = load_file(src, device="cpu")
    
    # Read metadata if present
    metadata = {}
    with safetensors.safe_open(src, framework="pt") as f:
        metadata = f.metadata() or {}
    metadata = dict(metadata)
    metadata["ss_network_dim"] = str(target_rank)
    metadata["loramancer_svd_resized"] = "true"
    metadata["loramancer_target_rank"] = str(target_rank)
    
    # Identify LoRA pairs
    # Standard format: lora_down.weight and lora_up.weight
    new_state_dict = {}
    down_keys = [k for k in state_dict.keys() if "lora_down" in k or "down.weight" in k or "lora_A" in k]
    processed_keys = set()
    
    print(f"[LoRAMancer SVD] Found {len(down_keys)} down/up LoRA weight pairs. Performing SVD decomposition...")
    
    for i, down_key in enumerate(down_keys):
        # Infer up key
        up_key = None
        if "lora_down.weight" in down_key:
            up_key = down_key.replace("lora_down.weight", "lora_up.weight")
        elif ".down.weight" in down_key:
            up_key = down_key.replace(".down.weight", ".up.weight")
        elif "lora_A" in down_key:
            up_key = down_key.replace("lora_A", "lora_B")
            
        if not up_key or up_key not in state_dict:
            new_state_dict[down_key] = state_dict[down_key]
            processed_keys.add(down_key)
            continue
            
        w_down = state_dict[down_key].float()
        w_up = state_dict[up_key].float()
        
        orig_rank = w_down.shape[0] if "lora_down" in down_key or "down.weight" in down_key else w_down.shape[1]
        
        if orig_rank <= target_rank:
            # Already smaller or equal to target rank; copy as is
            new_state_dict[down_key] = state_dict[down_key]
            new_state_dict[up_key] = state_dict[up_key]
            processed_keys.add(down_key)
            processed_keys.add(up_key)
            continue
            
        # Reconstruct delta matrix
        # For linear: up is (out_dim, rank), down is (rank, in_dim)
        if len(w_up.shape) == 2 and len(w_down.shape) == 2:
            delta = torch.mm(w_up, w_down)
            # SVD decomposition
            U, S, Vh = torch.linalg.svd(delta, full_matrices=False)
            
            U = U[:, :target_rank]
            S = S[:target_rank]
            Vh = Vh[:target_rank, :]
            
            s_sqrt = torch.diag(torch.sqrt(S))
            new_up = torch.mm(U, s_sqrt)
            new_down = torch.mm(s_sqrt, Vh)
            
            new_state_dict[up_key] = new_up.to(dtype=state_dict[up_key].dtype)
            new_state_dict[down_key] = new_down.to(dtype=state_dict[down_key].dtype)
        else:
            # Multi-dimensional conv weights or other shapes, keep original
            new_state_dict[down_key] = state_dict[down_key]
            new_state_dict[up_key] = state_dict[up_key]
            
        processed_keys.add(down_key)
        processed_keys.add(up_key)
        
        if (i + 1) % 50 == 0 or (i + 1) == len(down_keys):
            pct = int((i + 1) / len(down_keys) * 100)
            print(f"[LoRAMancer SVD] Progress: {i + 1}/{len(down_keys)} pairs compressed ({pct}%)...")

    # Copy any remaining tensors (alphas, biases, etc.)
    for k, v in state_dict.items():
        if k not in processed_keys:
            new_state_dict[k] = v
            
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    print(f"[LoRAMancer SVD] Writing compressed LoRA to {dst}...")
    save_file(new_state_dict, dst, metadata=metadata)
    print("[LoRAMancer SVD] Compression completed successfully!")

if __name__ == '__main__':
    resize_lora()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            onLog?.Invoke($"[LoRAMancer] Initiating SVD rank resizing to rank {targetRank}...");
            onLog?.Invoke($"[LoRAMancer] Python: {pythonExe}");

            var envVars = _toolkitSetup?.GetIsolatedEnvironmentVariables() ?? new Dictionary<string, string>();
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(outputLoraPath) ?? AppContext.BaseDirectory,
                envVars,
                line => onLog?.Invoke(line),
                line => onLog?.Invoke($"[stderr] {line}"),
                cancellationToken
            );

            sw.Stop();
            if (exitCode == 0 && File.Exists(outputLoraPath)) {
                long originalSize = new FileInfo(sourceLoraPath).Length;
                long newSize = new FileInfo(outputLoraPath).Length;
                double savings = (1.0 - ((double)newSize / originalSize)) * 100.0;
                string msg = $"Resized to rank {targetRank} in {sw.Elapsed.TotalSeconds:F1}s. Size: {originalSize / (1024.0 * 1024.0):F1}MB -> {newSize / (1024.0 * 1024.0):F1}MB ({savings:F1}% saved)";
                onLog?.Invoke($"[LoRAMancer] {msg}");
                return new LoraSurgeryResult(true, outputLoraPath, msg, sw.Elapsed);
            } else {
                string err = $"Python SVD resizing process exited with code {exitCode}. Check logs for details.";
                onLog?.Invoke($"[LoRAMancer Error] {err}");
                return new LoraSurgeryResult(false, outputLoraPath, err, sw.Elapsed);
            }
        } catch (Exception ex) {
            sw.Stop();
            string err = $"SVD surgery failed: {ex.Message}";
            onLog?.Invoke($"[LoRAMancer Exception] {err}");
            return new LoraSurgeryResult(false, outputLoraPath, err, sw.Elapsed);
        } finally {
            try {
                if (File.Exists(scriptPath)) {
                    File.Delete(scriptPath);
                }
            } catch { }
        }
    }

    public async Task<LoraSurgeryResult> MergeLorasAsync(
        string lora1Path,
        float weight1,
        string lora2Path,
        float weight2,
        string outputLoraPath,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(lora1Path)) {
            return new LoraSurgeryResult(false, outputLoraPath, $"Primary LoRA file '{lora1Path}' not found.", TimeSpan.Zero);
        }
        if (!File.Exists(lora2Path)) {
            return new LoraSurgeryResult(false, outputLoraPath, $"Secondary LoRA file '{lora2Path}' not found.", TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_merge_{Guid.NewGuid():N}.py");

        string pythonCode = $$"""
import sys, os, time
import torch
from safetensors.torch import load_file, save_file
import safetensors

def merge_loras():
    src1 = r"{{lora1Path}}"
    w1 = {{weight1}}
    src2 = r"{{lora2Path}}"
    w2 = {{weight2}}
    dst = r"{{outputLoraPath}}"
    
    print(f"[LoRAMancer Merger] Loading Model A ({w1:0.2f}x): {src1}")
    sd1 = load_file(src1, device="cpu")
    print(f"[LoRAMancer Merger] Loading Model B ({w2:0.2f}x): {src2}")
    sd2 = load_file(src2, device="cpu")
    
    merged = {}
    all_keys = set(sd1.keys()).union(set(sd2.keys()))
    print(f"[LoRAMancer Merger] Merging {len(all_keys)} tensor keys...")
    
    for i, k in enumerate(all_keys):
        if k in sd1 and k in sd2:
            t1 = sd1[k].float()
            t2 = sd2[k].float()
            if t1.shape == t2.shape:
                merged_tensor = (t1 * w1) + (t2 * w2)
                merged[k] = merged_tensor.to(dtype=sd1[k].dtype)
            else:
                # Shape mismatch, default to primary model
                merged[k] = (t1 * w1).to(dtype=sd1[k].dtype)
        elif k in sd1:
            merged[k] = (sd1[k].float() * w1).to(dtype=sd1[k].dtype)
        else:
            merged[k] = (sd2[k].float() * w2).to(dtype=sd2[k].dtype)
            
        if (i + 1) % 100 == 0 or (i + 1) == len(all_keys):
            pct = int((i + 1) / len(all_keys) * 100)
            print(f"[LoRAMancer Merger] Merging: {i + 1}/{len(all_keys)} tensors ({pct}%)...")
            
    # Read metadata from primary
    metadata = {}
    with safetensors.safe_open(src1, framework="pt") as f:
        metadata = f.metadata() or {}
    metadata = dict(metadata)
    metadata["loramancer_merged"] = "true"
    metadata["loramancer_model_a"] = os.path.basename(src1)
    metadata["loramancer_weight_a"] = str(w1)
    metadata["loramancer_model_b"] = os.path.basename(src2)
    metadata["loramancer_weight_b"] = str(w2)
    
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    print(f"[LoRAMancer Merger] Writing merged LoRA to {dst}...")
    save_file(merged, dst, metadata=metadata)
    print("[LoRAMancer Merger] LoRA merge completed successfully!")

if __name__ == '__main__':
    merge_loras()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            onLog?.Invoke($"[LoRAMancer] Merging LoRAs: {Path.GetFileName(lora1Path)} ({weight1:F2}) + {Path.GetFileName(lora2Path)} ({weight2:F2})...");
            onLog?.Invoke($"[LoRAMancer] Python: {pythonExe}");

            var envVars = _toolkitSetup?.GetIsolatedEnvironmentVariables() ?? new Dictionary<string, string>();
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(outputLoraPath) ?? AppContext.BaseDirectory,
                envVars,
                line => onLog?.Invoke(line),
                line => onLog?.Invoke($"[stderr] {line}"),
                cancellationToken
            );

            sw.Stop();
            if (exitCode == 0 && File.Exists(outputLoraPath)) {
                long mergedSize = new FileInfo(outputLoraPath).Length;
                string msg = $"Merged successfully in {sw.Elapsed.TotalSeconds:F1}s! Output size: {mergedSize / (1024.0 * 1024.0):F1}MB";
                onLog?.Invoke($"[LoRAMancer] {msg}");
                return new LoraSurgeryResult(true, outputLoraPath, msg, sw.Elapsed);
            } else {
                string err = $"Python merge process exited with code {exitCode}. Check logs for details.";
                onLog?.Invoke($"[LoRAMancer Error] {err}");
                return new LoraSurgeryResult(false, outputLoraPath, err, sw.Elapsed);
            }
        } catch (Exception ex) {
            sw.Stop();
            string err = $"Merge surgery failed: {ex.Message}";
            onLog?.Invoke($"[LoRAMancer Exception] {err}");
            return new LoraSurgeryResult(false, outputLoraPath, err, sw.Elapsed);
        } finally {
            try {
                if (File.Exists(scriptPath)) {
                    File.Delete(scriptPath);
                }
            } catch { }
        }
    }
}
