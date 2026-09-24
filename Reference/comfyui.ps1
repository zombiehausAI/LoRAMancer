#Requires -Version 7.0
# A robust PowerShell script to manage the ComfyUI server on Windows.
#
# Features:
# - Manages the process via a PID file for reliability
# - Uses startup options array to handle arguments safely
# - Redirects output to a log file for easier debugging
# - Implements a graceful shutdown with a timeout before force-killing
# - Provides status, start, stop, restart, update, and install commands

# Stop on errors
$ErrorActionPreference = "Stop"

# --- Configuration ---
$COMFYUI_PATH = "D:\AI\ComfyUI"
$OUTPUT_DIRECTORY = "G:\My Drive\AI\AICreated"
$USER_DIRECTORY = "G:\My Drive\AI\ComfyUI_User\default"
$PID_FILE = Join-Path $env:TEMP "comfyui.pid"
$PYTHON_EXEC = Join-Path $COMFYUI_PATH "venv-3.12\Scripts\python.exe"
$MAIN_SCRIPT = Join-Path $COMFYUI_PATH "main.py"
$COMFYUI_REPO_URL = "https://github.com/comfyanonymous/ComfyUI.git"
$PYTORCH_ROCM_BASE = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1"
$PYTORCH_TORCH_VERSION = "2.9.1+rocm7.2.1"
$PYTORCH_WHEELS = @(
    "$PYTORCH_ROCM_BASE/torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl",
    "$PYTORCH_ROCM_BASE/torchaudio-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl",
    "$PYTORCH_ROCM_BASE/torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl"
)
$ROCM_SDK_WHEELS = @(
    "$PYTORCH_ROCM_BASE/rocm-7.2.1.tar.gz",
    "$PYTORCH_ROCM_BASE/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
    "$PYTORCH_ROCM_BASE/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
    "$PYTORCH_ROCM_BASE/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl"
)
$LOG_FILE = Join-Path $env:TEMP "comfyui.log"

# Startup options will be determined based on GPU type
$STARTUP_OPTIONS = @(
    "--use-pytorch-cross-attention"
    "--disable-smart-memory"
    "--output-directory", "`"$OUTPUT_DIRECTORY`""
    "--user-directory", "`"$USER_DIRECTORY`""
    "--reserve-vram", "0.9"
    "--listen", "0.0.0.0"
)

# Cache for AMD GPU detection
$script:IsAmdGpuCached = $null

# --- Helper Functions ---

# Checks if an AMD GPU is present
function Test-AmdGpu {
    # Return cached result if available
    if ($null -ne $script:IsAmdGpuCached) {
        return $script:IsAmdGpuCached
    }

    try {
        $videoControllers = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop
        $hasAmd = $videoControllers | Where-Object { $_.Name -match "AMD|Radeon|ROCm" } | Select-Object -First 1
        $script:IsAmdGpuCached = ($null -ne $hasAmd)
        
        if ($script:IsAmdGpuCached) {
            Write-Host "-> AMD GPU detected: $($hasAmd.Name)"
        }
        
        return $script:IsAmdGpuCached
    }
    catch {
        $script:IsAmdGpuCached = $false
        return $false
    }
}

