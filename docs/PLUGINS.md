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
  "id": "dataset-web-annotator",
  "name": "Dataset Web Annotator",
  "version": "1.0.0",
  "description": "Embedded web application for visual dataset tagging and bounding-box annotation",
  "entryPoint": "plugin.py",
  "pythonVersion": "3.12",
  "menuSection": "Studio Workshop",
  "menuOrder": 30,
  "uiSlot": "StudioWorkshop",
  "uiType": "EmbeddedWeb",
  "navLabel": "Web Annotator",
  "icon": "Brush",
  "webPort": 8501
}
```

## Standalone Dynamic Menu System for Plugins

Yes! Plugins can now declare and control all menu and navigation behaviors dynamically without touching any host project code.

LoRAMancer treats plugins as first-class, standalone citizen modules. Plugin authors never need to edit the core application, submit pull requests to modify navigation templates, or touch C# Blazor code to insert menu items. When a plugin is dropped into `plugins/` or cloned via Git, LoRAMancer inspects its manifest (`plugin.json` for Python, or `PluginMetadata` for C#) and dynamically registers its sidebar links, section groupings, icons, order weights, and routing targets.

### Key Capabilities of the Dynamic Menu System

1. **Zero Host Code Modification**: All navigation and menu behaviors are configured entirely in the plugin's own folder via `plugin.json`.
2. **Standard Section Targeting**: Effortlessly tuck tools under existing core sections (`"Studio Workshop"`, `"Studio Pipeline"`, `"Post-Forge Showcase"`, `"Studio Modal Tools"`, `"Subsystems"`).
3. **Dynamic Custom Sections**: Specify any arbitrary string for `menuSection` (e.g., `"Civitai Tools"`, `"Dataset Studio"`, `"Community Extensions"`). LoRAMancer dynamically creates a visual divider, a capitalized section header, and groups all matching plugins underneath.
4. **Ordering & Weighting**: Control the exact position in the sidebar using `menuOrder` (ascending; lower numbers appear first).
5. **Modal vs. Embedded Page Routing**:
   - Web applications (Streamlit, Gradio, React, Flask) set `"uiType": "EmbeddedWeb"` with a `webPort` to render seamlessly inside LoRAMancer's native workspace at `/plugins/host/{pluginId}`.
   - Quick utilities set `"isModal": true` or `"uiType": "NativeModal"` to launch as focused dialogs over the active view without disrupting the user's workflow.
6. **Dynamic Icon Mapping**: Specify standard Material Design icon names (`TravelExplore`, `PhotoLibrary`, `AutoAwesome`, `CloudSync`, `SmartToy`, `Handyman`, `Speed`, etc.) to instantly give your plugin a polished look.

### Navigation Manifest Properties

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **`menuSection`** | `string` | `""` (falls back to `uiSlot`) | The target sidebar section for the plugin's navigation link. Supports standard sections or any custom section name. |
| **`menuOrder`** | `int` | `100` | Sort order within the section (ascending). Lower numbers appear higher in the menu list. |
| **`navLabel`** | `string` | `name` | Custom label displayed on the sidebar menu button. |
| **`icon`** | `string` | `"Extension"` | Material Design icon name (e.g., `TravelExplore`, `PhotoLibrary`, `AutoAwesome`, `CloudSync`, `Speed`, `Camera`, `Handyman`, `SmartToy`). |
| **`isModal`** | `bool` | `false` | When `true` (or when `uiType` is `"Modal"` / `"NativeModal"`), clicking the menu item launches a modal dialog rather than full-page navigation. |
| **`uiType`** | `string` | `"Command"` | `"EmbeddedWeb"`, `"NativeModal"`, or `"Command"`. |
| **`uiSlot`** | `string` | `"None"` | Backward-compatible slot specifier (mapped to `menuSection` if `menuSection` is omitted). |
| **`webPort`** | `int?` | `null` | Local port for embedded web apps (e.g. `8501` for Streamlit). |
| **`webUrl`** | `string?` | `null` | External or pre-configured web URL if hosted independently. |
| **`configSchema`** | `array?` | `null` | List of configurable options or API keys displayed in the plugin configuration modal. |

### Plugin Configuration & API Keys (`configSchema`)

Plugins often require user credentials, API keys, or custom endpoints (e.g. Civitai API key, Rule34 user credentials, custom model paths). LoRAMancer provides a built-in configuration modal accessible via the **Gear icon (⚙️)** on each plugin's card under `/plugins`. The gear icon is displayed dynamically whenever a plugin defines a `configSchema` or is an Image Harvester scraper.

#### Declaring Configuration Schema in `plugin.json`

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "configSchema": [
    {
      "key": "apiKey",
      "label": "API Key",
      "type": "password",
      "description": "Personal access token or API key.",
      "defaultValue": "",
      "isRequired": false
    },
    {
      "key": "userId",
      "label": "User ID",
      "type": "text",
      "description": "Optional account user ID.",
      "defaultValue": "",
      "isRequired": false
    }
  ]
}
```

