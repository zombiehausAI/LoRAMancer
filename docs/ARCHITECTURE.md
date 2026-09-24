# LoRAMancer Architecture

## High-Level Architecture

LoRAMancer is constructed as a .NET 10 MAUI Blazor Hybrid application for Windows. It combines the UI performance and rich component ecosystem of MudBlazor with native Windows file dialogs, background process orchestration, and deep Python/PyTorch virtual environment management.

```
+-------------------------------------------------------------------------+
|                              LoRAMancer UI                              |
|          Blazor Hybrid (MudBlazor, Dark Mode, Async UX, Native Pickers)  |
+-------------------------------------------------------------------------+
                                    |
      +-----------------------------+----------------------------+
      |                             |                            |
+-----v---------------+   +---------v------------+   +-----------v-----------+
| Metadata & Config   |   | Environment & Venv   |   | Plugin Engine         |
| - SafeTensorsReader |   | - AmdVenvProvisioner |   | - C# (.NET Assembly)  |
| - ConfigBuilder     |   | - ROCm Wheel Manager |   | - Python (Isolated)   |
| - ModelRegistry     |   | - ComfyUI Ingestor   |   |   each with own .venv |
| - Sanitizer (AMD)   |   |                      |   |                       |
+---------------------+   +----------------------+   +-----------------------+
      |                             |                            |
+-----v-----------------------------v----------------------------v-----------+
|                          Execution Subsystems                              |
| - ProcessRunner (AI-Toolkit & Kohya async process streaming)               |
| - AutoUpdateService (Version check, binary delta install)                  |
| - Python Subprocess IPC (STDIO / JSON RPC)                                 |
+----------------------------------------------------------------------------+
```

## Key Architectural Principles

1. **One-Type-Per-File**: Each C# model, engine, service, or interface resides in its own discrete file.
2. **K&R (OTBS) Code Formatting**: Opening brace on the statement line, closing brace matching statement indentation, braces required on all single-line blocks.
3. **Decoupled Python Execution**: LoRAMancer does not embed Python in-process; it manages isolated virtual environments (`.venv`) using `System.Diagnostics.Process` to protect memory safety, facilitate native AMD DLL loading, and support independent Python version upgrades.
4. **Zero Weight Loading SafeTensors Parser**: Reads only the 8-byte header length and JSON payload of `.safetensors` files directly from file streams, parsing LoRA metadata in sub-milliseconds without touching tensor weights.
5. **Hardware-Aware Configuration Sanitization**: Automatically translates NVIDIA-centric training configurations into AMD ROCm-compatible equivalents (`bf16` precision, `sdpa` attention, disk latent caching, and standard `adamw`).
6. **Extensible Model Architecture System (`ModelArchitectureRegistry`)**: Decoupled registry providing presets and detection heuristics for current and future diffusion architectures:
   - **FLUX.1** (`flux_1_dev`, `flux_1_schnell`): Flowmatch scheduling, dedicated linear/alpha dimensions, rectified flow text encoders.
   - **PonyXL V6** (`pony_xl_v6`): SDXL-based architecture with anime score tags (`score_9, score_8_up...`), euler scheduling, dual text encoders.
   - **Illustrious-XL** (`illustrious_xl`): SDXL anime/illustration architecture variant with high-resolution presets.
   - **Stable Diffusion XL & 1.5** (`sdxl_1_0`, `sd_1_5`): Standard SD architectures.
   - **Dynamic Registration**: Plugins and user extensions can call `registry.Register(new ModelArchitectureInfo { ... })` at runtime without altering core engine logic.
