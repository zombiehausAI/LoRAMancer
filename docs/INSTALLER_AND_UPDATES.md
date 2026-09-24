# Installer & Auto-Update Architecture

## Overview

LoRAMancer includes automated deployment packaging and in-app update mechanisms:
1. **PowerShell Bootstrap Installer (`installer/Install-LoRAMancer.ps1`)**: Handles prerequisite detection (.NET 10, Python 3.12, AMD GPU/ROCm drivers), directory setup, and desktop shortcut generation.
2. **Inno Setup Script (`installer/LoRAMancer.iss`)**: Generates an enterprise-ready Windows standalone installer executable.
3. **In-App Auto-Update Service (`AutoUpdateService.cs`)**: Checks remote release manifests, displays update notes, and manages zero-friction background download and restart transitions.

## In-App Auto-Update Flow

```
[Start App] 
     │
     ▼
[Check Updates Async] ──(GitHub API or Release Manifest)──> [Update Available?]
                                                                    │
                                                 ┌──────────────────┴──────────────────┐
                                                 │ Yes                                 │ No
                                                 ▼                                     ▼
                                       [Notify in UI Banner]                      [Up to Date]
                                                 │
                                       [User Clicks "Update"]
                                                 │
                                       [Download Package to Cache]
                                                 │
                                       [Verify Checksum & Sign]
                                                 │
                                       [Launch Updater & Exit App]
                                                 │
                                       [Updater Replaces Binaries]
                                                 │
                                       [Relaunch LoRAMancer]
```

## Update Manifest Schema

The update endpoint serves a lightweight JSON manifest:
```json
{
  "version": "1.1.0",
  "releaseDate": "2026-10-01T00:00:00Z",
  "downloadUrl": "https://github.com/loramancer/loramancer/releases/download/v1.1.0/LoRAMancer-Setup-1.1.0.exe",
  "sha256": "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",
  "changelog": "- Added support for FLUX.2 training\n- Enhanced AMD ROCm 7.2.1 memory allocator tuning",
  "mandatory": false
}
```