# Checks for required system dependencies
function Test-Dependencies {
    # Check for git
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Error "Error: 'git' command not found. Please install Git for Windows from https://git-scm.com/"
        exit 1
    }

    # Check for Python 3.12
    $pythonVersions = @()
    $pythonCommands = @("python3.12", "python3", "python")
    
    foreach ($cmd in $pythonCommands) {
        if (Get-Command $cmd -ErrorAction SilentlyContinue) {
            $version = & $cmd --version 2>&1 | Select-String -Pattern "Python (\d+\.\d+)" | ForEach-Object { $_.Matches.Groups[1].Value }
            if ($version -eq "3.12") {
                $script:PYTHON_CMD = $cmd

                # Check for ROCm if AMD GPU is detected
                if (Test-AmdGpu) {
                    # Check if ROCm is installed by looking for rocminfo
                    $rocmInfo = Get-Command rocminfo -ErrorAction SilentlyContinue
                    if (-not $rocmInfo) {
                        # Also check common ROCm installation paths
                        $rocmPaths = @(
                            "C:\Program Files\AMD\ROCm",
                            "C:\Program Files (x86)\AMD\ROCm"
                        )
                        $rocmFound = $false
                        foreach ($path in $rocmPaths) {
                            if (Test-Path $path) {
                                Write-Host "-> ROCm installation found at: $path"
                                $rocmFound = $true
                                break
                            }
                        }

                        if (-not $rocmFound) {
                            Write-Warning "AMD GPU detected, but ROCm installation not found."
                            Write-Warning "For optimal performance, install AMD ROCm 7.1.1 driver."
                            Write-Warning "Visit: https://www.amd.com/en/resources/support-articles/release-notes/RN-AMDGPU-LINUX-ROCM-7-1-PREVIEW.html"
                        }
                    }
                    else {
                        Write-Host "-> ROCm drivers detected."
                    }
                }

                return
            }
            $pythonVersions += "$cmd ($version)"
        }
    }

    Write-Error "Error: Python 3.12 not found. Available versions: $($pythonVersions -join ', ')"
    Write-Error "Please install Python 3.12 from https://www.python.org/downloads/"
    exit 1
}

# Installs Python dependencies from a requirements.txt file
# with special handling for AMD GPUs with ROCm support
function Install-RocmSdk {
    # Install ROCm SDK packages that provide the rocm_sdk Python module and bundled DLLs.
    # Uninstall the PyPI stub first so it doesn't shadow the real package.
    & $PYTHON_EXEC -m pip uninstall rocm-sdk -y 2>$null
    Write-Host "  > Installing AMD ROCm SDK packages..."
    & $PYTHON_EXEC -m pip install --no-cache-dir $ROCM_SDK_WHEELS
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install ROCm SDK packages"
    }

    # Patch rocm_sdk _dist_info.py to register libraries that don't exist on Windows.
    # Without this, rocm_sdk.initialize_process raises ModuleNotFoundError for these names.
    # Only add each entry if it isn't already present (newer rocm_sdk versions include them natively).
    $distInfoPath = Join-Path (Split-Path $PYTHON_EXEC) "..\Lib\site-packages\rocm_sdk\_dist_info.py"
    $distInfoPath = [System.IO.Path]::GetFullPath($distInfoPath)
    if (Test-Path $distInfoPath) {
        # Remove any previously-applied patch block so we start clean after a SDK upgrade.
        # This prevents duplicate LibraryEntry assertions when a newer SDK defines the same names natively.
        $content = Get-Content $distInfoPath -Raw
        if ($content -match '# \[comfyui-script\] windows-missing-libs') {
            $cleaned = $content -replace '\r?\n# \[comfyui-script\] windows-missing-libs\r?\n[\s\S]*?(?=\r?\n(?!LibraryEntry)|\Z)', ''
            Set-Content -Path $distInfoPath -Value $cleaned -NoNewline
            $content = $cleaned
            Write-Host "  > Removed stale comfyui-script patch from rocm_sdk _dist_info.py."
        }

        $missingEntries = @()
        $missingLibs = @(
            @{ name = "hipsparselt"; entry = 'LibraryEntry("hipsparselt", "core", "libhipsparselt.so.0", "")' },
            @{ name = "hipdnn"; entry = 'LibraryEntry("hipdnn", "core", "libhipdnn.so.0", "")' },
            @{ name = "rocm-openblas"; entry = 'LibraryEntry("rocm-openblas", "core", "librocm-openblas.so.0", "")' }
        )
        foreach ($lib in $missingLibs) {
            # Check for the shortname anywhere in the file, not just our exact entry string,
            # so we don't add a duplicate when a newer SDK already defines it with different args.
            if ($content -notmatch ([regex]::Escape('"' + $lib.name + '"'))) {
                $missingEntries += $lib.entry
            }
        }
        if ($missingEntries.Count -gt 0) {
            $patch = "`n# [comfyui-script] windows-missing-libs`n" + ($missingEntries -join "`n") + "`n"
            Add-Content -Path $distInfoPath -Value $patch
            Write-Host "  > Patched rocm_sdk _dist_info.py with missing Windows library stubs: $($missingEntries.Count) entries added."
        }
        else {
            Write-Host "  > rocm_sdk _dist_info.py already contains all required Windows library stubs; no patch needed."
        }
    }
}

