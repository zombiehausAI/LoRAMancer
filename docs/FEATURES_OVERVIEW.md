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
- **Single-GPU Training Queue & Background Runner**:
  - Sequential training job queue designed specifically for dedicated single-GPU workstations, preventing multi-process VRAM contention and driver crashes.
  - Non-blocking background execution: users can freely navigate between Curate, Lab, Test, and Vault while active runs continue unimpeded in the background.
  - Global top navbar telemetry badge displaying live step progress and count of queued jobs from any page.
  - Queue management: re-order pending jobs, remove individual runs, or cancel all with automatic 2-second VRAM cooldown between consecutive runs.
- **Single-Instance Application Mutex**:
  - Desktop-wide named mutex check (`Global\LoRAMancer_Studio_SingleInstance_Mutex`) ensuring only one instance of LoRAMancer can run simultaneously.
  - Automatically restores and focuses the active window if a second instance is launched.

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

---

## 9. One-Click ComfyUI Interactive Test Studio

- **Zero-Config Prompt Graph Generation**:
  - Automatically generates ComfyUI API execution graphs for FLUX.1 (Dev / Schnell), SDXL 1.0, and SD 1.5 without requiring manual workflow assembly.
  - Automatically loads checkpoints, attaches LoRA nodes with custom weights, binds CLIP positive/negative text prompts, sets latent dimensions, and samples via KSampler.
- **Local, LAN & WAN ComfyUI Connectivity**:
  - Connects to local instances (`http://127.0.0.1:8188`), LAN hosts, or remote WAN endpoints over HTTP REST and WebSockets.
  - Instant local symlinking (0ms, 0 extra disk space) into ComfyUI's `models/loras/` directory, with automatic multipart upload fallback for remote/WAN instances.
- **Real-Time WebSocket Feedback & Rendering**:
  - Connects to `/ws?clientId=...` to stream real-time sampling step percentages and stage progress directly inside LoRAMancer.
  - Renders generated image directly inside the test modal with one-click Save to Desktop.
  - Integrated into LoRA Library cards, Training Console, and Training History.

---

## 10. Dataset Curator & Batch Caption Studio

- **Tag Frequency Analysis & Lexicon Explorer**:
  - Scans entire dataset directories, parses comma-separated tags, and calculates exact occurrence counts and dataset percentages.
  - Interactive tag table with quick delete and find/replace population.
- **Civitai-Style In-Place Ollama Auto-Tagging**:
  - Automatically loads the active studio dataset directly into the Ollama LoRA Tagger with 1 click.
  - Writes `.txt` captions in-place next to source images with zero zip/duplicate folder overhead.
  - Instantly refreshes dataset metadata, gallery cards, and tag frequencies upon completion.
- **Dataset-Wide Focus & Style Defaults**:
  - Configurable dataset-wide subject focus presets (`Person / Character`, `Object / Prop`, `Scenery / Environment`, `Art Style / Aesthetic`, `General / Balanced`).
  - Dual caption styling modes: Comma-separated Visual Tags (for SDXL / Pony / Anime) vs Natural Language sentences (for FLUX.1).
- **Per-Image Inspect, Edit & Individual AI Auto-Tagging**:
  - Dynamically resizing CSS card grid and compact list views with scrollable multi-line caption editors.
  - Interactive high-res Inspect & Edit modal with parsed tag chip management.
  - Per-image AI subject focus and caption style overrides right next to the `✨ AI Auto-Tag` button for rapid single-image refinement.
- **Batch Prefix, Suffix & Mass Find/Replace**:
  - Mass find-and-replace across all `.txt` caption files with case-sensitivity and regex support.
  - 1-click prepend or append of trigger words and style tokens without duplicate tag contamination.
  - Automatic timestamped caption backups (`.captions_backup_YYYYMMDD_HHMMSS`) created before every batch modification.
