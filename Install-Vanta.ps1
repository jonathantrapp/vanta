# Installs or updates VANTA as a normal Windows app, or rolls back to the previous installed build.
#
#   Install / update:  powershell -ExecutionPolicy Bypass -File .\Install-Vanta.ps1 [-Source <published folder>]
#   Roll back:         powershell -ExecutionPolicy Bypass -File .\Install-Vanta.ps1 -Rollback
#   (or double-click Install-Vanta.cmd). Removal: Uninstall-Vanta.ps1.
#
# What it does:
#  - program files: C:\Program Files\VANTA (writable by administrators only, which matters because VANTA always runs
#    elevated). The new build is staged in VANTA.new and every file's SHA-256 is verified before anything is replaced;
#  - the swap is transactional (Phase 11). Folder names used next to the install folder:
#      VANTA.new       staging copy (deleted and rebuilt on every run)
#      VANTA.swap      the build being replaced, while the swap runs
#      VANTA.previous  the rollback copy (one level)
#      VANTA.discard   the old rollback copy, deleted only after its replacement is in place
#    Order: VANTA -> VANTA.swap; VANTA.new -> VANTA (if this fails, VANTA.swap goes straight back); then
#    VANTA.previous -> VANTA.discard, VANTA.swap -> VANTA.previous (if this fails, VANTA.discard goes back), then
#    VANTA.discard is deleted. A later run first finishes or undoes whatever an interrupted run left behind, so there is
#    always a usable VANTA folder, and the last good rollback copy is never deleted before its replacement exists;
#  - every folder name is validated and normalised first: the install folder must be a dedicated folder (not a drive
#    root, Windows, Program Files itself, a user profile root), source and install folders must not overlap, and an
#    existing install folder must be a VANTA folder (install.json, or HwSuite.exe with nothing else unexpected);
#  - writes install-manifest.json (every file and its SHA-256) so Uninstall-Vanta.ps1 deletes only VANTA's own files;
#  - one per-user Start-menu shortcut "VANTA" (target HwSuite.exe in the install folder; the path never changes);
#  - settings in %APPDATA%\HwSuite are not read or written;
#  - the "Run at Windows logon" task is never created here. If it exists and starts a different VANTA copy, only its
#    executable path and working folder are changed (the task's own XML is edited and re-registered; triggers, delay,
#    principal and settings are kept as they are). If it already starts the installed copy it is not touched;
#  - refuses while VANTA runs (Quit from the tray first).
# Test switches (isolated folders only): -Isolated skips elevation, the Start menu and the logon task;
# -FailAt <step> injects a failure (stage, swap-out, swap-in, rotate-out, rotate-in, rollback-out, rollback-in).
param([string]$Source = (Join-Path $PSScriptRoot 'dist\HwSuite'),
      [string]$InstallDir = (Join-Path $env:ProgramFiles 'VANTA'),
      [switch]$Rollback,
      [switch]$Isolated,
      [string]$FailAt,
      [string]$TaskName = 'HwSuite (Elevated Logon)',
      [string]$Log)
$ErrorActionPreference = 'Stop'