function Install-PythonDeps {
    param(
        [string]$RequirementsFile,
        [switch]$UpgradeTorch
    )

    if (-not (Test-Path $RequirementsFile)) {
        return
    }

    Write-Host "  > Installing packages from $RequirementsFile..."
    & $PYTHON_EXEC -m pip install --no-cache-dir -r $RequirementsFile
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install packages from $RequirementsFile"
    }

    # If AMD GPU is detected, override PyTorch with ROCm release wheels AFTER requirements.txt
    # so pip cannot replace them with a CPU-only build from PyPI.
    if (Test-AmdGpu) {
        $installedTorch = & $PYTHON_EXEC -m pip show torch 2>$null | Select-String "^Version:" | ForEach-Object { $_ -replace "^Version:\s*", "" }
        if ($UpgradeTorch -or ($installedTorch -ne $PYTORCH_TORCH_VERSION)) {
            Write-Host "  > Installing PyTorch ROCm wheels (current: '$installedTorch', want: '$PYTORCH_TORCH_VERSION')..."
            & $PYTHON_EXEC -m pip install --no-cache-dir --no-deps $PYTORCH_WHEELS
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to install PyTorch ROCm wheels"
            }
            Install-RocmSdk
        }
        else {
            Write-Host "  > PyTorch $PYTORCH_TORCH_VERSION already installed, skipping."
        }
    }
}

# Installs ComfyUI if it is not already present
function Install-ComfyUI {
    Test-Dependencies

    # --- Step 1: Ensure ComfyUI repository is cloned ---
    if (-not (Test-Path $COMFYUI_PATH)) {
        Write-Host "ComfyUI directory not found. Starting full installation..."
        $aitoolsPath = Split-Path $COMFYUI_PATH -Parent
        New-Item -ItemType Directory -Path $aitoolsPath -Force | Out-Null

        Write-Host "-> Cloning ComfyUI from $COMFYUI_REPO_URL..."
        git clone $COMFYUI_REPO_URL $COMFYUI_PATH
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to clone ComfyUI repository"
        }
    }
    else {
        Write-Host "ComfyUI directory already exists at $COMFYUI_PATH. Checking setup..."
    }

    # --- Step 2: Ensure Python virtual environment exists ---
    $venvPath = Join-Path $COMFYUI_PATH "venv-3.12"
    if (-not (Test-Path $venvPath)) {
        Write-Host "Python virtual environment not found. Creating it..."
        Write-Host "-> Creating virtual environment with Python 3.12..."
        
        # Find python command
        $pythonCmd = "python3.12"
        if (-not (Get-Command $pythonCmd -ErrorAction SilentlyContinue)) {
            $pythonCmd = "python"
        }
        
        & $pythonCmd -m venv $venvPath
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create virtual environment"
        }
    }
    else {
        Write-Host "Python virtual environment already exists."
    }

    # --- Step 3: Install dependencies ---
    Write-Host "-> Checking and installing Python dependencies..."
    $reqFile = Join-Path $COMFYUI_PATH "requirements.txt"
    Install-PythonDeps -RequirementsFile $reqFile
    Write-Host "Setup complete. You can now start ComfyUI using the 'start' command."
}

