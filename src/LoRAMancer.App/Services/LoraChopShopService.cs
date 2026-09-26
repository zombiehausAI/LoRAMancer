using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public enum ChopPartCategory {
    FaceAndAnatomy,
    EyesAndIris,
    HairAndHairstyle,
    ClothingAndOutfit,
    LightingAndAmbiance,
    SkinAndMicroDetails,
    PromptTriggers
}

public sealed class ChopDonorModel {
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string Architecture { get; set; } = "Unknown";
    public int Rank { get; set; } = 16;
    public double AverageNorm { get; set; }
    public List<string> InferredTags { get; set; } = new();
    public Dictionary<string, double> BlockEnergies { get; set; } = new();
    public string DominantFeature { get; set; } = "General";
}

public sealed class ChopPartAssignment {
    public ChopPartCategory Category { get; set; }
    public string PartName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string? SelectedDonorId { get; set; }
    public double Weight { get; set; } = 1.0;
    public string TargetBlocksDescription { get; set; } = string.Empty;
}

public sealed class AdvancedBlockOverride {
    public string BlockName { get; set; } = string.Empty;
    public string FunctionalGroup { get; set; } = string.Empty;
    public string? SelectedDonorId { get; set; }
    public double Weight { get; set; } = 1.0;
}

public sealed class ChopShopRecipe {
    public string RecipeName { get; set; } = "ChopShop_FrankenLoRA";
    public List<ChopDonorModel> Donors { get; set; } = new();
    public List<ChopPartAssignment> PartAssignments { get; set; } = new();
    public List<AdvancedBlockOverride> AdvancedOverrides { get; set; } = new();
    public int TargetRank { get; set; } = 16;
    public string OutputPath { get; set; } = string.Empty;
    public bool UseDenoisingTies { get; set; } = true;
}

public record ChopShopResult(bool Success, string OutputPath, string Message, int TensorsWritten, TimeSpan Duration);

public sealed class LoraChopShopService {
    private readonly ProcessRunner _processRunner;
    private readonly SettingsService? _settingsService;
    private readonly AiToolkitSetupService? _toolkitSetup;
    private readonly LoraSurgeryService _surgeryService;
    private readonly LoraDeAnonymizerService _deAnonymizer;

