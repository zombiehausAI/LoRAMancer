# LoRAMancer 🪄

LoRAMancer is a production-grade, highly responsive desktop LoRA Manager, Configuration Cloner, and Training Orchestrator application targeting **.NET 10 (Blazor Hybrid MAUI Desktop)** on Windows, with deep **AMD ROCm / PyTorch** environment integration.

## Key Features

- **ComfyUI Environment Ingestion & AMD Provisioning**: Automatically detects or parses your ComfyUI setup and ROCm wheel sources to provision a robust dedicated `.venv` (Python 3.12+) equipped with AMD ROCm-compatible PyTorch binaries.
- **LoRA Library Manager & SafeTensors Inspector**: Ultra-fast header-only `.safetensors` binary parsing in C# without loading heavy model weights. Instant inspection of rank (dim), alpha, learning rates, optimizer, and base model architecture.
- **Clone Configuration & AMD Hardware Sanitizer**: Clones hyperparameters from donor LoRAs into AI-Toolkit and Kohya-compatible training configs, automatically translating CUDA-only settings (such as 8-bit optimizers or Flash Attention) into AMD ROCm-safe counterparts (`bf16`, `sdpa`, disk latent caching).
- **Interactive Training Orchestrator**: Async process runner with live terminal telemetry, real-time step counters, loss metrics, and cancellation controls.
- **Dual Plugin Architecture**: Extensible plugin system supporting:
  - **C# Plugins**: Compiled `.dll` modules loaded dynamically from `plugins/<plugin_name>/<plugin_name>.dll`.
  - **Python Plugins**: Independent Python plugins located in `plugins/<plugin_name>/`, each running inside its own isolated `.venv`.
- **Installer & Auto-Update Engine**: Native Windows installer with built-in version checking and automated update pipelines.

## Project Structure

```
LoRAMancer/
├── src/
│   ├── LoRAMancer.App/         # .NET 10 MAUI Blazor Hybrid Application
│   │   ├── Components/         # MudBlazor UI Components, Pages & Drawers
│   │   ├── Engines/            # SafeTensors parser, config builder, runners
│   │   ├── Models/             # Domain and configuration entities
│   │   └── Services/           # Provisioner, training, plugin & update services
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
- [Architecture Overview](file:///d:/repos/LoRAMancer/docs/ARCHITECTURE.md)
- [AMD ROCm & Python Environment Provisioning](file:///d:/repos/LoRAMancer/docs/AMD_ROCM_SETUP.md)
- [Plugin System Guide (C# & Python)](file:///d:/repos/LoRAMancer/docs/PLUGINS.md)
- [Installer & Auto-Update Specifications](file:///d:/repos/LoRAMancer/docs/INSTALLER_AND_UPDATES.md)

## Requirements

- **Operating System**: Windows 10/11 (x64)
- **.NET SDK**: .NET 10 SDK (`net10.0-windows10.0.19041.0`)
- **Python**: Python 3.12 (preferred for AMD ROCm 7.x PyTorch compatibility)
- **GPU**: AMD Radeon GPU with ROCm support (or CPU fallback)
- **Git**: Installed and available on `PATH`

## License

This project is licensed under the MIT License - see [LICENSE](file:///d:/repos/LoRAMancer/LICENSE) for details.
