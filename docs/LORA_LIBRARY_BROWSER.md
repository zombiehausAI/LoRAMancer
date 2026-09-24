# LoRA Library & Visual Browser

## Overview

The **LoRA Library & Visual Browser** is LoRAMancer's core discovery and inspection hub (`/`). It enables creators with large existing collections of `.safetensors` models (e.g. from ComfyUI or Automatic1111) to visually browse models with preview thumbnails, inspect Civitai metadata, and seamlessly borrow proven hyperparameter recipes for new training runs.

---

## 1. Visual Card Gallery & Thumbnails

The browser supports both a rich **Card Grid** view and a compact **Table** view:
- **Non-Blocking Background Scanning**: When scanning large directories (even thousands of models), scanning executes asynchronously in the background (`ScanDirectoryStreamAsync`) without freezing the user interface.
  - Discovered models stream into the gallery in real time as each header is parsed.
  - Creators can immediately search, filter, inspect metadata, or click **"Use Settings"** on any loaded model while the background scan continues running.
  - A real-time progress banner displays current progress with a 1-click **"Stop Scan"** cancellation control.
- **Local Thumbnail Auto-Discovery**: When pointing to any folder, LoRAMancer automatically detects local companion images:
  - `<model_name>.png`
  - `<model_name>.preview.png`
  - `<model_name>.jpg`
  - `<model_name>.webp`
- **Civitai Auto-Enrichment**: If a model does not have a local thumbnail, clicking **"Find on Civitai"** (or **"Fetch Civitai Info"** in bulk) hashes the file with SHA256 and queries the Civitai API to retrieve:
  - Official model title and version name
  - High-resolution preview image (cached locally in `~/.loramancer/lora_cache/`)
  - Trained trigger words with a 1-click **Copy Trigger** button
  - Recommended sample prompts
- **Persistent Cache**: All hashes and downloaded preview thumbnails are cached in `~/.loramancer/lora_cache/` so subsequent folder loads are instantaneous.

---

## 2. "Use Training Settings" (Hyperparameter Cloning)

A central feature of LoRAMancer is borrowing the mathematical settings from high-quality donor LoRAs without copying the donor's unique subject or dataset:
1. Click **"Use Settings"** on any LoRA card.
2. The **Training Wizard** opens pre-seeded with the donor's proven configuration recipe:
   - **Target Base Model**: (FLUX.1, SDXL, Illustrious, Pony, etc.)
   - **Network Rank (Dim)** & **Network Alpha**
   - **Learning Rate** & **Optimizer**
   - **Epochs & Steps**
3. **User Supplies New Identity**:
   - The wizard prompts the user to enter their **New Run Name**, select their **New Dataset** (folder or `.zip`), and define their **New Trigger Word**.
   - This ensures that only the hyperparameters are cloned, while your new model remains completely unique to your concept or style.

---

## 3. Direct Explorer & Metadata Inspection

- **Inspect Header**: Launches the header inspector drawer, displaying raw JSON metadata, tensor keys, and model architecture tags with zero tensor weight overhead.
- **Reveal in Explorer**: Opens Windows Explorer with the specific `.safetensors` file selected.
