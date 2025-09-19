`````instructions
# sbin-installer AI Coding Agent Instructions
This is a **lightweight Windows package installer** that mimics macOS `/usr/sbin/installer` behavior. It's designed as a **simple, focused alternative to Chocolatey** for installing `.pkg` files (ZIP archives with metadata).

### Core Philosophy
- **Minimal and deterministic** - no cache, no complex state management
- **Direct operation** - `--pkg <path>` instead of repository sources
- **Unix philosophy** - do one thing well: install packages
- **Security-conscious** - elevation checks, script validation, proper permissions

## Architecture

### Key Components

```
src/installer/
├── Models/
│   ├── BuildInfo.cs           # YAML metadata structure from build-info.yaml
│   └── InstallModels.cs       # Command options, results, package info
├── Services/
│   └── PackageInstaller.cs    # Core installation logic
└── Program.cs                 # CLI interface using System.CommandLine
```

### Installation Flow
1. **Extract** `.pkg` (ZIP) to temp directory
2. **Parse** `build-info.yaml` metadata
3. **Execute** pre-install script (`scripts/preinstall.ps1` OR `tools/chocolateyBeforeInstall.ps1`)
4. **Mirror** files from `payload/` to target directory
5. **Execute** post-install script (`scripts/postinstall.ps1` OR `tools/chocolateyInstall.ps1`)
6. **Cleanup** temp directory

### Package Structure
```
package.pkg (ZIP)
├── payload/              # Files to install
├── scripts/              # Our preferred scripts
│   ├── preinstall.ps1
│   └── postinstall.ps1
├── tools/                # Chocolatey compatibility
│   ├── chocolateyBeforeInstall.ps1
│   └── chocolateyInstall.ps1
└── build-info.yaml       # Package metadata
```

## Development Patterns

### Command-Line Interface
- **System.CommandLine** for argument parsing - maintains macOS installer compatibility
- **Custom binder** (`InstallOptionsBinder`) maps CLI options to `InstallOptions` model
- **Target resolution** handles multiple formats: `/`, `CurrentUserHomeDirectory`, drive letters, paths

### Security & Elevation
- `IsElevated()` checks for admin privileges using `WindowsPrincipal`
- System directory installations require elevation
- PowerShell scripts run with `-ExecutionPolicy Bypass`

### Error Handling
- **Graceful degradation** - cleanup temp directories on failures
- **Exit codes** follow Unix conventions (0 = success, >0 = error)
- **Detailed logging** with configurable verbosity levels

### Cross-Platform Considerations
- **Windows-specific** implementation (WindowsIdentity, drive mapping)
- **PowerShell dependency** for script execution
- **File path handling** uses `Path.Combine()` and `Path.GetFullPath()`

## Key Implementation Details

### Package Extraction
```csharp
ZipFile.ExtractToDirectory(packagePath, tempDir);
// Always clean up: try { Directory.Delete(tempDir, true); } catch { }
```

### Script Execution
- Uses `Process` with `RedirectStandardOutput/Error`
- **Working directory** set to extracted package path
- **Async execution** with proper output handling
- **Exit code validation** - non-zero = failure

### File Mirroring
- **Preserves directory structure** from `payload/` to target
- **Overwrites existing files** (`File.Copy(source, target, true)`)
- **Creates directories** as needed

## Common Tasks

### Adding New Command Options
1. Add property to `InstallOptions` model
2. Create `Option<T>` in `Program.cs`
3. Update `InstallOptionsBinder.GetBoundValue()`
4. Handle in `HandleCommandAsync()`

### Extending Package Metadata
1. Add properties to `BuildInfo` model with `[YamlMember]` attributes
2. Update sample `build-info.yaml` in README
3. Handle new properties in display methods

### Adding New Target Types
- Update `ResolveTargetDirectory()` in `PackageInstaller.cs`
- Add to domain info display in `ShowDomainInfo()`
- Document in README target resolution section

## Dependencies

- **System.CommandLine** (2.0.0-beta) - CLI argument parsing
- **YamlDotNet** (13.7.1) - build-info.yaml parsing  
- **Microsoft.Extensions.Logging** - structured logging
- **.NET 9** - modern C# features, single-file publishing

## Build Configuration

- **Single-file executable** with `PublishSingleFile=true`
- **Self-contained** deployment with embedded runtime
- **Win-x64 specific** - Windows-only tool
- **Compression enabled** for smaller executable size

## Testing Approach

### Manual Testing
- Create test `.pkg` files with various structures
- Test elevation scenarios (admin vs user)
- Verify script execution and error handling
- Test all command-line options

### Edge Cases to Consider
- **Malformed packages** - missing directories, invalid YAML
- **Permission errors** - locked files, insufficient rights
- **Script failures** - syntax errors, execution policy issues
- **Large packages** - memory usage, extraction time

## Maintenance Notes

### When Adding Features
- **Maintain CLI compatibility** with macOS installer where possible
- **Preserve simplicity** - avoid feature creep like Chocolatey
- **Document breaking changes** clearly in README
- **Test with real packages** created by cimipkg

### Performance Considerations
- **Avoid caching** - always extract fresh (by design)
- **Stream large files** if memory usage becomes an issue  
- **Parallel extraction** could be added for large packages
- **Cleanup is critical** - temp directories can accumulate

This tool succeeds by staying focused and simple. When in doubt, ask: "Would macOS installer do this?"
`````