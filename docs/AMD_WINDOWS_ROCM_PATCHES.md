# AMD ROCm on Windows: Runtime Patches & Troubleshooting Log

This document serves as a comprehensive, running technical log of issues, architectural quirks, and runtime patches required to execute modern diffusion LoRA training (AI-Toolkit, Kohya) using AMD ROCm on Windows hosts.

---

## Environment Baseline

- **Operating System**: Windows 11 / Windows 10 (64-bit)
- **Python**: 3.12 (CPython x64)
- **PyTorch**: `torch==2.9.1+rocm7.2.1`, `torchvision`, `torchaudio`
- **ROCm SDK**: AMD ROCm 7.x (`rocm_sdk`, `rocm_sdk_core`, `rocm_sdk_devel`, `rocm_sdk_libraries_custom`)
- **Acceleration Stack**: MIOpen, HIP, PyTorch ROCm Windows
- **Engine**: AI-Toolkit (`ai-toolkit` by ostris)

---

## 1. Missing Library Stubs in `rocm_sdk` (`_dist_info.py`)

### The Problem
When initializing `torch` with ROCm on Windows:
```python
import torch
# Triggers: _rocm_init.initialize() -> import rocm_sdk -> from ._dist_info import __version__
```
The Windows port of `rocm_sdk` queries for Unix shared objects that do not exist on Windows, or fails with:
`ModuleNotFoundError: No module named 'libhipsparselt'` or `IndentationError: unexpected indent`.

### The Cause
1. `_dist_info.py` expects Linux `.so` shared libraries (`libhipsparselt.so.0`, `libhipdnn.so.0`, `librocm-openblas.so.0`).
2. Ad-hoc file appending often introduces indentation errors into `_dist_info.py`.

### The Patch
`AmdVenvProvisioner.PatchRocmSdkDistInfo` appends `optional=True` library entry stubs without leading indentation to:
`.venv/Lib/site-packages/rocm_sdk/_dist_info.py`:
```python
# [loramancer] windows-missing-libs
LibraryEntry("hipsparselt", "core", "libhipsparselt.so.0", "", optional=True)
LibraryEntry("hipdnn", "core", "libhipdnn.so.0", "", optional=True)
LibraryEntry("rocm-openblas", "core", "librocm-openblas.so.0", "", optional=True)
```

---

## 2. Process Output Buffering & Silent Hangs

### The Problem
When starting training via a subprocess, training appeared to hang indefinitely without log output, especially while downloading model weights or compiling kernels.

### The Cause
By default, Python buffers stdout and stderr when attached to a pipe (non-TTY). On Windows, buffer flushes can be delayed until tens of kilobytes are accumulated, giving the appearance of a hard lock during 10+ GB weight downloads.

### The Solution
`TrainingRunnerService` explicitly injects:
```csharp
startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
```
This forces line-by-line streaming of all engine logs and HuggingFace progress bars.

---

## 3. PyTorch Dependency Stripping vs. Missing Packages

### The Problem
Installing PyTorch with `--no-deps` prevents PyPI from overwriting ROCm PyTorch with CUDA/CPU versions, but strips core utilities needed by `ai-toolkit`, such as `httpx`, `numpy`, `Pillow`, `filelock`, `sympy`, `networkx`, and `jinja2`.

### The Solution
`AiToolkitSetupService` provisions dependencies in two separate stages:
1. Install AMD ROCm PyTorch wheels with `--no-cache-dir --no-deps`.
2. Install engine requirements (`requirements.txt`) without `--upgrade`, explicitly including foundational packages:
   ```powershell
   python.exe -m pip install --no-cache-dir -r requirements.txt sympy networkx jinja2 httpx
   ```

---

## 4. `torch._C._distributed_c10d` Missing on Windows ROCm (`torchao` & `diffusers`)

