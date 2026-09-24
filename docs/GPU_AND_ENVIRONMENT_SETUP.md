# Universal GPU & Environment Provisioning

## Overview

LoRAMancer features **Universal Multi-Vendor GPU Acceleration**, automatically detecting your host graphics hardware and dynamically provisioning an isolated Python 3.12 `.venv` with the exact hardware-accelerated PyTorch distribution matching your setup.

---

## Supported Accelerators & Distributions

| Hardware Vendor | Architecture | Accelerator Target | PyTorch Index / Source |
| :--- | :--- | :--- | :--- |
| **NVIDIA** | GeForce, RTX, Quadro, Tesla | CUDA 12.4 | `https://download.pytorch.org/whl/cu124` |
| **AMD** | Radeon RX 7000/6000, Radeon Pro | ROCm 7.2.1 / 7.3+ | `https://repo.radeon.com/rocm/windows/` + `rocm_sdk` |
| **Intel** | Arc, Data Center GPU Flex / Max | Intel XPU | `https://download.pytorch.org/whl/xpu` |
| **CPU / Fallback** | Any x86_64 Processor | CPU Optimized | `https://download.pytorch.org/whl/cpu` |

---

## Automated Hardware Detection Workflow

When provisioning or validating the training environment (`AmdVenvProvisioner`):
1. **WMI Hardware Probe**: Queries `Win32_VideoController` to detect installed GPU devices, adapter RAM (VRAM), and device driver identifiers.
2. **Vendor Identification**: Classifies the vendor as AMD, NVIDIA, Intel, or CPU fallback.
3. **Dedicated Virtual Environment Creation**: Creates an isolated `.venv` (`python -m venv .venv`).
4. **Base Tooling Upgrade**: Upgrades `pip`, `setuptools`, and `wheel` inside the virtual environment.
5. **Hardware-Matched PyTorch Installation**:
   - **NVIDIA**: Runs `pip install --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu124`.
   - **AMD ROCm**: Downloads and installs official AMD ROCm wheels, SDK wheels, and applies the automated Windows shared library entry stubs.
   - **Intel**: Runs `pip install --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/xpu`.
   - **CPU**: Runs `pip install --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu`.
6. **Dependency Protection**: AI-Toolkit and custom requirements are installed with `--no-deps` for any package hierarchy to prevent overwriting hardware-specific PyTorch binaries.

---

## AMD ROCm Windows Specifics

For AMD Radeon GPUs on Windows:
- **Default Wheels**:
  - `torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
  - `torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
  - `torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl`
- **ROCm SDK Patching**: Automatically injects library stubs (`hipsparselt`, `hipdnn`, `rocm-openblas`) inside `rocm_sdk/_dist_info.py` to prevent missing Unix shared library crashes on Windows.
- **Dynamic Wheel Updates**: In **Settings → AMD ROCm Wheels**, users can update the base repository URL (e.g. for ROCm 7.3+ releases) with live URL reachability probing.

---

## ComfyUI Environment Ingestion

If you already have a functional ComfyUI setup on your PC:
1. Navigate to **Environment Setup** (`/environment`).
2. Provide your ComfyUI runner script (`comfyui.ps1` or `run_gpu.bat`) or ComfyUI Python directory.
3. LoRAMancer extracts the exact Python executable, local wheel directories, and ROCm/CUDA parameters to provision an environment identical to your proven ComfyUI runtime.
