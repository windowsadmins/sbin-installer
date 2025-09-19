#!/usr/bin/env pwsh

# Create a basic installer icon (placeholder)
# In production, replace this with a proper .ico file

Write-Host "Creating placeholder installer icon..." -ForegroundColor Yellow

$IconPath = Join-Path $PSScriptRoot "installer.ico"

# Create a minimal 16x16 ICO file (placeholder)
# This is just a basic structure - replace with actual icon
$IcoHeader = @(
    0x00, 0x00,  # Reserved (must be 0)
    0x01, 0x00,  # Type (1 = ICO)
    0x01, 0x00   # Count of images
)

$IconDirEntry = @(
    0x10,        # Width (16)
    0x10,        # Height (16)
    0x00,        # Colors (0 = no palette)
    0x00,        # Reserved
    0x01, 0x00,  # Planes
    0x18, 0x00,  # Bits per pixel (24)
    0x68, 0x05, 0x00, 0x00,  # Size in bytes (1384)
    0x16, 0x00, 0x00, 0x00   # Offset to data
)

# Basic bitmap data for 16x16x24 (simplified - just creates a blue square)
$BitmapHeader = @(
    0x28, 0x00, 0x00, 0x00,  # Header size (40)
    0x10, 0x00, 0x00, 0x00,  # Width (16)
    0x20, 0x00, 0x00, 0x00,  # Height (32, includes mask)
    0x01, 0x00,              # Planes
    0x18, 0x00,              # Bits per pixel
    0x00, 0x00, 0x00, 0x00,  # Compression
    0x00, 0x00, 0x00, 0x00,  # Image size
    0x00, 0x00, 0x00, 0x00,  # X pixels per meter
    0x00, 0x00, 0x00, 0x00,  # Y pixels per meter
    0x00, 0x00, 0x00, 0x00,  # Colors used
    0x00, 0x00, 0x00, 0x00   # Important colors
)

# Create blue pixels (16x16 = 256 pixels * 3 bytes each)
$PixelData = @()
for ($i = 0; $i -lt 256; $i++) {
    $PixelData += @(0xFF, 0x80, 0x00)  # Orange color (BGR format)
}

# AND mask (16x16 bits = 32 bytes, all transparent)
$AndMask = @(0x00) * 32

# Combine all data
$AllData = $IcoHeader + $IconDirEntry + $BitmapHeader + $PixelData + $AndMask

# Write to file
[System.IO.File]::WriteAllBytes($IconPath, [byte[]]$AllData)

Write-Host "Created placeholder icon at: $IconPath" -ForegroundColor Green
Write-Host "Note: Replace this with a proper .ico file for production use." -ForegroundColor Yellow