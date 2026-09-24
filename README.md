# LoRAMancer 🪄

LoRAMancer is a production-grade, highly responsive desktop LoRA Manager, Configuration Cloner, and Training Orchestrator application targeting **.NET 10 (Blazor Hybrid MAUI Desktop)** on Windows, with deep **AMD ROCm / PyTorch** environment integration.

## Key Features

- **Extensible Model Architecture Support**: Native presets and automatic header inference for **FLUX.1** (dev/schnell), **PonyXL V6** (SDXL), **Illustrious-XL**, and standard SDXL/SD1.5, backed by an extensible registry (`ModelArchitectureRegistry`) for future architectures.
- **AI-Toolkit Automated Setup & Git Updates**: One-click automated setup and Git synchronization (`AiToolkitSetupService`) that clones the official AI-Toolkit repository, tracks active commits, pulls updates recursively, and updates dependencies into the dedicated AMD ROCm/PyTorch `.venv` with `--no-deps` protection.
- **HuggingFace & Civitai API Integrations**:
  - **HuggingFace**: Configurable user access tokens with live authentication check (`whoami-v2`) and gated model access validation (e.g. for downloading `black-forest-labs/FLUX.1-dev` weights).
  - **Civitai**: Instant model identification via SHA256 hash lookup (`CivitaiService`) to retrieve official model names, thumbnails, trained trigger words, sample prompts, and Civitai links.
- **Admin Settings & User Privacy**: Full in-app admin console (`/settings`) enabling users to update AMD PyTorch wheel repository URLs (e.g. for newer ROCm 7.3+ releases) with live URL reachability probing. All user settings, profile data, and API keys are stored isolated in the user's home profile (`~/.loramancer/settings.json`), keeping the repository clean of any user-identifiable data.
- **ComfyUI Environment Ingestion & AMD Provisioning**: Automatically detects or parses your ComfyUI setup and ROCm wheel sources to provision a robust dedicated `.venv` (Python 3.12+) equipped with AMD ROCm-compatible PyTorch binaries.
- **LoRA Library Manager & SafeTensors Inspector**: Ultra-fast header-only `.safetensors` binary parsing in C# without loading heavy model weights. Instant inspection of rank (dim), alpha, learning rates, optimizer, and base model architecture.
- **Clone Configuration & AMD Hardware Sanitizer**: Clones hyperparameters from donor LoRAs into AI-Toolkit and Kohya-compatible training configs, automatically translating CUDA-only settings (such as 8-bit optimizers or Flash Attention) into AMD ROCm-safe counterparts (`bf16`, `sdpa`, disk latent caching).
- **Interactive Training Orchestrator & Civitai-Style Wizard**: Streamlined setup with subject-type presets (Character, Style, Concept, Clothing), dataset health auditing (missing captions, low-res warnings), live hardware estimators (total steps, VRAM requirement vs detected AMD GPU, output file size, and training time), and zero-crash AMD ROCm pre-flight verification. Supports raw image folders and automatic ZIP archive unzipping.
- **Dual Plugin Architecture & Built-in Ollama Vision Tagger**: Extensible plugin system supporting C# DLLs and Python plugins running in isolated environments. Includes a default **Ollama Vision LoRA Tagger** plugin to automatically caption datasets with local vision models (`llama3.2-vision`, `llava`) with trigger prefixing and blacklist filters.
- **Installer & Auto-Update Engine**: Native Windows installer with built-in version checking and automated update pipelines.

## Project Structure

```
LoRAMancer/
├── src/
│   ├── LoRAMancer.App/         # .NET 10 MAUI Blazor Hybrid Application
│   │   ├── Components/         # MudBlazor UI Components, Pages & Drawers
│   │   ├── Engines/            # SafeTensors parser, config builder, runners, model registry
│   │   ├── Models/             # Domain and configuration entities
│   │   └── Services/           # Provisioner, training, AI-Toolkit, Civitai, HF, settings & plugins
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
- [Training Wizard & Estimators](file:///d:/repos/LoRAMancer/docs/TRAINING_WIZARD.md)
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