# Updates ComfyUI and all custom nodes from their git repositories
function Update-ComfyUI {
    Test-Dependencies
    $comfyUiUpdated = $false
    $comfyUiFailed = $false
    $updatedNodes = [System.Collections.Generic.List[string]]::new()
    $failedNodes = [System.Collections.Generic.List[string]]::new()
    Write-Host "Updating ComfyUI..."

    if (-not (Test-Path $COMFYUI_PATH)) {
        Write-Error "Error: ComfyUI directory not found at $COMFYUI_PATH"
        return $false
    }

    Write-Host "-> Pulling latest changes for ComfyUI repository..."
    Push-Location $COMFYUI_PATH
    try {
        # Ensure git trusts this directory (ownership changes after OS reinstall get a new SID)
        $ownerCheck = git rev-parse --git-dir 2>&1
        if ($ownerCheck -match "dubious ownership") {
            $safePath = ($COMFYUI_PATH -replace '\\', '/')
            Write-Host "  > Adding safe.directory exception for: $safePath"
            git config --global --add safe.directory $safePath
        }

        # If in a detached HEAD state, try to checkout a default branch before pulling
        $headRef = git symbolic-ref -q HEAD 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  > HEAD is detached. Attempting to switch to a default branch."
            $hasMain = git show-ref --verify --quiet refs/heads/main 2>&1; $?
            $hasMaster = git show-ref --verify --quiet refs/heads/master 2>&1; $?

            if ($hasMain) {
                Write-Host "  > Switching to 'main' branch."
                git checkout main
            }
            elseif ($hasMaster) {
                Write-Host "  > Switching to 'master' branch."
                git checkout master
            }
            else {
                Write-Host "  ! Could not find 'main' or 'master' branch. Update skipped."
                throw "No default branch found"
            }
        }

        $pullOutput = git pull 2>&1
        $pullOutput | ForEach-Object { Write-Host "  $_" }
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to pull ComfyUI updates"
        }
        $comfyUiUpdated = ($pullOutput -notmatch "Already up to date")

        # Install/update Python requirements for the main application
        $reqFile = Join-Path $COMFYUI_PATH "requirements.txt"
        Install-PythonDeps -RequirementsFile $reqFile -UpgradeTorch
    }
    catch {
        Write-Error "  ! Failed to update or install requirements for ComfyUI. Please check manually."
        $comfyUiFailed = $true
    }
    finally {
        Pop-Location
    }

    # Update custom nodes
    $customNodesPath = Join-Path $COMFYUI_PATH "custom_nodes"
    if (-not (Test-Path $customNodesPath)) {
        Write-Host "-> Custom nodes directory not found. Skipping node updates."
    }
    else {
        Write-Host "-> Updating custom nodes..."
        Get-ChildItem -Path $customNodesPath -Directory | ForEach-Object {
            $nodeDir = $_.FullName
            $gitDir = Join-Path $nodeDir ".git"

            if (Test-Path $gitDir) {
                $nodeName = $_.Name
                Write-Host "  - Updating node: $nodeName"

                Push-Location $nodeDir
                try {
                    # Ensure git trusts this directory (ownership changes after OS reinstall get a new SID)
                    $ownerCheck = git rev-parse --git-dir 2>&1
                    if ($ownerCheck -match "dubious ownership") {
                        $safePath = ($nodeDir -replace '\\', '/')
                        Write-Host "    > Adding safe.directory exception for: $safePath"
                        git config --global --add safe.directory $safePath
                    }

                    # If in a detached HEAD state, try to checkout a default branch
                    $headRef = git symbolic-ref -q HEAD 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host "    > HEAD is detached. Attempting to switch to a default branch."
                        $hasMain = git show-ref --verify --quiet refs/heads/main 2>&1; $?
                        $hasMaster = git show-ref --verify --quiet refs/heads/master 2>&1; $?

                        if ($hasMain) {
                            Write-Host "    > Switching to 'main' branch."
                            git checkout main
                        }
                        elseif ($hasMaster) {
                            Write-Host "    > Switching to 'master' branch."
                            git checkout master
                        }
                        else {
                            Write-Host "    ! Could not find 'main' or 'master' branch. Update skipped."
                            throw "No default branch found"
                        }
                    }

                    $pullOutput = git pull 2>&1
                    $pullOutput | ForEach-Object { Write-Host "    $_" }
                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to pull updates"
                    }

                    if ($pullOutput -notmatch "Already up to date") {
                        $updatedNodes.Add($nodeName)
                    }

                    # Check for and install Python requirements for the custom node
                    $nodeReqFile = Join-Path $nodeDir "requirements.txt"
                    Install-PythonDeps -RequirementsFile $nodeReqFile
                }
                catch {
                    Write-Error "  ! Failed to update or install requirements for node: $nodeName. Please check manually."
                    $failedNodes.Add($nodeName)
                }
                finally {
                    Pop-Location
                }
            }
        }
    }

    # --- Update Report ---
    Write-Host ""
    Write-Host "===== Update Report =====" -ForegroundColor Cyan
    if ($comfyUiFailed) {
        Write-Host "  ComfyUI core:   FAILED" -ForegroundColor Red
    }
    elseif ($comfyUiUpdated) {
        Write-Host "  ComfyUI core:   Updated" -ForegroundColor Green
    }
    else {
        Write-Host "  ComfyUI core:   Already up to date"
    }
    Write-Host "  Custom nodes updated:  $($updatedNodes.Count)"
    if ($updatedNodes.Count -gt 0) {
        $updatedNodes | ForEach-Object { Write-Host "    + $_" -ForegroundColor Green }
    }
    Write-Host "  Custom nodes failed:   $($failedNodes.Count)"
    if ($failedNodes.Count -gt 0) {
        $failedNodes | ForEach-Object { Write-Host "    x $_" -ForegroundColor Red }
    }
    Write-Host "=========================" -ForegroundColor Cyan

    $updateHadErrors = $comfyUiFailed -or ($failedNodes.Count -gt 0)
    return (-not $updateHadErrors)
}

