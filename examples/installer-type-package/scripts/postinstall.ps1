# postinstall.ps1 - Installer-type package script
Write-Host "Running installer-type post-install script..."

# In installer-type packages, payload files remain in extraction directory
$payloadDir = Join-Path $PSScriptRoot "..\payload"
$setupFile = Join-Path $payloadDir "setup.exe"

Write-Host "Payload directory: $payloadDir"
Write-Host "Setup file: $setupFile"

if (Test-Path $setupFile) {
    Write-Host "Found setup file, would normally run: $setupFile"
    Write-Host "For demo purposes, just copying content to temp location..."
    
    # Simulate installer behavior - copy to a custom location
    $customInstallDir = "C:\CustomInstall\InstallerTypeExample"
    New-Item -ItemType Directory -Path $customInstallDir -Force | Out-Null
    Copy-Item $setupFile (Join-Path $customInstallDir "installed-by-script.txt") -Force
    
    Write-Host "Simulated installation completed to: $customInstallDir"
} else {
    Write-Warning "Setup file not found: $setupFile"
    exit 1
}

Write-Host "Installer-type post-install script completed successfully"