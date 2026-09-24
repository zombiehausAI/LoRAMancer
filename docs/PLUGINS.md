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

## Developer Guide: Building & Integrating Plugins into the UI

This guide explains how external and open-source contributors can build new plugins for LoRAMancer and integrate them into the user interface.

### 1. Choosing Between Python and C#

| Feature | Python Plugin | C# (.NET 10) Plugin |
| :--- | :--- | :--- |
| **Best For** | AI/ML pipelines, vision models, dataset prep, Hugging Face / Civitai APIs | High-throughput file system parsing, native math, UI-heavy tools |
| **Execution** | Out-of-process in an isolated virtual environment (`.venv`) | In-process in an isolated `AssemblyLoadContext` |
| **GPU Acceleration** | Auto-provisions matching PyTorch wheels (AMD ROCm / NVIDIA CUDA / Intel XPU / CPU) | Native C# or pinvoke |
| **Distribution** | Git repository with `plugin.json` and `requirements.txt` | Compiled `.dll` package |

---

### 2. Standard Command Contract & Execution

Every plugin must respond to incoming commands with structured JSON:

```json
{
  "success": true,
  "message": "Operation completed successfully",
  "data": { ... }
}
```

#### Diagnostic `ping` Command
Every plugin **must** implement a `ping` command. The Plugin Manager UI executes `ping` when users click the **Test** button to verify the environment:

```python
# plugin.py
import sys, json

def handle_ping(params):
    return {"success": True, "message": "Pong! Plugin environment healthy.", "data": {"version": "1.0.0"}}

def main():
    if len(sys.argv) < 3:
        print(json.dumps({"success": False, "message": "Usage: python plugin.py <command> <json_params>"}))
        return
    command = sys.argv[1]
    params = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}

    if command == "ping":
        result = handle_ping(params)
    elif command == "my_custom_action":
        result = handle_custom_action(params)
    else:
        result = {"success": False, "message": f"Unknown command: {command}"}

    print(json.dumps(result))

if __name__ == "__main__":
    main()
```

---

### 3. Exposing Your Plugin in the User Interface

LoRAMancer provides three standard UI integration patterns depending on the plugin's workflow:

#### Pattern A: Standalone Workflow (Left Navigation Bar & Dialog)
Use this when your plugin performs a task that users want to trigger independently (e.g., dataset tagging, model conversion, Civitai syncing).

1. **Create a Blazor Dialog Component** in `src/LoRAMancer.App/Components/Dialogs/MyPluginDialog.razor`:
   ```razor
   @using LoRAMancer.App.Services
   @inject PluginManagerService PluginManager
   @inject ISnackbar Snackbar

   <MudDialog>
       <TitleContent>
           <MudText Typo="Typo.h6">My Plugin Tool</MudText>
       </TitleContent>
       <DialogContent>
           <!-- Parameters & options -->
           <MudTextField @bind-Value="_inputPath" Label="Target Path" Variant="Variant.Outlined" />
       </DialogContent>
       <DialogActions>
           <MudButton OnClick="RunPluginAsync" Color="Color.Primary" Variant="Variant.Filled">Run</MudButton>
       </DialogActions>
   </MudDialog>

   @code {
       [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
       private string _inputPath = string.Empty;

       private async Task RunPluginAsync() {
           var parameters = new Dictionary<string, object?> { ["input_path"] = _inputPath };
           var result = await PluginManager.ExecutePluginAsync("my-plugin-id", "my_custom_action", parameters);
           if (result.Success) {
               Snackbar.Add(result.Message, Severity.Success);
               MudDialog.Close(DialogResult.Ok(result));
           } else {
               Snackbar.Add(result.Message, Severity.Error);
           }
       }
   }
   ```

2. **Register a Link in the Navigation Bar** (`src/LoRAMancer.App/Components/Layout/NavMenu.razor`):
   ```razor
   <MudNavLink Icon="@Icons.Material.Filled.AutoFixHigh" OnClick="OpenMyPluginAsync">
       My Plugin Tool
   </MudNavLink>

   @code {
       private async Task OpenMyPluginAsync() {
           var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Medium, FullWidth = true };
           await DialogService.ShowAsync<MyPluginDialog>("My Plugin Tool", options);
       }
   }
   ```

#### Pattern B: In-Workflow Action (Toolbar or Wizard Integration)
Use this when your plugin enriches an existing workflow (e.g., adding auto-captioning directly inside the **Civitai Training Wizard**, or adding model checking to the **LoRA Library Browser** toolbar):
* Inject `IDialogService` into the existing page or wizard component.
* Add an action button that opens your dialog pre-seeded with context (e.g., the currently selected model or dataset folder).

#### Pattern C: Long-Running Asynchronous Background Services
For tasks taking minutes or hours (e.g., bulk downloads or heavy inference):
* Wrap plugin execution in a dedicated singleton service (similar to `LoraUpdaterService.cs`).
* Maintain a background status object with progress percentages and logs.
* Display a persistent banner in `MainLayout.razor` or `LoraManagerDashboard.razor` so users can monitor progress or continue working without keeping a dialog open.

---

### 4. Hardware Acceleration & PyTorch Matching

If your Python plugin relies on PyTorch (e.g. for vision or embedding models), include `torch` in `requirements.txt`. 

When LoRAMancer provisions the plugin's `.venv`, it automatically detects the host GPU vendor and executes hardware-matched installation before general dependencies:
* **AMD Radeon / ROCm**: Installs Windows ROCm wheels from the configured Radeon repository.
* **NVIDIA RTX / CUDA**: Installs CUDA wheels via `--index-url https://download.pytorch.org/whl/cu124`.
* **Intel Arc / XPU**: Installs Intel XPU wheels via `--index-url https://download.pytorch.org/whl/xpu`.
* **CPU fallback**: Installs lightweight CPU wheels.

Developers do **not** need to write custom GPU detection logic; LoRAMancer handles this automatically.

---

### 5. Distributing as an Open-Source Git Plugin

To publish a plugin that any LoRAMancer user can install via **Install from Git**:

1. Create a public Git repository containing:
   ```text
   my-loramancer-plugin/
   ├── plugin.json         # id, name, version, description, entryPoint
   ├── plugin.py           # CLI runner implementing "ping" and custom commands
   ├── requirements.txt    # Python dependencies
   ├── README.md           # Documentation for users
   └── LICENSE             # Open-source license (MIT, Apache 2.0, etc.)
   ```
2. Users can paste the repository URL into **Plugin Manager > Install from Git**.
3. LoRAMancer clones the repository, provisions an isolated `.venv`, and marks it ready for use.
