#!/usr/bin/env pwsh

# Build script for sbin-installer
param(
    [string]$Configuration = "Release",
    [switch]$Clean,
    [switch]$Test,
    [switch]$Install,
    [string]$CertificateThumbprint,
    [string]$TimeStampServer = "http://timestamp.digicert.com",
    [switch]$ListCerts,
    [string]$FindCertSubject,
    [switch]$SkipMsi,
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# Generate timestamp version if not provided
if ([string]::IsNullOrEmpty($Version)) {
    $now = Get-Date
    $Version = "$($now.Year).$($now.Month.ToString('D2')).$($now.Day.ToString('D2')).$($now.Hour.ToString('D2'))$($now.Minute.ToString('D2'))"
}

# Certificate management functions
function Find-CodeSigningCerts {
    param([string]$SubjectFilter = "")
    
    $certs = @()
    $stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")
    
    foreach ($store in $stores) {
        $storeCerts = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object {
            # Allow certificates with Code Signing EKU OR certificates that can be used for signing (have private key)
            ($_.EnhancedKeyUsageList -like "*Code Signing*" -or $_.HasPrivateKey) -and 
            $_.NotAfter -gt (Get-Date) -and
            ($SubjectFilter -eq "" -or $_.Subject -like "*$SubjectFilter*")
        }
        
        if ($storeCerts) {
            $certs += $storeCerts | Select-Object *, @{Name='Store'; Expression={$store}}
        }
    }
    
    return $certs | Sort-Object NotAfter -Descending
}

function Show-CertificateList {
    $certs = Find-CodeSigningCerts
    
    if ($certs) {
        Write-Host "Available code signing certificates:" -ForegroundColor Green
        for ($i = 0; $i -lt $certs.Count; $i++) {
            $cert = $certs[$i]
            Write-Host ""
            Write-Host "[$($i + 1)] Subject: $($cert.Subject)" -ForegroundColor Cyan
            Write-Host "    Issuer:  $($cert.Issuer)" -ForegroundColor Gray
            Write-Host "    Thumbprint: $($cert.Thumbprint)" -ForegroundColor Yellow
            Write-Host "    Valid Until: $($cert.NotAfter)" -ForegroundColor Gray
            Write-Host "    Store: $($cert.Store)" -ForegroundColor Gray
        }
        Write-Host ""
    } else {
        Write-Host "No valid code signing certificates found" -ForegroundColor Yellow
    }
    
    return $certs
}

function Get-BestCertificate {
    $certs = Find-CodeSigningCerts
    
    # First priority: Enterprise certificate (EmilyCarrU Intune)
    $enterpriseCert = $certs | Where-Object { $_.Subject -like "*EmilyCarrU Intune*" } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($enterpriseCert) {
        return $enterpriseCert
    }
    
    # Fallback: Prefer CurrentUser over LocalMachine, and newest expiration date
    $best = $certs | Sort-Object @{Expression={$_.Store -eq "Cert:\CurrentUser\My"}; Descending=$true}, NotAfter -Descending | Select-Object -First 1
    
    return $best
}

# Handle certificate management commands
if ($ListCerts) {
    Show-CertificateList | Out-Null
    return
}

if ($FindCertSubject) {
    Write-Host "Searching for certificates with subject containing: $FindCertSubject" -ForegroundColor Green
    $certs = Find-CodeSigningCerts -SubjectFilter $FindCertSubject
    
    if ($certs) {
        for ($i = 0; $i -lt $certs.Count; $i++) {
            $cert = $certs[$i]
            Write-Host ""
            Write-Host "[$($i + 1)] Subject: $($cert.Subject)" -ForegroundColor Cyan
            Write-Host "    Thumbprint: $($cert.Thumbprint)" -ForegroundColor Yellow
            Write-Host "    Valid Until: $($cert.NotAfter)" -ForegroundColor Gray
            Write-Host "    Store: $($cert.Store)" -ForegroundColor Gray
        }
    } else {
        Write-Host "No certificates found matching: $FindCertSubject" -ForegroundColor Yellow
    }
    return
}

Write-Host "Building sbin-installer..." -ForegroundColor Green
Write-Host "Version: $Version" -ForegroundColor Cyan

# Determine architecture-correct Program Files directory
$ProgramFilesDir = if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64" -or $env:PROCESSOR_ARCHITEW6432 -eq "AMD64") {
    ${env:ProgramW6432}  # Use ProgramW6432 for x64 and ARM64
} else {
    ${env:ProgramFiles}  # Fallback for x86
}

