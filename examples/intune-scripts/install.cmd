@echo off
REM sbin-installer Intune Installation Script
REM Version: 2025.09.20.1126

echo Installing sbin-installer to C:\Program Files\sbin...

REM Create target directory
if not exist "C:\Program Files\sbin" mkdir "C:\Program Files\sbin"

REM Copy executable
copy "%~dp0installer.exe" "C:\Program Files\sbin\installer.exe" /Y

REM Add to PATH (system-wide)
setx PATH "%PATH%;C:\Program Files\sbin" /M

echo Installation complete!
echo sbin-installer is now available as 'installer' command system-wide.