### The Problem
Attempting to run training triggers one of these errors:
```text
ModuleNotFoundError: No module named 'torch._C._distributed_c10d'; 'torch._C' is not a package
```
or:
```text
Failed to import diffusers.models.autoencoders.autoencoder_tiny because of the following error:
No module named 'torch._C._distributed_c10d'; 'torch._C' is not a package
```

### The Cause
- `ai-toolkit` requires `torchao` for `_DTYPE_TO_BIT_WIDTH` in `toolkit/config_modules.py`.
- `diffusers` inspects `torchao` on startup via `is_torchao_available()`.
- AMD PyTorch Windows wheels are compiled with `USE_DISTRIBUTED=0`. The C++ extension `torch._C._distributed_c10d.pyd` is **omitted**.
- Importing `torchao` causes a cascading import chain:
  `torchao/__init__.py` &rarr; `torchao.quantization` &rarr; `autoquant` &rarr; `torchao.dtypes` &rarr; `float8` &rarr; `float8_tensor.py` &rarr; `from torch.distributed._tensor import DTensor`.
- PyTorch's `torch.distributed._tensor` unconditionally imports `distributed_c10d`, which searches for `torch._C._distributed_c10d` and throws a fatal `ModuleNotFoundError`.

### The Patches
Because LoRAMancer runs single-GPU training, distributed operations are completely unused. `AmdVenvProvisioner.PatchTorchaoDistributedUtils` applies guards across four key files in `.venv/Lib/site-packages/torchao/`:

#### A. Guard `torchao/__init__.py`
Guards lines 41–46 so distributed modules do not crash package import:
```python
# [loramancer] windows-c10d-guard
try:
    from torchao.quantization import (
        autoquant,
        quantize_,
    )
    from . import dtypes, optim, testing
except Exception as e:
    logging.debug(f"Skipping distributed/c10d dependent modules: {e}")
```

#### B. Mock `torchao/float8/float8_tensor.py`
Guards line 10 (`DTensor` import):
```python
# [loramancer] windows-dtensor-mock
try:
    from torch.distributed._tensor import DTensor
except Exception:
    class DTensor:
        pass
```

#### C. Mock `torchao/float8/float8_utils.py`
Guards lines 10–11:
```python
# [loramancer] windows-dist-mock
try:
    import torch.distributed as dist
    from torch.distributed._functional_collectives import AsyncCollectiveTensor, all_reduce
except Exception:
    dist = None
    AsyncCollectiveTensor = None
    all_reduce = None
```

#### D. Mock `torchao/float8/distributed_utils.py`
Guards lines 8–9:
```python
# [loramancer] windows-distributed-mock
try:
    import torch.distributed._functional_collectives as funcol
    from torch.distributed._tensor import DTensor
except Exception:
    funcol = None
    DTensor = None
```

### Verification Command
Run this command in PowerShell to confirm compatibility:
```powershell
& "C:\AI\LoRAMancer\.venv\Scripts\python.exe" -c "from torchao.quantization.quant_primitives import _DTYPE_TO_BIT_WIDTH; from diffusers import AutoencoderTiny; print('SUCCESS: Both TorchAO and Diffusers loaded without errors!')"
```

---

## 5. Missing `torch.distributed` Stubs (`group`, `ReduceOp`) in Extensions

### The Problem
When `ai-toolkit` initializes, its `ExtensionJob` imports all built-in extensions (`extensions_built_in/diffusion_models/hidream`), which imports `moe.py`, resulting in:
```text
ImportError: cannot import name 'group' from 'torch.distributed' (C:\AI\LoRAMancer\.venv\Lib\site-packages\torch\distributed\__init__.py)
```

