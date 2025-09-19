# sbin-installer

A lightweight, deterministic `.pkg` installer for Windows, inspired by `/usr/sbin/installer` - the elegant simplicity of macOS package installation, brought to Windows.
This tool provides a simple, Chocolatey-free alternative for installing packages with a clean command-line interface.
This tool follows the Unix philosophy: **Do one thing and do it well.** 

## Why This Exists

**Chocolatey is overkill.** It's overcomplicated and does way more than we need and its package manager like `brew` on the Mac. 

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
| Package format | `.nupkg` | `.pkg` (same ZIP, better name) |
| Dependency resolution | Full tree | Simple list |
| Script support | tools\chocolatey*.ps1 | + scripts/*.ps1 |
| Performance | Slow | Fast |
| Complexity | High | Minimal |

## Building and Installation

### Quick Installation

**MSI Installer**
```bash
# Download from releases page
# https://github.com/windowsadmins/sbin-installer/releases
# Run the .msi file - it will install to C:\Program Files\sbin\ and add to PATH
# Make sure to sign with your enterprise certificate if your enviroment demands it.
```

### Development Build
```bash
# Basic build
.\build.ps1

# Clean build with tests
.\build.ps1 -Clean -Test

# Architecture-specific build (auto-detected)
.\build.ps1 -Configuration Release
```

### Code Signing
For production deployment, the executable must be signed:

```bash
# List available code signing certificates
.\cert-helper.ps1 -List

# Find certificate by subject
.\cert-helper.ps1 -Find -Subject "Your Company"

# Sign with certificate from certificate store
.\build.ps1 -CertificateThumbprint "YOUR_CERT_THUMBPRINT"

# Sign and install system-wide (requires admin)
.\build.ps1 -CertificateThumbprint "YOUR_CERT_THUMBPRINT" -Install
```

### System Installation
Install to `C:\Program Files\sbin\installer.exe` (or architecture-appropriate Program Files):

```bash
# Install to system (requires Administrator privileges)
.\build.ps1 -Install

# Or use dedicated installation script
.\install.ps1

# Build, sign, and install in one step
.\build.ps1 -CertificateThumbprint "YOUR_THUMBPRINT" -Install

# Force overwrite existing installation
.\install.ps1 -Force
```

The installer automatically:
- Detects architecture (x64, ARM64, x86) and uses appropriate Program Files directory
- Uses `%ProgramW6432%` for x64 and ARM64 systems
- Adds `C:\Program Files\sbin` to system PATH
- Enables running `installer` command from any location

### Manual Installation
```bash
dotnet publish src/installer/installer.csproj --configuration Release --runtime win-x64 --self-contained -o dist
```

### MSI Package Build
```bash
# Build MSI installer package
.\build\build-msi.ps1

# Build with version and signing
.\build\build-msi.ps1 -Version "1.0.1" -CertificateThumbprint "YOUR_THUMBPRINT"

# Clean build
.\build\build-msi.ps1 -Clean -Configuration Release
```

The MSI installer:
- Installs to `C:\Program Files\sbin\installer.exe`
- Automatically adds to system PATH
- Includes Windows uninstaller integration
- Supports silent installation: `msiexec /i sbin-installer.msi /quiet`

## Requirements

- **Windows 10/11** or Windows Server 2019+
- **PowerShell** for script execution
- **Administrator privileges** for system-wide installations

