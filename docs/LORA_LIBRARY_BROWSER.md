# LoRA Library & Visual Browser

## Overview

The **LoRA Library & Visual Browser** is LoRAMancer's core discovery and inspection hub (`/`). It enables creators with large existing collections of `.safetensors` models (e.g. from ComfyUI, Kohya_ss, or Automatic1111) to visually browse models with preview thumbnails, inspect Civitai metadata, organize and navigate subfolders, and borrow proven hyperparameter recipes for new training runs.

---

## 1. Recursive Background Scanning & Subfolder Organization

Large LoRA collections are typically structured into categorized subfolders (e.g. `Loras/Characters/Anime/`, `Loras/Styles/`, `Loras/Flux/`). LoRAMancer respects and navigates this hierarchy:

- **Recursive Background Scanning**: When a root directory is selected (e.g. `D:\models\loras`), LoRAMancer scans the entire directory tree recursively in the background (`ScanDirectoryStreamAsync`) without freezing the user interface.
  - Discovered models stream into the gallery in real time as each file header is parsed.
  - Creators can immediately browse, search, or inspect models while scanning continues in the background.
  - A real-time progress banner displays current progress (`Scanning LoRAs recursively in background: X of Y`) with a 1-click **"Stop Scan"** cancellation control.
- **Hierarchical Subfolder View**:
  - Immediate subdirectories under the current folder are rendered as interactive **Folder Cards** in Grid view (or chips in Table view), each displaying a folder icon, name, relative path, and a live model count badge (e.g., `14 LoRAs`).
  - Clicking any subfolder card immediately navigates into that directory.
  - If a folder contains only subfolders without loose `.safetensors` files directly at that level, the browser clearly displays the subfolders and invites the user to click into any folder.
- **Breadcrumb Navigation**:
  - A dedicated navigation bar displays an **[⬆ Up]** button to move up one folder level at any time.
  - Clickable breadcrumbs allow jumping directly to any parent folder or back to the root folder with a single click.
- **Flat vs. Hierarchical Toggle**:
  - Easily toggle between **"Subfolder View"** (organized by directory) and **"Flat (All)"** (flattened display of all indexed models across all subfolders).
  - When in Flat View or when searching, each model card displays its relative folder path (e.g., `📁 Characters/Anime`) for instant orientation.
- **Global Search Across All Subfolders**:
  - Typing in the search bar automatically searches across all indexed models across all subfolders, ensuring you can quickly find any model regardless of where it is stored in your directory tree.

---

## 2. Visual Card Gallery & Civitai Token Integration

The browser supports both a rich **Card Grid** view and a compact **Table** view:
- **Local Thumbnail Auto-Discovery**: When scanning any folder, LoRAMancer automatically detects companion local preview images:
  - `<model_name>.png`
  - `<model_name>.preview.png`
  - `<model_name>.jpg`
  - `<model_name>.preview.jpg`
  - `<model_name>.webp`
- **Non-Blocking Background Civitai Enrichment**:
  - Bulk and single-item Civitai metadata fetching execute entirely on background worker threads via `LoraLibraryService`, allowing full app usage, folder navigation, configuration cloning, and training wizard workflows while metadata is gathered.
  - A real-time background status banner displays the current file being inspected, completed count, and progress bar with a 1-click **"Stop"** button.
  - Individual LoRA cards and table rows feature direct 1-click **"Fetch Civitai Info"** actions that perform non-blocking lookups and push live desktop notifications upon identification.
  - **Civitai API Key & Cloudflare Bypass**: Automated fallback between HTTP `Authorization: Bearer <key>` headers and `?token=<key>` query parameters with desktop browser `User-Agent` emulation, enabling lookup of private, early-access, or member-only models.
  - **Local & SQLite Caching**: All retrieved Civitai model versions, trigger tags, descriptions, download URLs, and companion thumbnails are persisted to `~/.LoRAMancer/loras.db` and cached to `~/.LoRAMancer/lora_cache/`.
