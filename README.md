# LoRAMancer 🪄

LoRAMancer is a production-grade, highly responsive desktop LoRA Manager, Configuration Cloner, and Training Orchestrator application targeting **.NET 10 (Blazor Hybrid MAUI Desktop)** on Windows, with cross-platform **Remote Web UI** capabilities and universal hardware acceleration across **NVIDIA CUDA**, **AMD ROCm**, **Intel XPU**, and **CPU**.

## Key Features

- **Universal Multi-Vendor GPU Acceleration**: Automated hardware detection (`AmdVenvProvisioner`) that queries your installed graphics adapter and provisions an isolated Python 3.12 `.venv` with the exact hardware-matched PyTorch distribution:
  - **AMD Radeon**: Official ROCm 7.2.1/7.3+ wheels (`repo.radeon.com`) + `rocm_sdk` with automated Windows library stubs.
  - **NVIDIA GeForce / RTX**: Official PyTorch CUDA 12.4 index (`download.pytorch.org/whl/cu124`).
  - **Intel Arc / Xe**: Official PyTorch Intel XPU index (`download.pytorch.org/whl/xpu`).
  - **CPU Fallback**: Generic CPU-optimized wheels (`download.pytorch.org/whl/cpu`).
- **Remote Training & Cross-Platform Web UI**: Embedded Kestrel network server (`http://0.0.0.0:8420`) running on your AI PC:
  - **Browser Web Access (Zero Install)**: Open any web browser on a Linux PC, Mac, iPad, or smartphone to configure runs, upload dataset ZIPs, monitor live loss sparklines, and view terminal logs.
  - **Windows Desktop Remote Client**: Connect a secondary Windows laptop/PC running LoRAMancer to your AI PC over LAN or VPN (e.g. Tailscale) with live Server-Sent Events (SSE) telemetry.
- **Permanent LoRA Training History & Vault**: A Civitai-style local ledger (`/history`) cataloguing every trained model, trigger words, hyperparameter snapshots (rank, alpha, learning rate, optimizer, steps), final loss, and training duration. Unlike cloud services that purge models after 30 days, LoRAMancer records **never expire** until you explicitly remove them, with 1-click **Clone Config** to iterate on versions.
- **AI-Toolkit Automated Setup & Git Updates**: One-click automated setup and Git synchronization (`AiToolkitSetupService`) that clones the official AI-Toolkit repository, tracks active commits, pulls updates recursively, and updates dependencies into your hardware-matched `.venv` with `--no-deps` protection.
- **Dual Plugin Architecture & Built-in Ollama Vision Tagger**: Extensible plugin system supporting C# DLLs and Python plugins running in isolated environments. Includes a default **Ollama Vision LoRA Tagger** plugin supporting local, remote LAN, datacenter, and cloud vision models (`llama3.2-vision`, `llava`, `minicpm-v`, `qwen2-vl`) with custom Bearer tokens and automated dataset ZIP unzipping.
- **LoRA Library Manager & SafeTensors Inspector**: Ultra-fast header-only `.safetensors` binary parsing in C# without loading heavy model weights. Instant inspection of rank (dim), alpha, learning rates, optimizer, and base model architecture.
- **Hardware-Aware Configuration Sanitizer**: Clones hyperparameters from donor LoRAs into AI-Toolkit and Kohya-compatible training configs, automatically translating CUDA-only settings (such as 8-bit optimizers or Flash Attention) into safe equivalents when targeting AMD ROCm or CPU.
- **Interactive Training Orchestrator & Civitai-Style Wizard**: Streamlined setup with subject-type presets (Character, Style, Concept, Clothing), dataset health auditing (missing captions, low-res warnings), live hardware estimators (total steps, VRAM requirement vs detected GPU, output file size, and training time), and zero-crash pre-flight verification.
- **HuggingFace & Civitai API Integrations**: Instant model hash identification and Hugging Face token verification for gated model downloads (such as `black-forest-labs/FLUX.1-dev`).
- **Admin Settings & User Privacy**: All user profile info, API keys, credentials, and custom wheel URLs are stored isolated in the user's home profile (`~/.loramancer/`), keeping the codebase clean of user-identifiable data.

## Project Structure

```
LoRAMancer/
├── src/
│   ├── LoRAMancer.App/         # .NET 10 MAUI Blazor Hybrid & Embedded Server Application
│   │   ├── Components/         # MudBlazor UI Components, Pages, Drawers & Dialogs
│   │   ├── Engines/            # SafeTensors parser, config builder, runners, model registry
│   │   ├── Models/             # Domain, settings, telemetry & history entities
│   │   └── Services/           # Server, history, provisioner, AI-Toolkit, HF, Civitai & plugins
│   ├── LoRAMancer.PluginSdk/   # C# Plugin contract & interface library
│   └── plugins/                # Plugin directory (C# DLLs and Python venvs)
├── installer/                  # Packaging & auto-update scripts
├── docs/                       # Detailed architectural documentation
├── Reference/                  # Specifications & ComfyUI reference scripts
├── .gitignore
├── LICENSE
└── README.md
```

## Documentation

For comprehensive guides and technical specifications, refer to:
- [Universal GPU & Environment Provisioning](file:///d:/repos/LoRAMancer/docs/GPU_AND_ENVIRONMENT_SETUP.md)
- [Remote Training & Web UI Architecture](file:///d:/repos/LoRAMancer/docs/REMOTE_TRAINING.md)
- [LoRA Training History & Vault](file:///d:/repos/LoRAMancer/docs/HISTORY_AND_VAULT.md)
- [Training Wizard & Estimators](file:///d:/repos/LoRAMancer/docs/TRAINING_WIZARD.md)
- [Architecture Overview](file:///d:/repos/LoRAMancer/docs/ARCHITECTURE.md)
- [Plugin System Guide (C# & Python)](file:///d:/repos/LoRAMancer/docs/PLUGINS.md)
- [Installer & Auto-Update Specifications](file:///d:/repos/LoRAMancer/docs/INSTALLER_AND_UPDATES.md)

## Requirements

- **Operating System**: Windows 10/11 (x64) for desktop host; any OS with a modern browser for Web UI client
- **.NET SDK**: .NET 10 SDK (`net10.0-windows10.0.19041.0`)
- **Python**: Python 3.12 (preferred for PyTorch & ROCm compatibility)
- **GPU Acceleration**: NVIDIA GeForce / RTX (CUDA), AMD Radeon (ROCm), Intel Arc / Xe (XPU), or CPU fallback
- **Git**: Installed and available on `PATH`

## License

This project is licensed under the MIT License - see [LICENSE](file:///d:/repos/LoRAMancer/LICENSE) for details.

