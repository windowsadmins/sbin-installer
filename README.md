# sbin-installer

A lightweight, deterministic package installer for Windows supporting `.msi`, `.nupkg`, and `.pkg` formats.

Inspired by the elegant simplicity of macOS package installation `/usr/sbin/installer` — this tool provides a simple, Chocolatey-free alternative for installing packages with a clean command-line interface.

This tool follows the Unix philosophy: **Do one thing and do it well.** 

## Why This Exists

Chocolatey is a powerful tool, but it's often excessive for simple package installation tasks. Its package management model is more akin to `brew` on macOS—feature-rich, but with added complexity, state management, and overhead that aren't necessary when you just want to install a package quickly and cleanly.

All we want to run is `installer.exe --pkg /path/to/pkg --target /` on Windows.

### The Problem with Chocolatey

- Maintains its own cache and complex state management
- Requires a "source" concept with folder scanning
- Overly complex for simple package installation
- Heavy dependencies and slow performance
- Uses `.nupkg` extension (we prefer `.msi`)

### This Solution

A minimal, focused installer that:
- **No cache** - runs directly from package location
- **No --source** - you specify `--pkg <pathToPackage>` directly
- **Lightweight** - mimics `/usr/sbin/installer` behavior
- **Deterministic** - predictable, simple operation
- **Native MSI support** - in-process DTF API, no `msiexec` dependency
- **Native .NET** - single executable, no external dependencies

## Package Source Structure

Regardless of output format (`.msi`, `.nupkg`, or `.pkg`), every cimipkg
project has the same source layout:

```
my-package/
├── build-info.yaml    # Package metadata (required)
├── payload/           # Files to install (optional)
├── .env               # Signing credentials + script variables (optional, gitignored)
└── scripts/           # PowerShell install/uninstall scripts (optional)
    ├── preinstall.ps1
    ├── postinstall.ps1
    └── uninstall.ps1
```

`cimipkg` compiles this into whichever output format you choose. The source
structure never changes — only the packaging does.

## How It Works

### .msi Format (Recommended)

`.msi` is now the **default output format** for [cimipkg](https://github.com/windowsadmins/cimian-pkg)
and the recommended format for all new packages. cimipkg builds MSIs natively
via the DTF API (WixToolset.Dtf.WindowsInstaller) — no WiX Toolset or
`msiexec` dependency at build time.

sbin-installer processes cimipkg-built MSIs via the same DTF API in-process,
providing better error handling, progress callbacks, and conflict resolution
than shell-out to `msiexec.exe`. It can install any standard MSI, but for
third-party or non-cimipkg MSIs you're generally better off using `msiexec`
directly — sbin-installer's upgrade logic (UpgradeCode detection, conflict
removal, repair pass) is tuned for the conventions cimipkg embeds.

**What's inside a cimipkg-built MSI:**

| MSI feature | Detail |
|---|---|
| **Payload files** | Embedded in a compressed CAB archive inside the MSI |
| **PowerShell scripts** | Compiled to silent VBScript custom actions with pwsh 7 runtime detection (falls back to powershell.exe 5.1) |
| **build-info.yaml** | Serialized into the `CIMIAN_PKG_BUILD_INFO` MSI property for metadata round-trip |
| **UpgradeCode** | Deterministic UUID v5 derived from `product.identifier` — stable across versions, enables automatic upgrade |
| **File versioning** | PE FileVersion extracted from binaries and stamped in the MSI File table; unversioned files get a synthetic version from the package version so every build overwrites on-disk files |
| **Signing** | Authenticode-signed via `signtool` if a certificate is configured |

**How sbin-installer processes MSIs:**

1. Reads `ProductName`, `ProductVersion`, `UpgradeCode` from the MSI property table
2. Detects conflicting products (by UpgradeCode or display name) — handles WiX-to-cimipkg transitions
3. Installs the new MSI silently (`ALLUSERS=1`, `REBOOT=ReallySuppress`)
4. If conflicts were removed, runs a repair pass (`REINSTALL=ALL REINSTALLMODE=amus`) to restore files with different component GUIDs
5. Logs to `%TEMP%\cimian_msi_*.log`

### .nupkg Format (NuGet/Chocolatey)

A `.nupkg` file is a ZIP archive following the NuGet package specification,
commonly used by Chocolatey. Build with `cimipkg --nupkg <project>`.

```
package.nupkg
├── [Content_Types].xml
├── package.nuspec
├── _rels/
│   └── .rels
└── tools/
    ├── chocolateyInstall.ps1         # Payload copy + postinstall scripts
    ├── chocolateyBeforeModify.ps1    # Preinstall scripts (runs before upgrade/uninstall)
    ├── chocolateyUninstall.ps1       # Uninstall script
    └── payload/
        └── example.txt
```