- **Persistent SQLite Library (`~/.LoRAMancer/loras.db`)**: All indexed LoRA models, computed hashes, SafeTensors metadata, Civitai payloads, user favorites, and base model overrides are stored in an ACID SQLite database. When navigating to the LoRA Manager, the gallery loads instantly (0ms) from SQLite with zero background scan overhead.
- **Multi-Library Manager**:
  - Organize collections into multiple independent libraries (e.g. `FLUX Characters`, `Anime Styles`, `SDXL Concepts`, or specialized project folders).
  - All libraries reside within the same SQLite database (`~/.LoRAMancer/loras.db`) under the `Libraries` table.
  - Browse each library independently with instant switching from the top navigation dropdown or Library Manager modal.
  - **1-Click Subfolder Conversion**: Convert any active subfolder into its own independent library with a single click (`Make Subfolder a Library`), or bulk-convert all immediate subdirectories under a root path using **"Convert Subfolders"** in the Library Manager.
  - **Per-Library LoRA Updater**: The `LoraUpdater` plugin and dialog natively supports targeting a specific library from a dropdown selector, keeping each collection up to date with Civitai without scanning external directories.
- **On-Demand Differential Rescan**:
  - Scanning is strictly user-initiated via the **"Rescan / Sync"** menu or folder refresh buttons.
  - **Fast Delta Sync**: Compares file last-modified timestamps and file sizes against SQLite signatures, parsing headers only for newly added or modified files and pruning removed files in milliseconds.
  - **Scope-Specific Rescan**: Rescan your entire library, a specific subfolder, or refresh an individual LoRA file.
  - **Full Re-index**: Forces a clean re-scan and header re-parse across all files in the configured directory tree.
- **Favorites & Base Model Overrides**:
  - **Favorites (❤️)**: Mark any LoRA as a favorite with a single click on card or table view, and filter instantly with the "Favorites Only" chip.
  - **Dynamic Base Model Architecture Discovery**:
    - The **Filter Base** dropdown automatically discovers all base models actively present across your indexed LoRAs, appending real-time item counts (e.g. `Pony (45)`, `Illustrious (32)`, `FLUX.1 (72)`, `SD 3.5 (18)`, `SD 1.5 (25)`, `Chroma (18)`).
    - Unioned with full modern foundation presets: `FLUX.1-dev`, `FLUX.1-schnell`, `SDXL 1.0`, `Pony Diffusion V6 XL`, `Illustrious-XL / NoobAI`, `Stable Diffusion 3.5`, `Stable Diffusion 1.5`, `Stable Diffusion 2.1`, `ChromaHD-1`, `Wan 2.1`, `HunyuanVideo`, and `AuraFlow`.
  - **Smart Subfolder & Path Heuristic Detection**:
    - Community `.safetensors` files often lack architecture headers or use generic checkpoint names. LoRAMancer checks parent directory names upon scanning (e.g. `LoRAs/Pony/...`, `LoRAs/Illustrious/...`, `LoRAs/SD3.5/...`, `LoRAs/SD1.5/...`, `LoRAs/Chroma/...`, `LoRAs/Flux/...`, `LoRAs/Wan/...`) and automatically tags the model with the exact target architecture.
  - **Custom Base Model Assignment**:
    - Users can assign any custom base model name directly from the card overlay badge, table row menu, or the **"Assign Category & Tags"** dialog. Custom architectures are persisted in SQLite and immediately appear across all studio filter dropdowns.
  - **AI-Toolkit Dynamic Model Scanner**:
    - When AI-Toolkit is installed in `tools/ai-toolkit` or custom directories, LoRAMancer automatically scans `config/examples/` and `extensions_built_in/diffusion_models/` on boot and setup, registering newly supported models directly into `ModelArchitectureRegistry` for training and filtering.

---

## 3. Dynamic Studio Highlight / Accent Color Customization

Creators can personalize the accent illumination of LoRAMancer on the fly without editing configuration files:
- **Top AppBar Palette Selector**: A dedicated Palette button in the top navigation bar displays a quick popover with curated 1-click swatches tailored for graphite/dark themes:
  - 🟣 **Catppuccin Lavender** (`#cba6f7`)
  - 🟢 **Matrix Emerald** (`#10b981`)
  - 🟡 **Solar Amber** (`#f59e0b`)
  - 🔴 **Neon Rose** (`#f43f5e`)
  - 🟠 **Warm Coral** (`#fb923c`)
  - 🔵 **Cyan / Sky Blue** (`#38bdf8`)
  - 🪨 **Titanium Neutral Light** (`#cbd5e1`)
- **Interactive Color Picker**: Fine-tune any custom HEX or RGB color using the integrated `<MudColorPicker>`.
- **Global Reactive Illumination**: Changing the highlight color immediately updates `--accent-purple`, MudTheme `PaletteDark.Primary` / `PaletteLight.Primary`, button accents, tab indicators, chip borders, and active highlights across the entire studio in real time.
- **Persistent Preference**: Custom highlight colors are saved in `AppSettings.json` (`CustomPrimaryColor`) and can be reset back to theme defaults at any time.