    public LoraChopShopService(
        ProcessRunner processRunner,
        LoraSurgeryService surgeryService,
        LoraDeAnonymizerService deAnonymizer,
        SettingsService? settingsService = null,
        AiToolkitSetupService? toolkitSetup = null
    ) {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _surgeryService = surgeryService ?? throw new ArgumentNullException(nameof(surgeryService));
        _deAnonymizer = deAnonymizer ?? throw new ArgumentNullException(nameof(deAnonymizer));
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

    public static List<ChopPartAssignment> CreateDefaultPartAssignments() {
        return new List<ChopPartAssignment> {
            new() {
                Category = ChopPartCategory.FaceAndAnatomy,
                PartName = "Chassis & Face: Identity & Bone Structure",
                Description = "Mid-block anatomical geometry, head shape, jawline, eye socket spacing, and body proportions.",
                Icon = "Face",
                TargetBlocksDescription = "Middle block (MID00 / double_blocks 6-12)",
                Weight = 1.0
            },
            new() {
                Category = ChopPartCategory.EyesAndIris,
                PartName = "Headlights: Iris & Eye Refinement",
                Description = "Catchlights, iris pigmentation, pupil sharpness, and ocular reflections.",
                Icon = "Visibility",
                TargetBlocksDescription = "High-frequency output blocks (OUT09-OUT11 / single_blocks 30-37)",
                Weight = 1.0
            },
            new() {
                Category = ChopPartCategory.HairAndHairstyle,
                PartName = "Custom Paint & Hair: Hairstyle & Strands",
                Description = "Hair flow, bangs, braid textures, and specific hairstyle color projections.",
                Icon = "Brush",
                TargetBlocksDescription = "Cross-attention tokens & mid-high output layers",
                Weight = 1.0
            },
            new() {
                Category = ChopPartCategory.ClothingAndOutfit,
                PartName = "Upholstery & Armor: Outfits & Apparel",
                Description = "Garments, jackets, accessories, lace, leather, and uniform styling.",
                Icon = "Checkroom",
                TargetBlocksDescription = "Mid-late output blocks (OUT03-OUT06 / double_blocks 13-18)",
                Weight = 1.0
            },
            new() {
                Category = ChopPartCategory.LightingAndAmbiance,
                PartName = "Engine & Glow: Lighting, Palette & Mood",
                Description = "Volumetric lighting, atmospheric grading, shadow warmth, and color temperature.",
                Icon = "Lightbulb",
                TargetBlocksDescription = "Early input blocks (IN00-IN03 / double_blocks 0-5)",
                Weight = 1.0
            },
            new() {
                Category = ChopPartCategory.SkinAndMicroDetails,
                PartName = "Detail Polish: Skin Pores, Freckles & Clarity",
                Description = "Micro-surface texture, photorealistic skin pores, wrinkles, and fine edge clarity.",
                Icon = "Grain",
                TargetBlocksDescription = "Deep residual output layers & singular value tails",
                Weight = 1.0
            },
            new() {
                Category = ChopPartCategory.PromptTriggers,
                PartName = "Steering & Triggers: Prompt Responsiveness",
                Description = "Text encoder layers dictating how trigger keywords activate and steer generation.",
                Icon = "Psychology",
                TargetBlocksDescription = "Text encoders (lora_te / lora_clip / T5)",
                Weight = 1.0
            }
        };
    }

    public async Task<ChopDonorModel> InspectDonorAsync(string filePath) {
        if (!File.Exists(filePath)) {
            throw new FileNotFoundException("Donor LoRA not found", filePath);
        }

        var donor = new ChopDonorModel {
            FilePath = filePath
        };

        try {
            var analysis = await _surgeryService.AnalyzeLayerBlocksAsync(filePath);
            if (analysis != null) {
                donor.Architecture = analysis.DetectedArchitecture;
                donor.AverageNorm = analysis.OverallAverageNorm;
                foreach (var b in analysis.Blocks) {
                    donor.BlockEnergies[b.BlockName] = b.AverageNorm;
                }
            }

            var forensic = await _deAnonymizer.ReverseEngineerLoraAsync(filePath);
            if (forensic != null) {
                donor.Rank = forensic.EffectiveRank;
                donor.InferredTags = forensic.RecoveredTriggerTokens.Take(6).Select(t => t.Token).ToList();
            }

            donor.DominantFeature = DetermineDominantFeature(donor);
        } catch {
            donor.Architecture = "Unknown";
            donor.Rank = 16;
            donor.DominantFeature = "Standard LoRA";
        }

        return donor;
    }

    public static string DetermineDominantFeature(ChopDonorModel donor) {
        if (donor.BlockEnergies.Count == 0) return "General Feature";

        double midEnergy = donor.BlockEnergies.Where(k => k.Key.Contains("mid") || k.Key.Contains("double_block.8") || k.Key.Contains("double_block.9")).Select(x => x.Value).DefaultIfEmpty(0).Average();
        double earlyEnergy = donor.BlockEnergies.Where(k => k.Key.Contains("in") || k.Key.Contains("double_block.1") || k.Key.Contains("double_block.2")).Select(x => x.Value).DefaultIfEmpty(0).Average();
        double lateEnergy = donor.BlockEnergies.Where(k => k.Key.Contains("out") || k.Key.Contains("single_block")).Select(x => x.Value).DefaultIfEmpty(0).Average();

        if (midEnergy > earlyEnergy * 1.5 && midEnergy > lateEnergy * 1.5) {
            return "Facial Likeness & Anatomy";
        }
        if (earlyEnergy > midEnergy * 1.3 && earlyEnergy > lateEnergy * 1.3) {
            return "Lighting, Atmosphere & Style";
        }
        if (lateEnergy > earlyEnergy * 1.3) {
            return "Micro-Textures & Outfits";
        }

        return "Balanced Multi-Concept";
    }

    public ChopShopRecipe GenerateSmartAutoGraftRecipe(List<ChopDonorModel> donors, string outputFolder) {
        var recipe = new ChopShopRecipe {
            Donors = donors.ToList(),
            PartAssignments = CreateDefaultPartAssignments(),
            TargetRank = donors.Count > 0 ? donors.Max(d => d.Rank) : 16
        };

        if (donors.Count == 0) return recipe;

        string baseName = Path.GetFileNameWithoutExtension(donors[0].FilePath);
        recipe.RecipeName = $"{baseName}_ChopShop_Franken";
        recipe.OutputPath = Path.Combine(outputFolder, $"{recipe.RecipeName}.safetensors");

        var anatomyDonor = donors.OrderByDescending(d => d.BlockEnergies.Where(k => k.Key.Contains("mid")).Select(x => x.Value).DefaultIfEmpty(0).Average()).FirstOrDefault() ?? donors[0];
        var lightingDonor = donors.OrderByDescending(d => d.BlockEnergies.Where(k => k.Key.Contains("in") || k.Key.Contains("double_block.1")).Select(x => x.Value).DefaultIfEmpty(0).Average()).FirstOrDefault() ?? donors[0];
        var textureDonor = donors.OrderByDescending(d => d.BlockEnergies.Where(k => k.Key.Contains("out.9") || k.Key.Contains("out.10") || k.Key.Contains("single_block.3")).Select(x => x.Value).DefaultIfEmpty(0).Average()).FirstOrDefault() ?? donors[0];
        var outfitDonor = donors.OrderByDescending(d => d.BlockEnergies.Where(k => k.Key.Contains("out.3") || k.Key.Contains("out.4") || k.Key.Contains("out.5")).Select(x => x.Value).DefaultIfEmpty(0).Average()).FirstOrDefault() ?? donors[0];

        foreach (var assignment in recipe.PartAssignments) {
            assignment.SelectedDonorId = assignment.Category switch {
                ChopPartCategory.FaceAndAnatomy => anatomyDonor.Id,
                ChopPartCategory.EyesAndIris => textureDonor.Id,
                ChopPartCategory.HairAndHairstyle => textureDonor.Id,
                ChopPartCategory.SkinAndMicroDetails => textureDonor.Id,
                ChopPartCategory.LightingAndAmbiance => lightingDonor.Id,
                ChopPartCategory.ClothingAndOutfit => outfitDonor.Id,
                ChopPartCategory.PromptTriggers => anatomyDonor.Id,
                _ => donors[0].Id
            };
        }

        return recipe;
    }

    public async Task<ChopShopResult> AssembleChopShopLoraAsync(
        ChopShopRecipe recipe,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default
    ) {
        if (recipe.Donors.Count == 0) {
            return new ChopShopResult(false, recipe.OutputPath, "No donor LoRAs selected for Chop-Shop assembly.", 0, TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        string pythonExe = ResolvePythonExecutable();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"loramancer_chopshop_{Guid.NewGuid():N}.py");
        string jsonConfigPath = Path.Combine(Path.GetTempPath(), $"loramancer_chopshop_config_{Guid.NewGuid():N}.json");

        var donorDict = recipe.Donors.ToDictionary(d => d.Id, d => d.FilePath);
        var partMap = recipe.PartAssignments.Select(p => new {
            category = p.Category.ToString(),
            donor_id = p.SelectedDonorId,
            weight = p.Weight
        }).ToList();

        var overrideMap = recipe.AdvancedOverrides.Where(o => !string.IsNullOrWhiteSpace(o.SelectedDonorId)).Select(o => new {
            block_name = o.BlockName,
            donor_id = o.SelectedDonorId,
            weight = o.Weight
        }).ToList();

        var configObj = new {
            donors = donorDict,
            parts = partMap,
            overrides = overrideMap,
            target_rank = recipe.TargetRank,
            output_path = recipe.OutputPath,
            use_ties = recipe.UseDenoisingTies
        };

        await File.WriteAllTextAsync(jsonConfigPath, JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        string pythonCode = $$"""
import sys, os, time, json
import torch
from safetensors.torch import load_file, save_file
import safetensors

def classify_tensor(key):
    k = key.lower()
    if "lora_te" in k or "text_encoder" in k or "clip" in k:
        return "PromptTriggers"
    
    # SD 1.5 / SDXL U-Net Mapping
    if "input_blocks" in k:
        if any(f"input_blocks.{i}" in k for i in range(4)):
            return "LightingAndAmbiance"
        return "ClothingAndOutfit"
    if "middle_block" in k:
        return "FaceAndAnatomy"
    if "output_blocks" in k:
        if any(f"output_blocks.{i}" in k for i in range(0, 6)):
            return "ClothingAndOutfit"
        if any(f"output_blocks.{i}" in k for i in range(6, 9)):
            return "HairAndHairstyle"
        if any(f"output_blocks.{i}" in k for i in range(9, 12)):
            return "EyesAndIris"
        return "SkinAndMicroDetails"
        
    # FLUX.1 DiT Mapping
    if "double_blocks" in k:
        for i in range(6):
            if f"double_blocks.{i}" in k or f"double_blocks_{i}" in k:
                return "LightingAndAmbiance"
        for i in range(6, 13):
            if f"double_blocks.{i}" in k or f"double_blocks_{i}" in k:
                return "FaceAndAnatomy"
        return "ClothingAndOutfit"
    if "single_blocks" in k:
        for i in range(25):
            if f"single_blocks.{i}" in k or f"single_blocks_{i}" in k:
                return "ClothingAndOutfit"
        for i in range(25, 32):
            if f"single_blocks.{i}" in k or f"single_blocks_{i}" in k:
                return "HairAndHairstyle"
        return "SkinAndMicroDetails"

    # Chroma / Generic DiT / Transformer Blocks Mapping (e.g. layers.0, transformer_blocks.5, blocks.12)
    import re
    m = re.search(r'(?:layers|transformer_blocks|blocks)[\._](\d+)', k)
    if m:
        idx = int(m.group(1))
        if idx < 4:
            return "LightingAndAmbiance"
        elif idx < 10:
            return "FaceAndAnatomy"
        elif idx < 16:
            return "ClothingAndOutfit"
        elif idx < 22:
            return "HairAndHairstyle"
        elif idx < 26:
            return "EyesAndIris"
        else:
            return "SkinAndMicroDetails"
        
    return "FaceAndAnatomy"

def run_chop_shop():
    cfg_path = r"{{jsonConfigPath}}"
    with open(cfg_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
        
    donors = cfg["donors"]
    parts_map = {p["category"]: p for p in cfg["parts"]}
    overrides_map = {o["block_name"]: o for o in cfg["overrides"]}
    target_rank = int(cfg.get("target_rank", 16))
    out_path = cfg["output_path"]
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    
    print(f"[ChopShop] Loading {len(donors)} donor models...")
    donor_weights = {}
    for did, dpath in donors.items():
        if os.path.exists(dpath):
            donor_weights[did] = load_file(dpath, device="cpu")
            print(f"[ChopShop] Loaded donor '{did}': {len(donor_weights[did])} tensors")
            
    if not donor_weights:
        print("[ChopShop] Error: No donor models loaded successfully.")
        sys.exit(1)
        
    # Gather union of all keys, ignoring internal trainer state & auxiliary metadata keys
    all_keys = set()
    for ddict in donor_weights.values():
        for k in ddict.keys():
            if not k.startswith("_aux") and not k.startswith("__") and "optimizer" not in k.lower():
                all_keys.add(k)
        
    down_keys = [k for k in all_keys if "lora_down" in k or "down.weight" in k or "lora_A" in k]
    assembled_tensors = {}
    total_grafted = 0
    
    for dk in down_keys:
        prefix = dk.replace(".lora_down.weight", "").replace(".down.weight", "").replace(".lora_A.weight", "")
        uk = dk.replace("lora_down", "lora_up").replace(".down.weight", ".up.weight").replace(".lora_A.weight", ".lora_B.weight")
        ak = dk.replace("lora_down.weight", "alpha").replace("lora_A.weight", "alpha").replace("down.weight", "alpha")
        
        # Determine assigned category & donor
        assigned_donor_id = None
        assigned_weight = 1.0
        
        # Check explicit block override first
        for bname, o in overrides_map.items():
            if bname in prefix:
                assigned_donor_id = o.get("donor_id")
                assigned_weight = float(o.get("weight", 1.0))
                break
                
        # If no override, use high-level category
        if not assigned_donor_id:
            cat = classify_tensor(dk)
            part = parts_map.get(cat)
            if part:
                assigned_donor_id = part.get("donor_id")
                assigned_weight = float(part.get("weight", 1.0))
                
        if not assigned_donor_id or assigned_donor_id not in donor_weights:
            # Fallback to first donor
            assigned_donor_id = list(donor_weights.keys())[0]
            
        src_dict = donor_weights[assigned_donor_id]
        if dk in src_dict and uk in src_dict:
            down_tensor = src_dict[dk].to(torch.float32) * assigned_weight
            up_tensor = src_dict[uk].to(torch.float32)
            
            # SVD Resizing if donor rank != target_rank
            curr_rank = down_tensor.shape[0]
            if curr_rank > target_rank:
                delta = torch.matmul(up_tensor, down_tensor)
                U, S, Vh = torch.linalg.svd(delta, full_matrices=False)
                U_r = U[:, :target_rank]
                S_r = S[:target_rank]
                Vh_r = Vh[:target_rank, :]
                up_tensor = U_r * torch.sqrt(S_r)
                down_tensor = torch.diag(torch.sqrt(S_r)) @ Vh_r
                
            assembled_tensors[dk] = down_tensor.to(torch.float16)
            assembled_tensors[uk] = up_tensor.to(torch.float16)
            if ak in src_dict:
                assembled_tensors[ak] = src_dict[ak]
            total_grafted += 2
            
    # Include any remaining legitimate non-matrix model tensors (e.g., alphas, scales, text encoder projections)
    for dk in all_keys:
        if dk.startswith("_aux") or dk.startswith("__") or "optimizer" in dk.lower():
            continue
        if dk not in assembled_tensors and not ("lora_down" in dk or "lora_up" in dk or "lora_A" in dk or "lora_B" in dk):
            for sdict in donor_weights.values():
                if dk in sdict:
                    assembled_tensors[dk] = sdict[dk]
                    break
                    
    print(f"[ChopShop] Assembled {len(assembled_tensors)} tensors. Saving to {out_path}...")
    meta = {
        "generator": "LoRAMancer Chop-Shop Studio",
        "recipe_type": "Franken-LoRA Chimera",
        "target_rank": str(target_rank),
        "timestamp": str(time.time())
    }
    save_file(assembled_tensors, out_path, metadata=meta)
    print(f"[ChopShop] Successfully saved Franken-LoRA to {out_path}!")

if __name__ == '__main__':
    run_chop_shop()
""";

        try {
            await File.WriteAllTextAsync(scriptPath, pythonCode, Encoding.UTF8, cancellationToken);
            var envVars = _toolkitSetup?.GetIsolatedEnvironmentVariables() ?? new Dictionary<string, string>();
            int exitCode = await _processRunner.RunAsync(
                pythonExe,
                $"\"{scriptPath}\"",
                Path.GetDirectoryName(recipe.OutputPath) ?? AppContext.BaseDirectory,
                envVars,
                line => onLog?.Invoke(line),
                line => onLog?.Invoke($"[WARN] {line}"),
                cancellationToken
            );

            sw.Stop();

            if (exitCode == 0 && File.Exists(recipe.OutputPath)) {
                return new ChopShopResult(true, recipe.OutputPath, $"Successfully assembled Franken-LoRA '{Path.GetFileName(recipe.OutputPath)}'!", recipe.Donors.Count, sw.Elapsed);
            } else {
                return new ChopShopResult(false, recipe.OutputPath, "Chop-Shop assembly process exited with non-zero code.", 0, sw.Elapsed);
            }
        } catch (Exception ex) {
            return new ChopShopResult(false, recipe.OutputPath, $"Assembly exception: {ex.Message}", 0, sw.Elapsed);
        } finally {
            if (File.Exists(scriptPath)) try { File.Delete(scriptPath); } catch { }
            if (File.Exists(jsonConfigPath)) try { File.Delete(jsonConfigPath); } catch { }
        }
    }
}
