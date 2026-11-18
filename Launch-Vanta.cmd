@echo off
rem Launches VANTA (published build dist\HwSuite\HwSuite.exe; self-contained, no .NET install needed). Windows will ask for administrator rights.
start "" "%~dp0dist\HwSuite\HwSuite.exe" %*
