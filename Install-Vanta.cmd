@echo off
rem Installs or updates VANTA (C:\Program Files\VANTA + Start-menu shortcut). Windows asks for administrator rights once.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Vanta.ps1" %*
pause