if (-not $Isolated) {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        # program files and the task need administrator rights: ask once, run this same script elevated, wait for it
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Source', "`"$Source`"", '-InstallDir', "`"$InstallDir`"")
        if ($Rollback) { $a += '-Rollback' }
        $Log = Join-Path $PSScriptRoot 'install-last.log'
        $a += @('-Log', "`"$Log`"")
        $p = Start-Process powershell -Verb RunAs -ArgumentList $a -Wait -PassThru
        if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log | Where-Object { $_ -notmatch '^\*{10,}|^(Windows PowerShell transcript|Start time|End time|Username|RunAs User|Configuration Name|Machine|Host Application|Process ID|PSVersion|PSEdition|PSCompatibleVersions|BuildVersion|CLRVersion|WSManStackVersion|PSRemotingProtocolVersion|SerializationVersion|Transcript started)' } }
        exit $p.ExitCode
    }
}
if ($Log) { Start-Transcript -LiteralPath $Log -Force | Out-Null }
$code = 0
try {

function Fail-Point([string]$step) { if ($FailAt -and $FailAt -ieq $step) { throw "injected failure at '$step' (test)" } }

# A process started from inside an MSIX-packaged app (for example a desktop app session) can have its writes under the
# user profile silently redirected. The administrative share (\\localhost\C$) bypasses that; normal runs use plain paths.
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
    if ($script:Redirected) { Write-Host "Note: this process's profile writes are redirected (packaged-app context); writing through \\localhost\C$." }
}

# ---- path validation ------------------------------------------------------------------------------------------------
function Normalize([string]$p, [string]$what) {
    if ([string]::IsNullOrWhiteSpace($p)) { throw "$what is empty." }
    if ($p -match '[\*\?"<>|]') { throw "$what contains wildcard or invalid characters: $p" }
    if (-not [IO.Path]::IsPathRooted($p)) { throw "$what must be a full path: $p" }
    return [IO.Path]::GetFullPath($p).TrimEnd('\')
}
function Test-Under([string]$child, [string]$parent) { $child.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $child -ieq $parent }

$InstallDir = Normalize $InstallDir 'InstallDir'
if ($InstallDir -match '^[A-Za-z]:$' -or $InstallDir -match '^\\\\[^\\]+\\[^\\]+$') { throw "InstallDir must be a dedicated folder, not a drive or share root: $InstallDir" }
$leaf = Split-Path -Leaf $InstallDir
$parent = Split-Path -Parent $InstallDir
if (-not $parent -or $leaf -eq '' -or $InstallDir -match '^[A-Za-z]:$') { throw "InstallDir must be a dedicated folder, not a drive root: $InstallDir" }
$forbidden = @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE, (Split-Path -Parent $env:USERPROFILE),
               [Environment]::GetFolderPath('ApplicationData'), [Environment]::GetFolderPath('LocalApplicationData'), [Environment]::GetFolderPath('Desktop'),
               [Environment]::GetFolderPath('MyDocuments'), $programs) | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
foreach ($f in $forbidden) { if ($InstallDir -ieq $f) { throw "InstallDir may not be a system or profile folder: $InstallDir" } }
if (Test-Under $InstallDir $env:SystemRoot) { throw "InstallDir may not be inside the Windows folder: $InstallDir" }
if (-not (Test-Path -LiteralPath (Real $parent))) { throw "The parent folder of InstallDir does not exist: $parent" }
if ($leaf -match '\.(new|swap|previous|discard)$') { throw "InstallDir may not end in a name VANTA uses for its own work folders: $leaf" }

$dir = Real $InstallDir; $prev = Real "$InstallDir.previous"; $stage = Real "$InstallDir.new"; $swap = Real "$InstallDir.swap"; $discard = Real "$InstallDir.discard"
$exe = Join-Path $InstallDir 'HwSuite.exe'
$lnkPath = Join-Path $programs 'VANTA.lnk'
$workFolders = @($dir, $prev, $stage, $swap, $discard)

# A folder this script may replace or delete: one it made (install.json) or a VANTA build (HwSuite.exe + HwSuite.dll).
function Test-VantaFolder([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return $true }
    if (Test-Path -LiteralPath (Join-Path $p 'install.json')) { return $true }
    return (Test-Path -LiteralPath (Join-Path $p 'HwSuite.exe')) -and (Test-Path -LiteralPath (Join-Path $p 'HwSuite.dll'))
}
function Assert-VantaFolder([string]$p) { if (-not (Test-VantaFolder $p)) { throw "Refusing to touch $p : it exists but is not a VANTA folder (no install.json, HwSuite.exe or HwSuite.dll)." } }
function Remove-WorkFolder([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return }
    if ($workFolders -notcontains $p) { throw "internal: $p is not one of VANTA's folders" }
    Assert-VantaFolder $p
    Remove-Item -LiteralPath $p -Recurse -Force
}

function Stop-IfRunning {
    $running = @(Get-Process -Name HwSuite -ErrorAction SilentlyContinue)
    if ($running.Count) { throw "VANTA is running ($($running.Count) process(es), including its fan watchdog). Quit it from the tray (Quit, restore fans), then run this again." }
}

# ---- recovery of an interrupted earlier run -------------------------------------------------------------------------
function Repair-Interrupted {
    if ((Test-Path -LiteralPath $swap) -and -not (Test-Path -LiteralPath $dir)) {
        Assert-VantaFolder $swap
        Rename-Item -LiteralPath $swap -NewName (Split-Path -Leaf $dir)
        Write-Host "Recovered: an interrupted update had moved the installed build aside; it is back in $InstallDir."
    }
    if ((Test-Path -LiteralPath $discard) -and -not (Test-Path -LiteralPath $prev)) {
        Assert-VantaFolder $discard
        Rename-Item -LiteralPath $discard -NewName (Split-Path -Leaf $prev)
        Write-Host "Recovered: the rollback copy is back in $InstallDir.previous."
    }
    if ((Test-Path -LiteralPath $swap) -and (Test-Path -LiteralPath $dir)) {
        # the new build is in place; finish rotating the replaced build into the rollback slot
        Assert-VantaFolder $swap
        if (Test-Path -LiteralPath $prev) { Remove-WorkFolder $discard; Rename-Item -LiteralPath $prev -NewName (Split-Path -Leaf $discard) }
        Rename-Item -LiteralPath $swap -NewName (Split-Path -Leaf $prev)
        Write-Host "Recovered: finished an interrupted update (the replaced build is now $InstallDir.previous)."
    }
    if ((Test-Path -LiteralPath $discard) -and (Test-Path -LiteralPath $prev)) { Remove-WorkFolder $discard }
    if (Test-Path -LiteralPath $stage) { Remove-WorkFolder $stage }
}

function Get-Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

function Write-Manifest([string]$folder) {
    $root = (Get-Item -LiteralPath $folder).FullName.TrimEnd('\')
    $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object { $_.Name -ne 'install-manifest.json' } | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($root.Length).TrimStart('\'); Sha256 = Get-Hash $_.FullName }
    })
    [ordered]@{ Product = 'VANTA'; Written = (Get-Date).ToString('s'); Files = $files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'install-manifest.json') -Encoding UTF8
}

# An older build kept as the rollback copy gets a manifest too (only when its install.json matches its HwSuite.dll).
function Ensure-Manifest([string]$folder) {
    if (-not (Test-Path -LiteralPath $folder) -or (Test-Path -LiteralPath (Join-Path $folder 'install-manifest.json'))) { return }
    $info = Join-Path $folder 'install.json'; $dll = Join-Path $folder 'HwSuite.dll'
    if (-not ((Test-Path -LiteralPath $info) -and (Test-Path -LiteralPath $dll))) { return }
    try { $j = Get-Content -LiteralPath $info -Raw | ConvertFrom-Json } catch { return }
    if ($j.HwSuiteDllSha256 -and $j.HwSuiteDllSha256 -ieq (Get-Hash $dll)) { Write-Manifest $folder; Write-Host "Recorded the file list of $folder (its install.json matches its HwSuite.dll)." }
}

function Write-Shortcut {
    if ($Isolated) { return }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("VANTA-" + [guid]::NewGuid().ToString('N') + ".lnk")
    $sh = New-Object -ComObject WScript.Shell
    $l = $sh.CreateShortcut($tmp)
    $l.TargetPath = $exe
    $l.WorkingDirectory = $InstallDir
    $l.IconLocation = "$exe,0"
    $l.Description = 'VANTA: fans and lighting for this PC (asks for administrator rights)'
    $l.Save()
    New-Item -ItemType Directory -Force (Real $programs) | Out-Null
    Copy-Item -LiteralPath $tmp -Destination (Real $lnkPath) -Force
    Remove-Item -LiteralPath $tmp -Force
    # one entry only: remove other Start-menu shortcuts that start a VANTA/HwSuite build (old copies, renamed duplicates)
    $menus = @((Real $programs), (Real (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs')))
    foreach ($m in $menus) {
        Get-ChildItem -LiteralPath $m -Recurse -Filter *.lnk -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.FullName -eq (Real $lnkPath)) { return }
            $t = $sh.CreateShortcut($_.FullName).TargetPath
            if ($t -and ([IO.Path]::GetFileName($t) -ieq 'HwSuite.exe' -or $_.BaseName -ieq 'VANTA')) { Remove-Item -LiteralPath $_.FullName -Force; Write-Host "Removed duplicate shortcut $($_.FullName) (pointed at $t)" }
        }
    }
}

# The logon task, if it exists: change only <Command> and <WorkingDirectory> in its own definition and re-register it.
function Get-TaskXml { $ErrorActionPreference = 'Continue'; $x = & schtasks.exe /Query /TN $TaskName /XML 2>$null; if ($LASTEXITCODE -ne 0 -or -not $x) { return $null }; return ($x -join "`n") }
function Update-LogonTask {
    if ($Isolated) { return }
    $xmlText = Get-TaskXml
    if (-not $xmlText) { Write-Host "Run at Windows logon: off (not changed)."; return }
    [xml]$doc = $xmlText
    $ns = New-Object Xml.XmlNamespaceManager $doc.NameTable; $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $cmd = $doc.SelectSingleNode('//t:Actions/t:Exec/t:Command', $ns)
    if (-not $cmd) { Write-Warning "Run at Windows logon: the task has no program action; left unchanged."; return }
    if ($cmd.InnerText -ieq $exe) { Write-Host "Run at Windows logon: on, already starts $exe (not changed)."; return }
    $old = $cmd.InnerText
    $cmd.InnerText = $exe
    $wd = $doc.SelectSingleNode('//t:Actions/t:Exec/t:WorkingDirectory', $ns)
    if ($wd) { $wd.InnerText = $InstallDir }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("vanta-task-" + [guid]::NewGuid().ToString('N') + ".xml")
    try {
        $doc.Save($tmp)
        $ErrorActionPreference = 'Continue'
        $out = & schtasks.exe /Create /TN $TaskName /XML $tmp /F 2>&1
        $ok = $LASTEXITCODE -eq 0
        $ErrorActionPreference = 'Stop'
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    $after = Get-TaskXml
    if ($ok -and $after -and ([xml]$after).SelectSingleNode('//t:Actions/t:Exec/t:Command', $ns).InnerText -ieq $exe) {
        Write-Host "Run at Windows logon: on (your setting); now starts $exe instead of $old. Only the program path and working folder changed."
    } else { Write-Warning "Run at Windows logon: could not repoint the task ($out); it still starts $old. Turn it off and on in VANTA (System page) to fix." }
}

Stop-IfRunning
Repair-Interrupted

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $prev)) { throw "No previous build at $InstallDir.previous to roll back to." }
    Assert-VantaFolder $prev; Assert-VantaFolder $dir
    # current -> swap, previous -> current (undo on failure), swap -> previous
    Rename-Item -LiteralPath $dir -NewName (Split-Path -Leaf $swap)
    try { Fail-Point 'rollback-in'; Rename-Item -LiteralPath $prev -NewName (Split-Path -Leaf $dir) }
    catch { Rename-Item -LiteralPath $swap -NewName (Split-Path -Leaf $dir); throw "Rollback failed and was undone ($($_.Exception.Message)); $InstallDir is unchanged." }
    try { Fail-Point 'rollback-out'; Rename-Item -LiteralPath $swap -NewName (Split-Path -Leaf $prev) }
    catch { Write-Warning "Rolled back, but the replaced build is still in $InstallDir.swap ($($_.Exception.Message)); the next run moves it to $InstallDir.previous." }
    Write-Shortcut
    Update-LogonTask
    Write-Host "Rolled back: $InstallDir now holds the previous build; the replaced one is kept as $InstallDir.previous."
    return
}

