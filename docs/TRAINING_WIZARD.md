# LoRAMancer Training Wizard & Estimators

LoRAMancer provides a streamlined, Civitai-inspired training creation workflow tailored specifically for AMD ROCm hardware on Windows.

## 1. Subject-Type Intelligent Presets

Rather than requiring users to manually tune arcane hyperparameters, the Training Wizard offers calibrated presets:

- **Character / Person**: Rank 16, Alpha 16, LR `1e-4`, 10 epochs, 10 repeats. Optimizes facial, costume, and likeness consistency while preserving flexibility.
- **Art Style / Aesthetic**: Rank 32, Alpha 32, LR `5e-5`, 12 epochs, 8 repeats. Captures brush strokes, lighting palettes, and medium textures without burning colors.
- **Concept / Object**: Rank 16, Alpha 16, LR `1e-4`, 10 epochs, 12 repeats. Ideal for mechanical props, vehicles, or fantasy items.
- **Clothing / Outfit**: Rank 16, Alpha 16, LR `8e-5`, 10 epochs, 10 repeats. Tailored for apparel geometry across diverse poses.
- **Custom / Pro**: Complete manual override for learning rates, dimensions, schedulers, and repeats.

## 2. Live Resource & Duration Estimators

Before launching training, LoRAMancer calculates live predictions and generates exact step and checkpoint math:

$$\text{Steps per Epoch} = \left\lceil \frac{\text{Images} \times \text{Repeats}}{\text{Batch Size} \times \text{Gradient Accumulation}} \right\rceil$$

$$\text{Total Training Steps} = \text{Epochs} \times \text{Steps per Epoch}$$

$$\text{Save Every (Steps)} = \text{SaveEveryNEpochs} \times \text{Steps per Epoch}$$

- **Dynamic Epoch-to-Step Translation**: Users specify the intuitive number of **Epochs** (cycles through the dataset) and **Repeats per Image**. LoRAMancer scans the dataset folder, detects the image count, and computes the exact total steps and checkpoint save intervals for AI-Toolkit.
- **Total Steps**: Shows exact iteration budget and checkpoint save intervals.
- **Estimated VRAM**: Evaluates GPU memory requirements for the target architecture (Chroma1-HD: ~16 GB, FLUX.1: ~14 GB, SDXL/Pony/Illustrious: ~9.2 GB, SD 1.5: ~5.2 GB) against the user's detected AMD Radeon GPU.
- **Estimated Output File Size**: Calculates final `.safetensors` file size from network rank ($dim$).
- **Estimated Training Time**: Projects training duration based on AMD RDNA2 / RDNA3 / RDNA4 step velocity benchmarks.


## 3. Dataset Health Inspector & Auto-Tagging

- **Image Audit**: Scans dataset folders for valid image formats (`.png`, `.jpg`, `.jpeg`, `.webp`), checks for low-resolution files (<15 KB), and audits sample count.
- **Caption Audit**: Flags images lacking `.txt` or `.caption` files.
- **1-Click Trigger Prepend**: Automatically prepends the user's trigger word to all caption files (or creates them if missing) ensuring trigger token placement.

## 4. AMD ROCm Pre-Flight Safety Engine

Prior to initiating training, the pre-flight advisor validates:
1. **ROCm PyTorch Availability**: Ensures AMD GPU is recognized.
2. **VRAM Safety**: Alerts if estimated memory usage approaches or exceeds physical VRAM, recommending disk latent caching.
3. **Storage Buffer**: Confirms at least 10 GB of free disk space on the target output drive for weights, optimizer states, and latents.
4. **Attention & Precision Guards**: Automatically mandates `sdpa` attention and `bf16` precision to prevent Windows ROCm driver panics or NaN loss spikes.

## 5. Reopening & Editing Past or Failed Runs in Wizard

Failed or past training runs can be reopened directly in the visual Training Wizard with one click:
- **From Training Console**: When training encounters an error or fails, the failure banner displays an **Edit in Wizard** button alongside **Retry Training**, allowing instant adjustments.
- **From History & Vault**: Every card on the `/history` page includes an **Edit in Wizard** action, pre-populating all hyperparameters, datasets, run names, trigger words, and custom checkpoint paths.

## 6. Standalone & Custom Base Model Checkpoint Support

While standard models (FLUX.1, SDXL, SD 1.5) download their pipeline from Hugging Face automatically, standalone models (such as **Chroma1-HD**) require a local `.safetensors` file. The Training Wizard includes a dedicated **Custom Base Checkpoint File** browser to select local model files directly from disk.
