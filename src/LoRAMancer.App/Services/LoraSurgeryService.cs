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

    public async Task<GeneTherapyAnalysis?> AnalyzeLayerBlocksAsync(
        string loraPath,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(loraPath)) {
            return null;
        }

        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_genetherapy_{Guid.NewGuid():N}.py");
        string jsonOutPath = Path.Combine(Path.GetTempPath(), $"loramancer_blocks_{Guid.NewGuid():N}.json");

        string pythonCode = $$"""
import sys, os, json, re
import torch
from safetensors.torch import load_file
import safetensors

def analyze():
    src = r"{{loraPath}}"
    out_json = r"{{jsonOutPath}}"
    
    with safetensors.safe_open(src, framework="pt") as f:
        keys = f.keys()
        
    down_keys = [k for k in keys if "lora_down" in k or "down.weight" in k or "lora_A" in k]
    block_norms = {}
    
    # Identify architecture
    arch = "Unknown"
    if any("double_blocks" in k for k in keys):
        arch = "FLUX.1"
    elif any("input_blocks" in k for k in keys):
        arch = "SDXL / SD1.5"
    elif any("diffusion_model" in k for k in keys):
        arch = "Diffusion Transformer"
        
    sd = load_file(src, device="cpu")
    
    for dk in down_keys:
        uk = None
        if "lora_down.weight" in dk:
            uk = dk.replace("lora_down.weight", "lora_up.weight")
        elif ".down.weight" in dk:
            uk = dk.replace(".down.weight", ".up.weight")
        elif "lora_A" in dk:
            uk = dk.replace("lora_A", "lora_B")
            
        if not uk or uk not in sd:
            continue
            
        w_down = sd[dk].float()
        w_up = sd[uk].float()
        
        # Calculate delta norm
        if len(w_up.shape) == 2 and len(w_down.shape) == 2:
            delta = torch.mm(w_up, w_down)
            norm = float(torch.linalg.norm(delta).item())
        else:
            norm = float((torch.linalg.norm(w_up) * torch.linalg.norm(w_down)).item())
            
        # Group into structural block name
        block_name = "other"
        m_flux = re.search(r'(double_blocks\.\d+|single_blocks\.\d+|img_in|txt_in)', dk)
        m_sd = re.search(r'(input_blocks\.\d+|middle_block|output_blocks\.\d+|conditioner\.\w+)', dk)
        
        if m_flux:
            block_name = m_flux.group(1)
        elif m_sd:
            block_name = m_sd.group(1)
        elif "te" in dk or "clip" in dk or "text" in dk:
            block_name = "text_encoder"
            
        if block_name not in block_norms:
            block_norms[block_name] = []
        block_norms[block_name].append(norm)
        
    blocks_result = []
    all_avg_norms = []
    
    for bname, nlist in block_norms.items():
        avg_n = sum(nlist) / len(nlist) if nlist else 0.0
        max_n = max(nlist) if nlist else 0.0
        all_avg_norms.append(avg_n)
        blocks_result.append({
            "block_name": bname,
            "layer_count": len(nlist),
            "avg_norm": avg_n,
            "max_norm": max_n
        })
        
    overall_mean = (sum(all_avg_norms) / len(all_avg_norms)) if all_avg_norms else 1.0
    
    for b in blocks_result:
        b["is_toxic"] = b["avg_norm"] > (overall_mean * 2.8) and b["avg_norm"] > 0.5
        b["is_dead"] = b["avg_norm"] < (overall_mean * 0.05) or b["avg_norm"] < 1e-4
        b["weight_percentage"] = (b["avg_norm"] / (sum(all_avg_norms) or 1.0)) * 100.0
        
    blocks_result.sort(key=lambda x: x["block_name"])
    
    final_data = {
        "lora_path": src,
        "architecture": arch,
        "overall_mean_norm": overall_mean,
        "toxic_count": sum(1 for b in blocks_result if b["is_toxic"]),
        "dead_count": sum(1 for b in blocks_result if b["is_dead"]),
        "blocks": blocks_result
    }
    
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(final_data, f, indent=2)
    print(f"[GeneTherapy] Successfully analyzed {len(blocks_result)} layer blocks.")

if __name__ == '__main__':
    analyze()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            onLog?.Invoke($"[LoRAMancer] Analyzing layer block energy distribution for {Path.GetFileName(loraPath)}...");

            var envVars = _toolkitSetup?.GetIsolatedEnvironmentVariables() ?? new Dictionary<string, string>();
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(loraPath) ?? AppContext.BaseDirectory,
                envVars,
                line => onLog?.Invoke(line),
                line => onLog?.Invoke($"[stderr] {line}"),
                cancellationToken
            );

            if (exitCode == 0 && File.Exists(jsonOutPath)) {
                string json = await File.ReadAllTextAsync(jsonOutPath, cancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var blocks = new List<LayerBlockInfo>();
                if (root.TryGetProperty("blocks", out var blocksEl)) {
                    foreach (var b in blocksEl.EnumerateArray()) {
                        blocks.Add(new LayerBlockInfo(
                            b.GetProperty("block_name").GetString() ?? "unknown",
                            b.GetProperty("layer_count").GetInt32(),
                            b.GetProperty("avg_norm").GetDouble(),
                            b.GetProperty("max_norm").GetDouble(),
                            b.GetProperty("is_toxic").GetBoolean(),
                            b.GetProperty("is_dead").GetBoolean(),
                            b.GetProperty("weight_percentage").GetDouble()
                        ));
                    }
                }

                return new GeneTherapyAnalysis(
                    loraPath,
                    blocks,
                    root.GetProperty("overall_mean_norm").GetDouble(),
                    root.GetProperty("toxic_count").GetInt32(),
                    root.GetProperty("dead_count").GetInt32(),
                    root.GetProperty("architecture").GetString() ?? "Unknown"
                );
            }
        } catch (Exception ex) {
            onLog?.Invoke($"[LoRAMancer Exception] Analysis failed: {ex.Message}");
        } finally {
            try {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
                if (File.Exists(jsonOutPath)) File.Delete(jsonOutPath);
            } catch { }
        }

        return null;
    }

    public async Task<LoraSurgeryResult> PruneOrAttenuateBlocksAsync(
        string loraPath,
        string outputPath,
        List<string> targetBlocks,
        float factor = 0.0f,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(loraPath)) {
            return new LoraSurgeryResult(false, outputPath, "Source LoRA file not found.", TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_prune_{Guid.NewGuid():N}.py");

        string blocksJson = System.Text.Json.JsonSerializer.Serialize(targetBlocks);

        string pythonCode = $$"""
