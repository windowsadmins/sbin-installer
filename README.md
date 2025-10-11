# sbin-installer

A lightweight, deterministic `.pkg` installer for Windows, inspired by the elegant simplicity of macOS package installation `/usr/sbin/installer` - this tool provides a simple, Chocolatey-free alternative for installing packages with a clean command-line interface.

This tool follows the Unix philosophy: **Do one thing and do it well.** 

## Why This Exists

Chocolatey is a powerful tool, but it's often excessive for simple package installation tasks. Its package management model is more akin to `brew` on macOS—feature-rich, but with added complexity, state management, and overhead that aren't necessary when you just want to install a package quickly and cleanly.

All we want to run is `installer.exe --pkg /path/to/pkg --target /` on Windows.

### The Problem with Chocolatey

- Maintains its own cache and complex state management
- Requires a "source" concept with folder scanning
- Overly complex for simple package installation
- Heavy dependencies and slow performance
- Uses `.nupkg` extension (we prefer `.pkg`)

### This Solution

A minimal, focused installer that:
- **No cache** - runs directly from package location
- **No --source** - you specify `--pkg <pathToPackage>` directly
- **Lightweight** - mimics `/usr/sbin/installer` behavior
- **Deterministic** - predictable, simple operation
- **Native .NET 9** - single executable, no external dependencies

## How It Works

A `.pkg` file is simply a ZIP archive with a specific structure:

```
package.pkg (ZIP file)
├── payload/                   # Files and directories to be copied to target
│   └── example.txt
├── scripts/                   # Pre/Post-install scripts
│   ├── preinstall.ps1         # Runs before files are installed
│   └── postinstall.ps1        # Runs after files are installed
└── build-info.yaml            # Package metadata
```

### Chocolatey Package Support

`sbin-installer` also supports `.nupkg` (NuGet/Chocolatey) packages with automatic compatibility shims:

**What's Supported:**
- ✅ All NuGet schema versions (2010/07, 2011/08, 2012/06, 2013/01, etc.)
- ✅ Common Chocolatey helper functions automatically shimmed:
  - `Install-ChocolateyPath`, `Install-ChocolateyEnvironmentVariable`
  - `Install-ChocolateyPackage`, `Install-ChocolateyZipPackage`
  - `Get-ChocolateyWebFile`, `Get-ChocolateyUnzip`, `Install-ChocolateyShortcut`
  - Plus utility functions for architecture detection and environment management
- ✅ Automatic detection of `tools/chocolatey*.ps1` scripts with helper injection
- ✅ Tested with real packages (osquery 5.19.0, and more)

**Example:**
```powershell
# Install Chocolatey packages directly - no Chocolatey installation needed
installer.exe osquery.5.19.0.nupkg

# Result: Files installed, PATH updated, services configured automatically
```

**What's NOT Supported:**
- Package management database and state tracking
- Chocolatey sources/feeds and repository management  
- Automatic dependency resolution
- Package upgrades and side-by-side versions
- All ~50+ Chocolatey helpers (only common ones)

**When to Use:**
- ✅ Deploying via Intune, SCCM, or scripts with local/network `.nupkg` files
- ✅ Need lightweight, deterministic installation without Chocolatey overhead
- ✅ Want macOS-style `/usr/sbin/installer` behavior on Windows
- ❌ Need full package management with dependencies and updates → Use Chocolatey

A bridge between `.pkg` and `.nupkg` formats for deployment scenarios.

### Installation Process

1. **Extract** the `.pkg` (really a zip) to a temporary directory
2. **Run pre-install script** (`scripts/preinstall.ps1` or `tools/chocolateyBeforeInstall.ps1`)
3. **Mirror payload** from `payload/` directory to target location
4. **Run post-install script** (`scripts/postinstall.ps1` or `tools/chocolateyInstall.ps1`)
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
# Display package metadata
installer --pkginfo --pkg package.pkg

# Query specific information
installer --query RestartAction --pkg package.pkg
installer --query name --pkg package.pkg
installer --query version --pkg package.pkg
```

### System Information
```bash
# Show available installation domains
installer --dominfo

