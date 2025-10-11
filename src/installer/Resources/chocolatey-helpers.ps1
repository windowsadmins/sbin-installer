# Chocolatey Helper Functions Shim
# Minimal implementation of common Chocolatey helper functions
# Allows .nupkg packages to work with sbin-installer without full Chocolatey

$ErrorActionPreference = 'Stop'

# Get the package name from environment or use a fallback
$packageName = if ($env:ChocolateyPackageName) { $env:ChocolateyPackageName } else { 'package' }
$packageVersion = if ($env:ChocolateyPackageVersion) { $env:ChocolateyPackageVersion } else { '0.0.0' }
$packageFolder = if ($env:ChocolateyPackageFolder) { $env:ChocolateyPackageFolder } else { $PSScriptRoot }

function Write-ChocolateySuccess {
    param([string]$PackageName)
    Write-Host "✓ $PackageName installed successfully" -ForegroundColor Green
}

function Write-ChocolateyFailure {
    param(
        [string]$PackageName,
        [string]$FailureMessage
    )
    Write-Error "$PackageName failed: $FailureMessage"
}

function Install-ChocolateyPath {
    <#
    .SYNOPSIS
    Adds a directory to the system or user PATH environment variable
    
    .PARAMETER PathToInstall
    The path to add to PATH
    
    .PARAMETER PathType
    'Machine' for system-wide PATH, 'User' for user PATH
    #>
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$PathToInstall,
        
        [Parameter(Mandatory = $false)]
        [ValidateSet('Machine', 'User')]
        [string]$PathType = 'User'
    )
    
    Write-Host "Adding to PATH ($PathType): $PathToInstall"
    
    $envTarget = if ($PathType -eq 'Machine') { 
        [System.EnvironmentVariableTarget]::Machine 
    } else { 
        [System.EnvironmentVariableTarget]::User 
    }
    
    $currentPath = [Environment]::GetEnvironmentVariable('PATH', $envTarget)
    
    # Check if already in PATH
    if ($currentPath -like "*$PathToInstall*") {
        Write-Host "  Path already exists in PATH" -ForegroundColor Gray
        return
    }
    
    # Add to PATH
    $newPath = $currentPath.TrimEnd(';') + ";$PathToInstall"
    [Environment]::SetEnvironmentVariable('PATH', $newPath, $envTarget)
    
    # Also update current session
    $env:PATH = $env:PATH.TrimEnd(';') + ";$PathToInstall"
    
    Write-Host "  ✓ Added to PATH" -ForegroundColor Green
}

function Install-ChocolateyEnvironmentVariable {
    <#
    .SYNOPSIS
    Sets an environment variable
    
    .PARAMETER VariableName
    The name of the environment variable
    
    .PARAMETER VariableValue
    The value to set
    
    .PARAMETER VariableType
    'Machine' for system-wide, 'User' for user-level, 'Process' for current process only
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$VariableName,
        
        [Parameter(Mandatory = $false)]
        [string]$VariableValue = '',
        
        [Parameter(Mandatory = $false)]
        [ValidateSet('Machine', 'User', 'Process')]
        [string]$VariableType = 'User'
    )
    
    Write-Host "Setting environment variable: $VariableName = $VariableValue ($VariableType)"
    
    $envTarget = switch ($VariableType) {
        'Machine' { [System.EnvironmentVariableTarget]::Machine }
        'User' { [System.EnvironmentVariableTarget]::User }
        'Process' { [System.EnvironmentVariableTarget]::Process }
    }
    
    [Environment]::SetEnvironmentVariable($VariableName, $VariableValue, $envTarget)
    
    Write-Host "  ✓ Environment variable set" -ForegroundColor Green
}

function Get-ChocolateyWebFile {
    <#
    .SYNOPSIS
    Downloads a file (simplified version)
    
    .PARAMETER PackageName
    The name of the package
    
    .PARAMETER FileFullPath
    Where to save the file
    
    .PARAMETER Url
    The URL to download from
    
    .PARAMETER Url64bit
    The 64-bit URL (preferred on 64-bit systems)
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,
        
        [Parameter(Mandatory = $true)]
        [string]$FileFullPath,
        
        [Parameter(Mandatory = $false)]
        [string]$Url = '',
        
        [Parameter(Mandatory = $false)]
        [string]$Url64bit = '',
        
        [Parameter(Mandatory = $false)]
        [string]$Checksum = '',
        
        [Parameter(Mandatory = $false)]
        [string]$ChecksumType = 'sha256'
    )
    
    $urlToUse = if ([Environment]::Is64BitOperatingSystem -and $Url64bit) { $Url64bit } else { $Url }
    
    Write-Host "Downloading $PackageName from $urlToUse"
    
    try {
        # Use modern .NET method for downloads
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($urlToUse, $FileFullPath)
        Write-Host "  ✓ Downloaded to $FileFullPath" -ForegroundColor Green
    }
    catch {
        throw "Failed to download from $urlToUse : $($_.Exception.Message)"
    }
}

