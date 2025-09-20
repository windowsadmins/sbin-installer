# chocolateyBeforeInstall.ps1 - Pre-install script
Write-Host "Running Chocolatey before-install script for test-package"
Write-Host "Checking system requirements..."

# Example pre-install check
$osVersion = [System.Environment]::OSVersion.Version
Write-Host "Operating System: $($osVersion.ToString())"

if ($osVersion.Major -lt 10) {
    Write-Warning "This package is designed for Windows 10 or later"
}

Write-Host "Pre-install checks completed"