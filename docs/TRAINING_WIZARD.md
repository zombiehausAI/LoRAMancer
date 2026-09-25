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
- **Donor LoRA Auto-Conversion**: When importing recipes or donor LoRAs that specify fixed `TotalSteps` alongside `Epochs`, steps per epoch is automatically derived as $\text{Derived Steps per Epoch} = \text{TotalSteps} / \text{Epochs}$. Checkpoint save frequency (`save_every`) matches this exact epoch interval.
- **Dynamic Checkpoint Retention**: `max_step_saves_to_keep` dynamically scales to retain all epoch checkpoints ($\lceil \text{TotalSteps} / \text{save\_every} \rceil$) rather than discarding earlier epochs.
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

## 7. Dual Periodic Sample Prompts & Customizable Negative Prompts

During training, AI-Toolkit periodically pauses to generate preview images to verify that your concept or style is learning properly without overbaking:
- **1 or 2 Sample Prompts**:
  - **Sample Prompt 1 (Primary)**: Required prompt to evaluate primary subject recall and trigger activation.
  - **Sample Prompt 2 (Optional)**: Optional second prompt with a different camera angle, backdrop, or lighting. If left blank, only Sample Prompt 1 is submitted to AI-Toolkit.
  - **`{trigger}` Token Substitution**: Both prompt fields support the `{trigger}` placeholder, which is automatically substituted with your chosen trigger word upon saving or starting training.
- **Architecture-Calibrated Negative Prompts**:
  - Default negative values are automatically populated based on the chosen model architecture (e.g. standard negative embeddings for SDXL, Pony, Illustrious, and SD 1.5, or clean/empty defaults for flow-matching models like FLUX.1 and Chroma).
  - Users have full freedom to modify, replace, or completely clear the negative values.
  - A **"Reset Defaults"** button is provided to instantly revert both sample prompts and negative values to the model architecture's recommended defaults.

## 8. Donor LoRA Recipe Cloning & Identity Hygiene

When cloning or borrowing training parameters from an existing "donor" LoRA (via **Use Training Settings** in LoRA Manager or **Clone Configuration** in Metadata Inspector):
- **Mathematical Hyperparameter Extraction**: Network Rank (Dim), Network Alpha, Learning Rates (UNet and Text Encoder), Optimizer algorithm, and Epochs are extracted and converted into AMD ROCm safe standards.
- **Identity Isolation**: The donor's name/filename and activation trigger words are strictly omitted and left empty. This guarantees that your new project starts with a clean slate, prompting you to provide a unique run name and trigger word tailored specifically to your new dataset without polluting it with donor tags or naming schemes.

## 9. Pro Mode Tabbed Layout, Augmentations & Tag Conditioning

To prevent UI crowding and provide an intuitive workflow, Pro Mode organizes advanced controls into clean thematic tabs:
- **Training & Epochs**: Fine-tune Network Rank ($dim$), Alpha, Epochs, Repeats, Batch Size, Step overrides, and Checkpoint Save Frequency (`save_every` epochs).
- **Optimizer & LR**: Configure primary Learning Rate, UNet/Text Encoder independent rates, ROCm-optimized optimizers (`adamw`, `prodigy`, `adafactor`, `lion`), LR schedulers, and precision (`bf16`/`fp16`).
- **Augmentation & Conditioning**:
  - **Horizontal Flip Augmentation (`flip_aug`)**: Mirrors images randomly left-to-right to double effective dataset volume. Ideal for art styles, lighting, and symmetrical objects; easily disabled for asymmetric characters or text.
  - **Shuffle Caption Tags (`shuffle_tokens`)**: Randomizes comma-separated tags per epoch to break positional bias. Essential for Pony V6, Illustrious, and booru tag sets.
  - **Keep Initial Tokens (`keep_tokens`)**: Sets how many initial tags (such as `{trigger}` or `score_9, score_8_up`) remain anchored at the front of the prompt while the remaining tags are shuffled.
  - **CLIP Skip (`clip_skip`)**: Configures the penultimate text encoder layer skip. Automatically defaults to `2` for Pony Diffusion V6 XL and Illustrious architectures.
- **Sample Prompts**: Dual test prompts with `{trigger}` substitution and architecture-calibrated negative prompts.
