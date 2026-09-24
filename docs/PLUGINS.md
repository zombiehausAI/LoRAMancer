# LoRAMancer Plugin Architecture

## Overview

LoRAMancer provides a dual plugin architecture enabling extensions to be authored in either **C# (.NET 10)** or **Python (3.12+)**.

All plugins are discovered from the root `plugins/` directory:

```
plugins/
├── <csharp_plugin_name>/
│   └── <csharp_plugin_name>.dll       # Compiled .NET assembly
└── <python_plugin_name>/
    ├── plugin.json                    # Manifest (Name, Version, EntryPoint)
    ├── plugin.py                      # Main Python entry point
    ├── requirements.txt               # Plugin dependencies
    └── .venv/                         # Dedicated isolated virtual environment
```

## C# Plugins

C# plugins are implemented as standard .NET class libraries referencing `LoRAMancer.PluginSdk`.

### Plugin Structure
A C# plugin consists of a `.dll` placed in a subfolder under `plugins/`:
- Folder: `plugins/MyCSharpPlugin/`
- Binary: `plugins/MyCSharpPlugin/MyCSharpPlugin.dll`

### Interface Contract
C# plugins implement `ILoRAMancerPlugin`:
```csharp
namespace LoRAMancer.PluginSdk;

public interface ILoRAMancerPlugin {
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
    Task<PluginResult> ExecuteAsync(string command, IDictionary<string, object> parameters, CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}
```

The plugin assembly is loaded into an isolated `AssemblyLoadContext` to allow dynamic unloading and dependency isolation.

## Python Plugins

Python plugins run out-of-process to guarantee complete dependency and memory isolation.

### Plugin Structure
Each Python plugin lives in its own dedicated folder under `plugins/` and **must** contain its own `.venv` inside that folder:
- Folder: `plugins/MyPythonPlugin/`
- Virtual Environment: `plugins/MyPythonPlugin/.venv/`
- Manifest: `plugins/MyPythonPlugin/plugin.json`
- Entry Point: `plugins/MyPythonPlugin/plugin.py`
- Requirements: `plugins/MyPythonPlugin/requirements.txt`

### Plugin Manifest (`plugin.json`)
```json
{
  "id": "sample-python-plugin",
  "name": "Sample Python Plugin",
  "version": "1.0.0",
  "description": "Demonstrates isolated Python plugin execution",
  "entryPoint": "plugin.py",
  "pythonVersion": "3.12"
}
```

### Virtual Environment Provisioning
When a Python plugin is discovered or installed:
1. `PluginManagerService` checks for `plugins/<name>/.venv/`.
2. If missing, it invokes Python 3.12 to provision `python -m venv plugins/<name>/.venv`.
3. If `requirements.txt` is present, dependencies are installed into `plugins/<name>/.venv` using `pip install -r requirements.txt`.
4. Communication with the host occurs via standard JSON-RPC / STDIO streaming.
