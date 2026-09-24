# LoRA Training History & Vault

## Overview

LoRAMancer includes a permanent **Training History & Model Vault** to catalogue and track all LoRAs generated through the application.

Unlike online training platforms (such as Civitai, which automatically deletes user models and training files after 30 days unless downloaded), LoRAMancer's history log is **permanent and never expires** until you explicitly remove a record.

---

## Key Features

### 1. Comprehensive Run Tracking
Every completed or cancelled training run records:
- **Model Name & Base Architecture**: FLUX.1 (Dev/Schnell), SDXL 1.0, Illustrious, Pony, Chroma.
- **Trigger Word & Prompts**: One-click clipboard copy button for rapid prompt testing in ComfyUI or WebUIs.
- **Hyperparameter Snapshot**: Rank (Network Dim), Alpha, Learning Rate, Optimizer (`adamw_bf16`, `adamw`, etc.), Total Steps, and Final Epochs.
- **Training Metrics**: Final loss achieved, duration (elapsed time), and completion timestamp.
- **Dataset Context**: Number of images used and path to training dataset/archive.
- **Weights & Configs**: Direct reference to the generated `.safetensors` file on disk and the exact training `.yaml` configuration used.

### 2. Never Expiring Local Vault
- All history metadata is stored securely in the user profile directory under `~/.loramancer/history/history.json`.
- Files remain intact indefinitely across application restarts and updates.
- Records can be marked as **Starred Favorites** for quick filtering.

### 3. One-Click Config Cloning
- Click **Clone Config** on any historical record to immediately populate the Training Wizard or Training Console with that model's exact hyperparameters.
- Ideal for training iterative versions (e.g. `v1` -> `v2` with adjusted learning rate or rank) without having to manually reconstruct settings.

### 4. Direct Disk Integration
- **Open Folder**: Immediately reveals the `.safetensors` file in Windows Explorer.
- **Delete Management**: Remove historical records with the option to preserve the model file on disk or clean up both the record and weights simultaneously.