# Checks if the ComfyUI process is currently running based on the PID file
function Test-Running {
    if (Test-Path $PID_FILE) {
        $processId = Get-Content $PID_FILE -Raw
        $processId = $processId.Trim()
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            return $true
        }
        catch {
            return $false
        }
    }
    return $false
}

# Starts the ComfyUI server
function Start-ComfyUI {
    if (Test-Running) {
        $processId = Get-Content $PID_FILE
        Write-Host "ComfyUI is already running with PID $processId."
        return
    }

    Write-Host "Starting ComfyUI... Log file can be found at: $LOG_FILE"
    
    # Determine cross-attention flag based on GPU type
    $crossAttentionFlag = "--use-split-cross-attention"
    if (Test-AmdGpu) {
        $crossAttentionFlag = "--use-pytorch-cross-attention"
        Write-Host "-> Using PyTorch cross-attention for AMD ROCm"
        # Set ROCm environment variables for GFX1100 (RDNA 3 / RX 7900 XTX)
        $env:TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL = "1"
        $env:HSA_OVERRIDE_GFX_VERSION = "11.0.0"
        $env:HIP_FORCE_DEV_KERNARG = "1"
        $env:PYTORCH_ALLOC_CONF = "garbage_collection_threshold:0.8,max_split_size_mb:512"
        # Add ROCm DLL directories to PATH so Windows can resolve DLL dependencies
        $venvSitePackages = Join-Path $COMFYUI_PATH "venv-3.12\Lib\site-packages"
        $dllPaths = @(
            (Join-Path $venvSitePackages "_rocm_sdk_core\bin"),
            (Join-Path $venvSitePackages "_rocm_sdk_libraries_custom\bin")
        )
        $rocmBin = Get-ChildItem "C:\Program Files\AMD\ROCm" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1 |
        ForEach-Object { Join-Path $_.FullName "bin" }
        if ($rocmBin) { $dllPaths += $rocmBin }
        foreach ($p in $dllPaths) {
            if (Test-Path $p) {
                $currentPaths = $env:PATH -split ';'
                if ($p -notin $currentPaths) {
                    $env:PATH = "$p;$env:PATH"
                    Write-Host "-> Added to PATH: $p"
                }
            }
        }
    }
    
    # Build complete startup options with cross-attention flag
    $completeStartupOptions = @($crossAttentionFlag) + $STARTUP_OPTIONS
    
    # Start the process in the background, redirecting output to log file
    # Use separate files for stdout and stderr, then combine them
    $stdoutLog = "$LOG_FILE.out"
    $stderrLog = "$LOG_FILE.err"
    $processArgs = @($MAIN_SCRIPT) + $completeStartupOptions
    $process = Start-Process -FilePath $PYTHON_EXEC `
        -ArgumentList $processArgs `
        -WorkingDirectory $COMFYUI_PATH `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -WindowStyle Hidden `
        -PassThru

    # Store the PID
    $process.Id | Out-File -FilePath $PID_FILE -NoNewline

    # Give it a moment to start up and then check if it's still running
    Start-Sleep -Seconds 2
    if (-not (Test-Running)) {
        Write-Error "Error: ComfyUI failed to start. Check the log for details."
        Write-Host "Stdout: $stdoutLog"
        Write-Host "Stderr: $stderrLog"
        Remove-Item $PID_FILE -ErrorAction SilentlyContinue
        return
    }

    Write-Host "ComfyUI started successfully with PID: $($process.Id)"
    Write-Host "Logs: $stdoutLog (stdout), $stderrLog (stderr)"
}

