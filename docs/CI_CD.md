# CI/CD & Release Pipeline

LoRAMancer uses GitHub Actions to automate builds, testing, and distribution for Windows x64.

---

## Single Source of Truth for Versions

The project version is managed in a single settings file located in the root repository directory:

```json
// version.json
{
  "version": "1.0.0"
}
```

Whenever you want to bump the release version, edit this file. The build pipeline and installer script automatically consume it.

---

## Branching & Release Architecture

LoRAMancer uses a clean two-branch deployment strategy:

```
feature/bugfix branches
        │ (no CI builds)
        ▼
   dev branch ───► GitHub Actions: "LoRAMancer Dev Build"
        │          ├── Compiles Release binaries
        │          ├── Packages ZIP & Inno Setup EXE
        │          └── Publishes rolling "dev-preview" Pre-Release
        │
   (stable PR / merge)
        ▼
  main branch ───► GitHub Actions: "LoRAMancer Release"
                   ├── Runs complete test suite
                   ├── Compiles Release binaries
                   ├── Packages ZIP & Inno Setup EXE
                   └── Publishes official "vX.Y.Z" Latest Release
```

### 1. Dev Track (`dev` Branch)
- **Trigger**: Every push to `dev`, or manual trigger via `workflow_dispatch`.
- **Versioning**: `$baseVersion-dev.<github_run_number>` (e.g., `1.0.0-dev.14`).
- **Release Target**: Updates the rolling GitHub **Pre-release** tagged `dev-preview` with the latest `.exe` installer, `.zip` archive, and SHA-256 checksums.

### 2. Stable Track (`main` Branch)
- **Trigger**: Every push or merge into `main`, or manual trigger via `workflow_dispatch`.
- **Versioning**: Uses the clean version from `version.json` (e.g., `1.0.0`).
- **Release Target**: Creates or updates an official permanent release (e.g., `v1.0.0`) marked as **Latest Release**.

### 3. Feature / Working Branches
- Any other branch name (such as `feature/*`, `fix/*`, `experiment/*`) will **not** trigger builds on push. You can push as many intermediate commits as needed without triggering CI runs.

---

## In-App Auto-Update & Channel Awareness

LoRAMancer's internal auto-updater (`AutoUpdateService`) is build-aware:

1. **Automatic Channel Presetting**:
   - If the installed app is built from `dev` (version containing `-dev` or `-preview`), the updater automatically defaults to the **Dev Preview** channel.
   - If built from `main`, it defaults to the **Stable** channel.
2. **Channel Endpoints**:
   - **Stable Channel**: Queries GitHub Releases API for `/releases/latest`.
   - **Dev Channel**: Queries GitHub Releases API for `/releases/tags/dev-preview`.
3. **Manual Channel Switching**:
   - Users can switch their preferred channel directly inside the app on the `/updates` page.


## Local Packaging

To run the full build, test, and packaging pipeline locally on Windows:

```powershell
# Build and package based on version.json
.\Build-And-Package.ps1

# Override version dynamically for local testing
.\Build-And-Package.ps1 -Version "1.0.0-beta.1"

# Build and immediately install locally for testing
.\Build-And-Package.ps1 -Install
```
