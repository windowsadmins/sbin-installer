@echo off
REM sbin-installer Intune Uninstallation Script
REM Version: 2025.09.20.1126

echo Removing sbin-installer from C:\Program Files\sbin...

REM Remove executable
if exist "C:\Program Files\sbin\installer.exe" del "C:\Program Files\sbin\installer.exe" /Q

REM Remove directory if empty
rmdir "C:\Program Files\sbin" 2>nul

REM Note: PATH cleanup requires manual intervention or registry manipulation
echo Uninstallation complete!
echo Note: You may need to manually remove C:\Program Files\sbin from PATH