<#
.SYNOPSIS
    Safely pauses or restores Gigabyte background services and processes 
    to prevent SMBus (RAM RGB) and Super I/O (Fan) collisions during testing.
.EXAMPLE
    .\Toggle-Gigabyte.ps1 -Action Stop
    .\Toggle-Gigabyte.ps1 -Action Start
#>

param (
    [Parameter(Mandatory=$true)]
    [ValidateSet("Stop", "Start", "Status")]
    [string]$Action
)

# Check for Administrator elevation
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "This script requires Administrator privileges to manage hardware services. Please launch PowerShell as Administrator!"
    exit 1
}

# Known Gigabyte services that lock SMBus and Super I/O
$gbServices = @(
    "EasyTuneEngineService",
    "GService",
    "GCCService",
    "AppCenter",
    "GvService"
)

# Known Gigabyte background executables
$gbProcesses = @(
    "RGBFusion",
    "EasyTuneEngineService",
    "EasyTune",
    "SIV",
    "ThermalConsole",
    "GCC",
    "AppCenter",
    "CheckKill",
    "KillProcess"
)

if ($Action -eq "Stop") {
    Write-Host "[*] Stopping Gigabyte Services to free SMBus and Super I/O..." -ForegroundColor Cyan
    foreach ($srv in $gbServices) {
        $serviceObj = Get-Service -Name $srv -ErrorAction SilentlyContinue
        if ($serviceObj -and $serviceObj.Status -eq 'Running') {
            Stop-Service -Name $srv -Force -ErrorAction SilentlyContinue
            Write-Host "  [-] Stopped Service: $srv" -ForegroundColor Green
        }
    }

    Write-Host "[*] Terminating lingering Gigabyte user-space processes..." -ForegroundColor Cyan
    foreach ($proc in $gbProcesses) {
        $procObj = Get-Process -Name $proc -ErrorAction SilentlyContinue
        if ($procObj) {
            Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue
            Write-Host "  [-] Killed Process: $proc.exe" -ForegroundColor Green
        }
    }
    Write-Host "[+] Hardware buses (SMBus & Super I/O) are now clear for custom app testing." -ForegroundColor Yellow
}

elseif ($Action -eq "Start") {
    Write-Host "[*] Restoring Gigabyte Services..." -ForegroundColor Cyan
    foreach ($srv in $gbServices) {
        $serviceObj = Get-Service -Name $srv -ErrorAction SilentlyContinue
        if ($serviceObj -and $serviceObj.Status -ne 'Running') {
            Start-Service -Name $srv -ErrorAction SilentlyContinue
            Write-Host "  [+] Started Service: $srv" -ForegroundColor Green
        }
    }
    Write-Host "[+] Gigabyte services restored." -ForegroundColor Yellow
}

elseif ($Action -eq "Status") {
    Write-Host "=== Active Gigabyte Services ===" -ForegroundColor Cyan
    Get-Service -Name $gbServices -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize
    Write-Host "=== Active Gigabyte Processes ===" -ForegroundColor Cyan
    Get-Process -Name $gbProcesses -ErrorAction SilentlyContinue | Format-Table Id, ProcessName, CPU -AutoSize
}