### The Cause
In `torch/distributed/__init__.py`, PyTorch evaluates `is_available()`. Because Windows ROCm wheels are compiled with `USE_DISTRIBUTED=0`, `is_available()` returns `False`. In the fallback branch, PyTorch only creates a stub for `ProcessGroup`:
```python
else:
    # This stub is sufficient to get ... working even when USE_DISTRIBUTED=0.
    # Feel free to add more stubs as necessary.
    class _ProcessGroupStub:
        pass
    sys.modules["torch.distributed"].ProcessGroup = _ProcessGroupStub
```
It omits `group`, `ReduceOp`, and common distributed inquiries (`is_initialized`, `get_rank`, `get_world_size`), causing any package importing `from torch.distributed import group, ReduceOp` (like `torch.distributed.nn.functional`) to fail.

### The Patch
`AmdVenvProvisioner.PatchTorchDistributedInit` appends the missing stubs to `.venv/Lib/site-packages/torch/distributed/__init__.py`:
```python
    # [loramancer] windows-distributed-stubs
    class _GroupStub:
        WORLD = None

    class _ReduceOpStub:
        SUM = None
        PRODUCT = None
        MIN = None
        MAX = None
        BAND = None
        BOR = None
        BXOR = None

    sys.modules["torch.distributed"].group = _GroupStub
    sys.modules["torch.distributed"].ReduceOp = _ReduceOpStub
    sys.modules["torch.distributed"].is_initialized = lambda: False
    sys.modules["torch.distributed"].get_rank = lambda group=None: 0
    sys.modules["torch.distributed"].get_world_size = lambda group=None: 1
```

---

## 6. Triton Kernel Warnings

### The Warning
```text
WARNING:torchao.kernel.intmm:Warning: Detected no triton, on systems without Triton certain kernels will not work
```
### Notes
Triton is a Linux-native compiler that has limited, experimental support on Windows. This warning is informational; PyTorch and AI-Toolkit fall back to standard PyTorch ATen kernels and SDPA (Scaled Dot-Product Attention) for all matrix multiplications and attention operations.

---

## 7. Chroma1-HD Foundation Model Footprint

### Details
- **Architecture**: Chroma (`lodestones/Chroma1-HD`)
- **Parameters**: ~8.9B (~17–18 GB base model weights)
- **First-run download**: Downloaded to HuggingFace hub cache (`~/.cache/huggingface/hub/models--lodestones--Chroma1-HD/`).
- **Telemetry note**: Because weights are ~17 GB, download may take several minutes before step telemetry starts. LoRAMancer outputs diagnostic notices to inform the user that the process is downloading and not hung.

---

## 8. Chroma Model Path: UI Label vs. Local `.safetensors`

### The Problem
```text
ValueError: Model path ChromaHD-1 does not exist
```

### The Cause
1. In AI-Toolkit, `chroma_model.py` loads weights directly using `safetensors.torch.load_file(model_path)` rather than a Hugging Face Diffusers pipeline. It checks `if not os.path.exists(model_path): raise ValueError(...)`.
2. The UI dropdown previously emitted the raw display name (`ChromaHD-1`) into `name_or_path`.

### The Solution
1. `AiToolkitConfigBuilder.ResolveModelPath` maps display names back to canonical paths.
2. The Training Wizard UI includes a dedicated **Custom Base Checkpoint File** browser to select local `.safetensors` files directly.
3. Both the **History & Vault** cards and the **Training Console** failure banner include an **Edit in Wizard** button to reopen any failed or past training run with all hyperparameters and paths pre-filled for instant modification.

---

## 9. Optimizer Type Sanitization (`adamw` vs `adamw_bf16`)

### The Problem
```text
Error running job: Unknown optimizer type adamw_bf16
Traceback (most recent call last):
  File "C:\AI\LoRAMancer\tools\ai-toolkit\toolkit\optimizer.py", line 115, in get_optimizer
    raise ValueError(f'Unknown optimizer type {optimizer_type}')
ValueError: Unknown optimizer type adamw_bf16
```

