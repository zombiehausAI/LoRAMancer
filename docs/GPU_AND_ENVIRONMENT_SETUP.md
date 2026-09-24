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
6. **PyTorch Preservation & Full Dependency Resolution**: Hardware PyTorch wheels are installed directly without dependencies to prevent pulling standard PyPI CUDA/CPU packages. Subsequently, AI-Toolkit and training requirements are resolved with full dependency resolution (without `--upgrade`), ensuring core libraries like `numpy`, `Pillow`, `filelock`, `tqdm`, `requests`, `sympy`, `networkx`, and `jinja2` are fully provisioned while preserving hardware-specific PyTorch binaries.

---

## Vendor-Specific PyTorch Wheel Configuration & Customization

Under **Settings → GPU & PyTorch Wheels**, each GPU architecture has its own dedicated card configuration panel where users can use official defaults or provide custom wheel URLs / index repositories:

1. **AMD Radeon (ROCm)**:
   - **Default**: Official AMD ROCm 7.2.1 Windows wheels from `https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1`.
   - **Customization**: Update ROCm release base URL, PyTorch version tag, direct `.whl` links, and ROCm SDK wheels with auto-generation and reachability probing.
   - **Reset**: One-click "Reset AMD Defaults" button.

2. **NVIDIA GeForce / RTX (CUDA)**:
   - **Default**: Official PyTorch CUDA 12.4 index (`https://download.pytorch.org/whl/cu124`) with `torch torchvision torchaudio`.
   - **Customization**: Switch to custom direct wheels or alternate CUDA index repositories (e.g. `cu121`). Includes live link reachability testing.
   - **Reset**: One-click "Reset NVIDIA Defaults" button.

3. **Intel Arc / Core Ultra / Data Center (XPU)**:
   - **Default**: Official PyTorch XPU index (`https://download.pytorch.org/whl/xpu`) with native PyTorch 2.5+ Intel GPU acceleration.
   - **Customization**: Specify custom XPU wheel URLs or alternative Intel oneAPI/XPU package repositories with reachability testing.
   - **Reset**: One-click "Reset Intel Defaults" button.

4. **CPU Fallback**:
   - **Default**: Official CPU-optimized PyTorch build (`https://download.pytorch.org/whl/cpu`).
   - **Reset**: One-click "Reset CPU Defaults" button.

---

## ComfyUI Environment Ingestion

If you already have a functional ComfyUI setup on your PC:
1. Navigate to **Environment Setup** (`/environment`).
2. Provide your ComfyUI runner script (`comfyui.ps1` or `run_gpu.bat`) or ComfyUI Python directory.
3. LoRAMancer extracts the exact Python executable, local wheel directories, and ROCm/CUDA parameters to provision an environment identical to your proven ComfyUI runtime.
