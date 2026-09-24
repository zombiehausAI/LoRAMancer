# LoRAMancer 🪄

**LoRAMancer** is a production-grade, highly responsive desktop LoRA Manager, Configuration Cloner, and Training Orchestrator application targeting **.NET 10 (Blazor Hybrid MAUI Desktop)** on Windows, with cross-platform **Remote Web UI** capabilities and universal hardware acceleration across **NVIDIA CUDA**, **AMD ROCm**, **Intel XPU**, and **CPU**.

## What LoRAMancer Does

LoRAMancer streamlines the entire LoRA lifecycle for AI creators:

1. **Multi-Library Management & Visual Browsing**: Index thousands of `.safetensors` models into an instant, SQLite-backed library (`~/.LoRAMancer/loras.db`). Organize models across independent named libraries, convert subfolders into libraries with one click, search globally, and view auto-discovered companion preview thumbnails with zero UI freezing.
2. **Hyperparameter Recipe Cloning**: Inspect and borrow proven mathematical settings (rank, alpha, learning rates, optimizer, epochs) from any donor LoRA to pre-seed your next training run while preserving your own unique dataset and identity.
3. **Automated AI-Toolkit Orchestration**: Streamlined Civitai-style wizard with built-in presets (Characters, Styles, Concepts, Clothing), dataset health auditing, and live hardware estimators.
4. **Permanent Training History & Vault**: Keep permanent records of all training runs, prompt triggers, loss curves, and configurations without cloud retention limits.
5. **Universal Hardware Provisioning**: Automated detection and provisioning of hardware-matched PyTorch environments for AMD ROCm, NVIDIA CUDA, Intel XPU, and CPU.
6. **Remote Control & Public Serving**: Embedded Kestrel network server and Cloudflare Quick Tunnels to monitor training and manage LoRAs from any browser or mobile PWA.
7. **In-App Document Viewer & Theme Engine**: Read full system documentation directly inside the application, and customize your workspace with high-contrast dark themes and importable/exportable JSON palettes.

## Documentation Index

Comprehensive guides and architectural specifications are located in the [`docs/`](docs/) folder and can also be viewed directly inside the application under the **Documentation** tab:

- 📖 [Comprehensive Features & Subsystems Reference](docs/FEATURES_OVERVIEW.md)
- 🎛️ [LoRA Training Hyperparameters & Options Guide](docs/HYPERPARAMETER_GUIDE.md)
- 📚 [LoRA Library & Multi-Library Browser Guide](docs/LORA_LIBRARY_BROWSER.md)
- 🧙 [Civitai-Style Training Wizard & Estimators](docs/TRAINING_WIZARD.md)
- ⚡ [Universal GPU & Environment Provisioning (ROCm / CUDA / Intel / CPU)](docs/GPU_AND_ENVIRONMENT_SETUP.md)
- 🔴 [AMD ROCm Dedicated Windows Setup](docs/AMD_ROCM_SETUP.md)
- 🌐 [Remote Training, PWA Web App & Public Internet Serving](docs/REMOTE_TRAINING.md)
- 🏛️ [Permanent LoRA Training History & Vault](docs/HISTORY_AND_VAULT.md)
- 🔌 [Plugin System Guide (C# & Python Extensions)](docs/PLUGINS.md)
- 🏗️ [Architecture Overview & Core Data Flows](docs/ARCHITECTURE.md)
- 📦 [Installer & Packaging Specifications](docs/INSTALLER_AND_UPDATES.md)

## Requirements & Quickstart

- **Operating System**: Windows 10/11 (x64) for desktop host; modern web browser for remote clients.
- **.NET SDK**: .NET 10 SDK (`net10.0-windows10.0.19041.0`).
- **Python**: Python 3.12 (automatically managed or local).
- **GPU Acceleration**: NVIDIA GeForce/RTX, AMD Radeon, Intel Arc/Xe, or CPU fallback.

### Building & Running

```powershell
# Restore dependencies and build
dotnet build src/LoRAMancer.App/LoRAMancer.App.csproj

# Run tests
dotnet test tests/LoRAMancer.Tests/LoRAMancer.Tests.csproj

# Build, package installer (Inno Setup) and portable zip
pwsh -File .\Build-And-Package.ps1
```

## License

This project is licensed under the MIT License - see [LICENSE](LICENSE) for details.

---

*Assisted by AI, for AI.*

