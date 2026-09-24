# LoRAMancer Feature & Subsystem Reference

This document provides a comprehensive overview of all core capabilities, engines, and integrations in LoRAMancer.

---

## 1. Universal Multi-Vendor Hardware Acceleration

LoRAMancer automatically inspects local system graphics hardware (`AmdVenvProvisioner` / `GpuDetector`) and provisions an isolated, self-contained Python 3.12 virtual environment (`.venv`) with official wheels matched to your GPU:

- **AMD Radeon (ROCm)**:
  - Official PyTorch ROCm 7.2.1 / 7.3+ wheels distributed via `repo.radeon.com/rocm/wheels/`.
  - Provisions `rocm_sdk` with automated Windows library stubs (e.g. `hipblas.dll`, `amdhip64.dll`).
  - Automatically translates CUDA-only settings into safe AMD ROCm equivalents.
- **NVIDIA GeForce & RTX (CUDA)**:
  - Official PyTorch CUDA 12.4+ distributions from `download.pytorch.org/whl/cu124`.
  - Supports Flash Attention, xFormers, and bitsandbytes 8-bit optimizers.
- **Intel Arc & Data Center GPU (Intel XPU)**:
  - Official PyTorch Intel XPU distribution from `download.pytorch.org/whl/xpu`.
- **CPU Fallback**:
  - Generic SIMD-accelerated CPU PyTorch wheels from `download.pytorch.org/whl/cpu` for systems without dedicated accelerators.

---

## 2. Multi-Library LoRA Management & Visual Browser

- **Persistent ACID SQLite Storage (`~/.LoRAMancer/loras.db`)**:
  - Automatically indexes thousands of `.safetensors` models into an embedded SQLite database.
  - Zero-wait (0ms) instant gallery loading on startup or navigation without re-reading disks.
- **Multiple Named Libraries**:
  - Organize collections into separate named libraries (e.g., *FLUX Characters*, *Anime Styles*, *SDXL Concepts*).
  - All libraries reside within the same SQLite database under the `Libraries` table.
  - Browse each library independently, or quick-switch via the top navigation dropdown.
  - **1-Click Subfolder Conversion**: Convert any active subfolder into its own independent library with a single click (`Make Subfolder a Library`), or bulk-import all subdirectories in the Library Manager.
- **Hierarchical & Flat Browsing**:
  - Subfolder navigation with interactive folder cards, live model counts, and clickable breadcrumbs.
  - Flat view toggle to search or browse across all subfolders simultaneously.
- **Non-Blocking Background Operations**:
  - Background recursive disk scanning with live progress and delta indexing (only parses new or modified files).
  - Asynchronous background thumbnail loading and lazy image cache prevents any UI hitching.
  - Non-blocking Civitai metadata retrieval with Bearer token authentication and Cloudflare bypass.
- **Favorites & Base Model Overrides**:
  - 1-click **Favorites (❤️)** filterable at any time.
  - Inline base model selector to assign or override detected architectures (FLUX.1, SDXL, Pony, Illustrious, Chroma, HunyuanVideo, Wan 2.1).

---

## 3. Civitai-Style Training Wizard & Hyperparameter Cloning

- **Hyperparameter Cloning ("Use Settings")**:
  - Borrow mathematical hyperparameter recipes directly from any donor LoRA: rank (dim), alpha, learning rates, optimizer, epochs, and resolution.
  - Clones settings while preserving your new unique concept, dataset, and trigger word.
- **Preset System & Auditing**:
  - Wizard presets for Characters, Styles, Concepts, and Clothing.
  - Live dataset health auditing (verifies caption pairing, warns on missing tags or low-resolution images).
  - Real-time hardware estimators (estimated VRAM vs detected GPU, total steps, and completion duration).

---

## 4. Permanent Training History & Vault (`/history`)

- **Local Civitai-Style Ledger**:
  - Logs every training run locally to permanent storage: trigger words, full hyperparameter snapshots, loss sparklines, and duration.
  - Records never expire or get purged by cloud retention limits.
  - 1-click **Clone Config** from historical runs to iterate on versions.

---

## 5. Remote Web UI, PWA & Public Internet Serving

- **Embedded Kestrel Network Server**:
  - Host runs locally while controlling LoRAMancer from any browser on LAN or mobile.
- **Progressive Web App (PWA)**:
  - Installable on iOS, Android, macOS, and Linux with offline shell caching and native app chrome.
- **Public Internet Tunnels (Cloudflare Quick Tunnels)**:
  - Securely expose your local training workstation to the public web via `https://*.trycloudflare.com` with PIN and Access Token security.
- **Core Auth Token Manager (`AuthTokenManagerService`)**:
  - Role-based access tokens (Admin, Trainer, ReadOnly) with expiration and live usage telemetry.

---

## 6. SafeTensors Header Parser & Configuration Sanitizer

- **Instant Zero-Weight Header Inspection**:
  - C# native binary parser that reads only the initial header bytes of `.safetensors` files without loading multi-gigabyte tensor weights into RAM.
- **Hardware-Aware Sanitizer**:
  - Translates optimizer types (e.g. converting `AdamW8bit` to `AdamW` when running on ROCm or CPU) and adjusts attention mechanisms to prevent driver panics.

---

## 7. Extensible Plugin System

- **Dual Plugin Engine**:
  - Supports managed C# plugin assemblies (`.dll`) implementing `ILoRAMancerPlugin` and Python plugins running in isolated virtual environments.
- **Built-in Plugins**:
  - **Ollama Vision LoRA Tagger**: Automates image captioning and keyword tagging using vision LLMs (`llama3.2-vision`, `llava`, `qwen2-vl`).
  - **Lora Updater**: Checks local libraries against Civitai via SHA256 hashes, verifies base model compatibility, creates backups, and updates files per library.

---

## 8. Dynamic JSON Theme Engine

- **High-Contrast Dark Themes**:
  - Curated palettes optimized for high-contrast viewing: Slate Greys (Obsidian), Catppuccin Mocha, Tokyo Night, Crimson Blood, Emerald Cyber, Solar Amber, and Cobalt Sapphire.
- **Import / Export**:
  - Import custom themes from `.json` files or raw JSON strings.
  - Export active themes for sharing.
  - Custom themes persist in `~/.LoRAMancer/themes/*.json`.
