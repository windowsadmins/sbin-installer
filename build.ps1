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
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

# Certificate management functions
function Find-CodeSigningCerts {
    param([string]$SubjectFilter = "")
    
    $certs = @()
    $stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")
    
    foreach ($store in $stores) {
        $storeCerts = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object {
            $_.EnhancedKeyUsageList -like "*Code Signing*" -and 
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
    
    # Prefer CurrentUser over LocalMachine, and newest expiration date
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
    dotnet clean --configuration $Configuration
    Remove-Item -Path "bin", "obj", "dist" -Recurse -Force -ErrorAction SilentlyContinue
}

# Restore packages
Write-Host "Restoring packages..." -ForegroundColor Yellow
dotnet restore

# Build the solution
Write-Host "Building..." -ForegroundColor Yellow
dotnet build --configuration $Configuration --no-restore

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
    -p:PublishTrimmed=true

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
    
    $BuildRoot = $PSScriptRoot
    $MsiPath = Join-Path $BuildRoot "build\msi"
    
    # Update version in WiX file if specified
    if ($Version -ne "1.0.0") {
        Write-Host "Updating MSI version to $Version..." -ForegroundColor Yellow
        $WxsPath = Join-Path $MsiPath "sbin-installer.wxs"
        if (Test-Path $WxsPath) {
            $wxsContent = Get-Content $WxsPath -Raw
            # Fix any broken XML version declaration
            $wxsContent = $wxsContent -replace '<\?xml Version="[^"]*"', '<?xml version="1.0"'
            # Update the Package Version attribute
            $wxsContent = $wxsContent -replace 'Version="[\d\.]+?"', "Version=`"$Version`""
            Set-Content $WxsPath $wxsContent
        }
    }
    
    # Build MSI
    $CurrentLocation = Get-Location
    try {
        Set-Location $MsiPath
        
        # Install WiX toolset if not present
        Write-Host "Ensuring WiX toolset is available..." -ForegroundColor Yellow
        dotnet tool install --global wix --version 6.0.2 2>$null | Out-Null
        
        # Build MSI using WiX 6
        Write-Host "Compiling MSI package..." -ForegroundColor Yellow
        wix build sbin-installer.wxs -o "bin\$Configuration\sbin-installer-$Version.msi" -arch x64
        
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "MSI build failed with exit code $LASTEXITCODE"
        } else {
            # Find the generated MSI
            $GeneratedMsi = Get-ChildItem "bin\$Configuration\*.msi" | Select-Object -First 1
            
            if ($GeneratedMsi) {
                Write-Host "MSI built successfully: $($GeneratedMsi.FullName)" -ForegroundColor Green
                
                # Sign MSI if certificate is available
                if ($CertificateThumbprint) {
                    Write-Host "Signing MSI..." -ForegroundColor Yellow
                    
                    # Find signtool.exe (reuse from executable signing)
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
                    
                    if ($SignTool) {
                        # Sign MSI (suppress verbose output)
                        $null = & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimeStampServer /td SHA256 $GeneratedMsi.FullName 2>&1
                        
                        if ($LASTEXITCODE -eq 0) {
                            Write-Host "Successfully signed MSI" -ForegroundColor Green
                        } else {
                            Write-Warning "MSI signing failed"
                        }
                    }
                }
                
                # Copy MSI to dist folder
                $FinalMsiPath = Join-Path $PSScriptRoot "dist\sbin-installer-$Version.msi"
                Copy-Item $GeneratedMsi.FullName $FinalMsiPath -Force
                
                # Show MSI info
                $MsiFile = Get-Item $FinalMsiPath
                Write-Host "Final MSI: $FinalMsiPath" -ForegroundColor Cyan
                Write-Host "MSI size: $([math]::Round($MsiFile.Length / 1MB, 2)) MB" -ForegroundColor Cyan
            } else {
                Write-Warning "MSI file not found after build"
            }
        }
    } catch {
        Write-Warning "MSI build failed: $($_.Exception.Message)"
    } finally {
        Set-Location $CurrentLocation
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