# Stops the ComfyUI server gracefully
function Stop-ComfyUI {
    if (-not (Test-Running)) {
        Write-Host "ComfyUI is not running."
        # If the PID file is stale, remove it
        Remove-Item $PID_FILE -ErrorAction SilentlyContinue
        return
    }

    $processId = Get-Content $PID_FILE
    $processId = $processId.Trim()
    Write-Host "Stopping ComfyUI (PID: $processId)..."

    try {
        $process = Get-Process -Id $processId -ErrorAction Stop
        
        # Try graceful shutdown first
        $process.CloseMainWindow() | Out-Null
        
        # Wait up to 5 seconds for it to terminate
        $waited = $process.WaitForExit(5000)
        
        if ($waited) {
            Write-Host "ComfyUI stopped."
        }
        else {
            Write-Host "ComfyUI did not stop gracefully after 5 seconds. Forcing termination..."
            Stop-Process -Id $processId -Force
            Write-Host "ComfyUI stopped forcefully."
        }
    }
    catch {
        Write-Host "Process not found or already terminated."
    }
    finally {
        Remove-Item $PID_FILE -ErrorAction SilentlyContinue
    }
}

# Takes ownership of the ComfyUI directory tree by the current user.
# Needed when the install was migrated from a previous Windows account (SID mismatch).
function Repair-Ownership {
    Write-Host "-> Taking ownership of: $COMFYUI_PATH"
    Write-Host "   This may take a moment for large directories..."

    takeown /f $COMFYUI_PATH /r /d y 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "takeown failed. Try running this script as Administrator."
        return
    }

    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    icacls $COMFYUI_PATH /grant "${currentUser}:(OI)(CI)F" /t /q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "icacls failed. Try running this script as Administrator."
        return
    }

    Write-Host "-> Ownership transferred to: $currentUser"
    Write-Host "-> Run 'update' again to proceed normally."
}

# Displays the current status of the ComfyUI server
function Show-Status {
    if (Test-Running) {
        $processId = Get-Content $PID_FILE
        Write-Host "ComfyUI is running with PID $processId."
    }
    else {
        Write-Host "ComfyUI is stopped."
    }
}

# --- Main Logic ---

# Parse command line argument (defaults to 'status')
$command = if ($args.Count -gt 0) { $args[0] } else { "status" }

switch ($command.ToLower()) {
    "start" {
        Start-ComfyUI
    }
    "stop" {
        Stop-ComfyUI
    }
    "restart" {
        Stop-ComfyUI
        Start-ComfyUI
    }
    "install" {
        Install-ComfyUI
    }
    "update" {
        $wasRunning = Test-Running
        if ($wasRunning) {
            Write-Host "-> ComfyUI is running. It will be stopped for the update and restarted afterward."
            Stop-ComfyUI
        }

        $updateSuccess = Update-ComfyUI
        if ($updateSuccess -and $wasRunning) {
            Write-Host "-> Restarting ComfyUI after successful update..."
            Start-ComfyUI
        }
        elseif (-not $updateSuccess) {
            Write-Error "Error: Update failed. ComfyUI will not be restarted. Please check the logs."
        }
    }
    "status" {
        Show-Status
    }
    "fix-ownership" {
        Repair-Ownership
    }
    default {
        $scriptName = Split-Path $MyInvocation.MyCommand.Path -Leaf
        Write-Host "Usage: $scriptName {start|stop|restart|status|update|install|fix-ownership}"
        exit 1
    }
}
