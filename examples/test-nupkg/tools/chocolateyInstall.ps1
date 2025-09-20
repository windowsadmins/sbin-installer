# chocolateyInstall.ps1 - Chocolatey install script
Write-Host "Running Chocolatey install script for test-package"

$packageName = 'test-package'
$installDir = Join-Path $env:ProgramFiles $packageName

# Create installation directory
if (!(Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Write-Host "Created directory: $installDir"
}

# Copy files from lib directory to installation directory
$libDir = Join-Path (Get-Location) "lib"
if (Test-Path $libDir) {
    Copy-Item -Path "$libDir\*" -Destination $installDir -Recurse -Force
    Write-Host "Copied files from lib to $installDir"
}

Write-Host "Chocolatey install completed successfully!"