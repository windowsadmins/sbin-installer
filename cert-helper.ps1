#!/usr/bin/env pwsh

# Certificate helper for finding code signing certificates
param(
    [switch]$List,
    [switch]$Find,
    [string]$Subject
)

# List all code signing certificates in the current user store
if ($List) {
    Write-Host "Code signing certificates in CurrentUser\My:" -ForegroundColor Green
    $certs = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.EnhancedKeyUsageList -like "*Code Signing*" -and $_.NotAfter -gt (Get-Date)
    }
    
    if ($certs) {
        $certs | ForEach-Object {
            Write-Host ""
            Write-Host "Subject: $($_.Subject)" -ForegroundColor Cyan
            Write-Host "Issuer:  $($_.Issuer)" -ForegroundColor Gray
            Write-Host "Thumbprint: $($_.Thumbprint)" -ForegroundColor Yellow
            Write-Host "Valid Until: $($_.NotAfter)" -ForegroundColor Gray
            Write-Host "Usage: .\build.ps1 -CertificateThumbprint `"$($_.Thumbprint)`"" -ForegroundColor White
        }
    } else {
        Write-Host "No valid code signing certificates found in CurrentUser\My" -ForegroundColor Yellow
    }
    
    Write-Host ""
    Write-Host "Checking LocalMachine\My:" -ForegroundColor Green
    $machineCerts = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object {
        $_.EnhancedKeyUsageList -like "*Code Signing*" -and $_.NotAfter -gt (Get-Date)
    }
    
    if ($machineCerts) {
        $machineCerts | ForEach-Object {
            Write-Host ""
            Write-Host "Subject: $($_.Subject)" -ForegroundColor Cyan
            Write-Host "Issuer:  $($_.Issuer)" -ForegroundColor Gray
            Write-Host "Thumbprint: $($_.Thumbprint)" -ForegroundColor Yellow
            Write-Host "Valid Until: $($_.NotAfter)" -ForegroundColor Gray
            Write-Host "Usage: .\build.ps1 -CertificateThumbprint `"$($_.Thumbprint)`"" -ForegroundColor White
        }
    } else {
        Write-Host "No valid code signing certificates found in LocalMachine\My" -ForegroundColor Yellow
    }
    
    return
}

# Find certificate by subject
if ($Find -and $Subject) {
    Write-Host "Searching for certificates with subject containing: $Subject" -ForegroundColor Green
    
    $found = @()
    $stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")
    
    foreach ($store in $stores) {
        $certs = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object {
            $_.Subject -like "*$Subject*" -and
            $_.EnhancedKeyUsageList -like "*Code Signing*" -and
            $_.NotAfter -gt (Get-Date)
        }
        
        if ($certs) {
            $found += $certs
        }
    }
    
    if ($found) {
        $found | ForEach-Object {
            Write-Host ""
            Write-Host "Found in: $($_.PSParentPath)" -ForegroundColor Gray
            Write-Host "Subject: $($_.Subject)" -ForegroundColor Cyan
            Write-Host "Thumbprint: $($_.Thumbprint)" -ForegroundColor Yellow
            Write-Host "Valid Until: $($_.NotAfter)" -ForegroundColor Gray
            Write-Host "Usage: .\build.ps1 -CertificateThumbprint `"$($_.Thumbprint)`"" -ForegroundColor White
        }
    } else {
        Write-Host "No certificates found matching: $Subject" -ForegroundColor Yellow
    }
    
    return
}

# Default: Show usage
Write-Host "Certificate Helper for sbin-installer" -ForegroundColor Green
Write-Host ""
Write-Host "Usage:" -ForegroundColor Cyan
Write-Host "  .\cert-helper.ps1 -List                    # List all code signing certificates"
Write-Host "  .\cert-helper.ps1 -Find -Subject `"Cimian`"   # Find certificates by subject"
Write-Host ""
Write-Host "Examples:" -ForegroundColor Yellow
Write-Host "  # List available certificates"
Write-Host "  .\cert-helper.ps1 -List"
Write-Host ""
Write-Host "  # Find specific certificate"
Write-Host "  .\cert-helper.ps1 -Find -Subject `"Your Company`""
Write-Host ""
Write-Host "  # Use found certificate in build"
Write-Host "  .\build.ps1 -CertificateThumbprint `"ABCD1234...`""