# Show available volumes
installer --volinfo

# Show version
installer --vers
```

### Verbose Output
```bash
# Detailed logging
installer --pkg package.pkg --target / --verbose

# Dump logs to stderr
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

## Package Creation

Packages are created using [cimipkg](https://github.com/windowsadmins/cimian-pkg). The folder structure is:

```
project/
├── payload/                   # Files/folders to be written to disk
│   └── example.txt
├── scripts/                   # Pre-/Post-install scripts  
│   ├── preinstall.ps1         # Runs before files are installed
│   └── postinstall.ps1        # Runs after files are installed
└── build-info.yaml            # Metadata for package generation
```

### Sample build-info.yaml
```yaml
name: "MyApplication"
version: "1.0.0"
description: "A sample application package"
author: "Your Name"
license: "MIT"
target: "/"
restart_action: "None"
dependencies: []
```

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
| Package format | `.nupkg` | `.pkg` (ZIP) + `.nupkg` support |
| Dependency resolution | Full tree | Simple list |
| Script support | tools\chocolatey*.ps1 | ✅ Supported via shim + scripts/*.ps1 |
| Chocolatey helpers | Native | ✅ Common helpers shimmed |
| Performance | Slow | Fast |
| Complexity | High | Minimal |
| Package database | Yes | No |
| Use case | Package management | Direct installation |

## Installation

### Quick Start

**MSI Installer (Recommended)**
```bash
# Download from GitHub releases and run:
msiexec /i sbin-installer.msi /quiet
# Installs to C:\Program Files\sbin\ and adds to PATH
```

**Portable Executable**
```bash
# Download installer.exe, place anywhere, no installation required
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
# Basic build (includes executable + MSI with timestamp version)
.\build.ps1

# Clean build with tests
.\build.ps1 -Clean -Test

# Executable only (skip MSI)
.\build.ps1 -SkipMsi

# Custom version (default is timestamp: YYYY.MM.DD.HHMM)
.\build.ps1 -Version "2025.09.18.1200"

# Architecture-specific build (auto-detected)
.\build.ps1 -Configuration Release
```

### Code Signing & Enterprise
```bash
# Certificate management
.\build.ps1 -ListCerts                                 # List available certificates
.\build.ps1 -FindCertSubject "Your Company"           # Find by subject

# Build with signing (auto-detects best certificate)
.\build.ps1                                            # Auto-detects and uses certificate
.\build.ps1 -CertificateThumbprint "THUMBPRINT"       # Use specific certificate

# Enterprise installation
.\build.ps1 -Install                                   # Build, auto-sign, install
```

### System Installation
```bash
.\build.ps1 -Install                    # Install to C:\Program Files\sbin\
.\install.ps1 -Force                    # Force overwrite existing
```

**Features:**
- Auto-detects and uses code signing certificates when available
- Auto-detects architecture (x64, ARM64, x86) and uses appropriate Program Files
- Uses `%ProgramW6432%` for x64/ARM64 systems  
- Adds `C:\Program Files\sbin` to system PATH
- Enables `installer` command globally

### Advanced Build Options
```bash
# Manual .NET build
dotnet publish src/installer/installer.csproj --configuration Release --runtime win-x64 --self-contained -o dist

# Skip MSI build (executable only)
.\build.ps1 -SkipMsi

# Custom timestamp version (format: YYYY.MM.DD.HHMM)
.\build.ps1 -Version "2025.12.25.1430"

# If WiX tool permission issues occur, try:
# 1. Run PowerShell as Administrator  
# 2. Or reinstall: dotnet tool uninstall --global wix; dotnet tool install --global wix
# 3. Or skip MSI: .\build.ps1 -SkipMsi
```

**Note**: MSI packaging is included by default but may require elevated permissions on some systems. Use `-SkipMsi` for environments with restricted permissions. Version format follows `YYYY.MM.DD.HHMM` timestamp convention.

## Requirements

- **Windows 10/11** or Windows Server 2019+
- **PowerShell** for script execution
- **Administrator privileges** for system-wide installations