$Source = Normalize $Source 'Source'
foreach ($w in @($InstallDir, "$InstallDir.previous", "$InstallDir.new", "$InstallDir.swap", "$InstallDir.discard")) {
    if ((Test-Under $Source $w) -or (Test-Under $w $Source)) { throw "Source ($Source) and the install folders ($w) overlap." }
}
if (-not (Test-Path -LiteralPath (Join-Path $Source 'HwSuite.exe'))) { throw "No HwSuite.exe in $Source (publish the build first)." }
Assert-VantaFolder $dir; Assert-VantaFolder $prev

# 1. stage and verify (nothing installed changes until every file matches)
& robocopy.exe $Source $stage /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy to $InstallDir.new failed (robocopy $LASTEXITCODE)" }
$srcFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -File)
$bad = @($srcFiles | Where-Object {
    $rel = $_.FullName.Substring($Source.Length).TrimStart('\')
    $copy = Join-Path $stage $rel
    -not (Test-Path -LiteralPath $copy) -or (Get-Hash $copy) -ne (Get-Hash $_.FullName)
})
if ($bad.Count) { Remove-WorkFolder $stage; throw "$($bad.Count) file(s) differ after copying (first: $($bad[0].FullName)); nothing was replaced." }
$dllHash = Get-Hash (Join-Path $stage 'HwSuite.dll')
[ordered]@{ Product = 'VANTA'; InstalledLocal = (Get-Date).ToString('s'); Source = $Source; Files = $srcFiles.Count; HwSuiteDllSha256 = $dllHash;
            Version = (Get-Item -LiteralPath (Join-Path $stage 'HwSuite.exe')).VersionInfo.FileVersion } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'install.json') -Encoding UTF8
