# Removes the installed VANTA app: its Start-menu shortcut, its own files in C:\Program Files\VANTA (and the rollback
# copy), and the "Run at Windows logon" task if that task starts the installed build. Settings in %APPDATA%\HwSuite are
# KEPT unless you add -RemoveSettings. Refuses while VANTA runs.
#
# Fan hand-back first (Phase 11): if VANTA's record (controlled.json) lists fan channels it still claims (after a crash),
# VANTA's own "--restore" runs and its result is checked: exit 0 and an empty record, or nothing is removed. Note what
# --restore can and cannot do: GPU fans return to the VBIOS curve; motherboard headers are set to 100% because BIOS control
# can only be handed back by the process that took it, so the BIOS fan curve returns after the next reboot.
#
# Only VANTA's own files are deleted: those listed in install-manifest.json (written by Install-Vanta.ps1). Anything
# else found in the folder is reported and left in place. A folder without a manifest is not deleted.
#
#   powershell -ExecutionPolicy Bypass -File .\Uninstall-Vanta.ps1 [-RemoveSettings]
# Test switches: -Isolated (no elevation, Start menu or task), -DataDir <settings folder>, -RestoreCommand <program>.
param([string]$InstallDir = (Join-Path $env:ProgramFiles 'VANTA'), [switch]$RemoveSettings, [string]$Log,
      [switch]$Isolated, [string]$DataDir, [string]$RestoreCommand, [string]$TaskName = 'HwSuite (Elevated Logon)')
$ErrorActionPreference = 'Stop'

