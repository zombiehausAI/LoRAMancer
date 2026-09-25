# AMD ROCm & Python Environment Provisioning

## Overview

LoRAMancer targets AMD Radeon GPUs on Windows utilizing official AMD ROCm wheels. The application requires **Python 3.12+ (prefer 3.12)** and establishes a dedicated `.venv` in the application environment without modifying any existing external tools (such as ComfyUI).

> [!NOTE]
> **Performance Expectation (AMD ROCm vs. NVIDIA CUDA)**:
> Native Windows AMD ROCm training may currently run slower than native NVIDIA CUDA training due to current Windows driver maturity, attention kernel implementations (SDPA vs. Flash Attention), and PyTorch Windows ROCm runtime tuning. However, it is **fully working natively on Windows** without requiring dual-boot Linux configurations, heavy WSL2 virtual machines, or restrictive DirectML fallbacks.

## Multi-Vendor Accelerator Auto-Detection

While LoRAMancer specializes in solving the AMD ROCm tooling gap on Windows, `AmdVenvProvisioner` automatically detects the host hardware accelerator and installs the appropriate PyTorch distribution:

| Detected Hardware | Target Accelerator | Provisioned Wheels & Source |
| :--- | :--- | :--- |
| **AMD Radeon** | ROCm 7.2.1 | Official AMD Wheels (`repo.radeon.com`) + `rocm_sdk` + Windows stubs |
| **NVIDIA GeForce / RTX** | CUDA 12.4 | Official PyTorch CUDA index (`download.pytorch.org/whl/cu124`) |
| **Intel Arc / Xe** | Intel XPU | Official PyTorch Intel XPU index (`download.pytorch.org/whl/xpu`) |
| **CPU / Generic** | CPU Optimized | Official PyTorch CPU index (`download.pytorch.org/whl/cpu`) |

Users can allow the system to auto-detect their GPU or manually override the target architecture profile via the dropdown in **Environment Setup** (`/environment`).

## Wheel Sourcing & Admin Configuration

LoRAMancer supports dynamic, script-free wheel updates via the **Admin & Settings Page** (`/settings`):
- **Base Repository URL**: Define or update the AMD release URL (e.g., `https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1` or newer 7.3+ releases).
- **Auto-Generation & Custom Overrides**: Automatically generate wheel paths from the base URL and version tag, or override individual wheel URLs for `torch`, `torchvision`, and `torchaudio`.
- **Live URL Reachability Probing**: Built-in HTTP probe verifies wheel links and reports HTTP status and file sizes before attempting provisioning.
- **Persistence**: Saved securely to the user profile under `~/.loramancer/settings.json` (outside the repository), allowing upgrades without editing source files or repository configs.

## Default Wheel Sourcing

When not overridden, LoRAMancer defaults to:
- **PyTorch ROCm Base**: `https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1`
- **PyTorch Version**: `2.9.1+rocm7.2.1`
- **Wheels**:
  - `torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
  - `torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
  - `torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
- **ROCm SDK Packages**:
  - `rocm-7.2.1.tar.gz`
  - `rocm_sdk_core-7.2.1-py3-none-win_amd64.whl`
  - `rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl`
  - `rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl`

## ROCm SDK Windows Patching

The `rocm_sdk` package on Windows requires library entry stubs for non-present Unix shared objects (`hipsparselt`, `hipdnn`, `rocm-openblas`) inside `rocm_sdk/_dist_info.py`. `AmdVenvProvisioner` implements this patch automatically after wheel installation to prevent `ModuleNotFoundError` during process initialization.

## TorchAO & Diffusers Compatibility Patching

Windows ROCm PyTorch builds do not contain `torch._C._distributed_c10d`. AI-Toolkit requires `torchao` for `_DTYPE_TO_BIT_WIDTH`, and `diffusers` inspects `torchao` on startup. Because `torchao` unconditionally attempts to import `torch.distributed` across `torchao/__init__.py` and `torchao.float8.distributed_utils`, `AmdVenvProvisioner` automatically guards these import blocks in `.venv` before training begins, ensuring `torchao`, `diffusers`, and `ai-toolkit` all run seamlessly together on Windows AMD ROCm.

## PyTorch Protection & Dependency Resolution

Hardware PyTorch wheels (`torch`, `torchvision`, `torchaudio`) are installed directly with `--no-deps` to ensure they are isolated from standard PyPI indexes. When subsequently installing training engine requirements (AI-Toolkit or Kohya), requirements are installed with full dependency resolution (omitting `--upgrade`), ensuring all core packages (`numpy`, `Pillow`, `filelock`, `tqdm`, `requests`, `sympy`, `networkx`, `jinja2`) are fully provisioned without replacing or downgrading the AMD ROCm PyTorch binaries.
