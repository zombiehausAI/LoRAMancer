# AMD ROCm & Python Environment Provisioning

## Overview

LoRAMancer targets AMD Radeon GPUs on Windows utilizing official AMD ROCm wheels. The application requires **Python 3.12+ (prefer 3.12)** and establishes a dedicated `.venv` in the application environment without modifying any existing external tools (such as ComfyUI).

## Wheel Sourcing & Admin Configuration

LoRAMancer supports dynamic, script-free wheel updates via the **Admin & Settings Page** (`/settings`):
- **Base Repository URL**: Define or update the AMD release URL (e.g., `https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1` or newer 7.3+ releases).
- **Auto-Generation & Custom Overrides**: Automatically generate wheel paths from the base URL and version tag, or override individual wheel URLs for `torch`, `torchvision`, and `torchaudio`.
- **Live URL Reachability Probing**: Built-in HTTP probe verifies wheel links and reports HTTP status and file sizes before attempting provisioning.
- **Persistence**: Saved to `%LOCALAPPDATA%\LoRAMancer\settings.json`, allowing upgrades without editing source files or PowerShell scripts.

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

As documented in `Reference/comfyui.ps1`, the `rocm_sdk` package on Windows requires library entry stubs for non-present Unix shared objects (`hipsparselt`, `hipdnn`, `rocm-openblas`) inside `rocm_sdk/_dist_info.py`. `AmdVenvProvisioner` implements this patch automatically after wheel installation to prevent `ModuleNotFoundError` during process initialization.

## Dependency Protection

When installing AI-Toolkit or Kohya requirements, packages must be installed with `--no-deps` for any package tree that attempts to overwrite torch/torchvision with PyPI CUDA or CPU binaries.
