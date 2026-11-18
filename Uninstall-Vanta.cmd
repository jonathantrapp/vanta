@echo off
rem Removes the installed VANTA app; your settings in %APPDATA%\HwSuite are kept (add -RemoveSettings to delete them).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Vanta.ps1" %*
pause