**Chocolatey limitation:** `chocolateyBeforeModify.ps1` only fires when an
existing package is being upgraded or uninstalled — not on a fresh install.
sbin-installer does not have this limitation and runs `chocolateyBeforeModify.ps1`
unconditionally before every install.

### .pkg Format (Legacy — deprecated)

> **Note:** The `.pkg` format is deprecated. New packages should use `.msi`
> (the default). sbin-installer continues to support `.pkg` for backward
> compatibility, but no new features will be added to the `.pkg` code path.
> Build with `cimipkg --pkg <project>` if you still need it.

A `.pkg` file is a ZIP archive created by [cimipkg](https://github.com/windowsadmins/cimian-pkg):

```
package.pkg
├── payload/
│   └── example.txt
├── scripts/
│   ├── preinstall.ps1
│   ├── postinstall.ps1
│   └── uninstall.ps1
└── build-info.yaml
```

### Chocolatey Package Support

`sbin-installer` installs `.nupkg` (NuGet/Chocolatey) packages with automatic compatibility shims:

**What's Supported:**
- All NuGet schema versions (2010/07, 2011/08, 2012/06, 2013/01, etc.)
- Common Chocolatey helper functions automatically shimmed:
  - `Install-ChocolateyPath`, `Install-ChocolateyEnvironmentVariable`
  - `Install-ChocolateyPackage`, `Install-ChocolateyZipPackage`
  - `Get-ChocolateyWebFile`, `Get-ChocolateyUnzip`, `Install-ChocolateyShortcut`
  - Plus utility functions for architecture detection and environment management
- Automatic detection of `tools/chocolatey*.ps1` scripts with helper injection
- Tested with real packages (osquery 5.19.0, and more)

**Example:**
```powershell
installer.exe osquery.5.19.0.nupkg
```

**What's NOT Supported:**
- Package management database and state tracking
- Chocolatey sources/feeds and repository management  
- Automatic dependency resolution
- Package upgrades and side-by-side versions
- All ~50+ Chocolatey helpers (only common ones)

**When to Use:**
- **Yes:** Deploying via Intune, SCCM, or scripts with local/network `.nupkg` files
- **Yes:** Need lightweight, deterministic installation without Chocolatey overhead
- **Yes:** Want macOS-style `/usr/sbin/installer` behavior on Windows
- **No:** Need full package management with dependencies and updates → Use Chocolatey

### Installation Process

**MSI packages:**
1. Read metadata (ProductName, ProductVersion, UpgradeCode) from MSI property table
2. Detect and remove conflicting products (by UpgradeCode or display name)
3. Install silently via DTF in-process API
4. If conflicts were removed, repair to restore files with different component GUIDs
5. Custom actions (preinstall/postinstall/uninstall) run automatically as part of the MSI sequence

**nupkg and pkg packages:**
1. **Extract** the package to a temporary directory
2. **Run pre-install script** (`chocolateyBeforeModify.ps1` for .nupkg or `scripts/preinstall.ps1` for .pkg)
3. **Mirror payload** to the install location
4. **Run post-install script** (`chocolateyInstall.ps1` for .nupkg or `scripts/postinstall.ps1` for .pkg)
5. **Clean up** temporary extraction directory

## Usage

### Basic Installation
```bash
installer --pkg package.pkg --target /
installer --pkg package.pkg --target CurrentUserHomeDirectory
installer --pkg C:\packages\myapp.pkg --target C:\Program Files
```

### Package Information
```bash
installer --pkginfo --pkg package.pkg
installer --query RestartAction --pkg package.pkg
installer --query name --pkg package.pkg
installer --query version --pkg package.pkg
```

### System Information
```bash
installer --dominfo
installer --volinfo
installer --vers
```

### Verbose Output
```bash
installer --pkg package.pkg --target / --verbose
installer --pkg package.pkg --target / --dumplog
```

## Command-Line Options

Closely mirrors macOS `/usr/sbin/installer` options:

| Option | Description |
|--------|-------------|
| `--pkg <path>` | Path to the .pkg file to install |
| `--target <device>` | Target directory (`/`, `CurrentUserHomeDirectory`, drive letter, or path) |
| `--pkginfo` | Display package information |
| `--dominfo` | Display domains available for installation |
| `--volinfo` | Display volumes available for installation |
| `--query <flag>` | Query package metadata (`RestartAction`, `name`, `version`, etc.) |
| `--verbose` | Display detailed information |
| `--verboseR` | Display detailed information with simplified progress |
| `--dumplog` | Write log information to standard error |
| `--plist` | Display information in XML plist format |
| `--allowUntrusted` | Allow untrusted package signatures |
| `--vers` | Display version information |
| `--config` | Display command line parameters |

## Target Resolution

The `--target` parameter supports multiple formats:

- `/` → `C:\` (system root)
- `CurrentUserHomeDirectory` → User's home folder
- `/Volumes/Drive` → `Drive:\` (Windows drive mapping)
- `C` → `C:\` (drive letter)
- Any absolute path → Used directly

## Package Creation with cimipkg

Packages are created using [cimipkg](https://github.com/windowsadmins/cimian-pkg).
MSI is the default output format:

```bash
# Build an MSI (default)
cimipkg <project_directory>

# Build a Chocolatey .nupkg
cimipkg --nupkg <project_directory>

# Build a legacy .pkg (deprecated)
cimipkg --pkg <project_directory>
```

### Project Structure

```
project/
├── build-info.yaml    # Package metadata (required)
├── payload/           # Files to install (optional)
├── .env               # Signing credentials + script variables (optional, gitignored)
└── scripts/           # PowerShell install/uninstall scripts (optional)
    ├── preinstall.ps1     # Runs before payload is copied
    ├── postinstall.ps1    # Runs after payload is copied
    └── uninstall.ps1      # Runs when the package is removed
```

### Example build-info.yaml

```yaml
product:
  name: MyApplication
  version: ${TIMESTAMP}
  identifier: com.company.myapplication
  developer: ACME Corp
  description: A sample application package
install_location: C:\Program Files\MyApplication
postinstall_action: none
signing_thumbprint: ${SIGNING_CERT_THUMBPRINT}
```

Any `${NAME}` placeholder in build-info.yaml is resolved from built-in tokens
(`TIMESTAMP`, `DATE`, `DATETIME`), a `.env` file, or process environment
variables. This lets signing credentials stay out of source control. See the
[cimipkg README](https://github.com/windowsadmins/cimian-pkg#placeholders) for
the full placeholder reference.

## Security & Elevation

- **Automatic elevation detection** - Warns when admin rights are required
- **Target validation** - Ensures appropriate permissions for installation paths
- **Script execution** - Runs PowerShell scripts with bypass execution policy
- **Signature validation** - Supports `--allowUntrusted` for unsigned packages

## Comparison

| Feature | Chocolatey | sbin-installer |
|---------|------------|----------------|
| Cache management | Complex | None |
| Source repositories | Required | Direct file path |
| Package format | `.nupkg` | `.msi` (default) + `.nupkg` + `.pkg` (legacy) |
| MSI support | Via `msiexec` shelling | Native DTF in-process API |
| Dependency resolution | Full tree | Simple list |
| Script support | tools\chocolatey*.ps1 | Yes (Supported via shim + scripts/*.ps1 + MSI custom actions) |
| Chocolatey helpers | Native | Yes (Common helpers shimmed) |
| BeforeModify on fresh install | No | Yes (always runs preinstall) |
| Performance | Slow | Fast |
| Complexity | High | Minimal |
| Package database | Yes | No |
| Use case | Package management | Direct installation |

## Installation

### Quick Start

**MSI Installer (Recommended)**
```bash
msiexec /i sbin-installer.msi /quiet
```

**Portable Executable**
```bash
installer --pkg package.pkg --target /
```

### Enterprise Deployment

- **GitHub Actions CI/CD**: Automated builds for x64/ARM64/x86 architectures
- **Code Signing**: Certificate-based signing with timestamp validation
- **Silent Installation**: `msiexec /i sbin-installer.msi /quiet`
- **Elevation Handling**: Auto-detects `sudo` (Win11 22H2+) or PowerShell UAC
- **Path Management**: Uses `%ProgramW6432%` for proper architecture detection

### Development Build
```bash
.\build.ps1
.\build.ps1 -Clean -Test
.\build.ps1 -SkipMsi
.\build.ps1 -Version "2025.09.18.1200"
.\build.ps1 -Configuration Release
```

### Code Signing & Enterprise
```bash
.\build.ps1 -ListCerts
.\build.ps1 -FindCertSubject "Your Company"
.\build.ps1
.\build.ps1 -CertificateThumbprint "THUMBPRINT"
.\build.ps1 -Install
```

### System Installation
```bash
.\build.ps1 -Install
.\install.ps1 -Force
```

**Features:**
- Auto-detects and uses code signing certificates when available
- Auto-detects architecture (x64, ARM64, x86) and uses appropriate Program Files
- Uses `%ProgramW6432%` for x64/ARM64 systems  
- Adds `C:\Program Files\sbin` to system PATH
- Enables `installer` command globally

### Advanced Build Options
```bash
dotnet publish src/installer/installer.csproj --configuration Release --runtime win-x64 --self-contained -o dist
.\build.ps1 -SkipMsi
.\build.ps1 -Version "2025.12.25.1430"
```

**Note**: MSI packaging is included by default but may require elevated permissions on some systems. Use `-SkipMsi` for environments with restricted permissions. Version format follows `YYYY.MM.DD.HHMM` timestamp convention.

## Requirements

- **Windows 10/11** or Windows Server 2019+
- **PowerShell** for script execution
- **Administrator privileges** for system-wide installations