### The Cause
In `ai-toolkit`, `toolkit/optimizer.py` implements `get_optimizer()` which parses the `optimizer:` configuration field against a strict list of canonical optimizer names (`"adamw"`, `"prodigy"`, `"adam"`, `"lion"`, `"adafactor"`).
Unlike Kohya or scripts that accept compound strings like `adamw_bf16`, `paged_adamw_8bit`, or `adamw8bit`, `ai-toolkit` decouples optimizer algorithms from precision:
- Algorithm is specified in `train.optimizer: adamw`
- Numerical precision is specified separately in `train.dtype: bf16`

### The Solution
`AiToolkitConfigBuilder.SanitizeOptimizer` normalizes any incoming optimizer name:
- `adamw_bf16`, `paged_adamw_8bit`, `adamw8bit`, `adamw_8bit`, etc., are mapped directly to `"adamw"`.
- `TrainingWizardDialog.razor` defaults the optimizer dropdown to `"adamw"`.

---

## 10. `torch.distributed.tensor` (`DTensor`) & Accelerate `model_has_dtensor`

### The Problem
During the pre-train hook (`self.hook_before_train_loop()`), `ai-toolkit` invokes `accelerator.prepare(self.sd.vae)`:
```text
  File "accelerate\accelerator.py", line 1802, in prepare_model
    and not model_has_dtensor(model)
  File "accelerate\utils\other.py", line 243, in model_has_dtensor
    from torch.distributed.tensor import DTensor
  File "torch\distributed\tensor\__init__.py", line 4, in <module>
    import torch.distributed.tensor._ops
  File "torch\distributed\tensor\_ops\__init__.py", line 2, in <module>
    from ._conv_ops import *
  File "torch\distributed\tensor\_ops\_conv_ops.py", line 5, in <module>
    from torch.distributed.tensor._dtensor_spec import DTensorSpec, TensorMeta
  File "torch\distributed\tensor\_dtensor_spec.py", line 6, in <module>
    from torch.distributed.tensor.placement_types import (
  File "torch\distributed\tensor\placement_types.py", line 8, in <module>
    import torch.distributed._functional_collectives as funcol
  File "torch\distributed\_functional_collectives.py", line 9, in <module>
    import torch.distributed.distributed_c10d as c10d
  File "torch\distributed\distributed_c10d.py", line 23, in <module>
    from torch._C._distributed_c10d import (
ModuleNotFoundError: No module named 'torch._C._distributed_c10d'; 'torch._C' is not a package
```

### The Cause
1. In `accelerate.prepare_model()`, Accelerate checks `if not model_has_dtensor(model): model.to(device)`.
2. Inside `accelerate.utils.other.model_has_dtensor()`, it unconditionally runs `from torch.distributed.tensor import DTensor`.
3. In PyTorch 2.x, `torch.distributed.tensor.__init__` unconditionally executes `import torch.distributed.tensor._ops`, which cascades into `_functional_collectives` -> `distributed_c10d.py` -> `from torch._C._distributed_c10d import ...`.
4. Because Windows ROCm PyTorch wheels are compiled with `USE_DISTRIBUTED=0`, the binary extension `torch._C._distributed_c10d` does not exist.

### The Patch
`AmdVenvProvisioner.PatchTorchaoDistributedUtils` applies targeted guards before starting any training run:
1. **`torch/distributed/tensor/__init__.py`**: Wraps the package imports in a `try...except Exception:` block that defines `class DTensor: pass` when distributed ops cannot load.
2. **`accelerate/utils/other.py`**: Guards `def model_has_dtensor(model)` with a `try...except Exception: return False` block.
On single-GPU training, models never contain distributed tensors, allowing `accelerator.prepare()` to smoothly move models and VAEs to the ROCm GPU device without triggering distributed code paths.

> [!NOTE]
> Global mock modules in `sys.modules` (e.g. via `sitecustomize.py`) must be avoided because standard Python introspection utilities (`inspect.getmodule()`, `inspect.findsource()`) traverse `sys.modules.values()` and expect valid module attributes (`__file__` as string or `None`). Targeted patches in `torch.distributed.tensor` and `accelerate.utils.other` keep the Python runtime pristine while completely bypassing the missing c10d binaries.




