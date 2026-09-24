# LoRAMancer Plugin Architecture

## Overview

LoRAMancer provides a dual plugin architecture enabling extensions to be authored in either **C# (.NET 10)** or **Python (3.12+)**.

All plugins are discovered from the root `plugins/` directory:

```
plugins/
├── <csharp_plugin_name>/
│   └── <csharp_plugin_name>.dll       # Compiled .NET assembly
└── <python_plugin_name>/
    ├── plugin.json                    # Manifest (Name, Version, EntryPoint)
    ├── plugin.py                      # Main Python entry point
    ├── requirements.txt               # Plugin dependencies
    └── .venv/                         # Dedicated isolated virtual environment
```

## C# Plugins

C# plugins are implemented as standard .NET class libraries referencing `LoRAMancer.PluginSdk`.

### Plugin Structure
A C# plugin consists of a `.dll` placed in a subfolder under `plugins/`:
- Folder: `plugins/MyCSharpPlugin/`
- Binary: `plugins/MyCSharpPlugin/MyCSharpPlugin.dll`

### Interface Contract
C# plugins implement `ILoRAMancerPlugin`:
```csharp
namespace LoRAMancer.PluginSdk;

public interface ILoRAMancerPlugin {
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
    Task<PluginResult> ExecuteAsync(string command, IDictionary<string, object> parameters, CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}
```

The plugin assembly is loaded into an isolated `AssemblyLoadContext` to allow dynamic unloading and dependency isolation.

## Python Plugins

Python plugins run out-of-process to guarantee complete dependency and memory isolation.

### Plugin Structure
Each Python plugin lives in its own dedicated folder under `plugins/` and **must** contain its own `.venv` inside that folder:
- Folder: `plugins/MyPythonPlugin/`
- Virtual Environment: `plugins/MyPythonPlugin/.venv/`
- Manifest: `plugins/MyPythonPlugin/plugin.json`
- Entry Point: `plugins/MyPythonPlugin/plugin.py`
- Requirements: `plugins/MyPythonPlugin/requirements.txt`

### Plugin Manifest (`plugin.json`)
```json
{
  "id": "sample-python-plugin",
  "name": "Sample Python Plugin",
  "version": "1.0.0",
  "description": "Demonstrates isolated Python plugin execution",
  "entryPoint": "plugin.py",
  "pythonVersion": "3.12"
}
```

### Virtual Environment Provisioning & PyTorch Hardware Matching
When a Python plugin is discovered, installed, or updated:
1. `PluginManagerService` checks for `plugins/<name>/.venv/`.
2. If missing, it invokes Python to create the isolated virtual environment: `python -m venv plugins/<name>/.venv`.
3. Upgrades `pip`, `setuptools`, and `wheel` inside the plugin's `.venv`.
4. **PyTorch Hardware Matching**: If `requirements.txt` requires `torch`, the plugin manager queries the host's hardware vendor (AMD ROCm, NVIDIA CUDA, Intel XPU, or CPU fallback) and automatically installs the appropriate hardware-accelerated PyTorch distribution before general packages:
   - **AMD ROCm**: Installs the configured ROCm Windows wheels from Radeon repos (or local cache).
   - **NVIDIA**: Installs CUDA wheels via `--index-url https://download.pytorch.org/whl/cu124`.
   - **Intel**: Installs XPU wheels via `--index-url https://download.pytorch.org/whl/xpu`.
   - **CPU**: Installs CPU-only wheels via `--index-url https://download.pytorch.org/whl/cpu`.
5. Dependencies in `requirements.txt` are then installed with `pip install --no-cache-dir -r requirements.txt`.

## Git Repository Integration & Plugin Manager

LoRAMancer includes full Git integration in the UI for effortless plugin discovery, installation, and maintenance:

### 1. Installing from a Git Repository
- Users provide a Git URL (e.g., `https://github.com/user/loramancer-plugin.git`) directly in the Plugin Manager UI.
- The manager executes `git clone --recurse-submodules <url> plugins/<plugin_name>`.
- The plugin is automatically inspected, registered, and provisioned with an isolated `.venv` (including hardware-matched PyTorch if specified).

### 2. Updating Git Plugins
- **Single Plugin Update**: Click **Git Pull** on any Git-based plugin card to pull the latest commits and submodules (`git pull --recurse-submodules`), followed by updating `.venv` requirements.
- **Batch Update**: Click **Update All (Git)** in the header toolbar to scan and update every installed Git plugin sequentially with live streaming logs.

