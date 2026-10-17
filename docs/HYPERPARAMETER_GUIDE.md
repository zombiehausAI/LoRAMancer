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
  - **ChromaHD-1**: Next-generation flow-matching transformer model (`lodestones/Chroma1-HD` via `arch: chroma`). Supported natively in AI-Toolkit at 1024x1024 resolution.
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

---

## 6. Subject-Specific Recipe Architectures & Calibration

Different training subjects require completely different mathematical bottlenecks and captioning strategies. LoRAMancer ships with calibrated, built-in recipes targeting three distinct training philosophies:

### A. Characters & Likeness Preserving (Ultra-Low Rank Bottleneck)

* **Goal**: 100% accurate facial and anatomical likeness with complete freedom to change clothing, pose, lighting, and background scenes.
* **The Problem It Solves**: Traditional high-rank character training (Rank 32/64) causes "plastic skin", doll eyes, and background bleed where the subject can only ever be generated in the lighting or room of the training photos.
* **The Formula**:
  * **Network Rank**: **2** (Chroma) or **4** (Pony, FLUX.1, Illustrious, SDXL, SD 1.5, SD 3.5).
  * **Network Alpha**: `16.0` (or `4.0`).
  * **Text Encoders**: **Frozen (`0.0`)**. Crucial for preventing the prompt encoder from redefining generic words like "person", "woman", or "man".
  * **Total Steps**: **1,000 to 1,200 steps** (the golden pocket for human facial geometry).
  * **Augmentation**: `FlipAug: true`, `ShuffleTokens: false`, `KeepTokens: 1`.
* **Why It Works**: The mathematical capacity of Rank 2–4 is too narrow to memorize background rooms, camera lenses, or furniture. The model is forced to allocate 100% of its parameters to what varies least: the geometry of the person's face.

### B. Clothing & Attire Isolation (Zero-Bleed Surface Texturing)

* **Goal**: Perfect transfer of a garment, hosiery, costume, or armor onto any character, body type, or scene without background bleed.
* **The Problem It Solves**: Datasets of clothing (like pantyhose, jackets, or dresses) frequently show lower bodies in bedrooms or living rooms. Naive training permanently grafts the bedroom and high heels onto the garment.
* **The Formula**:
  * **Negative Decoupling Captioning**: Explicitly tag all neighboring elements: footwear (`stiletto heels`, `sneakers`), backgrounds (`bedroom`, `wooden floor`, `white backdrop`), surrounding clothes (`miniskirt`, `shorts`), and poses (`kneeling`, `standing`). Whatever you describe in the caption is attributed to prompt tokens; only the uncaptioned garment is bound to the trigger.
  * **Network Rank**: **4** (`Alpha: 4.0`).
  * **Text Encoders**: **Frozen (`0.0`)**.
  * **Total Steps**: **900 to 950 steps** (fabric textures and sheerness converge faster than human faces; stopping before 1,200 avoids overbaking).
  * **Augmentation**: `FlipAug: true`, `ShuffleTokens: true`, `KeepTokens: 1`.

### C. Multi-Concept Environments, Themes & Game Worlds (Hierarchical Token Formula)

* **Goal**: Capturing an entire game universe or complex architectural theme (e.g. *Star Citizen*, *Cyberpunk*, fantasy cities) containing distinct ships, cockpits, hangars, orbital stations, and planetary outposts without them blending into an unrecognizable "sci-fi soup".
* **The Problem It Solves**: In a multi-concept world, training with a single trigger word causes the diffusion model to average all geometry together—cockpits appear floating inside space stations and hangars have random thrusters sticking out of walls.
* **The Formula**:
  * **Hierarchical Token Triggering**:
    * **Master Aesthetic Trigger** (applied to all images): `{trigger}_universe` (learns global shaders, materials, hard-surface textures, and lighting).
    * **Sub-Concept Anchors** (applied only to their specific category): `sc_cockpit`, `sc_interior`, `sc_hangar`, `sc_station`, `sc_planet`, `sc_ship`.
  * **Network Rank**: **32** (`Alpha: 16.0`). Unlike characters and clothing, an entire world needs wide orthogonal subspace capacity so distinct rooms and structures do not interfere.
  * **Anchor Protection**: `KeepTokens: 2` (protects `{trigger}_universe, {trigger}_subconcept` at the front of every prompt), `ShuffleTokens: false`.
  * **Total Steps**: **1,800 to 2,500 steps** (default: `2,000 steps` across a balanced dataset of 60–100 images).
  * **Dataset Balancing**: Maintain roughly equal image counts per category (~15–20 images per sub-concept) to prevent one environment type from dominating the weights.

