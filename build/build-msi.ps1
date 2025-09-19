#!/usr/bin/env pwsh

# MSI Build script for sbin-installer
param(
    [string]$Configuration = "Release",
    [switch]$Clean,
    [string]$Version = "1.0.0",
    [string]$CertificateThumbprint,
    [string]$TimeStampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

Write-Host "Building sbin-installer MSI..." -ForegroundColor Green

$BuildRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $BuildRoot)
$MsiProject = Join-Path $BuildRoot "msi\sbin-installer-msi.wixproj"

# Ensure main executable is built first
Write-Host "Building main executable..." -ForegroundColor Yellow
& "$ProjectRoot\build.ps1" -Configuration $Configuration -CertificateThumbprint $CertificateThumbprint

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build main executable"
}

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning MSI build..." -ForegroundColor Yellow
    Remove-Item -Path "$BuildRoot\msi\bin", "$BuildRoot\msi\obj" -Recurse -Force -ErrorAction SilentlyContinue
}

# Check if WiX is installed
$WixInstalled = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $WixInstalled) {
    Write-Error "dotnet CLI is required but not found. Install .NET SDK."
}

# Update version in WiX file if specified
if ($Version -ne "1.0.0") {
    Write-Host "Updating version to $Version..." -ForegroundColor Yellow
    $WxsPath = Join-Path $BuildRoot "msi\sbin-installer.wxs"
    $wxsContent = Get-Content $WxsPath -Raw
    $wxsContent = $wxsContent -replace 'Version="1\.0\.0"', "Version=`"$Version`""
    Set-Content $WxsPath $wxsContent
}

# Build MSI
Write-Host "Building MSI package..." -ForegroundColor Yellow
Set-Location "$BuildRoot\msi"

try {
    # Check if executable exists
    $ExePath = Join-Path $ProjectRoot "dist\installer.exe"
    if (-not (Test-Path $ExePath)) {
        Write-Error "Executable not found at $ExePath. Build main project first."
    }

    # Install WiX toolset if not present
    dotnet tool install --global wix --version 5.0.0 2>$null

    # Build MSI
    dotnet build $MsiProject --configuration $Configuration

    if ($LASTEXITCODE -ne 0) {
        Write-Error "MSI build failed"
    }

    # Find the generated MSI
    $MsiPath = Get-ChildItem "$BuildRoot\msi\bin\$Configuration\*.msi" | Select-Object -First 1
    
    if (-not $MsiPath) {
        Write-Error "MSI file not found after build"
    }

    Write-Host "MSI built successfully: $($MsiPath.FullName)" -ForegroundColor Green
    
    # Sign MSI if certificate provided
    if ($CertificateThumbprint) {
        Write-Host "Signing MSI..." -ForegroundColor Yellow
        
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
            Write-Warning "Could not find signtool.exe. MSI will not be signed."
        } else {
            Write-Host "Using SignTool: $SignTool" -ForegroundColor Gray
            
            # Sign MSI
            & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimeStampServer /td SHA256 $MsiPath.FullName
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Successfully signed MSI: $($MsiPath.FullName)" -ForegroundColor Green
            } else {
                Write-Warning "MSI signing failed"
            }
        }
    }
    
    # Copy MSI to dist folder
    $DistPath = Join-Path $ProjectRoot "dist"
    if (-not (Test-Path $DistPath)) {
        New-Item -ItemType Directory -Path $DistPath | Out-Null
    }
    
    $FinalMsiPath = Join-Path $DistPath "sbin-installer-$Version.msi"
    Copy-Item $MsiPath.FullName $FinalMsiPath -Force
    
    Write-Host "Final MSI: $FinalMsiPath" -ForegroundColor Cyan
    
    # Show file size
    $MsiFile = Get-Item $FinalMsiPath
    Write-Host "MSI size: $([math]::Round($MsiFile.Length / 1MB, 2)) MB" -ForegroundColor Cyan
    
} finally {
    Set-Location $ProjectRoot
}