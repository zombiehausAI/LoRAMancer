# LoRAMancer Architecture

## High-Level Architecture

LoRAMancer is constructed as a .NET 10 MAUI Blazor Hybrid application for Windows with cross-platform network capability. It combines the UI performance and rich component ecosystem of MudBlazor with native Windows file dialogs, background process orchestration, an embedded Kestrel web server, and deep Python/PyTorch virtual environment management across NVIDIA, AMD, Intel, and CPU.

```
+-----------------------------------------------------------------------------------------+
|                                      LoRAMancer UI                                      |
|    Blazor Hybrid Desktop (MudBlazor, Dark Mode) / Embedded Web UI (Linux, Mac, Tablet)   |
+-----------------------------------------------------------------------------------------+
                                             |
       +--------------------+----------------+--------------------+--------------------+
       |                    |                                     |                    |
+------v--------+    +------v--------------+               +------v--------+    +------v--------+
| Meta & Config |    | Environment & Venv  |               | Server & Host |    | Vault & Log   |
| - SafeTensors |    | - Multi-Vendor GPU  |               | - Kestrel API |    | - LoraHistory |
| - Registry    |    |   NVIDIA / AMD      |               | - SSE Stream  |    | - Non-Expire  |
| - Sanitizer   |    |   Intel / CPU       |               | - Remote Node |    | - 1-Click     |
| - AI-Toolkit  |    | - Wheel Manager     |               | - Web Client  |    |   Cloning     |
+---------------+    +---------------------+               +---------------+    +---------------+
       |                    |                                     |                    |
+------v--------------------v-------------------------------------v--------------------v--------+
|                                    Execution Subsystems                                       |
| - ProcessRunner (AI-Toolkit & Kohya async process and telemetry streaming)                    |
| - PluginManagerService (C# assemblies and isolated Python .venvs with hardware PyTorch)       |
| - DatasetInspectorService (ZIP extraction, caption auditing, and Ollama Vision tagging)       |
| - AutoUpdateService (Version check, binary delta install)                                     |
+-----------------------------------------------------------------------------------------------+
```

## Key Architectural Principles

1. **One-Type-Per-File**: Each C# model, engine, service, or interface resides in its own discrete file.
2. **K&R (OTBS) Code Formatting**: Opening brace on the statement line, closing brace matching statement indentation, braces required on all single-line blocks.
3. **Decoupled Python Execution**: LoRAMancer does not embed Python in-process; it manages isolated virtual environments (`.venv`) using `System.Diagnostics.Process` to protect memory safety, facilitate native GPU DLL loading, and support independent Python upgrades.
4. **Universal Hardware Acceleration**: Automatically detects host graphics hardware via WMI/driver probes and provisions the exact hardware-matched PyTorch distribution:
   - **NVIDIA**: CUDA 12.4 index (`download.pytorch.org/whl/cu124`).
   - **AMD**: ROCm 7.2.1/7.3+ wheels with automated Windows shared object stubs.
   - **Intel**: Intel XPU index (`download.pytorch.org/whl/xpu`).
   - **CPU**: CPU-optimized distribution (`download.pytorch.org/whl/cpu`).
5. **Distributed & Remote Execution**:
   - **Host Mode**: Runs an embedded Kestrel server (`http://0.0.0.0:8420`) providing a web control center for Linux/Mac browsers and a REST/SSE API for desktop clients.
   - **Client Mode**: Secondary Windows laptops running LoRAMancer can dispatch training jobs and stream real-time loss metrics from an AI PC over LAN or Tailscale.
6. **Permanent LoRA History & Vault (`LoraHistoryService`)**: Maintains an unexpiring local ledger (`~/.loramancer/history/`) of all trained models, trigger words, hyperparameter matrices, and loss curves with one-click configuration cloning.
7. **Zero Weight Loading SafeTensors Parser**: Reads only the 8-byte header length and JSON payload of `.safetensors` files directly from file streams, parsing LoRA metadata in sub-milliseconds without touching tensor weights.
8. **Extensible Model Architecture System (`ModelArchitectureRegistry`)**: Decoupled registry providing presets and detection heuristics for current and future diffusion architectures (FLUX.1, SDXL, Illustrious, PonyXL, Chroma).