#### Storage & Execution Injection

1. **User Profile Persistence**:
   - **Image Scraper Plugins**: Configuration is saved directly in the user's home directory under `~/.loramancer/scrapers/<plugin_id>.json` (each scraper maintains its own JSON configuration file).
   - **General Plugins**: Configuration is saved to `~/.loramancer/plugin_configs/<plugin_id>.json`.
2. **Environment Variable Injection**: When LoRAMancer runs a Python plugin script or command, all configured key-value pairs are automatically injected as environment variables:
   - `LORAMANCER_CONFIG_<KEY>` (e.g. `LORAMANCER_CONFIG_APIKEY`)
   - `PLUGIN_<KEY>` (e.g. `PLUGIN_APIKEY`)
   - `LORAMANCER_PLUGIN_CONFIG_FILE` (pointing to the configuration JSON file)
   - `LORAMANCER_SCRAPER_CONFIG_FILE` (pointing to the scraper's JSON file under `~/.loramancer/scrapers/` for scraper plugins)
3. **Generic Fallback**: If a plugin or scraper does not declare an explicit `configSchema`, the configuration modal provides a dynamic key-value editor so users can still supply custom keys, flags, or credentials.

### Image Scraper Plugins & Shared `.venv` Architecture

LoRAMancer treats image scrapers as a specialized plugin category to optimize performance, disk usage, and user experience:

1. **Clean Navigation (No Sidebar Clutter)**: Scrapers are excluded from the main navigation sidebar. They integrate directly into the native **Image Harvester** (`/harvester`) search provider pool and are managed under a dedicated tab in the Plugin Manager.
2. **Shared Virtual Environment (`~/.loramancer/scraper_venv`)**:
   - Rather than creating a separate 2–3 GB `.venv` for every individual scraper, all scraper plugins share a single consolidated virtual environment located at `~/.loramancer/scraper_venv`.
   - Automatically pre-installed with core web scraping and parsing libraries (`requests`, `beautifulsoup4`, `cloudscraper`, `urllib3`).
   - Saving tens of gigabytes of disk space across 18+ scrapers while allowing 1-click provisioning via the "Setup Shared Scraper .venv" action in the UI.
   - Non-scraper Python plugins (such as Streamlit apps or vision model taggers) continue to maintain their dedicated isolated `.venv` in their own folder.
3. **Dedicated "Image Scrapers" Tab**: The Plugin Manager UI (`/plugins`) features dedicated tabs:
   - **Image Scrapers**: Focused view of all installed harvester scrapers, showing shared `.venv` readiness, enabled status, and gear configuration buttons.
   - **General Extensions**: Full-featured UI extensions, web apps, and modal tools.
   - **All Installed**: Unified view of all installed C# and Python plugins.

### Standard Target Sections

Plugins can place themselves into any of the primary sections built into LoRAMancer:
- **`"Studio Pipeline"`**: Rendered beneath the 5 core pipeline steps.
- **`"Studio Workshop"`**: Dedicated tools for editing, inspecting, or workshop utilities (e.g., Web Annotator, LoRA Chop-Shop).
- **`"Post-Forge Showcase"`**: Post-generation galleries, evaluation tools, and asset viewers.
- **`"Studio Modal Tools"`**: Quick-action modal utilities (e.g., Ollama Vision Tagger, LoRA Surgery).
- **`"Subsystems"`**: Administrative, environment, or system-level extensions.
- **`"HarvesterScraper"`**: Specialized slot for dataset image scrapers integrated into `/harvester`. Auto-registered as search providers and invoked concurrently via `--query ... --limit ... --json`.

> [!TIP]
> Friendly alias names are normalized automatically. For example, `"workshop"` or `"studioworkshop"` normalizes to `"Studio Workshop"`, `"modal"` to `"Studio Modal Tools"`, and `"postforge"` to `"Post-Forge Showcase"`.

### Dynamic Custom Sections

If a plugin specifies a `menuSection` that is not one of the standard sections (for example `"Civitai Tools"`, `"Dataset Studio"`, or `"Community Feeds"`), LoRAMancer automatically renders:
1. A visual section divider.
2. A capitalized section header caption.
3. Ordered links for all plugins belonging to that custom section.

Multiple plugins sharing the same custom section name are grouped together seamlessly.

### Example Manifests

#### 1. Embedded Web App in Studio Workshop (Streamlit / Gradio)
```json
{
  "id": "dataset-web-annotator",
  "name": "Dataset Web Annotator",
  "version": "1.0.0",
  "description": "Embedded web application for visual dataset tagging and bounding-box annotation",
  "entryPoint": "plugin.py",
  "pythonVersion": "3.12",
  "menuSection": "Studio Workshop",
  "menuOrder": 30,
  "uiType": "EmbeddedWeb",
  "navLabel": "Web Annotator",
  "icon": "Brush",
  "webPort": 8501
}
```

#### 2. Modal Tool in Studio Modal Tools
```json
{
  "id": "ollama_lora_tagger",
  "name": "Ollama Vision LoRA Tagger",
  "version": "1.0.0",
  "entryPoint": "plugin.py",
  "menuSection": "Studio Modal Tools",
  "menuOrder": 20,
  "isModal": true,
  "uiType": "NativeModal",
  "navLabel": "Ollama Auto-Tagger",
  "icon": "AutoAwesome"
}
```

#### 3. Custom Standalone Section for Third-Party Extension
```json
{
  "id": "comfy_workflow_bridge",
  "name": "ComfyUI Workflow Bridge",
  "version": "1.0.0",
  "entryPoint": "plugin.py",
  "menuSection": "ComfyUI Tools",
  "menuOrder": 10,
  "uiType": "EmbeddedWeb",
  "navLabel": "Workflow Bridge",
  "icon": "SmartToy",
  "webPort": 8188
}
```

#### 4. Image Harvester Scraper Extension
```json
{
  "id": "civitai-scraper",
  "name": "Civitai Community Showcase",
  "version": "1.0.0",
  "description": "Python scraper harvesting generation showcase images and prompt metadata from Civitai",
  "entryPoint": "plugin.py",
  "pythonVersion": "3.12",
  "menuSection": "Studio Workshop",
  "uiSlot": "HarvesterScraper",
  "navLabel": "Civitai Showcase",
  "icon": "Brush"
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

### 3. Exposing Your Plugin in the User Interface (Zero Host Code Required)

Under LoRAMancer's standalone plugin model, **plugin authors never edit core application code, Razor views, or navigation files** (such as `NavMenu.razor`). All UI integration is driven declaratively through your plugin's manifest (`plugin.json` for Python plugins, or `PluginMetadata` attributes for C# plugins).

When your plugin is added to `plugins/`, LoRAMancer automatically discovers it, registers its navigation links in the sidebar, mounts embedded web interfaces, and manages its lifecycle.

#### Pattern A: Embedded Web UI (Streamlit, Gradio, Flask, React, HTML)
This is the recommended pattern for full-featured visual tools, interactive scrapers, analyzers, and generators:
1. **Develop Your UI**: Build your interface in Python using Streamlit, Gradio, Flask, FastAPI, or any local web server framework inside your plugin directory.
2. **Declare in `plugin.json`**:
   ```json
   {
     "id": "my_visual_tool",
     "name": "My Visual Tool",
     "version": "1.0.0",
     "entryPoint": "plugin.py",
     "menuSection": "Studio Workshop",
     "menuOrder": 40,
     "uiType": "EmbeddedWeb",
     "navLabel": "Visual Tool",
     "icon": "Palette",
     "webPort": 8505
   }
   ```
3. **Automatic Mounting**:
   - When the user clicks the plugin's link in the sidebar (or navigates to it), LoRAMancer runs `plugin.py`, spins up the web server on its assigned `webPort`, and embeds the live interface seamlessly inside LoRAMancer's native workspace at `/plugins/host/my_visual_tool`.
   - Zero changes to host C# or Blazor code are needed.

#### Pattern B: Native Modal Dialog / Quick-Launch Utility
For focused utility tools (e.g., one-off taggers, inspectors, format converters) that run as dialog overlays:
1. **Declare in `plugin.json`**:
   ```json
   {
     "id": "quick_converter",
     "name": "Quick Format Converter",
     "version": "1.0.0",
     "entryPoint": "plugin.py",
     "menuSection": "Studio Modal Tools",
     "menuOrder": 25,
     "isModal": true,
     "uiType": "NativeModal",
     "navLabel": "Quick Converter",
     "icon": "Transform"
   }
   ```
2. **Behavior**:
   - LoRAMancer dynamically places a button in the specified `menuSection` (or renders a custom section).
   - Clicking the button launches the tool in a dedicated modal without interrupting active training or navigation.

#### Pattern C: Dynamic Custom Sidebar Sections
Need your tool or suite of tools grouped in their own dedicated sidebar area?
- Simply specify any custom string for `"menuSection"` in `plugin.json`:
  ```json
  "menuSection": "Community Extensions"
  ```
- LoRAMancer automatically creates a visual divider, renders a capitalized section header (`COMMUNITY EXTENSIONS`), and places all matching plugins into that custom section.

#### Pattern D: Headless Command & Background Automation
For plugins that provide automated background capabilities, batch CLI operations, or API bridges (e.g., Civitai sync, dataset verification):
1. **Declare in `plugin.json`**:
   ```json
   {
     "id": "dataset_auditor",
     "name": "Dataset Auditor",
     "version": "1.0.0",
     "entryPoint": "plugin.py",
     "uiType": "Command"
   }
   ```
2. **Execution**:
   - The plugin responds to CLI commands (`ping`, `audit`, etc.) using the standard JSON contract.
   - Users can test, run, and inspect output directly from the **Plugin Manager** (`/plugins`) with real-time logs, without needing navigation bar real estate.

#### Pattern E: Image Scraper Plugins (`uiSlot: "HarvesterScraper"`)
For community search engines, media archives, and reference image discovery extensions that integrate directly into the **Image Harvester** (`/harvester`):

1. **Declare in `plugin.json`**:
   ```json
   {
     "id": "my_custom_scraper",
     "name": "My Custom Scraper",
     "version": "1.0.0",
     "description": "Scrapes reference imagery from MySource API",
     "entryPoint": "plugin.py",
     "uiSlot": "HarvesterScraper",
     "menuSection": "Harvester Scraper",
     "icon": "TravelExplore",
     "configSchema": [
       { "key": "apiKey", "label": "API Key", "type": "password", "required": false },
       { "key": "username", "label": "User ID / Account", "type": "text", "required": false }
     ]
   }
   ```

2. **Harvester Integration & Execution Contract**:
   - Scrapers are automatically discovered by `ImageHarvesterService` and registered as selectable provider pills in `/harvester`.
   - To save disk space, all scraper plugins share a consolidated environment at `~/.loramancer/scraper_venv` pre-configured with `requests`, `beautifulsoup4`, `cloudscraper`, and `urllib3`.
   - Credentials and settings configured via the gear icon (⚙️) are persisted to `~/.loramancer/scrapers/<id>.json`.
   - When a harvest search executes, LoRAMancer invokes the scraper via standardized CLI arguments:
     ```bash
     python plugin.py --query "<search phrase>" --limit <maxResults> --json
     ```

3. **Mandatory Search Value Retention & Relevance Requirements**:
   All scraper plugins **MUST strictly retain, respect, and apply the search value** passed via `--query`:
   - **No Discarding or Silently Ignoring Queries**: Under no circumstances should a scraper drop the `--query` value or fall back to an unconstrained, all-time popular, or front-page image dump when a search query is provided.
   - **Zero-Match Integrity**: If the upstream provider has 0 results for the query, the scraper **must return an empty JSON array `[]`**. Scrapers must *never* return unrelated "fallback masterpieces" or static default feeds when a user has entered a search phrase.
   - **Query Translation & Routing**: If the upstream API does not support search on general image endpoints (for example, Civitai's `/api/v1/images` ignores queries), the scraper must route the query to an endpoint that supports keyword matching (such as Civitai's `/api/v1/models?query=...`) and extract images from the matching entities.
   - **Score Thresholds for Fuzzy Engines**: When querying search engines with fuzzy/fallback matching (such as museum ElasticSearch APIs like ArtIC), scrapers must enforce a minimum relevance score cutoff (e.g. `score >= 1.0`) to avoid leaking zero-relevance fallback entries.
   - **Default Queries**: Default keywords (such as `"concept art"` or `"portrait"`) should *only* be used if `--query` is completely empty or omitted by the caller.

4. **Standard JSON Output Format**:
   The scraper must print a JSON array to `stdout` containing candidate objects with the following fields:
   ```json
   [
     {
       "sourceUrl": "https://example.com/highres.jpg",
       "thumbnailUrl": "https://example.com/thumb.jpg",
       "title": "Descriptive title or prompt matching search query",
       "width": 1920,
       "height": 1080
     }
   ]
   ```

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

---

