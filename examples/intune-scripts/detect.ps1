# PowerShell detection script for Intune
# Checks if sbin-installer is properly installed

$targetPath = "C:\Program Files\sbin\installer.exe"
$pathEnv = [Environment]::GetEnvironmentVariable("PATH", "Machine")

if ((Test-Path $targetPath) -and ($pathEnv -like "*C:\Program Files\sbin*")) {
    Write-Output "sbin-installer is installed"
    exit 0
} else {
    exit 1
}