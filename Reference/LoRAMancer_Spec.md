Act as a Principal .NET and Python Systems Architect. Scaffold a production-grade, highly responsive desktop LoRA Manager, Configuration Cloner, and Training Orchestrator application named "LoRAMancer" targeting .NET 10 (Blazor Hybrid / MAUI Desktop) on Windows.

### Architectural Overview & Coding Standards
* Target Platform: .NET 10 (Blazor Hybrid MAUI Desktop targeting net10.0-windows10.0.19041.0 or later).
* UI System & Aesthetics: MudBlazor + Tailwind CSS conventions. Dark-mode-first, minimalist, modern, highly polished, responsive, and ergonomic.
* UX Rule: Avoid manual raw path text fields. Use native Windows file and directory pickers (`IFilePicker` and folder dialogs) for choosing files, donor LoRAs, and datasets, paired with drag-and-drop support.
* Coding Standards: Strict C# async discipline with cancellation tokens, one-type-per-file, guard clauses, K&R/OTBS brace style throughout. Clean service abstractions; no direct business logic in Razor markup. Strict K&R Formatting in all C# and Javascript files.
* Python Interop: Decoupled Python execution engine using `System.Diagnostics.Process` to run inside a dedicated virtual environment configured with AMD ROCm-compatible PyTorch wheels.

### Core Features to Implement

1. ComfyUI Environment Ingestion & AMD Venv Provisioning:
   - Provide an "Environment Setup" service that takes the user's existing ComfyUI startup/runner script or ComfyUI Python directory.
   - Parse the script to extract the exact Python executable, local wheel directories, or ROCm wheel paths.
   - Automatically provision a dedicated `.venv` for LoRAMancer, install matching AMD-compiled torch/torchvision/torchaudio wheels, and install AI-Toolkit training dependencies with `--no-deps` to protect the AMD binaries.

2. LoRA Library Manager & Metadata Inspector:
   - Drag-and-drop zone directly in the Blazor interface for `.safetensors` files or directories.
   - In-app file/folder browser with native picker buttons (`Browse LoRA...`, `Browse Library...`).
   - Deep metadata parser: Binary stream header parser in C# to inspect `.safetensors` metadata without loading model weights into memory.
   - Cleanly display donor hyperparameters: Rank (dim), Alpha, Learning Rate, Optimizer, Base Model tag, Epochs, and Step counts.

3. "Clone Configuration" & AMD Hardware Sanitizer:
   - Clone hyperparameter matrix from any donor LoRA.
   - Wizard/Drawer UI to supply:
     * New run name
     * New dataset folder path (via native directory picker)
     * New trigger word & sample prompts
     * Target base model architecture (FLUX.1, SDXL, Chroma/Pony, etc.)
   - AMD Windows ROCm Safety Engine (hardcoded translation rules):
     * Translate NVIDIA-specific 8-bit optimizers (`adamw8bit`, `PagedAdamW`, `Lion8bit`) to safe variants (`adamw`, `adamw_bf16`).
     * Enforce native Scaled Dot-Product Attention (`sdpa`) instead of `flash_attn_2` or `xformers`.
     * Disable quantization (`quantize: false`) and enable disk/RAM latent caching (`cache_latents_to_disk: true`).
     * Enforce `dtype: bf16`.
   - Output: Generates valid AI-Toolkit training `.yaml` configs and Kohya-compatible configs.

4. Training Process Orchestrator:
   - Async process runner to launch AI-Toolkit `run.py` in the background.
   - Live streaming stdout/stderr into a polished, auto-scrolling log console component with real-time step progress and loss monitoring.

### Deliverables Required
1. Solution Layout & .csproj:
   - Clean architecture: `LoRAMancer.App` with folders `Components`, `Services`, `Models`, and `Engines`.
   - Complete `.csproj` referencing MudBlazor, .NET 10 MAUI/Blazor Hybrid, and necessary system packages.
2. Core C# Services:
   - `SafeTensorsMetadataReader.cs`: Standalone C# utility to parse header bytes from `.safetensors` files.
   - `AmdVenvProvisioner.cs`: Service to parse ComfyUI launch scripts and provision the ROCm-compatible venv.
   - `AiToolkitConfigBuilder.cs`: Service to translate donor metadata into AMD-sanitized YAML configs.
   - `TrainingRunnerService.cs`: Process manager handling async standard I/O streaming, pause/cancel, and live telemetry.
3. Modern Razor Components:
   - `LoraManagerDashboard.razor`: Modern, responsive dashboard with drag-and-drop zone, library grid, drawer inspector, native picker dialogs, and the "Clone to Config" modal.
   - `TrainingConsole.razor`: Live terminal log viewer with real-time step counters and process status badges.

Generate clean, idiomatic, and production-ready code files implementing this architecture.

Strictly adhere to the following K&R Formatting standards for ALL C# and Javascript files:
1. Opening braces on the same line as the statement.
2. Closing braces on a new line, indented to match the statement.
3. No extra whitespace padding around braces.
4. Braces required for all single-line statements.
5. No unnecessary vertical whitespace between related statements.