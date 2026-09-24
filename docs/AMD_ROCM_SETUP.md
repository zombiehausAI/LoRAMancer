# AMD ROCm & Python Environment Provisioning

## Overview

LoRAMancer targets AMD Radeon GPUs on Windows utilizing official AMD ROCm wheels. The application requires **Python 3.12+ (prefer 3.12)** and establishes a dedicated `.venv` in the application environment.

## Wheel Sourcing Strategy

LoRAMancer can ingest AMD wheel configurations directly from:
1. **ComfyUI Reference Scripts**: Ingests paths and ROCm base URLs from user-provided scripts (such as `Reference/comfyui.ps1`).
2. **AMD Radeon Official Repositories**:
   - PyTorch ROCm Base: `https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1`
   - PyTorch Version: `2.9.1+rocm7.2.1`
   - Wheels:
     - `torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
     - `torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
     - `torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
   - ROCm SDK Packages:
     - `rocm-7.2.1.tar.gz`
     - `rocm_sdk_core-7.2.1-py3-none-win_amd64.whl`
     - `rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl`
     - `rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl`

## ROCm SDK Windows Patching

As documented in `Reference/comfyui.ps1`, the `rocm_sdk` package on Windows requires library entry stubs for non-present Unix shared objects (`hipsparselt`, `hipdnn`, `rocm-openblas`) inside `rocm_sdk/_dist_info.py`. `AmdVenvProvisioner` implements this patch automatically after wheel installation to prevent `ModuleNotFoundError` during process initialization.

## Dependency Protection

When installing AI-Toolkit or Kohya requirements, packages must be installed with `--no-deps` for any package tree that attempts to overwrite torch/torchvision with PyPI CUDA or CPU binaries.
