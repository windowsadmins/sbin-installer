#!/usr/bin/env pwsh

# Build script for sbin-installer
param(
    [string]$Configuration = "Release",
    [switch]$Clean,
    [switch]$Test,
    [switch]$Install,
    [string]$CertificateThumbprint,
    [string]$TimeStampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

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

# Sign the executable if certificate thumbprint provided
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
    
    # Sign with certificate from certificate store
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimeStampServer /td SHA256 $ExePath
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Code signing failed with exit code $LASTEXITCODE"
    }
    
    Write-Host "Successfully signed: $ExePath" -ForegroundColor Green
    
    # Verify signature
    Write-Host "Verifying signature..." -ForegroundColor Yellow
    & $SignTool verify /pa /v $ExePath
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Signature verification successful" -ForegroundColor Green
    } else {
        Write-Warning "Signature verification failed or returned warnings"
    }
} else {
    Write-Warning "No certificate thumbprint provided. Executable will not be signed."
    Write-Host "To sign, use: .\build.ps1 -CertificateThumbprint <thumbprint>" -ForegroundColor Yellow
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
            $args = $MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_ $($MyInvocation.BoundParameters[$_])" }
            sudo powershell -ExecutionPolicy Bypass -File $scriptPath $args
            return
        } else {
            # Fallback to PowerShell elevation
            Write-Host "Sudo not available, using PowerShell elevation..." -ForegroundColor Gray
            $scriptPath = $MyInvocation.MyCommand.Path
            $args = $MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_ $($MyInvocation.BoundParameters[$_])" }
            Start-Process powershell -ArgumentList "-ExecutionPolicy", "Bypass", "-File", $scriptPath, $args -Verb RunAs -Wait
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
    Write-Host "Build complete! Executable: $ExePath" -ForegroundColor Green
    Write-Host "To install system-wide, use: .\build.ps1 -Install (requires admin)" -ForegroundColor Yellow
}