function Install-ChocolateyPackage {
    <#
    .SYNOPSIS
    Installs a software package (simplified - just runs the installer)
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,
        
        [Parameter(Mandatory = $false)]
        [string]$FileType = 'exe',
        
        [Parameter(Mandatory = $false)]
        [string[]]$SilentArgs = @(),
        
        [Parameter(Mandatory = $false)]
        [string]$File = '',
        
        [Parameter(Mandatory = $false)]
        [string]$File64 = ''
    )
    
    $fileToInstall = if ([Environment]::Is64BitOperatingSystem -and $File64) { $File64 } else { $File }
    
    Write-Host "Installing $PackageName from $fileToInstall"
    
    $installerArgs = @{
        FilePath = $fileToInstall
        ArgumentList = $SilentArgs
        Wait = $true
        PassThru = $true
    }
    
    if ($FileType -eq 'msi') {
        $installerArgs.FilePath = 'msiexec.exe'
        $installerArgs.ArgumentList = @('/i', $fileToInstall) + $SilentArgs
    }
    
    $process = Start-Process @installerArgs
    
    if ($process.ExitCode -ne 0) {
        throw "Installation failed with exit code $($process.ExitCode)"
    }
    
    Write-Host "  ✓ Installed successfully" -ForegroundColor Green
}

function Install-ChocolateyZipPackage {
    <#
    .SYNOPSIS
    Extracts a zip file
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,
        
        [Parameter(Mandatory = $true)]
        [string]$Url,
        
        [Parameter(Mandatory = $true)]
        [string]$UnzipLocation,
        
        [Parameter(Mandatory = $false)]
        [string]$Url64bit = ''
    )
    
    $urlToUse = if ([Environment]::Is64BitOperatingSystem -and $Url64bit) { $Url64bit } else { $Url }
    
    Write-Host "Downloading and extracting $PackageName"
    
    $tempFile = Join-Path $env:TEMP "$PackageName.zip"
    
    try {
        # Download
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($urlToUse, $tempFile)
        
        # Extract
        if (-not (Test-Path $UnzipLocation)) {
            New-Item -ItemType Directory -Path $UnzipLocation -Force | Out-Null
        }
        
        Expand-Archive -Path $tempFile -DestinationPath $UnzipLocation -Force
        
        Write-Host "  ✓ Extracted to $UnzipLocation" -ForegroundColor Green
    }
    finally {
        if (Test-Path $tempFile) {
            Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-ChocolateyUnzip {
    <#
    .SYNOPSIS
    Alias for Install-ChocolateyZipPackage
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileFullPath,
        
        [Parameter(Mandatory = $true)]
        [string]$Destination,
        
        [Parameter(Mandatory = $false)]
        [string]$SpecificFolder = '',
        
        [Parameter(Mandatory = $false)]
        [string]$PackageName = $packageName
    )
    
    Write-Host "Extracting $FileFullPath to $Destination"
    
    if (-not (Test-Path $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
    
    Expand-Archive -Path $FileFullPath -DestinationPath $Destination -Force
    
    Write-Host "  ✓ Extracted successfully" -ForegroundColor Green
}

function Install-ChocolateyShortcut {
    <#
    .SYNOPSIS
    Creates a shortcut
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutFilePath,
        
        [Parameter(Mandatory = $true)]
        [string]$TargetPath,
        
        [Parameter(Mandatory = $false)]
        [string]$WorkingDirectory = '',
        
        [Parameter(Mandatory = $false)]
        [string]$Arguments = '',
        
        [Parameter(Mandatory = $false)]
        [string]$IconLocation = '',
        
        [Parameter(Mandatory = $false)]
        [string]$Description = ''
    )
    
    Write-Host "Creating shortcut: $ShortcutFilePath"
    
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutFilePath)
    $shortcut.TargetPath = $TargetPath
    
    if ($WorkingDirectory) { $shortcut.WorkingDirectory = $WorkingDirectory }
    if ($Arguments) { $shortcut.Arguments = $Arguments }
    if ($IconLocation) { $shortcut.IconLocation = $IconLocation }
    if ($Description) { $shortcut.Description = $Description }
    
    $shortcut.Save()
    
    Write-Host "  ✓ Shortcut created" -ForegroundColor Green
}

function Get-OSArchitectureWidth {
    <#
    .SYNOPSIS
    Returns 32 or 64 based on OS architecture
    #>
    if ([Environment]::Is64BitOperatingSystem) { return 64 } else { return 32 }
}

function Get-EnvironmentVariable {
    <#
    .SYNOPSIS
    Gets an environment variable value
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        
        [Parameter(Mandatory = $false)]
        [ValidateSet('Machine', 'User', 'Process')]
        [string]$Scope = 'Process'
    )
    
    $envTarget = switch ($Scope) {
        'Machine' { [System.EnvironmentVariableTarget]::Machine }
        'User' { [System.EnvironmentVariableTarget]::User }
        'Process' { [System.EnvironmentVariableTarget]::Process }
    }
    
    return [Environment]::GetEnvironmentVariable($Name, $envTarget)
}

function Update-SessionEnvironment {
    <#
    .SYNOPSIS
    Refreshes environment variables in current session
    #>
    Write-Host "Refreshing environment variables..."
    
    # Reload PATH from registry
    $machinePath = [Environment]::GetEnvironmentVariable('PATH', [System.EnvironmentVariableTarget]::Machine)
    $userPath = [Environment]::GetEnvironmentVariable('PATH', [System.EnvironmentVariableTarget]::User)
    $env:PATH = "$machinePath;$userPath"
    
    Write-Host "  ✓ Environment refreshed" -ForegroundColor Green
}

# Note: We don't use Export-ModuleMember because this script is dot-sourced, not imported as a module
# All functions are automatically available in the calling scope
Write-Verbose "Chocolatey helper shim loaded (dot-sourced)"
