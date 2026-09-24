# LoRA Training Hyperparameters & Options Guide

This guide explains every setting, hyperparameter, and concept encountered when configuring a LoRA training run or cloning settings from an existing model in LoRAMancer.

---

## 1. Core Model Identifiers & Setup

### Target Base Model Architecture
- **What it is**: The foundation diffusion or flow-matching checkpoint against which your LoRA is trained.
- **Architectures Supported**:
  - **FLUX.1 Dev / Schnell**: 12B parameter flow-matching multimodal DiT. Uses `double_blocks` and `single_blocks` attention layers. Requires higher VRAM (16GB+ recommended).
  - **SDXL 1.0**: 3.5B parameter latent diffusion model with dual text encoders (CLIP ViT-L + OpenCLIP ViT-G). Excellent balance of speed and fidelity.
  - **Pony Diffusion / Illustrious**: Specialized SDXL-derivative architectures optimized for stylized, anime, and character datasets.
  - **Chroma / HunyuanVideo / Wan 2.1**: Video and specialized next-generation transformer models.
  - **SD 1.5**: Legacy 1B parameter model with low VRAM footprint.
- **Rule of thumb**: A LoRA trained for FLUX.1 cannot be loaded into SDXL (and vice-versa). Always choose the base model family you plan to generate images with.

### Run Name
- **What it is**: The unique project identifier used for checkpoint subfolders, tensor keys, and configuration files.
- **Best Practice**: Use alphanumeric characters and underscores (e.g. `cyberpunk_street_flux`, `character_elena_v1`). When cloning from a donor LoRA, LoRAMancer automatically suffixes `_fork` to protect existing files.

### Trigger Word
- **What it is**: The unique keyword or token that prompts the model to recall your trained subject, concept, or style.
- **Examples**: `ohwx woman`, `sk_artstyle`, `neon_cyber_suit`.
- **When to use**:
  - **Unique Subject (Character/Object)**: Always use a trigger word (e.g. `zxc_dog`) so the model associates your specific subject with this rare token.
  - **Style LoRA**: Optional. If omitted, the style blends generally into the base model's default aesthetic.

---

## 2. Low-Rank Adaptation (LoRA) Hyperparameters

LoRA works by freezing the original foundation model's weights $W$ and decomposing weight updates $\Delta W$ into two low-rank matrices: $A$ and $B$, such that:
$$\Delta W = B \times A \times \frac{\alpha}{r}$$
where $r$ is the **Rank (Dim)** and $\alpha$ is **Network Alpha**.

### Network Rank (Dim)
- **What it is**: The rank $r$ of the decomposition matrices. It defines the parameter capacity and expressiveness of the LoRA.
- **Effect on Model**:
  - **Low Rank (4 - 16)**: Low file size (~10MB - 35MB for SDXL, ~40MB - 120MB for FLUX), fast training, highly regularized. Great for simple styles, moods, or distinct objects. Low risk of overfitting.
  - **Medium Rank (16 - 32)**: Standard sweet spot for characters, likenesses, detailed clothing, and artistic styles. Excellent balance of flexibility and file size.
  - **High Rank (64 - 128+)**: Maximum capacity for complex subjects, diverse clothing sets, or composite styles. Generates larger files (200MB - 800MB+) and increases risk of memorization/overfitting if the dataset is small.
- **Recommended Default**: `16` or `32` for characters and styles; `16` for FLUX.1.

### Network Alpha ($\alpha$)
- **What it is**: A scalar multiplier that controls the strength and scale of the low-rank updates relative to the base model.
- **How it works**: Weight updates are scaled by $\frac{\alpha}{\text{Dim}}$.
  - If $\alpha = \text{Dim}$ (e.g., 16/16), the scaling multiplier is $1.0$. This is the standard 1:1 ratio used in most Kohya and AI-Toolkit recipes.
  - If $\alpha = \frac{\text{Dim}}{2}$ (e.g., 8/16 or 16/32), the scaling multiplier is $0.5$, resulting in softer, gentler weight updates that are less prone to blowups.
- **Recommended Default**: Set equal to Rank (`16.0` when Dim is 16) or half of Rank.

---

## 3. Training Dynamics & Convergence

### Learning Rate (LR)
- **What it is**: The step size taken by the optimizer when adjusting weights along the loss gradient.
- **Formatting**: Expressed in scientific notation:
  - `1e-4` = $0.0001$ (Standard baseline for AdamW on SDXL and FLUX)
  - `5e-5` = $0.00005$ (Conservative/fine-tuning rate for delicate styles)
  - `2e-4` = $0.0002$ (Aggressive rate for fast convergence or small datasets)
- **Warning Signs**:
  - **Too High**: Loss spikes or produces NaN; generated images become garbled, grainy, or saturated.
  - **Too Low**: Loss decreases too slowly; the model fails to learn the likeness or concept even after many epochs.

### Epochs
- **What it is**: The number of complete cycles through the entire training dataset.
- **Recommended Range**: `5` to `20` epochs for typical datasets (15 - 50 images).
- **Rule of thumb**: Smaller datasets with few images need more repeats or epochs; larger datasets (100+ images) need fewer epochs to avoid overtraining.

### Repeats per Image
- **What it is**: How many times each individual image in your dataset is presented to the network within a single epoch.
- **Why it exists**: Allows you to balance multi-folder datasets (e.g., repeating rare poses 10x while repeating common portrait poses only 2x).
- **Recommended Default**: `5` to `10` repeats.

### Batch Size
- **What it is**: The number of image pairs processed simultaneously in a single forward/backward pass.
- **VRAM Impact**:
  - `Batch Size 1`: Lowest VRAM usage (fits comfortably on 8GB - 16GB GPUs).
  - `Batch Size 2 or 4`: Faster gradient descent stability on high-VRAM cards (24GB+ RTX 3090/4090 or Radeon RX 7900 XTX), but increases VRAM requirements proportionately.

### Calculating Total Steps
Total optimization steps are calculated mathematically as:
$$\text{Total Steps} = \frac{\text{Total Images} \times \text{Repeats} \times \text{Epochs}}{\text{Batch Size}}$$
*Example*: 20 images $\times$ 10 repeats $\times$ 10 epochs $\div$ batch size 1 = **2,000 steps**.

---

## 4. Hardware Safety & Hardware Sanitization

### AMD ROCm Considerations
- When running on AMD Radeon hardware (via ROCm 7.x):
  - 8-bit optimizers (e.g. `AdamW8bit` from bitsandbytes) are automatically sanitized to standard 32-bit `AdamW` or PyTorch native fused optimizers to avoid CUDA-specific binary dependency crashes.
  - Flash Attention 2 (which relies on CUDA assembly kernels) is gracefully substituted with PyTorch's native Scaled Dot-Product Attention (`sdpa`) or `math`/`mem_efficient` backends.
  - LoRAMancer performs all of these conversions automatically when cloning configurations.

---

## 5. Sample Generation Prompts

- **What it is**: A test prompt executed periodically during training (e.g. every 250 steps or every epoch) to generate progress images.
- **Template format**: Use the `{trigger}` placeholder, e.g.:
  - `a high quality photograph of {trigger}, cinematic lighting, 8k uhd`
  - `digital painting of {trigger} in vibrant colors, detailed art`
- **Why it matters**: Allows you to visually observe the learning curve in real time without stopping the training process.