Write-Manifest $stage
Fail-Point 'stage'

# 2. swap: current -> swap, new -> current (undo on failure)
$hadCurrent = Test-Path -LiteralPath $dir
if ($hadCurrent) { Ensure-Manifest $dir; Fail-Point 'swap-out'; Rename-Item -LiteralPath $dir -NewName (Split-Path -Leaf $swap) }
try { Fail-Point 'swap-in'; Rename-Item -LiteralPath $stage -NewName (Split-Path -Leaf $dir) }
catch {
    if ($hadCurrent) { Rename-Item -LiteralPath $swap -NewName (Split-Path -Leaf $dir) }
    throw "Installing the new build failed and was undone ($($_.Exception.Message)); $InstallDir is unchanged."
}

# 3. rotate: previous -> discard, swap -> previous (undo on failure), then delete discard
if ($hadCurrent) {
    $hadPrev = Test-Path -LiteralPath $prev
    if ($hadPrev) { Remove-WorkFolder $discard; Rename-Item -LiteralPath $prev -NewName (Split-Path -Leaf $discard) }
    try { Fail-Point 'rotate-in'; Rename-Item -LiteralPath $swap -NewName (Split-Path -Leaf $prev) }
    catch {
        if ($hadPrev) { Rename-Item -LiteralPath $discard -NewName (Split-Path -Leaf $prev) }
        Write-Warning "The new build is installed, but the replaced one is still in $InstallDir.swap ($($_.Exception.Message)); the next run makes it the rollback copy. The earlier rollback copy is unchanged."
    }
    try { Fail-Point 'rotate-out'; Remove-WorkFolder $discard } catch { Write-Warning "Could not delete $InstallDir.discard ($($_.Exception.Message)); the next run removes it." }
}

# 4. Start menu and (only if already on) the logon task
Write-Shortcut
Update-LogonTask
Write-Host "Installed $($srcFiles.Count) files to $InstallDir (HwSuite.dll $($dllHash.Substring(0,12))...)."
if (-not $Isolated) { Write-Host "Start menu: $lnkPath -> $exe" }
if (Test-Path -LiteralPath $prev) { Write-Host "Previous build kept: $InstallDir.previous (Install-Vanta.ps1 -Rollback)" }
Write-Host "Settings are unchanged: $([Environment]::GetFolderPath('ApplicationData'))\HwSuite"
} catch { Write-Host "ERROR: $($_.Exception.Message)"; $code = 1 }
finally { if ($Log) { Stop-Transcript | Out-Null } }
exit $code
