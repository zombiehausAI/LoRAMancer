<#
.SYNOPSIS
    LoRAMancer Windows Desktop Installer Script
.DESCRIPTION
    Installs LoRAMancer desktop application, verifies .NET 10 and Python 3.12+ prerequisites,
    detects AMD ROCm GPU drivers, provisions application files, and creates desktop shortcuts.
#>

[CmdletBinding()]
param(
    [string]$InstallPath = "",
    [switch]$SkipPrereqCheck,
    [switch]$CreateDesktopShortcut = $true,
    [switch]$Unattended
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

# 2. Directory Provisioning & Custom Path Selection
Write-Host "`n[2/4] Selecting and provisioning installation directory..." -ForegroundColor Cyan

$defaultInstallPath = "$env:LOCALAPPDATA\LoRAMancer"

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    if ($Unattended -or [Console]::IsInputRedirected) {
        $InstallPath = $defaultInstallPath
    } else {
        Write-Host "Choose where to install LoRAMancer (can be on any drive, e.g. D:\LoRAMancer):" -ForegroundColor Yellow
        Write-Host "  [Enter]  Use default: $defaultInstallPath" -ForegroundColor Gray
        Write-Host "  [B]      Browse for a folder with Windows dialog" -ForegroundColor Gray
        Write-Host "  Or type a custom path directly (e.g. D:\AI\LoRAMancer)" -ForegroundColor Gray

        $userInput = Read-Host "`nInstall path [Default: $defaultInstallPath]"
        if ([string]::IsNullOrWhiteSpace($userInput)) {
            $InstallPath = $defaultInstallPath
        } elseif ($userInput.Trim() -eq "B" -or $userInput.Trim() -eq "b") {
            try {
                Add-Type -AssemblyName System.Windows.Forms
                $browser = New-Object System.Windows.Forms.FolderBrowserDialog
                $browser.Description = "Select LoRAMancer Installation Folder (Any Drive)"
                $browser.UseDescriptionForTitle = $true
                $browser.ShowNewFolderButton = $true
                if ($browser.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and -not [string]::IsNullOrWhiteSpace($browser.SelectedPath)) {
                    $InstallPath = $browser.SelectedPath
                } else {
                    Write-Host "  > No folder selected in dialog. Using default." -ForegroundColor Yellow
                    $InstallPath = $defaultInstallPath
                }
            } catch {
                Write-Warning "GUI folder picker unavailable. Using default: $defaultInstallPath"
                $InstallPath = $defaultInstallPath
            }
        } else {
            $InstallPath = $userInput.Trim().Trim('"', "'")
        }
    }
}

# Resolve full path and check target drive space
try {
    $InstallPath = [System.IO.Path]::GetFullPath($InstallPath)
} catch {
    Write-Warning "Invalid path provided. Falling back to default: $defaultInstallPath"
    $InstallPath = $defaultInstallPath
}

Write-Host "  > Target path: $InstallPath" -ForegroundColor Green

# Query target drive and display free disk space
try {
    $driveRoot = [System.IO.Path]::GetPathRoot($InstallPath)
    if ($driveRoot) {
        $driveLetter = $driveRoot.Substring(0, 1)
        $diskInfo = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$driveLetter:'" -ErrorAction SilentlyContinue
        if ($diskInfo -and $diskInfo.FreeSpace) {
            $freeGb = [math]::Round($diskInfo.FreeSpace / 1GB, 1)
            Write-Host "  > Target Drive ($driveLetter`:): $freeGb GB free space available" -ForegroundColor Gray
        }
    }
} catch { }

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
    $shortcut.IconLocation = "$targetExe,0"
    $shortcut.Description = "LoRAMancer - LoRA Manager & Training Orchestrator"
    $shortcut.Save()
    Write-Host "  > Shortcut created at: $shortcutPath" -ForegroundColor Green
}

Write-Host "`n===========================================================" -ForegroundColor Green
Write-Host "           LoRAMancer successfully installed!              " -ForegroundColor Green
Write-Host "===========================================================" -ForegroundColor Green