- **Dataset Clear & Unload**:
  - One-click dataset clearing to safely unload the active workspace, wipe thumbnail memory caches, reset curation session state, and automatically clean up temporary extracted working copy directories.
- **Dataset ZIP Archive Import, Working Copy & Save-to-ZIP**:
  - Direct import of `.zip` dataset archives via the "Import ZIP" action in the studio header and empty state.
  - Automatically extracts archives into an isolated temporary working directory (`%TEMP%\loramancer_working_datasets\<guid>\`), enabling full studio curation, batch find/replace, Ollama vision autotagging, and radar inspection on the archive's contents.
  - Dedicated **"Save to ZIP"** action compresses the working directory and atomically updates the original `.zip` archive while keeping the dataset open for continuous editing.
  - Unloading or clearing the dataset safely purges the temporary extracted directory.
- **Dataset ZIP Archive Packaging**:
  - 1-click dialog to compress images and `.txt` captions into `<folder_name>.zip`.
  - Toggle between saving directly inside the dataset folder (`<dataset_folder>\<folder_name>.zip`) or alongside in the parent directory (`<parent_dir>\<folder_name>.zip`).
  - Real-time compression progress, automatic exclusion of pre-existing `.zip` archives, and 1-click "Open in File Explorer" upon completion.
- **Aspect Ratio Bucketing & Image Audit**:
  - Analyzes image dimensions across dataset files without loading full bitmaps into RAM.
  - Groups images into standard training buckets (1:1 Square, 3:4/2:3/9:16 Portrait, 4:3/3:2/16:9 Landscape) with distribution percentages.
  - Detects corrupt, truncated, or unreadable image headers.

---

## 11. LoRA Surgery Studio (SVD Rank Resizing & Weight Merging)

- **SVD Rank Reduction (Model Compression)**:
  - Compresses heavy LoRAs (ranks 64, 128, 256) down to compact target ranks (16, 32) using truncated Singular Value Decomposition.
  - Shrinks 400MB+ `.safetensors` files down to 25MB-50MB while retaining over 95% of concept likeness.
  - Preserves Safetensors header metadata and updates `ss_network_dim`.
- **LoRA Weight Matrix Merger**:
  - Merges two trained LoRAs of the same architecture using linear interpolation weights ($W_{new} = w_A W_A + w_B W_B$).
  - Produces unified standalone `.safetensors` outputs ready for immediate inference or testing.

---

## 12. TensorBoard & Pre-Flight OOM / VRAM Estimator

- **Dry-Run Pre-Flight Hardware Auditing**:
  - Analyzes model architecture (FLUX vs SDXL vs SD 1.5), network rank, batch size, and latent caching against detected physical GPU VRAM.
  - Alerts users before launching if estimated VRAM exceeds hardware capacity, preventing out-of-memory driver crashes.
  - Verifies target disk free space for latent cache files and checkpoints.
- **Embedded TensorBoard Integration**:
  - 1-click launch of TensorBoard server on port 6006 directly against the active run output directory.
  - Interactive real-time loss curves, learning rate progression, and gradient norms in your browser.

---

## 13. LoRA Gene Therapy (Layer Block Energy Heatmap & Toxic Outlier Pruner)

- **Block Energy Distribution & Frobenius Norm Heatmap**:
  - Computes the Frobenius weight delta norm ($\| \Delta W \|_F$) across every transformer and UNet block ($W_{up} \times W_{down}$).
  - Visually renders the energy distribution and flags toxic outliers ($> 2.8\times$ mean energy) causing color fry, contrast crushing, and prompt bleeding.
  - Detects dead or flat layers ($< 0.05\times$ mean energy) that waste parameter capacity.
- **Surgical Layer Pruning & Selective Attenuation**:
  - Select and attenuate ($0.0\times - 0.75\times$) or zero-out defective layers without retraining.
  - Instantly cures "fried" models and salvages overcooked LoRA checkpoints in seconds.
- **Cohesive Studio Target LoRA Integration**:
  - Seamlessly utilizes the active target LoRA loaded in Stage 3 (marked "Ready for Surgery") directly inside the Vector Gene Therapy tab.
  - Supports 1-click active model block energy scans, interactive toxic block health tables, and 1-click auto-purge cleansing that updates the active session.
  - Automatically pre-populates `InitialLoraPath` when launching the full Gene Therapy Suite dialog.

---

## 14. Instant Model-to-LoRA Checkpoint Extraction

- **Direct Checkpoint Subtraction ($\Delta W = W_{ft} - W_{base}$)**:
  - Subtracts a base model from a fine-tuned model checkpoint to extract parameter deltas directly.
  - Employs truncated Singular Value Decomposition (`torch.linalg.svd`) to factorize delta matrices into compact low-rank LoRA matrices ($A \in \mathbb{R}^{d \times r}, B \in \mathbb{R}^{r \times k}$).
  - Extracts full ~30MB LoRAs from 24GB full models in < 60 seconds on CPU or GPU with zero training required.

---

## 15. LoRA Visual Diff Inspector

- **Side-by-Side Weight Cosine Drift & Layer Matrix**:
  - Compares two LoRA `.safetensors` files side-by-side with tensor-level precision.
  - Computes Frobenius norm deltas and high-dimensional weight angle cosine similarity across all shared tensors.
  - Flags divergence levels: Identical, Subtle Drift, Moderate Drift, Heavy Divergence, or Topology Mismatch.
- **Training Recipe & Hyperparameter Diffs**:
  - Compares base architecture, learning rates, schedulers, rank/alpha ratios, dataset tags, and optimizer configurations from Safetensors headers.
  - Exports clean Markdown audit reports for model versioning and merge comparisons.
- **Stage 3 Baseline Model A Binding**:
  - The Visual Diff tab in Stage 3 automatically locks the active target LoRA as Model A (Baseline/Reference) and accepts comparison models or epoch checkpoints for inline comparisons or pre-populated full inspector launches.

---

## 16. Semantic Collision Radar & Anti-Bleed Token Synthesizer

- **CLIP / T5 Lexical Prior Collision Detection**:
  - Evaluates trigger words and caption tags against high-impact visual archetypes (colors, physical elements, genres, and dictionary primitives).
  - Calculates a Collision Severity Index (0 - 100%) and explains exactly why a trigger word will cause concept bleeding.
- **Zero-Collision Synthetic Token Generation**:
  - Synthesizes phonetically pronounceable rare-token sequences (e.g. `ohwx`, `v9x`, `qelx_zenz`) that have dormant semantic presence in base models.
- **Full Dataset Caption Cleanser**:
  - Audits entire caption datasets to surface high-risk tokens.
  - Automatically replaces conflicting archetype words with clean synthetic tokens across all `.txt` caption files in 1 click.

---

## 17. Automated AI Benchmark Matrix (Sweet Spot Finder)

- **Multi-Epoch Automated Evaluation Matrix**:
  - Point to a training output folder containing multiple epoch checkpoints.
  - Executes a standardized 4-part visual challenge matrix: [1] Identity Likeness, [2] Style Flexibility, [3] Negative Bleed Stress, [4] Complex Composition.
  - Automates rendering across epochs via ComfyUI WebSocket pipeline.
- **Mathematical Sweet Spot Scoring**:
  - Evaluates Likeness growth, Style Flexibility decay, and Contrast/Burn penalty curves across training epochs.
  - Pinpoints the exact mathematical optimal epoch checkpoint before overfitting occurred.
  - 1-click promotion of the winning checkpoint into the primary LoRA Library.

---

## 18. 5-Stage Creative Studio Pipeline & Concept Session

LoRAMancer unifies individual workflows into a cohesive 5-stage production studio pipeline:

- **Persistent Active Concept Session (`StudioSessionService`)**:
  - Tracks the user's active concept name, training dataset path, trigger words, base model architecture, and target `.safetensors` model.
  - State automatically flows between every studio workspace without re-entering paths or parameters.
  - Session persists across restarts in `~/.loramancer/studio_session.json`.
- **Top Studio Pipeline Switcher**:
  1. **Curate (`/curate`)**: Full-screen dataset gallery, inline caption editor, tag frequency data grid, batch prefix/suffix trigger words, aspect ratio bucketing, and Semantic Collision Radar.
  2. **Train (`/training`)**: Forge and training studio with live AI-Toolkit loss telemetry, real-time log stream, and hardware acceleration monitors.
  3. **Lab (`/lab`)**: LoRA surgery, block weight attenuation, SVD rank compression, vector gene therapy, SVD Overbake Radar, Ghost Hunter, Style Decoupler, Forensic De-Anonymizer, and the LoRA Vehicle Chop-Shop. Includes 1-click model clearing and switching back to the browse empty state.
  4. **Test (`/test`)**: ComfyUI inference studio with live WebSocket image rendering, prompt testing, and AI benchmark matrix sweeps.
  5. **Vault (`/`)**: Central LoRA model library, visual card/table browser, Civitai metadata enricher, and version management.
- **Configurable Startup Section**:
  - Configurable in Settings (`General & Paths`) allowing users to choose which pipeline section opens on load (Section 1: Curate, Section 2: Train, Section 3: Lab, Section 4: Test, or Section 5: Vault).
  - Defaults to Section 1 (Curate: `/curate`) on initial load and unless explicitly configured.

---

## 19. SVD Spectral Energy Decay & Overbake Radar

- **Mathematical Rank & Spectral Entropy Decomposition**:
  - Performs Singular Value Decomposition (SVD: $\Delta W = U \Sigma V^T$) across every attention projection matrix in the LoRA.
  - Measures singular value decay distributions, Frobenius norms ($\| \Delta W \|_F$), and Shannon spectral entropy ($H = -\sum p_i \ln p_i$).
  - Detects **singular value monopolization** ($\sigma_1$ energy dominance) where a single vector dominates model behavior, causing blown-out saturation and training collapse.
- **Overbake Severity Index & Categorization**:
  - Calculates a real-time 0 - 100% score mapped to 4 actionable diagnostic tiers:
    - **Underbaked (< 20%)**: Concept has low activation and may require higher prompt weights or additional training steps.
    - **Optimal Sweet Spot (20% - 65%)**: Clean rank distribution with high flexibility and strong concept capture.
    - **Overcooked Warning (65% - 85%)**: High Top-1 singular value concentration; prompts are beginning to fight the LoRA.
    - **Burned Collapse (> 85%)**: Severe rank collapse, extreme contrast blowout, or irreversible style bleed.
- **Multi-Epoch Checkpoint Sequence Audit**:
  - Scans an entire folder of checkpoint `.safetensors` files from a training run.
  - Automatically identifies the exact sweet-spot epoch before singular value monopolization escalated.

---

## 20. LoRA Ghost & Echo Hunter + Style vs. Identity Decoupler

- **Style vs. Identity Decoupler**:
  - Solves the classic problem where training a character or person bakes in the lighting, background, or photographic medium of the dataset.
  - Allows independent scaling sliders for **Identity Retention** (preserving facial anatomy, key features, and character likeness) vs. **Style Bleed Attenuation** (dampening cross-attention and text-encoder layers that carry color palette and medium bias).
  - Produces a purified LoRA that adopts whatever style is requested in the prompt.
- **Orthogonal Negative Vector & Ghost Repulsion**:
  - Eliminates unwanted traits, defective hands, or bad styling inherited from a donor LoRA or failed training checkpoint.
  - Uses high-dimensional Gram-Schmidt orthogonal projection: decomposes the LoRA weight matrices into parallel and orthogonal components relative to the bad vector, projecting the undesirable trait out of the model weight space.

---

## 21. LoRA Forensic Reverse-Engineering & De-Anonymizer

- **Architecture Fingerprinting**:
  - Forensically reconstructs the target base model architecture (FLUX.1-Dev/Schnell, SDXL 1.0, Pony Diffusion, SD 1.5, HunyuanVideo, Wan 2.1) directly from tensor key topologies and dimensional signatures, even when training metadata has been stripped.
- **Topology & Hyperparameter Reconstruction**:
  - Accurately recovers effective network rank ($dim$), network alpha ($\alpha$), and alpha/rank scaling ratios.
  - Extracts training hyperparameters (learning rates, UNet/Text Encoder split LRs, optimizer type, LR scheduler, total training steps, batch size, and resolution).
- **Trigger Word & Concept Recovery**:
  - Recovers trained concept keywords and frequent dataset tags embedded in headers or inferred via heuristic token analysis.
  - 1-click binding: instantly assigns recovered trigger words into the active Studio Session for immediate inference and testing.

---

## 22. LoRA Vehicle Chop-Shop: Standalone Multi-Model Grafting Studio (`/chop-shop`)

- **Standalone Studio Architecture**:
  - Accessible directly from the main sidebar under **Studio Pipeline** (`LoRA Chop-Shop`), operating independently from single-model diagnostic sessions.
  - Divided into 4 focused tabs: **The Garage & Donors**, **Anatomical Assembly**, **Block Matrix Overrides**, and **Bake & Synthesis**.
- **The Garage (Dynamic Multi-Donor Shelf)**:
  - Dynamically load unlimited donor `.safetensors` models.
  - Automatically identifies architecture, rank, Frobenius energy norm, dominant feature badges (*Facial Likeness & Anatomy*, *Lighting & Ambiance*, *Micro-Textures & Outfits*, etc.), and extracted trigger tags.
- **Anatomical & Style Assembly Bay**:
  - Modular visual assembly mapping human concepts directly to neural weight blocks:
    - 👤 **Chassis & Face**: Mid-block anatomical geometry, head shape, jawline, eye socket spacing (`MID00` / `double_blocks 6-12`).
    - 👁️ **Headlights & Eyes**: Iris pigmentation, catchlights, ocular reflections, pupil sharpness (`OUT09-OUT11` / `single_blocks 30-37`).
    - 💇 **Custom Paint & Hair**: Hairstyle flow, bangs, braid textures, and hair color projections.
    - 👗 **Upholstery & Armor**: Garments, jackets, accessories, lace, leather, and uniform styling (`OUT03-OUT06` / `double_blocks 13-18`).
    - 💡 **Engine & Glow**: Volumetric lighting, atmospheric grading, shadow warmth, and color temperature (`IN00-IN03` / `double_blocks 0-5`).
    - ⚡ **Detail Polish**: Photorealistic skin pores, wrinkles, and fine edge clarity (`OUT10-OUT11`).
    - 🔤 **Steering & Triggers**: Text encoder layers dictating prompt responsiveness (`lora_te` / `lora_clip`).
  - Independent blend multiplier sliders (0.0x to 2.0x) per component.
- **Advanced Block-by-Block Matrix Overrides**:
  - Dedicated table allowing power users to route specific U-Net or DiT transformer blocks to explicit donors with custom weights.
  - Overrides take precedence over high-level category cards.
- **Smart Auto-Craft Recipe**:
  - 1-click intelligent analysis: automatically evaluates donor Frobenius norms and assigns the optimal donor to each anatomical and aesthetic part.
- **SVD Matrix Re-Compression, TIES Denoising & ComfyUI Launch**:
  - Zero-training algebraic synthesis compiling delta weight matrices into clean, target-rank `.safetensors` files (Rank 8, 16, 32, or 64).
  - TIES consensus denoising to eliminate multi-vector interference and artifact bleed.
  - Real-time compilation console with progress tracking.
  - 1-click direct promotion to the active session and 1-click **Test in ComfyUI Studio** button.




