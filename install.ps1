#!/usr/bin/env pwsh

# Installation script for sbin-installer
param(
    [string]$CertificateThumbprint,
    [string]$SourcePath = ".\dist\installer.exe",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Check for admin privileges and elevate if needed
$IsAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $IsAdmin) {
    Write-Host "Installation requires administrator privileges. Attempting to elevate..." -ForegroundColor Yellow
    
    # Check if sudo is available (Windows 11 22H2+)
    if (Get-Command sudo -ErrorAction SilentlyContinue) {
        Write-Host "Using sudo to elevate..." -ForegroundColor Gray
        $scriptPath = $MyInvocation.MyCommand.Path
        $args = $MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_ `"$($MyInvocation.BoundParameters[$_])`"" }
        sudo powershell -ExecutionPolicy Bypass -File $scriptPath @args
        exit $LASTEXITCODE
    } else {
        # Fallback to PowerShell elevation
        Write-Host "Sudo not available, using PowerShell elevation..." -ForegroundColor Gray
        $scriptPath = $MyInvocation.MyCommand.Path
        $args = $MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_ `"$($MyInvocation.BoundParameters[$_])`"" }
        $argString = ($args -join " ")
        if ($Force) { $argString += " -Force" }
        Start-Process powershell -ArgumentList "-ExecutionPolicy", "Bypass", "-File", "`"$scriptPath`"", $argString -Verb RunAs -Wait
        exit $LASTEXITCODE
    }
}

# Determine architecture-correct Program Files directory
$ProgramFilesDir = if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64" -or $env:PROCESSOR_ARCHITEW6432 -eq "AMD64") {
    ${env:ProgramW6432}  # Use ProgramW6432 for x64 and ARM64
} else {
    ${env:ProgramFiles}  # Fallback for x86
}

$InstallPath = Join-Path $ProgramFilesDir "sbin"
$FinalExePath = Join-Path $InstallPath "installer.exe"

Write-Host "Installing sbin-installer to: $FinalExePath" -ForegroundColor Green

# Check if source exists
if (-not (Test-Path $SourcePath)) {
    Write-Error "Source executable not found: $SourcePath. Run .\build.ps1 first."
}

# Check if already installed
if ((Test-Path $FinalExePath) -and -not $Force) {
    $existing = Get-ItemProperty $FinalExePath
    Write-Host "Already installed: $($existing.VersionInfo.FileVersion)" -ForegroundColor Yellow
    $response = Read-Host "Overwrite existing installation? (y/N)"
    if ($response -ne 'y' -and $response -ne 'Y') {
        Write-Host "Installation cancelled." -ForegroundColor Yellow
        exit 0
    }
}

# Create sbin directory if it doesn't exist
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Write-Host "Created directory: $InstallPath" -ForegroundColor Gray
}

# Copy executable to final location
Copy-Item $SourcePath $FinalExePath -Force
Write-Host "Copied executable to: $FinalExePath" -ForegroundColor Green

# Verify the installation
if (Test-Path $FinalExePath) {
    $exe = Get-ItemProperty $FinalExePath
    Write-Host "Installed version: $($exe.VersionInfo.FileVersion)" -ForegroundColor Cyan
    Write-Host "File size: $([math]::Round($exe.Length / 1MB, 2)) MB" -ForegroundColor Cyan
} else {
    Write-Error "Installation failed - executable not found at target location"
}

# Add to PATH if not already there
$CurrentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
if ($CurrentPath -notlike "*$InstallPath*") {
    Write-Host "Adding $InstallPath to system PATH..." -ForegroundColor Yellow
    $NewPath = $CurrentPath.TrimEnd(';') + ";$InstallPath"
    [Environment]::SetEnvironmentVariable("PATH", $NewPath, [EnvironmentVariableTarget]::Machine)
    Write-Host "Added to PATH. Restart shell to use 'installer' command globally." -ForegroundColor Green
} else {
    Write-Host "PATH already contains $InstallPath" -ForegroundColor Gray
}

# Test installation
Write-Host "Testing installation..." -ForegroundColor Yellow
try {
    $output = & $FinalExePath --vers 2>&1
    Write-Host $output -ForegroundColor Gray
    Write-Host "Installation successful!" -ForegroundColor Green
} catch {
    Write-Error "Installation test failed: $_"
}

Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host "Usage: installer --pkg <package.pkg> --target <target>" -ForegroundColor Cyan
Write-Host "Help:  installer --help" -ForegroundColor Cyan