import sys, os, json
import torch
from safetensors.torch import load_file, save_file
import safetensors

def prune():
    src = r"{{loraPath}}"
    dst = r"{{outputPath}}"
    factor = {{factor}}
    target_blocks = {{blocksJson}}
    
    print(f"[GeneTherapy Pruner] Loading {src}...")
    sd = load_file(src, device="cpu")
    
    with safetensors.safe_open(src, framework="pt") as f:
        meta = dict(f.metadata() or {})
        
    meta["loramancer_pruned_blocks"] = ",".join(target_blocks)
    meta["loramancer_attenuation_factor"] = str(factor)
    
    modified_count = 0
    for k in list(sd.keys()):
        for target in target_blocks:
            if target in k:
                if factor == 0.0:
                    sd[k] = torch.zeros_like(sd[k])
                else:
                    sd[k] = sd[k] * factor
                modified_count += 1
                break
                
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    save_file(sd, dst, metadata=meta)
    print(f"[GeneTherapy Pruner] Successfully modified {modified_count} tensors across {len(target_blocks)} blocks.")

if __name__ == '__main__':
    prune()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            onLog?.Invoke($"[LoRAMancer] Pruning {targetBlocks.Count} blocks (factor={factor:F2}) into {Path.GetFileName(outputPath)}...");

            var envVars = _toolkitSetup?.GetIsolatedEnvironmentVariables() ?? new Dictionary<string, string>();
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory,
                envVars,
                line => onLog?.Invoke(line),
                line => onLog?.Invoke($"[stderr] {line}"),
                cancellationToken
            );

            sw.Stop();
            if (exitCode == 0 && File.Exists(outputPath)) {
                string msg = $"Pruned {targetBlocks.Count} blocks in {sw.Elapsed.TotalSeconds:F1}s!";
                onLog?.Invoke($"[LoRAMancer] {msg}");
                return new LoraSurgeryResult(true, outputPath, msg, sw.Elapsed);
            } else {
                return new LoraSurgeryResult(false, outputPath, "Prune script exited with error.", sw.Elapsed);
            }
        } catch (Exception ex) {
            sw.Stop();
            return new LoraSurgeryResult(false, outputPath, ex.Message, sw.Elapsed);
        } finally {
            try {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            } catch { }
        }
    }

    public async Task<LoraSurgeryResult> ExtractLoraFromCheckpointAsync(
        string finetunedPath,
        string basePath,
        string outputPath,
        int targetRank = 16,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (!File.Exists(finetunedPath)) {
            return new LoraSurgeryResult(false, outputPath, $"Finetuned checkpoint '{finetunedPath}' not found.", TimeSpan.Zero);
        }
        if (!File.Exists(basePath)) {
            return new LoraSurgeryResult(false, outputPath, $"Base checkpoint '{basePath}' not found.", TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_extract_{Guid.NewGuid():N}.py");

        string pythonCode = $$"""
import sys, os, time
import torch
from safetensors.torch import load_file, save_file
import safetensors

def extract():
    src_ft = r"{{finetunedPath}}"
    src_base = r"{{basePath}}"
    dst = r"{{outputPath}}"
    target_rank = {{targetRank}}
    
    print(f"[LoRA Extractor] Loading finetuned checkpoint: {src_ft}")
    sd_ft = load_file(src_ft, device="cpu")
    print(f"[LoRA Extractor] Loading base checkpoint: {src_base}")
    sd_base = load_file(src_base, device="cpu")
    
    lora_sd = {}
    linear_keys = [k for k in sd_ft.keys() if k in sd_base and len(sd_ft[k].shape) == 2]
    print(f"[LoRA Extractor] Found {len(linear_keys)} shared 2D weight matrices to subtract & decompose...")
    
    for i, k in enumerate(linear_keys):
        w_ft = sd_ft[k].float()
        w_base = sd_base[k].float()
        delta = w_ft - w_base
        
        # SVD decomposition: delta = U * S * Vh
        U, S, Vh = torch.linalg.svd(delta, full_matrices=False)
        U = U[:, :target_rank]
        S = S[:target_rank]
        Vh = Vh[:target_rank, :]
        
        s_sqrt = torch.diag(torch.sqrt(S))
        new_up = torch.mm(U, s_sqrt)
        new_down = torch.mm(s_sqrt, Vh)
        
        # Standard LoRA key names
        base_clean = k.replace(".weight", "")
        up_key = f"lora_unet_{base_clean}.lora_up.weight"
        down_key = f"lora_unet_{base_clean}.lora_down.weight"
        
        lora_sd[up_key] = new_up.to(dtype=torch.float16)
        lora_sd[down_key] = new_down.to(dtype=torch.float16)
        
        if (i + 1) % 50 == 0 or (i + 1) == len(linear_keys):
            pct = int((i + 1) / len(linear_keys) * 100)
            print(f"[LoRA Extractor] Decomposed: {i + 1}/{len(linear_keys)} matrices ({pct}%)...")
            
    metadata = {
        "ss_network_dim": str(target_rank),
        "ss_network_alpha": str(target_rank),
        "loramancer_extracted": "true",
        "loramancer_source_ft": os.path.basename(src_ft),
        "loramancer_source_base": os.path.basename(src_base)
    }
    
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    save_file(lora_sd, dst, metadata=metadata)
    print(f"[LoRA Extractor] Successfully extracted {target_rank}-rank LoRA to {dst}!")

if __name__ == '__main__':
    extract()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, cancellationToken);
            onLog?.Invoke($"[LoRAMancer] Extracting rank-{targetRank} LoRA from checkpoint subtraction...");

            var envVars = _toolkitSetup?.GetIsolatedEnvironmentVariables() ?? new Dictionary<string, string>();
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory,
                envVars,
                line => onLog?.Invoke(line),
                line => onLog?.Invoke($"[stderr] {line}"),
                cancellationToken
            );

            sw.Stop();
            if (exitCode == 0 && File.Exists(outputPath)) {
                long size = new FileInfo(outputPath).Length;
                string msg = $"Extracted rank-{targetRank} LoRA in {sw.Elapsed.TotalSeconds:F1}s! Output size: {size / (1024.0 * 1024.0):F1}MB";
                onLog?.Invoke($"[LoRAMancer] {msg}");
                return new LoraSurgeryResult(true, outputPath, msg, sw.Elapsed);
            } else {
                return new LoraSurgeryResult(false, outputPath, $"Extraction exited with code {exitCode}.", sw.Elapsed);
            }
        } catch (Exception ex) {
            sw.Stop();
            return new LoraSurgeryResult(false, outputPath, ex.Message, sw.Elapsed);
        } finally {
            try {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            } catch { }
        }
    }
}

public record LayerBlockInfo(
    string BlockName,
    int LayerCount,
    double AverageNorm,
    double MaxNorm,
    bool IsToxic,
    bool IsDead,
    double WeightPercentage
);

public record GeneTherapyAnalysis(
    string LoraPath,
    List<LayerBlockInfo> Blocks,
    double OverallAverageNorm,
    int ToxicBlockCount,
    int DeadBlockCount,
    string DetectedArchitecture
);