### 3. Plugin Lifecycle Management
- **Status Indicators**: Card headers clearly show `.venv Ready` (green) when the virtual environment and entry point are provisioned, or `No .venv` (yellow) if provisioning is needed.
- **Setup .venv**: For unconfigured Python plugins, creates an isolated virtual environment and installs all dependencies (with hardware-appropriate PyTorch distributions).
- **Rebuild .venv**: Cleanly tears down the existing `.venv` directory (clearing any file attribute locks) and performs a fresh provisioning and dependency installation run.
- **Remove .venv**: Safely deletes the `.venv` directory for a Python plugin, resetting its state without deleting the plugin code or Git repository.
- **Enable / Disable**: Toggle the switch on any plugin card. Disabled plugins are flagged with a `.disabled` marker file in their directory, persisting state across application restarts and updates.
- **Delete Plugin**: Click the delete icon to remove the plugin directory and unregister it. Read-only Git attributes are automatically cleared to prevent file lock errors on Windows.
- **Test Run**: Send a ping diagnostic command to verify that the C# assembly or Python script executes cleanly inside its environment.
- **Live Output Log**: A streaming console card displays real-time `stdout`/`stderr` from Git operations, `pip` installations, and venv rebuilds with thread-safe UI updates.

## Default Included Plugins

### Ollama Vision LoRA Tagger & Captioner (`plugins/ollama_lora_tagger/`)
LoRAMancer ships with a built-in Python plugin for automated AI image tagging and caption generation powered by Ollama vision models:
- **Flexible Endpoints & Datacenters**:
  - Connects to local default (`http://localhost:11434`), remote LAN host, or enterprise datacenter proxy.
  - Supports optional Bearer Auth / API Key tokens for secured remote endpoints.
  - Interactive "Test Connection" button in UI with auto-detection of available models on the host.
- **Local & Cloud Model Support**:
  - Out of the box support for popular vision models: `llama3.2-vision`, `llava`, `llava:34b`, `minicpm-v`, `qwen2-vl`.
  - Supports custom and cloud-hosted vision models simply by entering the model identifier or tag.
- **Flexible Inputs**: Accepts either a folder of images or a raw `.zip` archive.
- **Auto-Extraction**: ZIP archives are automatically extracted into a managed cache directory (`~/.loramancer/extracted_datasets/`).
- **Customizable Tagging Rules**:
  - **Trigger Word Injection**: Prepends your training trigger word to every generated caption.
  - **Inclusion Enforcement**: Guarantees specified mandatory terms/phrases are included.
  - **Blacklist Filtering**: Strips unwanted words, watermarks, or quality artifacts from generated captions.
  - **Caption Styles**: Supports comma-separated visual tags (ideal for SDXL, Pony, Illustrious) or natural language descriptive sentences (ideal for FLUX.1).
- **Export Formats**: Outputs directly to a folder or generates a compressed `.zip` archive ready for training. The Training Wizard seamlessly accepts and auto-extracts `.zip` archives.

### Lora Updater (`plugins/lora_updater/`)
LoRAMancer ships with a built-in Python plugin for automated Civitai model version synchronization, update detection, and verified downloads:
- **Hash-Based Identification**: Computes SHA256 hashes of local `.safetensors` files using fast 1MB block-streaming, guaranteeing exact matching on Civitai regardless of local filename changes.
- **Model Type Verification**: Validates that matched models are legitimate `LORA` models (safely skipping Checkpoints, VAEs, or other asset types).
- **Base Model Architecture Safeguards**:
  - Compares local base model (e.g. SD 1.5, SDXL, FLUX.1) against the latest release.
  - Automatically flags and skips cross-architecture updates (e.g., an SDXL successor released for an SD 1.5 original) to prevent breaking existing pipelines, unless the user explicitly enables "Allow Base Mismatch".
- **Dry-Run Inspection (`check_updates`)**:
  - Scans specified directories (with recursive subfolder toggle) and queries Civitai without downloading or modifying any files.
  - Outputs a detailed matrix: model name, local version, latest version, download size, and status (`Up to Date`, `Update Available`, `Base Mismatch`, `Not on Civitai`).
- **Safe Atomic Replacement & Backups (`scan_and_update`, `update_single`)**:
  - Streams downloads directly to a `.tmp` file and performs SHA256 integrity verification against Civitai's checksum before replacing.
  - **Organized Backups**: When a backup directory is configured, old versions are moved into a timestamped run folder categorized into subfolders by base model (e.g. `backups/LoraUpdater_2026-09-24_11-00-00/SDXL/<old_file>.safetensors`). If no backup folder is set, creates a companion `.bak` file.
  - **Automatic Rollback**: If moving the downloaded file fails, the original backup is immediately restored.
- **Civitai Authentication & Cloudflare Bypass**:
  - Transmits Civitai API keys both via Bearer authorization headers and URL query parameters to unlock private, early-access, or member-only models.
  - Employs desktop browser `User-Agent` headers to prevent Cloudflare HTTP 403 Forbidden responses.
- **Non-Blocking Background Execution & Productivity Protection**:
  - Operates completely asynchronously via `LoraUpdaterService` without locking or freezing the UI thread.
  - Creators can click **"Run in Background"** inside the dialog to dismiss the modal and continue working anywhere in LoRAMancer (e.g. creating training configs, auditing datasets, or inspecting models) while updates proceed in the background.
  - A persistent background banner on the main dashboard displays real-time operation status, current file progress, and 1-click controls to reopen details or cancel execution at any time.
- **Interactive UI**: Launched directly from the LoRA Library Browser toolbar (**"Lora Updater"**) or via the Plugin Manager.


