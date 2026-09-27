# LoRAMancer 🪄

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D4)](#requirements--quickstart)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-brightgreen)](LICENSE)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/zombiehaus)

**LoRAMancer** is a production-grade, highly responsive desktop LoRA Manager, Configuration Cloner, and Training Orchestrator application targeting **.NET 10 (Blazor Hybrid MAUI Desktop)** on Windows, with cross-platform **Remote Web UI** capabilities and universal hardware acceleration across **NVIDIA CUDA**, **AMD ROCm**, **Intel XPU**, and **CPU**.

> [!NOTE]
> **Project Context & Disclaimer**:
> - **Solo Developer Side Project**: LoRAMancer is an independent side project created and maintained by a single developer. Due to full-time work and outside commitments, I'm unable to dedicate full-time hours to it—updates, bug fixes, and maintenance happen as personal time permits. Your patience and understanding are greatly appreciated!
> - **Proof of Concept**: This application, setup scripts, utilities, and technical documentation represent an early **proof of concept** created to overcome current Windows AMD ROCm and cross-vendor hardware hurdles. While these setups have been tested and verified on local AMD hardware (such as Radeon RX 7000 and RX 9000 series GPUs), hardware configurations, Windows driver versions, and environments can differ significantly—what works seamlessly in one test environment may require adjustments or further testing on yours. Community feedback, issue reports, and real-world testing are warmly welcomed!
> - **Performance Expectation (AMD vs. NVIDIA)**: Native Windows AMD ROCm training may currently be slower than native NVIDIA CUDA training due to Windows ROCm driver maturity and kernel optimizations. However, it is **fully working natively on Windows** without requiring dual-boot Linux configurations, heavy WSL2 virtual machines, or restrictive DirectML fallbacks.
> - **AI-Assisted Development**: This application, its runtime patches, and documentation were created with AI assistance. In the spirit of complete transparency: if you prefer not to use AI-assisted code, please feel free to pass on this project and wait for official upstream Windows ROCm fixes from the respective project maintainers.

---

## What LoRAMancer Does

LoRAMancer streamlines the entire LoRA lifecycle for AI creators:

1. **Multi-Library Management, Organizer & Collections**: Index thousands of `.safetensors` models into an instant, SQLite-backed library (`~/.LoRAMancer/loras.db`). Organize models across independent named libraries, convert subfolders into libraries with one click, physically move LoRAs (and companion previews) across libraries with "Move to Library...", group models into arbitrary virtual user-defined Collections without moving files, search globally, and view auto-discovered companion preview thumbnails with zero UI freezing.
2. **Hyperparameter Recipe Cloning**: Inspect and borrow proven mathematical settings (rank, alpha, learning rates, optimizer, epochs) from any donor LoRA to pre-seed your next training run while preserving your own unique dataset and identity.
3. **Automated AI-Toolkit Orchestration & Training Queue**: Streamlined Easy Use wizard with built-in presets (Characters, Styles, Concepts, Clothing), dataset health auditing, live hardware estimators, sequential single-GPU training queue, and background execution with desktop-wide single-instance protection.
4. **Permanent Training History & Vault**: Keep permanent records of all training runs, prompt triggers, loss curves, and configurations without cloud retention limits.
5. **Universal Hardware Provisioning**: Automated detection and provisioning of hardware-matched PyTorch environments for AMD ROCm, NVIDIA CUDA, Intel XPU, and CPU.
6. **Remote Control & Public Serving**: Embedded Kestrel network server and Cloudflare Quick Tunnels to monitor training and manage LoRAs from any browser or mobile PWA with 1:1 desktop parity (Curate Studio with in-place Ollama vision auto-tagging, Easy Use Wizard, Chop-Shop anatomical grafting, Diagnostic Lab, and ComfyUI testing).
7. **In-App Document Viewer & Theme Engine**: Read full system documentation directly inside the application, and customize your workspace with high-contrast dark themes and importable/exportable JSON palettes.
8. **One-Click ComfyUI Test Studio**: Automatically generate prompt graphs for FLUX.1, SDXL, and SD 1.5, deploy LoRAs locally via symlinks (or remote LAN/WAN multipart uploads), stream WebSocket sampling progress, and render test outputs directly inside LoRAMancer.
9. **Dataset Curator & Batch Caption Studio**: Tag frequency analysis, mass find/replace across `.txt` captions, trigger word prefixing/suffixing with automatic backups, and aspect ratio bucketing distribution auditing.
10. **LoRA Surgery Studio (SVD, Merging & Extraction)**: Compress high-rank LoRAs down to compact ranks (16/32) using truncated SVD, merge pairs of LoRAs with linear interpolation, and extract instant ~30MB LoRAs directly from full fine-tuned checkpoints ($\Delta W = W_{ft} - W_{base}$) with zero training.
11. **LoRA Gene Therapy (Layer Energy Heatmap & Outlier Pruning)**: Calculate Frobenius weight norms ($\| \Delta W \|_F$) across every transformer/UNet block, detect toxic outliers causing color burn or style bleed, and surgically attenuate or zero-out defective layers without retraining.
12. **LoRA Visual Diff Inspector**: Compare two LoRAs side-by-side with high-dimensional weight angle cosine similarity drift across every tensor, identifying subtle fine-tuning drift vs heavy divergence alongside recipe hyperparameter diffs.
13. **Semantic Collision Radar & Anti-Bleed Token Synthesizer**: Detect lexical collisions between proposed trigger words and the base model's CLIP/T5 vocabulary priors to prevent concept bleed; synthesize zero-collision phonetic tokens (e.g. `ohwx`, `v9x`) and cleanse entire datasets in one click.
14. **Automated AI Benchmark Matrix (Sweet Spot Finder)**: Automatically evaluate multiple epoch checkpoints across a 4-part visual battery (likeness, style flexibility, bleed stress, composition) via ComfyUI, plot the learning curve, and pinpoint the optimal checkpoint before overfitting occurs.
15. **TensorBoard & Pre-Flight OOM Protection**: Dry-run hardware estimation comparing model requirements against detected GPU VRAM to prevent driver crashes, paired with 1-click TensorBoard launch.
16. **5-Stage Creative Studio Pipeline**: Cohesive production studio flow spanning Curate (`/curate`), Train (`/training`), Lab (`/lab`), Test (`/test`), and Vault (`/`) with auto-persisted concept session state.
17. **SVD Spectral Energy Decay & Overbake Radar**: SVD analysis measuring singular value monopolization ($\sigma_1$), Frobenius norms, and Shannon spectral entropy to diagnose overbaking and auto-audit multi-epoch checkpoints.
18. **LoRA Ghost Hunter & Style Decoupler**: Gram-Schmidt orthogonal repulsion to purge bad traits/artifacts from donor models, and cross-attention attenuation to isolate character identity from art style bleed.
19. **Forensic Reverse-Engineering & De-Anonymizer**: Reconstructs base architectures, effective rank/alpha scaling ratios, trained trigger keywords, and training recipes from raw weights.
20. **Multi-Provider Metadata Lookup & Gap-Filling**: Queries Civitai (SHA-256), Hugging Face Hub (model cards, tags, and README `instance_prompt`), and Danbooru (tag frequency classification) to enrich LoRAs with trigger words, previews, and descriptions. Completely open and free by default, with optional API keys in Settings to unlock private models and higher rate limits.

## Documentation Index

Comprehensive guides and architectural specifications are located in the [`docs/`](docs/) folder and can also be viewed directly inside the application under the **Documentation** tab:

- 📖 [Comprehensive Features & Subsystems Reference](docs/FEATURES_OVERVIEW.md)
- 🎛️ [LoRA Training Hyperparameters & Options Guide](docs/HYPERPARAMETER_GUIDE.md)
- 📚 [LoRA Library & Multi-Library Browser Guide](docs/LORA_LIBRARY_BROWSER.md)
- 🧙 [Easy Use Training Wizard & Estimators](docs/TRAINING_WIZARD.md)
- ⚡ [Universal GPU & Environment Provisioning (ROCm / CUDA / Intel / CPU)](docs/GPU_AND_ENVIRONMENT_SETUP.md)
- 🔴 [AMD ROCm Dedicated Windows Setup](docs/AMD_ROCM_SETUP.md)
- 🛠️ [AMD ROCm Windows Runtime Patches & Troubleshooting Log](docs/AMD_WINDOWS_ROCM_PATCHES.md)
- 🚀 [Standalone AMD ROCm AI-Toolkit Setup & Patch Guide](docs/AMD_AI_TOOLKIT_STANDALONE.md)
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

### Standalone AMD AI-Toolkit Utility (No GUI Required)

For AMD creators and developers who wish to train LoRAs directly from the command line using `ai-toolkit` without running the full LoRAMancer desktop application:

```powershell
# Automated environment setup and custom patching for AMD ROCm on Windows
pwsh -File .\Utilities\Setup-AmdAiToolkit.ps1 -TargetDir "C:\AI\ai-toolkit"
```
See the [Standalone AMD ROCm AI-Toolkit Technical Guide](docs/AMD_AI_TOOLKIT_STANDALONE.md) for full patch specifications and troubleshooting.

## Development Transparency

This project and its accompanying utilities were developed with AI assistance (*Assisted by AI, for AI*). We believe in open transparency: if you prefer not to use or engage with AI-assisted software, please be aware before installing, evaluating, or running this application.

## Acknowledgements & Credits

LoRAMancer stands on the shoulders of incredible open-source projects and communities:

- **[ComfyUI](https://github.com/comfyanonymous/ComfyUI)** — The powerful, modular node-based visual inference engine powering interactive test canvas generation, live sampling streams, and automated benchmark matrices.
- **[ai-toolkit](https://github.com/ostris/ai-toolkit)** (by ostris) — The state-of-the-art training suite orchestrating LoRAMancer's multi-architecture fine-tuning, training queues, and LoRA surgery extraction pipelines.
- **[Ollama](https://github.com/ollama/ollama)** — Fast, lightweight local LLM and vision model runtime powering local image captioning and concept tagging in the Dataset Curator Studio.
- **[PyTorch](https://github.com/pytorch/pytorch)** & **[AMD ROCm](https://rocm.docs.amd.com/)** — Universal deep learning framework and compute platform providing hardware acceleration across AMD Radeon, NVIDIA CUDA, Intel Arc, and CPU.
- **[MudBlazor](https://mudblazor.com/)** — The Material Design component framework powering LoRAMancer's high-contrast desktop interface and responsive remote web client.
- **[Civitai](https://civitai.com/)** — Community model repository whose public API provides automated metadata enrichment, trigger keyword detection, and preview caching.
- **[Hugging Face Hub](https://huggingface.co/)** — Universal open-source machine learning hub used for model card and README `instance_prompt` trigger word extraction and weights discovery.
- **[Danbooru](https://danbooru.donmai.us/)** — Open anime/art tagging platform whose public classification API isolates character, series, and artist tokens from raw `ss_tag_frequency` training headers.
- **[Cloudflare Quick Tunnels](https://github.com/cloudflare/cloudflared)** — Secure zero-configuration tunnels providing encrypted remote studio access across LAN and WAN without manual port forwarding.

## Support & Contributions

If you find LoRAMancer helpful for your AI workflows, training setups, or Windows ROCm research, consider supporting ongoing development:

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/zombiehaus)

## License

This project is licensed under the MIT License - see [LICENSE](LICENSE) for details.