if (-not $Isolated) {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
        if ($RemoveSettings) { $a += '-RemoveSettings' }
        $Log = Join-Path $PSScriptRoot 'uninstall-last.log'
        $a += @('-Log', "`"$Log`"")
        $p = Start-Process powershell -Verb RunAs -ArgumentList $a -Wait -PassThru
        if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log | Where-Object { $_ -notmatch '^\*{10,}|^(Windows PowerShell transcript|Start time|End time|Username|RunAs User|Configuration Name|Machine|Host Application|Process ID|PSVersion|PSEdition|PSCompatibleVersions|BuildVersion|CLRVersion|WSManStackVersion|PSRemotingProtocolVersion|SerializationVersion|Transcript started)' } }
        exit $p.ExitCode
    }
}
if ($Log) { Start-Transcript -LiteralPath $Log -Force | Out-Null }
$code = 0
try {

# same packaged-app redirection handling as Install-Vanta.ps1
function Real([string]$p) { if ($script:Redirected -and $p -match '^([A-Za-z]):\\(.*)$') { "\\localhost\$($Matches[1].ToUpper())`$\$($Matches[2])" } else { $p } }
$programs = [Environment]::GetFolderPath('Programs')
$script:Redirected = $false
if (-not $Isolated) {
    try {
        $marker = Join-Path $programs (".vanta-probe-" + [guid]::NewGuid().ToString('N'))
        Set-Content -LiteralPath $marker -Value probe
        $script:Redirected = $true
        $script:Redirected = -not (Test-Path -LiteralPath (Real $marker))
        Remove-Item -LiteralPath $marker -Force
    } catch { $script:Redirected = $false }
}

# ---- validate ------------------------------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($InstallDir) -or -not [IO.Path]::IsPathRooted($InstallDir) -or $InstallDir -match '[\*\?"<>|]') { throw "InstallDir must be a full path: $InstallDir" }
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if ($InstallDir -match '^[A-Za-z]:$' -or -not (Split-Path -Parent $InstallDir)) { throw "InstallDir must be a dedicated folder: $InstallDir" }
foreach ($f in @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE, [Environment]::GetFolderPath('ApplicationData')) | Where-Object { $_ }) {
    if ($InstallDir -ieq [IO.Path]::GetFullPath($f).TrimEnd('\')) { throw "InstallDir may not be a system or profile folder: $InstallDir" }
}

$running = @(Get-Process -Name HwSuite -ErrorAction SilentlyContinue)
if ($running.Count) { throw "VANTA is running. Quit it from the tray (Quit, restore fans), then run this again." }
$exe = Join-Path $InstallDir 'HwSuite.exe'
$data = if ($DataDir) { $DataDir } else { Real (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'HwSuite') }

function Claimed {
    $claimed = Join-Path $data 'controlled.json'
    if (-not (Test-Path -LiteralPath $claimed)) { return @() }
    $raw = (Get-Content -LiteralPath $claimed -Raw).Trim()
    if ($raw -in @('', '[]')) { return @() }
    try { return @($raw | ConvertFrom-Json) } catch { return @('unreadable') }
}

# ---- 1. hand back claimed fans, and check that it worked -------------------------------------------------------------
$before = @(Claimed)
if ($before.Count -gt 0) {
    $restore = if ($RestoreCommand) { $RestoreCommand } else { Real $exe }
    if (-not (Test-Path -LiteralPath $restore)) { throw "VANTA's record lists $($before.Count) fan channel(s) it still claims, but $restore is missing, so they cannot be handed back. Nothing was removed. Reboot (the BIOS and GPU firmware take the fans back), then run this again." }
    Write-Host "VANTA's record lists $($before.Count) fan channel(s) it still claims; running its --restore first."
    $result = Join-Path ([IO.Path]::GetTempPath()) ("vanta-restore-" + [guid]::NewGuid().ToString('N') + ".json")
    $p = Start-Process -FilePath $restore -ArgumentList @('--restore', '--result', "`"$result`"") -Wait -PassThru -WindowStyle Hidden
    $detail = if (Test-Path -LiteralPath $result) { try { (Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).Text } catch { '' } } else { '' }
    Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
    switch ($p.ExitCode) {
        0 { }
        3 { throw "--restore reported that VANTA (or a hardware test) still holds the hardware. Nothing was removed." }
        4 { throw "--restore could not hand every fan back ($detail). Nothing was removed; its record is kept. Reboot, then run this again." }
        default { throw "--restore ended with exit code $($p.ExitCode) ($detail). Nothing was removed." }
    }
    $after = @(Claimed)
    if ($after.Count -gt 0) { throw "--restore reported success but VANTA's record still lists $($after.Count) channel(s). Nothing was removed." }
    Write-Host "Fans handed back: $detail"
    Write-Host "Motherboard headers that VANTA drove stay at full speed until the next reboot; the BIOS fan curve returns then."
}

# ---- 2. what may be deleted: manifest-listed files only --------------------------------------------------------------
function Plan-Folder([string]$folder) {
    $real = Real $folder
    if (-not (Test-Path -LiteralPath $real)) { return $null }
    $man = Join-Path $real 'install-manifest.json'
    if (-not (Test-Path -LiteralPath $man)) { throw "$folder has no install-manifest.json, so VANTA cannot tell its own files from anything else there. Nothing was removed. Run Install-Vanta.ps1 once (it records the file list), then uninstall." }
    $m = Get-Content -LiteralPath $man -Raw | ConvertFrom-Json
    if ($m.Product -ne 'VANTA') { throw "$man is not a VANTA manifest. Nothing was removed." }
    $root = (Get-Item -LiteralPath $real).FullName.TrimEnd('\')
    $listed = @{}
    foreach ($f in $m.Files) {
        if ($f.Path -match '(^|\\)\.\.(\\|$)' -or [IO.Path]::IsPathRooted($f.Path)) { throw "$man lists an unsafe path ($($f.Path)). Nothing was removed." }
        $listed[[IO.Path]::GetFullPath((Join-Path $root $f.Path)).ToLowerInvariant()] = $true
    }
    $listed[(Join-Path $root 'install-manifest.json').ToLowerInvariant()] = $true
    $all = @(Get-ChildItem -LiteralPath $real -Recurse -File -Force)
    $ours = @($all | Where-Object { $listed.ContainsKey($_.FullName.ToLowerInvariant()) })
    $other = @($all | Where-Object { -not $listed.ContainsKey($_.FullName.ToLowerInvariant()) })
    return [pscustomobject]@{ Folder = $folder; Real = $real; Ours = $ours; Other = $other }
}
$plans = @(foreach ($d in @($InstallDir, "$InstallDir.previous")) { Plan-Folder $d }) | Where-Object { $_ }

# ---- 3. logon task (only when it starts the installed copy) and shortcuts --------------------------------------------
if (-not $Isolated) {
    $ErrorActionPreference = 'Continue'
    $xml = & schtasks.exe /Query /TN $TaskName /XML 2>$null
    $taskTarget = if ($LASTEXITCODE -eq 0 -and $xml) { ([xml]($xml -join "`n")).Task.Actions.Exec.Command } else { $null }
    $ErrorActionPreference = 'Stop'
    if ($taskTarget -and $taskTarget -ieq $exe) { & schtasks.exe /Delete /TN $TaskName /F | Out-Null; Write-Host "Removed the Run-at-logon task (it started $exe)." }
    elseif ($taskTarget) { Write-Host "Run-at-logon task left as is: it starts $taskTarget, not the installed copy." }

    $sh = New-Object -ComObject WScript.Shell
    foreach ($m in @((Real $programs), (Real (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs')))) {
        Get-ChildItem -LiteralPath $m -Recurse -Filter *.lnk -ErrorAction SilentlyContinue | ForEach-Object {
            $t = $sh.CreateShortcut($_.FullName).TargetPath
            if ($t -and ($t -ieq $exe -or $t.StartsWith($InstallDir + '\', [StringComparison]::OrdinalIgnoreCase))) { Remove-Item -LiteralPath $_.FullName -Force; Write-Host "Removed shortcut $($_.FullName)" }
        }
    }
}

# ---- 4. files ----------------------------------------------------------------------------------------------------------
foreach ($p in $plans) {
    foreach ($f in $p.Ours) { Remove-Item -LiteralPath $f.FullName -Force }
    # remove now-empty folders, deepest first
    Get-ChildItem -LiteralPath $p.Real -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    if (-not (Get-ChildItem -LiteralPath $p.Real -Force)) { Remove-Item -LiteralPath $p.Real -Force; Write-Host "Removed $($p.Folder) ($($p.Ours.Count) files)" }
    else { Write-Host "Removed VANTA's $($p.Ours.Count) files from $($p.Folder); left $($p.Other.Count) file(s) that are not VANTA's: $(($p.Other | Select-Object -First 5 | ForEach-Object { $_.Name }) -join ', ')" }
}
foreach ($w in @("$InstallDir.new", "$InstallDir.swap", "$InstallDir.discard")) {
    if (Test-Path -LiteralPath (Real $w)) { Write-Host "Left $w in place (an interrupted install's work folder): run Install-Vanta.ps1 to resolve it, or delete it yourself." }
}

if ($RemoveSettings) {
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force; Write-Host "Removed settings $data" }
} else { Write-Host "Settings kept: $data (config, fan mappings, keyboard verification). Add -RemoveSettings to delete them." }
} catch { Write-Host "ERROR: $($_.Exception.Message)"; $code = 1 }
finally { if ($Log) { Stop-Transcript | Out-Null } }
exit $code