$InstallPath = Join-Path $ProgramFilesDir "sbin"
$FinalExePath = Join-Path $InstallPath "installer.exe"

Write-Host "Target installation path: $FinalExePath" -ForegroundColor Cyan

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning previous build..." -ForegroundColor Yellow
    # Manual cleanup is more reliable than dotnet clean for this scenario
    Remove-Item -Path "bin", "obj", "dist" -Recurse -Force -ErrorAction SilentlyContinue
    # Clean NuGet cache for this project to ensure fresh packages
    Remove-Item -Path "$env:USERPROFILE\.nuget\packages" -Include "*installer*" -Recurse -Force -ErrorAction SilentlyContinue
}

# Restore packages
Write-Host "Restoring packages..." -ForegroundColor Yellow
dotnet restore

# Build the solution
Write-Host "Building..." -ForegroundColor Yellow
if ($Clean) {
    # For clean builds, don't use --no-restore to ensure everything is properly restored
    dotnet build --configuration $Configuration
} else {
    dotnet build --configuration $Configuration --no-restore
}

# Run tests if requested
if ($Test) {
    Write-Host "Running tests..." -ForegroundColor Yellow
    # Add test command here when tests are added
    Write-Host "No tests configured yet" -ForegroundColor Gray
}

# Determine runtime identifier based on architecture
$RuntimeId = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64" -or $env:PROCESSOR_ARCHITEW6432 -eq "ARM64") {
    "win-arm64"
} elseif ($env:PROCESSOR_ARCHITECTURE -eq "AMD64" -or $env:PROCESSOR_ARCHITEW6432 -eq "AMD64") {
    "win-x64"
} else {
    "win-x86"
}

Write-Host "Building for runtime: $RuntimeId" -ForegroundColor Yellow

# Publish single-file executable
Write-Host "Publishing single-file executable..." -ForegroundColor Yellow
dotnet publish src/installer/installer.csproj `
    --configuration $Configuration `
    --runtime $RuntimeId `
    --self-contained true `
    --output "dist" `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:PublishTrimmed=true `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:UseSourceLink=false

$ExePath = "dist/installer.exe"

# Show file size
$exe = Get-Item $ExePath
Write-Host "File size: $([math]::Round($exe.Length / 1MB, 2)) MB" -ForegroundColor Cyan

# Auto-detect and use certificate if not explicitly provided
if (-not $CertificateThumbprint) {
    $bestCert = Get-BestCertificate
    if ($bestCert) {
        $CertificateThumbprint = $bestCert.Thumbprint
        Write-Host "Auto-detected certificate: $($bestCert.Subject)" -ForegroundColor Green
        Write-Host "Thumbprint: $CertificateThumbprint" -ForegroundColor Gray
    }
}

# Sign the executable if certificate thumbprint provided or auto-detected
if ($CertificateThumbprint) {
    Write-Host "Signing executable..." -ForegroundColor Yellow
    
    # Find signtool.exe
    $SignTool = $null
    $PossiblePaths = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
        "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows\*\bin\signtool.exe"
    )
    
    foreach ($Path in $PossiblePaths) {
        $Found = Get-ChildItem $Path -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if ($Found) {
            $SignTool = $Found.FullName
            break
        }
    }
    
    if (-not $SignTool) {
        Write-Error "Could not find signtool.exe. Install Windows SDK."
    }
    
    Write-Host "Using SignTool: $SignTool" -ForegroundColor Gray
    
    # Sign with certificate from certificate store (suppress verbose output)
    $null = & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimeStampServer /td SHA256 $ExePath 2>&1
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Code signing failed with exit code $LASTEXITCODE"
    }
    
    Write-Host "Successfully signed: $ExePath" -ForegroundColor Green
    
    # Simple signature verification
    Write-Host "Verifying signature..." -ForegroundColor Yellow
    $verifyOutput = & $SignTool verify /pa $ExePath 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ Signature verification successful" -ForegroundColor Green
    } else {
        # Extract just the certificate subject for concise output
        $certLine = ($verifyOutput | Where-Object { $_ -like "*Issued to:*" } | Select-Object -First 1) -replace ".*Issued to: ", ""
        if ($certLine) {
            Write-Host "✓ Signed with certificate: $certLine" -ForegroundColor Green
        }
        Write-Host "⚠ Certificate chain verification failed (expected for test certificates)" -ForegroundColor Yellow
    }
} else {
    Write-Host "No code signing certificate found. Executable will not be signed." -ForegroundColor Yellow
    Write-Host "To sign, provide: .\build.ps1 -CertificateThumbprint <thumbprint>" -ForegroundColor Gray
    Write-Host "Or list available certificates: .\build.ps1 -ListCerts" -ForegroundColor Gray
}