---

## 3. Categories, Custom Tags & Classification Pipeline

LoRAMancer features a robust, assignable category and tagging architecture persisted in SQLite (`~/.LoRAMancer/loras.db`):

- **Assignable Primary Categories**:
  - Assign any LoRA to curated classifications such as **Character**, **Style**, **Concept**, **Clothing**, **Pose**, **Environment**, **Vehicle**, or **Enhancement**.
  - **1-Click Card & Table Assignment**: Quick-assign categories via the colored badge on the lower-left corner of any card thumbnail or the dedicated table row chip.
  - **Category Manager**: Launch the manager via the **"Categories"** toolbar button or context menu to create custom categories, customize hex accent colors, assign icons, and edit descriptions. Deleting a category automatically gives you the choice to clear or reassign associated models.
- **Custom Model Tags**:
  - Assign multiple custom tags (e.g. `anime`, `cyberpunk`, `portrait`, `lighting`) to any model via the **"Assign Category & Tags..."** dialog.
  - Custom tags appear as styled mini-badges directly below the model title on cards and are searchable in real-time.
- **Searchable & Filterable**:
  - **Category Filter Chips**: A dedicated filter bar displays live counts for each category (e.g. `Character (14)`, `Style (8)`, `Uncategorized (3)`). Clicking any pill immediately restricts the vault view.
  - **Omni-Search Bar**: Entering terms in the global search bar matches against category names and custom tags alongside file names, Civitai titles, and trigger words.
- **Multi-Factor Sorting**:
  - Sort your vault instantly with the **"Sort By"** selector:
    - **Name (A-Z / Z-A)**
    - **Category (A-Z / Z-A)**
    - **Date Modified (Newest / Oldest)**
    - **File Size (Largest / Smallest)**
    - **Rank / Dim (Highest)**
    - **Tags Count (Most)**

---

## 4. "Use Training Settings" (Hyperparameter Cloning)

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

## 4. Direct Explorer & Metadata Inspection

- **Inspect Header**: Launches the header inspector drawer, displaying raw metadata, tensor keys, and model architecture tags with zero tensor weight overhead.
- **Save & Copy Metadata JSON**:
  - Available directly on each LoRA card, table row, and inside the Metadata Inspector drawer via the **`DataObject` (`{ }`)** icon and buttons.
  - Automatically exports a formatted sidecar `<model_name>_metadata.json` alongside the `.safetensors` model file.
  - Automatically copies the full JSON payload directly to your system clipboard for instant sharing or debugging.
  - The exported JSON includes:
    - `file_info`: File path, file size, formatted size, and computed SHA256 model hash.
    - `hyperparameters`: Rank (dim), alpha, learning rates (UNet and Text Encoder), optimizer, scheduler, epochs, steps, resolution, and precision.
    - `civitai`: Model version, base model, Civitai URL, and trained trigger tags.
    - `raw_header_metadata`: Complete key-value dictionary of all raw embedded SafeTensors header attributes (such as `ss_network_args`, `ss_optimizer_args`, `ss_dataset_dirs`, `ss_tag_frequency`, `ss_bucket_info`, etc.).
- **Reveal in Explorer**: Opens Windows Explorer with the specific `.safetensors` file selected.

---

## 5. Right-Click Context Menu & Studio Synergy Pipeline

Every LoRA card and table row in the Vault features instant access to a comprehensive 4-section context menu designed for complete studio synergy:
- **Right-Click Anywhere**: Right-clicking on any LoRA card or table row brings up the context menu at the cursor position.
- **Dedicated Diagnostics Button**: A visible **`Science` (⚗️)** icon button is also available on both card and table action toolbars for immediate one-click diagnostic routing.

### Context Menu Sections & Actions:

1. **⚡ Studio Pipeline Routing**:
   - **Send to Diagnostic Lab (Stage 3)**: Auto-loads the model and primary trigger into the studio session and navigates directly to `/lab`.
   - **Test in ComfyUI Canvas (Stage 4)**: Auto-loads the model and trigger word into the studio session and routes straight to `/test`.
   - **Clone Recipe into Trainer (Stage 2)**: Re-seeds the training hyperparameter matrix (dim, alpha, learning rates, optimizer, scheduler, resolution) directly into the Training Wizard.
   - **Seed Concept to Dataset Curator (Stage 1)**: Transfers the model's concept name or primary trigger token into Stage 1 Dataset Curator (`/curator?concept=...`) for instant paired dataset gathering.

