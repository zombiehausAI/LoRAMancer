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
  - **Base Model Assignment**: Assign or override detected base models (e.g. FLUX.1, SDXL, SD 1.5, Pony, Illustrious, Chroma, HunyuanVideo, Wan 2.1) directly from quick dropdown menus on cards and table rows, automatically persisting changes to SQLite.

---

## 3. "Use Training Settings" (Hyperparameter Cloning)

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

## 5. Theme Engine & JSON Import / Export

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
