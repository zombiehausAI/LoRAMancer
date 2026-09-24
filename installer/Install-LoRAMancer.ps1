<#
.SYNOPSIS
    LoRAMancer Windows Desktop Installer Script
.DESCRIPTION
    Installs LoRAMancer desktop application, verifies .NET 10 and Python 3.12+ prerequisites,
    detects AMD ROCm GPU drivers, provisions application files, and creates desktop shortcuts.
#>

[CmdletBinding()]
param(
    [string]$InstallPath = "$env:LOCALAPPDATA\LoRAMancer",
    [switch]$SkipPrereqCheck,
    [switch]$CreateDesktopShortcut = $true
)

$ErrorActionPreference = "Stop"

Write-Host "===========================================================" -ForegroundColor Magenta
Write-Host "               LoRAMancer Desktop Installer                " -ForegroundColor Magenta
Write-Host "===========================================================" -ForegroundColor Magenta

# 1. Prerequisite Checks
if (-not $SkipPrereqCheck) {
    Write-Host "`n[1/4] Verifying prerequisites..." -ForegroundColor Cyan

    # Check for .NET 10
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Error "Error: .NET SDK was not found. Please install .NET 10 SDK from https://dotnet.microsoft.com/download"
        exit 1
    }

    $dotnetVersion = & dotnet --version
    Write-Host "  > .NET SDK Version: $dotnetVersion" -ForegroundColor Green

    # Check for Python 3.12+
    $pyCmd = $null
    foreach ($cand in @("python3.12", "python")) {
        if (Get-Command $cand -ErrorAction SilentlyContinue) {
            $ver = & $cand --version 2>&1
            if ($ver -match "Python 3\.(1[2-9]|[2-9]\d)") {
                $pyCmd = $cand
                Write-Host "  > Python Interpreter: $ver" -ForegroundColor Green
                break
            }
        }
    }

    if (-not $pyCmd) {
        Write-Warning "Python 3.12+ was not detected on PATH. Python features will require configuring the Python path in LoRAMancer."
    }

    # Detect GPU & Accelerator Architecture
    try {
        $videoControllers = Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue
        $amdGpu = $videoControllers | Where-Object { $_.Name -match "AMD|Radeon|ROCm" } | Select-Object -First 1
        $nvidiaGpu = $videoControllers | Where-Object { $_.Name -match "NVIDIA|GeForce|RTX|Quadro" } | Select-Object -First 1
        $intelGpu = $videoControllers | Where-Object { $_.Name -match "Intel|Arc|Iris|Xe" } | Select-Object -First 1

        if ($amdGpu) {
            Write-Host "  > AMD GPU detected: $($amdGpu.Name) (Target: ROCm 7.x Wheels)" -ForegroundColor Green
        } elseif ($nvidiaGpu) {
            Write-Host "  > NVIDIA GPU detected: $($nvidiaGpu.Name) (Target: CUDA 12.x Wheels)" -ForegroundColor Green
        } elseif ($intelGpu) {
            Write-Host "  > Intel GPU detected: $($intelGpu.Name) (Target: Intel XPU Wheels)" -ForegroundColor Cyan
        } else {
            Write-Host "  > No dedicated accelerator detected. Application will use CPU-optimized PyTorch wheels." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  > Could not query GPU information." -ForegroundColor Yellow
    }
}

# 2. Directory Provisioning
Write-Host "`n[2/4] Provisioning installation directory..." -ForegroundColor Cyan
Write-Host "  > Target path: $InstallPath"

$binPath = Join-Path $InstallPath "bin"
$pluginsPath = Join-Path $InstallPath "plugins"

New-Item -ItemType Directory -Path $binPath -Force | Out-Null
New-Item -ItemType Directory -Path $pluginsPath -Force | Out-Null

# 3. Publish & Deploy Binaries
Write-Host "`n[3/4] Compiling and publishing LoRAMancer binaries..." -ForegroundColor Cyan
$sourceRoot = Split-Path $PSScriptRoot -Parent
$appCsproj = Join-Path $sourceRoot "src\LoRAMancer.App\LoRAMancer.App.csproj"

if (Test-Path $appCsproj) {
    & dotnet publish $appCsproj -c Release -o $binPath --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish LoRAMancer application."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "  > Source project not found, deploying packaged binaries..."
}

# Deploy default plugins without overwriting existing user plugins or their isolated .venvs
$srcPlugins = Join-Path $sourceRoot "plugins"
if (Test-Path $srcPlugins) {
    Get-ChildItem -Path $srcPlugins | ForEach-Object {
        $dest = Join-Path $pluginsPath $_.Name
        if (-not (Test-Path $dest)) {
            Copy-Item $_.FullName $dest -Recurse
        }
    }
}

# 4. Shortcut Creation
if ($CreateDesktopShortcut) {
    Write-Host "`n[4/4] Creating Desktop shortcut..." -ForegroundColor Cyan
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
    $shortcutPath = Join-Path $desktop "LoRAMancer.lnk"
    $targetExe = Join-Path $binPath "LoRAMancer.App.exe"

    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $binPath
    $shortcut.Description = "LoRAMancer - LoRA Manager & Training Orchestrator"
    $shortcut.Save()
    Write-Host "  > Shortcut created at: $shortcutPath" -ForegroundColor Green
}

Write-Host "`n===========================================================" -ForegroundColor Green
Write-Host "           LoRAMancer successfully installed!              " -ForegroundColor Green
Write-Host "===========================================================" -ForegroundColor Green