2. **🔬 Diagnostics & Surgery**:
   - **SVD Overbake Radar (Auto-Scan)**: Navigates straight to Stage 3 Lab Tab 4 with the LoRA auto-inserted, automatically running the SVD singular value monopolization scan.
   - **Forensic De-Anonymizer (Reverse-Engineer)**: Navigates to Stage 3 Lab Tab 6 with the LoRA auto-inserted, automatically reconstructing architecture fingerprint, rank/alpha scaling, trigger tokens, and training hyperparameters.
   - **Ghost Hunter & Style Decoupler**: Opens Stage 3 Lab Tab 5 with the LoRA pre-loaded into identity retention and style damping controls.
   - **Layer Surgery & Resizing**: Opens Stage 3 Lab Tab 1 with the LoRA pre-loaded for SVD rank compression or cross-attention block scaling.
   - **Vector Gene Therapy Suite**: Launches the interactive Gene Therapy modal dialog with the LoRA pre-selected for eigenvalue outlier suppression.
   - **Visual Diff & Angle Drift Inspector**: Launches the Visual Diff modal dialog with Model A pre-set to the selected model.
   - **Batch Benchmark & Sweet Spot Matrix**: Launches the automated epoch sweep matrix evaluator, testing likeness, style flexibility, and color burn.

3. **📋 Prompts & Clipboard**:
   - **Copy Trigger Word**: Copies the model's primary trigger keyword to the system clipboard with an instant confirmation snackbar.
   - **Copy Prompt Syntax (`<lora:...>`)**: Automatically formats and copies standard WebUI/ComfyUI prompt notation (e.g. `<lora:my_model:0.85>, trigger_word`).
   - **Copy Full File Path**: Copies the absolute system path of the `.safetensors` model file for external script or workflow integration.
   - **Save & Copy Metadata JSON**: Exports a formatted `<model>_metadata.json` sidecar alongside the file and copies the full payload to the clipboard.

4. **🗄️ Vault Actions**:
   - **Toggle Favorite**: Quickly pin or unpin high-priority LoRAs to the top of the Vault.
   - **Fetch Civitai Info & Preview**: Queries Civitai API by hash to download model cards, descriptions, tags, and preview thumbnails.
   - **Refresh from Disk**: Re-reads headers directly from storage to update dimensions and file state.
   - **Inspect Header Metadata**: Slides out the non-blocking header inspector drawer.
   - **Reveal in Explorer**: Opens Windows Explorer with the specific `.safetensors` file selected.

---

## 6. Theme Engine & JSON Import / Export

- **High-Contrast Dark Themes**: Curated modern palettes tailored for OLED and high-resolution displays, including Catppuccin Mocha, Tokyo Night, Slate Greys (Obsidian), Crimson Blood, Emerald Cyber, Solar Amber, and Cobalt Sapphire.
- **JSON Theme Import & Export**:
  - Export any active theme or design custom palettes using JSON definitions with color tokens (`id`, `name`, `isDark`, `background`, `surface`, `accent`, `textPrimary`, etc.).
  - Custom imported themes are persisted to `~/.LoRAMancer/themes/<id>.json` and load seamlessly at runtime.
  - Import themes directly from `.json` files or paste raw JSON in the Settings page.

---

## 6. UI Resilience, Thread-Safe Search & Error Containment

To ensure complete stability across large model collections and rapid user interactions:
- **Thread-Safe Search & Filter Isolation**: All collection counts and paginated model queries synchronize across background workers with `_filterLock`. Materialized snapshots (`FilteredLorasSnapshot`, `PagedLoras`) prevent `InvalidOperationException` collection modification errors when typing in search while background tasks or thumbnails update.
- **Debounced Search Dispatch**: Keystrokes in the global search bar cancel pending query delays via atomic cancellation tokens (`_searchLock`), avoiding thread collisions and unnecessary renders.
- **Application & Dialog Error Boundaries**:
  - Modal dialogs (such as the Training Wizard and Config Cloner) are guarded by dedicated `<ErrorBoundary>` wrappers in `MainLayout.razor`, isolating any rendering or calculation issues and preventing global WebView2 circuit reload crashes.
  - The **"Use Settings"** action defensively validates donor files and model architectures, providing non-intrusive warning snackbars if metadata attributes are incomplete.