### D. Artistic Styles, Mediums & Aesthetics (The Subject-Captioning Paradox)

* **Goal**: Transferring an artist's signature rendering technique, line weight, brushwork, or visual medium (e.g. oil impasto, watercolor, retro anime cel-shading, neon noir) onto any arbitrary subject without forcing specific objects or characters.
* **The Problem It Solves**: The "character trap." If a style dataset consists mostly of portraits of one character type or setting, training with generic captions locks the model into generating that character whenever the style trigger is invoked.
* **The Formula**:
  * **The Captioning Paradox (Describe the Subject, Never the Style)**:
    * Explicitly caption all tangible subjects: `a knight with a silver sword, standing on a misty mountain ridge, pine trees`.
    * **Never** caption the art technique (`oil painting, thick brushstrokes, moody lighting, masterpiece`). Leaving the medium uncaptioned forces the LoRA to attribute 100% of the stroke texture and color harmony to your trigger word alone.
  * **Dataset Subject Diversity**: Maximum variety across subjects (20% portraits, 20% landscapes, 20% architecture, 20% objects/vehicles, 20% dynamic action).
  * **Network Rank**: **16** (`Alpha: 16.0`). Standard 1:1 scaling provides the exact capacity needed for rich color harmonies and texture without memorizing subjects.
  * **Text Encoders**: **Frozen (`0.0`)**. Protects base model vocabulary so standard nouns (`car`, `dog`, `building`) retain their prompt meaning.
  * **Total Steps**: **1,400 to 1,600 steps** (default: `1,500 steps`). Styles require more steps than clothing to generalize across diverse subjects, but fewer than full game environments.
  * **Augmentation**: `FlipAug: true`, `ShuffleTokens: true`, `KeepTokens: 1`.

---

## 7. Master Domain Calibration Reference

| Subject Domain | Optimal Rank / Alpha | Text Encoders | Target Steps | Token Shuffling | Captioning Core Rule |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **1. People & Likeness** | **Rank 2 – 4** / 16.0 | Frozen (`0.0`) | 1,000 – 1,200 | Off (`false`) | Narrow capacity bottleneck; isolates facial geometry without background bleed. |
| **2. Clothing & Wearables** | **Rank 4** / 4.0 | Frozen (`0.0`) | 900 – 950 | On (`true`) | **Negative Decoupling**: Explicitly tag footwear, rooms, and other clothing. |
| **3. Environments & Worlds** | **Rank 32** / 16.0 | Gentle / Frozen | 1,800 – 2,500 | Off (`false`) | **Hierarchical Tokens**: `{trigger}_universe` + category sub-anchors (`sc_cockpit`, etc.). |
| **4. Artistic Styles** | **Rank 16** / 16.0 | Frozen (`0.0`) | 1,400 – 1,600 | On (`true`) | **Style Paradox**: Thoroughly tag subjects/objects; never tag the style words. |

---

## 8. Ollama Vision Autotagging Presets

LoRAMancer's integrated Ollama Vision Auto-Tagger (`OllamaTaggerDialog` and `DatasetStudioPage`) includes dedicated prompt presets tailored to these 4 training paradigms:

| Preset in Studio | Prompt Strategy | Training Synergy |
| :--- | :--- | :--- |
| **👤 Person** | Heavy focus on facial features, expression, eye direction, hair, posture, and lighting. | Pairs with Rank 2–4 bottleneck to isolate likeness. |
| **👗 Clothing** | **Negative Decoupling**: Aggressively tags surrounding room, flooring, furniture, footwear/shoes, body posture, and non-target clothing, while omitting the focal garment. | Prevents bedroom/shoe bleed into hosiery, dresses, or armor. |
| **🏞 Scenery** | **Archetype Classifier**: Explicitly categorizes zone archetypes (`cockpit`, `hangar`, `station corridor`, `outpost`, `interior`, `exterior`) plus materials and lighting. | Automatically generates the category sub-anchors needed for hierarchical world training. |
| **🎨 Style** | **Style Inversion**: Captions tangible subjects, characters, clothing, and objects in detail; **strictly forbids** mentioning the art medium, brushwork, or style descriptors. | Ensures 100% of the aesthetic gradient flows into your trigger token. |



