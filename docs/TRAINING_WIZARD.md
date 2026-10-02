# LoRAMancer Training Wizard & Estimators

LoRAMancer provides a streamlined, Easy Use training creation workflow tailored specifically for AMD ROCm hardware on Windows.

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

## 10. Background Execution & GPU Resource Lifecycle Management

Training execution is decoupled from page navigation and UI lifecycle:
- **Decoupled Background Execution**: When training starts, the Python process runs on an asynchronous worker pool managed by `TrainingRunnerService`. Navigating away from `/training` (e.g. browsing your LoRA library, inspecting metadata, or checking history) does not interrupt or abort the active training run.
- **Log Buffering & Seamless Reconnection**: Log streams and step progression are buffered in-memory. Returning to the Training Console immediately repopulates the console window with prior terminal output.
- **Synchronous Process Tree Termination**: Clicking **Stop Training** halts Python and its worker tree synchronously (`WaitForExit`), preventing orphaned PyTorch processes.
- **GPU Driver & VRAM Cooldown**: Windows ROCm / HIP driver context teardown requires 1–2 seconds to release pinned allocations. LoRAMancer enforces an automatic cooldown guard before initializing a new training run, preventing `HIP error` or `CUDA OutOfMemory` failures when stopping and quickly restarting.

## 11. Favorite & Custom Training Recipes (JSON Presets)

LoRAMancer lets creators save their battle-tested hyperparameter recipes and favorite configs for quick reuse whenever creating a new LoRA:
- **Individual JSON Storage**: All custom recipes are stored as human-readable, portable JSON files in your user configuration directory (`~/.loramancer/training_recipes/<name>.json`).
- **One-Click Recipe Dropdown**: Choose from your favorite recipes or built-in presets (FLUX.1 Dev Character, SDXL Artistic Style, Pony Booru Anime, Chroma Concept) directly at the top of the Training Wizard. Selecting a recipe instantly applies the architecture, rank, alpha, learning rates, optimizer, scheduler, repeats, augmentations, and sample prompts.
- **Right-Click Any LoRA to Save as Favorite Recipe**: From either the Card View or Table View in the LoRA Library (`/`), right-click any model and select **Save as Favorite Recipe**. LoRAMancer extracts all mathematical hyperparameters (rank, alpha, learning rates, optimizer, precision, epochs, schedulers) without carrying over the old dataset folder, output paths, or previous model filenames. The recipe is saved straight into `~/.loramancer/training_recipes/` with `⭐` favorite status, ready to be selected in the wizard at any time.
- **Save Current as Favorite**: Found a hyperparameter combination that produces great likeness or texture? Click **Save Favorite Recipe** in the wizard bar, enter a recipe name and description, and your exact configuration is instantly saved and marked as a favorite (`⭐`).
- **Favorite & Deletion Management**: Toggle favorite status (`⭐`) on any recipe directly from the selector bar or delete obsolete custom presets with one click.

## 12. Stage 2: Recipe Studio & Auxiliary LoRA Conditioning (`/recipe`)

To give complex training workflows room to breathe without modal constraints, LoRAMancer provides a dedicated **Stage 2: Recipe Studio & Configuration Forge** page:
- **Dedicated Full-Width Workspace**: Replaces cramped dialogs with a responsive multi-column layout organizing Model Architecture, Auxiliary LoRA conditioning, Dataset inspection, Hyperparameters, and Live Telemetry.
- **Incorporated Auxiliary LoRA (Residual Delta Training)**:
  - Condition new training runs on an existing frozen LoRA adapter.
  - Mathematically, backpropagation gradients adapt around the auxiliary adapter weights, teaching the new LoRA only the novel residual features without concept fighting or destructive interference.
  - Adjustable conditioning multiplier / scale slider (`0.1x` to `2.0x`).
  - Real-time architecture parity detection (verifies compatibility between the attached LoRA and the chosen base diffusion model).
- **Surgical Layer Filter (Chop-Shop Inspired)**:
  - Selectively condition on specific anatomical or functional parts of the auxiliary LoRA rather than the entire file.
  - **Calibrated Presets**:
    - 🌟 **All Components (100% Full Model)**: Passes all layers directly.
    - 🎨 **Visual DiT / UNet Only**: Automatically zeroes text encoders (`lora_te`, `lora_clip`, `T5`) to prevent trigger tag pollution or prompt overwriting while preserving full visual and stylistic transfer.
    - 🔤 **Prompt Triggers & Steering Only**: Retains only text encoders to transfer concept steering while leaving visual geometry completely open for new learning.
    - 👤 **Identity & Bone Structure Only**: Retains mid-block geometry (`MID00`, FLUX `double_blocks 6-12`).
    - 💡 **Lighting, Palette & Mood Only**: Retains early input layers (`IN00-IN03`, FLUX `double_blocks 0-5`).
    - 🔍 **Detail, Texture & Skin Pores Only**: Retains high-frequency output layers (`OUT09-OUT11`, FLUX `single_blocks 30+`).
    - ✂️ **Custom Chop-Shop Matrix**: Expand the interactive parts matrix to customize individual component weights (`0.1x` to `1.5x`) or toggle individual parts on/off.
  - **Zero-Latency Slicing & Caching**: When filtering is requested, LoRAMancer slices the tensor dictionary into a cached companion file (`~/.loramancer/aux_slices/`), zeroing out excluded weights while preserving mathematical rank integrity.
- **Library Context Action ("Add to Recipe")**:
  - Right-click any LoRA card or table row in the LoRA Library (`/`) and select **Add to Recipe (Auxiliary LoRA)** to instantly load it into the Recipe Studio as the conditioning adapter.
- **1-Click Forge Handoff**:
  - Click **Send to Forge & Launch** to generate the AI-Toolkit YAML and automatically dispatch to **Stage 3: Train & Forge** (`/training?config=...&autostart=true`).
  - Or click **Queue Only** to add the run to the training queue without immediate execution.