# Build MSI package (unless skipped)
if (-not $SkipMsi) {
    Write-Host ""
    Write-Host "Building MSI package..." -ForegroundColor Green
    
    # Convert timestamp to MSI-compatible version format (like ReportMate does)
    $msiVersion = $Version -replace '^20(\d{2})\.0?(\d+)\.0?(\d+)\.(\d{4})$', '$1.$2.$3.$4'
    Write-Host "MSI version: $msiVersion" -ForegroundColor Gray
    
    # Check for WiX toolset by checking global tools list
    $wixFound = $false
    $wixVersion = $null
    try {
        $wixTool = & dotnet tool list --global 2>$null | Select-String "^wix\s"
        if ($wixTool) {
            $wixFound = $true
            # Parse version from output like "wix             5.0.1        wix"
            $wixInfo = $wixTool.ToString().Trim() -split '\s+'
            if ($wixInfo.Length -ge 2) {
                $wixVersion = $wixInfo[1]
                $majorVersion = [int]($wixVersion -split '\.')[0]
                Write-Host "✅ WiX Toolset v$majorVersion found (version $wixVersion)" -ForegroundColor Green
            } else {
                Write-Host "✅ WiX Toolset found: $($wixTool.ToString().Trim())" -ForegroundColor Green
            }
        }
    } catch {
        Write-Warning "Failed to check for WiX toolset: $($_.Exception.Message)"
    }
    
    if ($wixFound) {
        try {
            $MsiDir = Join-Path $PSScriptRoot "build\msi"
            $MsiStagingDir = Join-Path $PSScriptRoot "build\msi-staging"
            
            # Clean and prepare MSI staging directory
            if (Test-Path $MsiStagingDir) {
                Remove-Item $MsiStagingDir -Recurse -Force
            }
            New-Item -ItemType Directory -Path $MsiStagingDir -Force | Out-Null
            
            # Copy executable to staging
            Copy-Item $ExePath (Join-Path $MsiStagingDir "installer.exe") -Force
            Write-Verbose "Copied installer.exe to MSI staging"
            
            # Build MSI using CimianTools approach - dotnet build with .wixproj
            Write-Host "Building MSI with WiX using .wixproj..." -ForegroundColor Yellow
            $WixProjPath = Join-Path $MsiDir "sbin-installer.wixproj"
            
            # Build MSI output path
            $MsiPath = Join-Path $PSScriptRoot "dist\sbin-installer-$Version.msi"
            
            # Build with dotnet using the updated project (exactly like CimianTools)
            $buildArgs = @(
                "build"
                $WixProjPath
                "-p:Platform=x64"
                "-p:InstallerPlatform=x64"
                "-p:ProductVersion=$msiVersion"
                "-p:BinDir=$MsiStagingDir"
                "-p:OutputName=sbin-installer"
                "--configuration", "Release"
                "--nologo"
                "--verbosity", "minimal"
            )
            
            Write-Host "Running: dotnet $($buildArgs -join ' ')" -ForegroundColor Gray
            & dotnet @buildArgs
            if ($LASTEXITCODE -ne 0) {
                throw "WiX build failed with exit code $LASTEXITCODE"
            }
            
            # Find the output MSI in the build output (following CimianTools pattern)
            $builtMsi = Join-Path $MsiDir "bin\x64\Release\sbin-installer.msi"
            if (Test-Path $builtMsi) {
                Copy-Item $builtMsi $MsiPath -Force
                Write-Host "MSI package built successfully at $MsiPath" -ForegroundColor Green
            } else {
                # Try alternate paths (like CimianTools does)
                $altPaths = @(
                    (Join-Path $MsiDir "bin\x64\Release\sbin-installer.msi"),
                    (Join-Path $MsiDir "bin\x64\Debug\sbin-installer.msi"), 
                    (Join-Path $MsiDir "bin\Release\sbin-installer.msi"),
                    (Join-Path $MsiDir "bin\Debug\sbin-installer.msi"),
                    (Join-Path $MsiDir "bin\sbin-installer.msi")
                )
                $found = $false
                foreach ($altPath in $altPaths) {
                    if (Test-Path $altPath) {
                        Copy-Item $altPath $MsiPath -Force
                        Write-Host "MSI package built successfully at $MsiPath (found at $altPath)" -ForegroundColor Green
                        $found = $true
                        break
                    }
                }
                if (-not $found) {
                    throw "WiX build completed but output MSI not found. Expected at $builtMsi"
                }
            }
            
            # Sign MSI if certificate is available
            if ($CertificateThumbprint -and $SignTool) {
                Write-Host "Signing MSI..." -ForegroundColor Yellow
                $null = & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimeStampServer /td SHA256 $MsiPath 2>&1
                
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "Successfully signed MSI" -ForegroundColor Green
                } else {
                    Write-Warning "MSI signing failed"
                }
            }
            
            $MsiFile = Get-Item $MsiPath
            Write-Host "Final MSI: $MsiPath" -ForegroundColor Cyan
            Write-Host "MSI size: $([math]::Round($MsiFile.Length / 1MB, 2)) MB" -ForegroundColor Cyan
            
            # Clean up staging directory
            Remove-Item $MsiStagingDir -Recurse -Force -ErrorAction SilentlyContinue
            
        } catch {
            Write-Error "MSI creation failed: $($_.Exception.Message)"
            Write-Host "MSI creation is required for deployment. Please fix WiX installation." -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Warning "WiX Toolset not found - MSI creation skipped"
        Write-Host "Install with: dotnet tool install --global wix" -ForegroundColor Yellow
        Write-Host "MSI creation is required for deployment." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Skipping MSI build (use -SkipMsi flag)" -ForegroundColor Yellow
}

# Install if requested and running as administrator
if ($Install) {
    $IsAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    
    if (-not $IsAdmin) {
        Write-Host "Installation requires administrator privileges. Attempting to elevate..." -ForegroundColor Yellow
        
        # Check if sudo is available (Windows 11 22H2+)
        if (Get-Command sudo -ErrorAction SilentlyContinue) {
            Write-Host "Using sudo to elevate..." -ForegroundColor Gray
            $scriptPath = $MyInvocation.MyCommand.Path
            $scriptArgs = $MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_ $($MyInvocation.BoundParameters[$_])" }
            sudo powershell -ExecutionPolicy Bypass -File $scriptPath $scriptArgs
            return
        } else {
            # Fallback to PowerShell elevation
            Write-Host "Sudo not available, using PowerShell elevation..." -ForegroundColor Gray
            $scriptPath = $MyInvocation.MyCommand.Path
            $scriptArgs = $MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_ $($MyInvocation.BoundParameters[$_])" }
            Start-Process powershell -ArgumentList "-ExecutionPolicy", "Bypass", "-File", $scriptPath, $scriptArgs -Verb RunAs -Wait
            return
        }
    }
    
    Write-Host "Installing to: $FinalExePath" -ForegroundColor Yellow
    
    # Create sbin directory if it doesn't exist
    if (-not (Test-Path $InstallPath)) {
        New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
        Write-Host "Created directory: $InstallPath" -ForegroundColor Gray
    }
    
    # Copy executable to final location
    Copy-Item $ExePath $FinalExePath -Force
    Write-Host "Installed: $FinalExePath" -ForegroundColor Green
    
    # Add to PATH if not already there
    $CurrentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
    if ($CurrentPath -notlike "*$InstallPath*") {
        Write-Host "Adding $InstallPath to system PATH..." -ForegroundColor Yellow
        $NewPath = $CurrentPath.TrimEnd(';') + ";$InstallPath"
        [Environment]::SetEnvironmentVariable("PATH", $NewPath, [EnvironmentVariableTarget]::Machine)
        Write-Host "Added to PATH. Restart shell to use 'installer' command." -ForegroundColor Green
    } else {
        Write-Host "Path already contains $InstallPath" -ForegroundColor Gray
    }
    
    # Test installation
    Write-Host "Testing installation..." -ForegroundColor Yellow
    & $FinalExePath --vers
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Installation successful!" -ForegroundColor Green
    } else {
        Write-Error "Installation test failed"
    }
} else {
    Write-Host ""
    Write-Host "Build complete!" -ForegroundColor Green
    Write-Host "Executable: $ExePath" -ForegroundColor Cyan
    if (-not $SkipMsi) {
        $MsiPath = "dist\sbin-installer-$Version.msi"
        if (Test-Path $MsiPath) {
            Write-Host "MSI Package: $MsiPath" -ForegroundColor Cyan
        }
    }
    Write-Host "To install system-wide, use: .\build.ps1 -Install (requires admin)" -ForegroundColor Yellow
}
