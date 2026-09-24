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
- **Civitai Token & Cloudflare Anti-Bot Compatibility**:
  - If a model does not have a local thumbnail, clicking **"Find on Civitai"** (or **"Fetch Civitai Info"** in bulk) hashes the file with SHA256 and queries the Civitai API (`/api/v1/model-versions/by-hash/{hash}`).
  - **Civitai API Key Support**: If configured under **Settings** (`CivitaiApiKey`), your API token is automatically supplied both in the HTTP `Authorization: Bearer <key>` header and as the `?token=<key>` query parameter. This enables fetching metadata and downloading thumbnails for private, early-access, or member-only models.
  - **Cloudflare Bypass**: HTTP requests specify standard browser `User-Agent` headers to prevent HTTP 403 Forbidden errors when fetching data from Civitai CDN endpoints.
  - Retrieved metadata includes the official model title, version name, high-resolution preview image (cached locally), and trained trigger words with a 1-click **Copy Trigger** button.
- **Persistent Cache**: All computed hashes, metadata payloads, and downloaded preview thumbnails are cached in `~/.loramancer/lora_cache/` so subsequent folder loads are instantaneous.

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

- **Inspect Header**: Launches the header inspector drawer, displaying raw JSON metadata, tensor keys, and model architecture tags with zero tensor weight overhead.
- **Reveal in Explorer**: Opens Windows Explorer with the specific `.safetensors` file selected.
