# Post-Forge Showcase 🎨

## Overview

The **Post-Forge Showcase** (`/post-forge`) is LoRAMancer's native, hardware-accelerated media gallery and generation evaluation suite. Designed as the culmination of the creation pipeline (Stage 5), it catalogs, inspects, and organizes generated content produced by newly trained LoRAs across images, video, and audio.

---

## Key Features

### 1. Multi-Format Media Support
The showcase goes beyond standard static images to cover the full spectrum of generative AI outputs:
- **Images**: `.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp`, `.tiff`
- **Video**: `.mp4`, `.webm`, `.mov`, `.mkv`, `.avi` (AnimateDiff, SVD, Wan2.1, Hunyuan, CogVideo, LTX-Video)
- **Audio**: `.wav`, `.mp3`, `.flac`, `.ogg`, `.m4a` (AudioCraft, Stable Audio, Suno, Udio, Bark)

### 2. Automated AI Detection & Verification Badging
The media engine automatically scans binary chunks and container metadata to identify AI-generated content:
- **Automatic1111 / WebUI Forge**: Reads embedded `parameters` text chunks, extracting prompts, negative prompts, seed, steps, sampler, CFG scale, base model, and `<lora:name:strength>` tags.
- **ComfyUI & ModusFlow**: Parses embedded `prompt` and `workflow` JSON node graphs. Specifically features dedicated support for **[ComfyUI-ModusFlow](https://github.com/zombiehausAI/ComfyUI-ModusFlow)**:
  - `ModusFlowMultiCLIPTextEncode`: Extracts up to 4 per-CLIP text encodings based on active `enable_clip1..4` toggles.
  - `ModusFlowTextEditor`: Extracts structured positive and negative prompts.
  - `OllamaPromptRefinerNode` / `ModusFlowPromptRefiner`: Extracts refined prompts and negative prompts.
  - `ModusFlowShowText`: Extracts dynamic prompt text streams.
  - `ModusFlowAceStepAudio`: Extracts audio tags, lyrics, and prompts.
  - `ModusFlowPowerLoraLoader`: Detects connected multi-LoRA stacks and strengths.
  - `ModusFlowFluxLoader` / `ModusFlowModelLoader`: Extracts checkpoint and diffusion model references.
  - **Embedded EXIF & Adobe XMP**: Reads `ImageDescription` EXIF tags in JPEG and `<dc:description>` XMP packets in WebP saved by `ModusFlowSaveImage`.
  - Badges outputs explicitly as **ComfyUI (ModusFlow)**.
- **NovelAI**: Identifies proprietary generation software and prompt tags.
- **Diffusers & AI-Toolkit**: Parses companion `.json` and `.txt` files containing epoch milestone prompts and seeds.
- **Verification Badge**: Validated AI content is highlighted with a glowing **AI GENERATED** badge on card previews and in the exploded modal inspector.

### 3. Exploded Modal Inspector
Clicking any card in the gallery opens a large, responsive detail modal:
- **High-Resolution Viewer**:
  - **Images**: Full-scale rendering with zoom, pan, and aspect ratio preservation.
  - **Videos**: Built-in HTML5 video player with loop, playback speed controls, and full audio support.
  - **Audio**: Interactive HTML5 audio player with waveform visualizer and duration metrics.
- **1-Click Actions**:
  - **Assign as LoRA Preview**: Opens the interactive LoRA Preview Connector dialog:
    - **Detected in Recipe**: Auto-detects LoRAs used in the generation recipe (via `<lora:...>`, `ModusFlowPowerLoraLoader`, or companion JSON) for instant 1-click preview selection.
    - **Library Browser**: Live search and filter across all installed LoRAs with multi-selection support.
    - **Companion File Sync**: Copies the image as `{lora_name}.preview.png` alongside the `.safetensors` model in the filesystem so ComfyUI, Forge, and A1111 instantly display the preview.
    - **Database Sync**: Updates `ThumbnailPath` in LoRAMancer's SQLite database and broadcasts updates across the UI.
  - **Copy Prompt / Negative Prompt**: Instant clipboard copy with toast confirmation.
  - **Copy File Path**: Copies absolute system path to clipboard.
  - **Open in Explorer**: Launches Windows File Explorer with the target file highlighted (`explorer.exe /select,"<path>"`).
  - **Send to ComfyUI Test**: Automatically pre-fills the prompt into the Stage 4 ComfyUI Inference Studio.

### 4. LoRA Preview Connections & Gallery Badging
- **Connected LoRA Badges**: Showcase gallery cards display a glowing **Preview** badge when an image is linked as a preview to one or more LoRAs, along with a tooltip listing the associated model names.
- **Bi-directional Association**: LoRAMancer maintains the connection in `ShowcaseMediaItem.AssociatedLoraNames`, allowing users to see exactly which trained LoRAs are represented by each showcase piece.

### 5. Folder Hierarchy Architecture & Dual View Modes
The gallery supports an intuitive hierarchical folder browsing experience reflecting real-world directory trees:
- **Hierarchical Tree Traversal (`root -> subfolder + media -> media`)**:
  - **Root Libraries**: Configured directories (e.g., ComfyUI output, custom archive paths) appear as root library nodes.
  - **Interactive Breadcrumbs**: Dynamic breadcrumb trail (`All Libraries > Output > FluxRenders > Landscapes`) with quick 1-click jumps to any ancestor directory level and an **Up One Level (⬆)** button.
  - **Subfolder Explorer Cards**: Displays child folders with:
    - Thumbnail preview generated from the first available image inside the folder.
    - Media item counter badges (direct media count vs total recursive media count).
    - Subdirectory counters indicating nested folders.
  - **Folder-Scoped Media Items**: Viewing a directory renders only media located directly inside that folder level, cleanly separating folder categories from actual media.
  - **"Include Subfolders" Toggle**: When enabled within hierarchy view, aggregates all media from the current folder and all of its descendants.
- **Dual View Modes**:
  - **📂 Hierarchy Mode**: Full directory tree navigation through breadcrumbs, subfolder explorer cards, and local media.
  - **🗃️ Flat Gallery Mode**: Aggregated, global feed displaying every indexed generation across all configured libraries in one view.

### 6. On-The-Fly Thumbnail Generation & Settings Cache
To deliver fast, responsive loading without freezing the UI or tripping WebView2 local file access restrictions, thumbnails are generated on the fly and cached permanently in the user settings directory:
- **Storage Location**: Stored as compressed, high-performance JPEGs in `~/.loramancer/showcase_thumbnails/`.
- **Media Format Handlers**:
  - **Images**: Native downscaling via `Windows.Graphics.Imaging` down to 380px with EXIF orientation correction, falling back to FFmpeg if needed.
  - **Videos**: Extracts video snapshot frames using `ffmpeg -ss 00:00:01 -i ... -vframes 1 -vf "scale=380:-1"` (with fallback to `00:00:00` for micro-clips).
  - **Audio**: Generates purple waveform visualizations directly from audio files using `ffmpeg -i ... -filter_complex "showwavespic=s=380x190:colors=#cba6f7"`.
- **In-Memory & Background Concurrency**: Non-blocking asynchronous loading with `ConcurrentDictionary` deduplication ensures instant UI rendering and smooth scrolling.
- **WebView2 Safe Delivery**: Delivered as base64 data URIs (`data:image/jpeg;base64,...`), bypassing Chromium/WebView2 cross-origin local file restrictions.

### 7. Root Library Management (Add, Remove & Browse)
Users have full control over the root output directories scanned by LoRAMancer:
- **Add Root Folder**: Click **"Add Folder"** to open a native Windows folder picker and add any local or network drive directory.
- **Manage Folders Dialog**: Dedicated management panel displaying all registered libraries with indexed item counts, quick 1-click **Open in Windows File Explorer**, and **Remove Library** buttons.
- **1-Click Card Removal**: In folder hierarchy view at the root level, each library card features a direct **Remove (🗑️)** button to detach the library immediately from the Showcase.
- **Persistence**: Configured libraries are stored in `ShowcaseDirectories` inside `~/.loramancer/settings.json`.

### 8. Non-Blocking Background Scanning & Zero-Lag UI Architecture
Scanning massive generation folders or thousands of models runs entirely decoupled from the UI thread without micro-stutter, lag, or thread contention:
- **True Background Thread Offloading**: Scans execute via `Task.Run` on thread-pool workers managed by `PostForgeShowcaseService` and `LoraLibraryService`.
- **Cooperative Async Yielding**: Every 30–40 items scanned, background tasks yield cooperatively (`await Task.Delay(1, cancellationToken)`), guaranteeing CPU time slices for UI rendering and system I/O.
- **Throttled Progress Dispatching**: Progress notifications are throttled to at most once per 250ms with batch milestones, preventing Blazor render queue flooding.
- **Cached Snapshot Materialization**: Gallery media items and folder trees are cached as pre-sorted, immutable list snapshots (`_cachedSnapshot`), turning collection access from O(N log N) disk/lock operations into instant O(1) zero-allocation reads.
- **Subdirectory Query Caching**: Synchronous file system probes (`Directory.Exists` and `Directory.GetDirectories`) are cached via high-performance concurrent caches with automatic expiration, eliminating synchronous disk I/O on UI render cycles.
- **Decoupled View Rebuilding**: Gallery and folder view models in `PostForgePage.razor` maintain cached view lists (`_filteredItems`, `_currentChildFolders`) that update only upon explicit user actions (navigation, filtering, sorting) or scan completion, so progress ticks merely repaint the progress bar without re-evaluating thousands of items.
- **Incremental Database Batch Persistence**: New images and media metadata are persisted to SQLite or PostgreSQL in batches of 25 during active scans. The UI snapshot updates in real time, and previously extracted items are never lost if a scan is interrupted or canceled.
- **Differential Metadata Skipping**: On startup and rescans, previously indexed items stored in the database are loaded immediately and skipped during disk extraction, eliminating redundant re-scanning from scratch.
- **Full PostgreSQL Server Support**: Gallery media records, favorites, categories, tags, and metadata are fully synchronized with PostgreSQL schemas when connected to a remote database server.

### 9. Persistent Per-Folder Sorting
Different directories often require different viewing priorities (e.g., sorting epoch comparisons alphabetically, while sorting general outputs by newest first):
- **Per-Folder Memory**: LoRAMancer remembers the sort order configured for each directory independently, stored in `ShowcaseFolderSortOrders` in `~/.loramancer/settings.json`.
- **Instant Restoration on Navigation**: Navigating between folders (or root libraries) via breadcrumbs or cards immediately restores that folder's specific sort preference.
- **Comprehensive Sort Options**:
  - **Newest First** (`newest`): Sorts by creation/modification timestamp descending.
  - **Oldest First** (`oldest`): Sorts by creation/modification timestamp ascending.
  - **Largest File Size** (`largest`): Sorts by disk file size descending.
  - **Smallest File Size** (`smallest`): Sorts by disk file size ascending.
  - **Name (A-Z)** (`alphabetical`): Alphabetical sort by file name.
  - **Name (Z-A)** (`reverse_alphabetical`): Reverse alphabetical sort by file name.

### 10. Custom Categories, Tags & Filtering
- **Categories**: Classify media into customizable categories (*Portraits*, *Styles*, *Landscapes*, *Characters*, *Objects*, *Concepts*, *Animations*, *Soundscapes*).
- **Custom Tags**: Tag outputs with keywords (e.g., `keeper`, `v1-epoch10`, `hands-fixed`, `overbaked`) with live filtering.
- **Favorites**: Star favorite outputs to quickly filter down to top generations.
- **Multi-Criteria Filtering**: Filter by media type, AI verified status, tags, folders, or full-text prompt search.


