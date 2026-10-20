<#
  Skyline Together - player setup (Cities: Skylines II co-op)

  Installs the mod into the game's local Mods folder, checks for common problems, and collects logs for bug reports.
  The mod itself is embedded in this script, so this one file is all you need.

    Install:        double-click Install.cmd
                    (or: powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-SkylineTogether.ps1)
    Collect logs:   double-click Collect-Logs.cmd   (or add -CollectLogs; add -IncludeDumps for crash dumps)
    Uninstall:      add -Uninstall (add -RemoveSettings to also delete the mod's settings without being asked)
#>
param(
    [switch]$CollectLogs,
    [switch]$IncludeDumps,   # with -CollectLogs: also the crash dumps (memory snapshots that can contain passwords)
    [switch]$Uninstall,
    [switch]$RemoveSettings, # with -Uninstall: also delete the mod's settings (session password, ban list)
    [switch]$NoPause,
    [string]$OutDir = '',
    [switch]$SkipGameCheck,  # testing only
    [switch]$TestNoDesktop   # testing only: -CollectLogs as if the Desktop folder did not resolve
)

$ErrorActionPreference = 'Stop'
$ModName = 'SkylineTogether'
$Version = '__VERSION__'
$BuildId = '__BUILDID__'
$PayloadSha256 = '__PAYLOADSHA__'
$OldMultiplayerModId = '150432'

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "   OK   $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "   WARN $text" -ForegroundColor Yellow }
function Write-Bad([string]$text) { Write-Host "   FAIL $text" -ForegroundColor Red }
# Hashing and zip handling use .NET directly: the Get-FileHash and *-Archive cmdlets live in modules that fail to
# load when Windows PowerShell is started from PowerShell 7 (its PSModulePath leaks into the child process).
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Finish([int]$code) {
    if (-not $NoPause) { Write-Host ''; [void](Read-Host 'Press Enter to close') }
    exit $code
}
# every path is used literally: a user folder with [ ] in its name must not be read as a wildcard pattern
function New-Dir([string]$path) { [void][IO.Directory]::CreateDirectory($path) }
function Remove-Dir([string]$path) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
}
# collected text files name the Windows user folder (and so the user name); it is replaced by %USERPROFILE%
function Hide-UserFolder([string]$file) {
    $profileDir = $env:USERPROFILE
    if (-not $profileDir) { return }
    try {
        $text = [IO.File]::ReadAllText($file)
        $paths = @($profileDir)
        # the 8.3 short form of the user folder names the user too
        try { $paths += (New-Object -ComObject Scripting.FileSystemObject).GetFolder($profileDir).ShortPath } catch { }
        # each with backslashes, forward slashes, doubled backslashes (JSON) and as in a file:/// URL (a space as %20)
        $forms = @($paths | ForEach-Object { $_; $_.Replace('\', '/'); $_.Replace('\', '\\'); try { ([Uri]$_).AbsoluteUri.Substring(8) } catch { } }) | Where-Object { $_ } | Select-Object -Unique
        foreach ($form in $forms) {
            $text = [Text.RegularExpressions.Regex]::Replace($text, [Text.RegularExpressions.Regex]::Escape($form), '%USERPROFILE%', 'IgnoreCase')
        }
        [IO.File]::WriteAllText($file, $text)
    } catch {
        Write-Warn2 "Could not remove the user folder from $([IO.Path]::GetFileName($file)); check it before sending."
    }
}

$UserData = Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'
$ModsDir = Join-Path $UserData 'Mods'
$ModDir = Join-Path $ModsDir $ModName
$LogsDir = Join-Path $UserData 'Logs'

Write-Host "Skyline Together $Version (build $BuildId)" -ForegroundColor White

# ------------------------------------------------------------------------------------------- collect logs
if ($CollectLogs) {
    Write-Step 'Collecting logs for a bug report'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stage = Join-Path $env:TEMP "SkylineTogether-logs-$stamp"
    New-Dir $stage
    $patterns = @(
        @{ Dir = $LogsDir; Filter = 'SkylineTogether*' },
        @{ Dir = $LogsDir; Filter = 'Modding.log' },
        @{ Dir = $LogsDir; Filter = 'UI.log' },
        @{ Dir = $UserData; Filter = 'Player.log' },
        @{ Dir = $UserData; Filter = 'Player-prev.log' }
    )
    $count = 0
    $dumps = 0
    # diagnostics files of versions before 0.2.0 contain full IP addresses and the user name: only those written since
    # the installed copy was built are collected (its DLL keeps the release's time)
    $installedDll = Join-Path $ModDir 'SkylineTogether.dll'
    $since = if (Test-Path -LiteralPath $installedDll) { (Get-Item -LiteralPath $installedDll).LastWriteTimeUtc } else { $null }
    $oldDiagnostics = 0
    foreach ($p in $patterns) {
        if (-not (Test-Path -LiteralPath $p.Dir)) { continue }
        Get-ChildItem -LiteralPath $p.Dir -Filter $p.Filter -File -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -like 'SkylineTogether-diagnostics-*' -and (-not $since -or $_.LastWriteTimeUtc -lt $since)) {
                if ($since) { $oldDiagnostics++; return }
                Write-Warn2 "Collected $($_.Name), but no installed copy tells its age: files of versions before 0.2.0 contain full IP addresses. Delete old ones in the Logs folder first."
            }
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage $_.Name) -Force
            $count++
        }
    }
    if ($oldDiagnostics -gt 0) {
        Write-Ok "Left out $oldDiagnostics diagnostics file(s) written before this version was installed (older versions wrote full IP addresses)"
    }
    $recordings = Join-Path $UserData 'ModsData\SkylineTogether\Recordings'
    if (Test-Path -LiteralPath $recordings) {
        Get-ChildItem -LiteralPath $recordings -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
            $dst = Join-Path $stage ('recording-' + $_.Name)
            New-Dir $dst
            Get-ChildItem -LiteralPath $_.FullName -Filter 'verdict*' -File -ErrorAction SilentlyContinue | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dst $_.Name) -Force
            }
        }
    }
    # Unity crash reports of the last two crashes: error.log always, crash.dmp only with -IncludeDumps (a dump is a
    # snapshot of the game's memory and can contain passwords and chat)
    $crashRoots = @(
        (Join-Path $env:TEMP 'Colossal Order\Cities Skylines II\Crashes'),
        (Join-Path $env:LOCALAPPDATA 'Temp\Colossal Order\Cities Skylines II\Crashes'),
        (Join-Path $UserData 'Crashes')
    ) | Select-Object -Unique
    foreach ($root in $crashRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2 | ForEach-Object {
            $dst = Join-Path $stage ('crash-' + $_.Name)
            New-Dir $dst
            Get-ChildItem -LiteralPath $_.FullName -File -ErrorAction SilentlyContinue |
                Where-Object { ($_.Extension -in '.log', '.txt', '.json' -or ($IncludeDumps -and $_.Extension -eq '.dmp')) -and $_.Length -lt 200MB } |
                ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dst $_.Name) -Force
                    $count++
                    if ($_.Extension -eq '.dmp') { $dumps++ }
                }
        }
    }
    if ($count -eq 0) {
        Remove-Dir $stage
        Write-Bad "No logs found in $LogsDir. Start the game with the mod once, then try again."
        Finish 1
    }
    # the text files name the Windows user folder: replaced by %USERPROFILE%
    Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object { $_.Extension -in '.log', '.txt', '.json' } | ForEach-Object { Hide-UserFolder $_.FullName }
    # the Desktop, else (a redirected or offline Desktop: GetFolderPath returns '') the user folder, this script's
    # folder or TEMP; -OutDir names the folder (created when missing)
    $desktop = ''
    if ($OutDir) {
        try { New-Dir $OutDir; $desktop = $OutDir } catch { Write-Warn2 "Could not use $OutDir ($($_.Exception.Message))." }
    } elseif (-not $TestNoDesktop) {
        try { $desktop = [Environment]::GetFolderPath('Desktop') } catch { $desktop = '' }
    }
    if (-not $desktop -or -not (Test-Path -LiteralPath $desktop -PathType Container)) {
        $fallback = @($env:USERPROFILE, $PSScriptRoot, $env:TEMP) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } | Select-Object -First 1
        if (-not $fallback) {
            Remove-Dir $stage
            Write-Bad 'Found no folder to save the logs in (no Desktop, user folder or TEMP). Run again with -OutDir <folder>.'
            Finish 1
        }
        $why = if ($OutDir) { "$OutDir could not be used" } else { 'Your Desktop folder could not be found' }
        Write-Warn2 "$why; the zip is saved in $fallback instead."
        $desktop = $fallback
    }
    $zip = Join-Path $desktop "SkylineTogether-logs-$stamp.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Remove-Dir $stage
    Write-Ok "Saved $count log file(s) to:"
    Write-Host "        $zip"
    $size = (Get-Item -LiteralPath $zip).Length
    if ($size -gt 25MB) {
        Write-Warn2 ('The zip is {0:N0} MB; GitHub accepts at most 25 MB per file. Attach the files from the zip separately.' -f ($size / 1MB))
    }
    if ($dumps -gt 0) {
        Write-Host '   It contains player names, Steam IDs, the session name and IP addresses, but only in shortened form; paths to your user folder were replaced by %USERPROFILE%.'
        Write-Warn2 "It also contains $dumps crash dump(s): snapshots of the game's memory, which can include passwords and chat."
        Write-Host  '        Send this zip privately to the person who asked for it, not as an attachment to a public bug report.'
    } else {
        Write-Host '   Attach this zip to your bug report on GitHub: https://github.com/ljkx/SkylineTogether/issues'
        Write-Host '   It contains player names, Steam IDs, the session name and IP addresses, but only in shortened form (no passwords, no chat); paths to your user folder were replaced by %USERPROFILE%.'
        Write-Host '   Crash dumps are not included. Add -IncludeDumps only when asked, and send dumps privately.'
    }
    Finish 0
}

# ------------------------------------------------------------------------------------------- checks
Write-Step 'Checking the game'
if (-not (Test-Path -LiteralPath $UserData)) {
    Write-Bad "Cities: Skylines II user folder not found ($UserData)."
    Write-Host '   Start the game once (until the main menu), close it, and run this again.'
    Finish 1
}
Write-Ok 'Cities: Skylines II user folder found'

$game = if ($SkipGameCheck) { $null } else { Get-Process -Name 'Cities2' -ErrorAction SilentlyContinue }
while ($game) {
    if ($NoPause) {
        # nobody can press Enter: stop instead of waiting forever
        Write-Bad 'Cities: Skylines II is running. Close the game completely, then run this again.'
        exit 1
    }
    Write-Warn2 'Cities: Skylines II is running. Close the game completely, then press Enter.'
    [void](Read-Host)
    $game = Get-Process -Name 'Cities2' -ErrorAction SilentlyContinue
}
Write-Ok 'The game is not running'

# leftovers of an install that was interrupted (outside the Mods folder, so the game never loads them); a previous copy
# that could not be put back is kept while no copy is installed
Get-ChildItem -LiteralPath $UserData -Directory -Filter "$ModName-setup-*" -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Name.StartsWith("$ModName-setup-old-") -or (Test-Path -LiteralPath (Join-Path $ModsDir $ModName)) } |
    ForEach-Object { Remove-Dir $_.FullName }

# ------------------------------------------------------------------------------------------- uninstall
if ($Uninstall) {
    Write-Step 'Removing Skyline Together'
    if (Test-Path -LiteralPath $ModDir) { Remove-Item -LiteralPath $ModDir -Recurse -Force; Write-Ok "Removed $ModDir" }
    else { Write-Ok 'Skyline Together was not installed' }
    # temporary files of joins, snapshots, setups and log collections
    $temps = @(Join-Path $env:TEMP $ModName)
    $temps += @(Get-ChildItem -LiteralPath $env:TEMP -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$ModName-setup-*" -or $_.Name -like "$ModName-logs-*" } | ForEach-Object { $_.FullName })
    $removedTemps = 0
    foreach ($t in $temps) {
        if (Test-Path -LiteralPath $t) { Remove-Dir $t; $removedTemps++ }
    }
    if ($removedTemps -gt 0) { Write-Ok 'Removed temporary files' }
    # the settings hold the session password and the ban list: deleted only when the player says so
    $settingsDir = Join-Path $UserData "ModsSettings\$ModName"
    if (Test-Path -LiteralPath $settingsDir) {
        $remove = [bool]$RemoveSettings
        if (-not $remove -and -not $NoPause) {
            $answer = Read-Host '   Also delete your Skyline Together settings (player name, session password, ban list)? [y/N]'
            $remove = $answer -match '^\s*(y|yes|j|ja)\s*$'
        }
        if ($remove) { Remove-Dir $settingsDir; Write-Ok "Removed the settings ($settingsDir)" }
        else { Write-Ok "Kept the settings ($settingsDir); run with -Uninstall -RemoveSettings to delete them" }
    }
    Finish 0
}

# ------------------------------------------------------------------------------------------- install
Write-Step 'Installing Skyline Together'
$tmp = Join-Path $env:TEMP ("SkylineTogether-setup-" + [Guid]::NewGuid().ToString('N'))
New-Dir $tmp
# the new copy is staged next to the Mods folder (same drive, outside Mods so the game never loads it) and swapped in
# with two renames; the old copy is kept until the new one is in place, and put back if that fails
$staging = Join-Path $UserData ("$ModName-setup-new-" + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $UserData ("$ModName-setup-old-" + [Guid]::NewGuid().ToString('N'))
try {
    $zipPath = Join-Path $tmp 'mod.zip'
    $self = [IO.File]::ReadAllText($PSCommandPath)
    # the markers are assembled at run time so this code never matches itself
    $open = '<' + '#PAYLOAD'
    $close = 'PAYLOAD#' + '>'
    $i = $self.LastIndexOf($open)
    $j = $self.LastIndexOf($close)
    if ($i -lt 0 -or $j -le $i) { Write-Bad 'The embedded mod is missing from this script. Download the setup again.'; Finish 1 }
    $b64 = $self.Substring($i + $open.Length, $j - $i - $open.Length) -replace '\s', ''
    [IO.File]::WriteAllBytes($zipPath, [Convert]::FromBase64String($b64))
    $actual = Get-Sha256 $zipPath
    if ($actual -ne $PayloadSha256) {
        Write-Bad 'The embedded mod is damaged (checksum mismatch). Download the setup again.'
        Finish 1
    }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, (Join-Path $tmp 'x'))
    $source = Join-Path $tmp "x\$ModName"
    if (-not (Test-Path -LiteralPath (Join-Path $source 'SkylineTogether.dll'))) {
        Write-Bad 'The embedded mod is incomplete. Download the setup again.'
        Finish 1
    }
    New-Dir $ModsDir
    New-Dir $staging
    Get-ChildItem -LiteralPath $source | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $staging $_.Name) -Recurse -Force }
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'SkylineTogether.dll'))) { throw 'copying the mod failed' }
    $hadOld = Test-Path -LiteralPath $ModDir
    if ($hadOld) { [IO.Directory]::Move($ModDir, $previous) }
    try {
        [IO.Directory]::Move($staging, $ModDir)
    }
    catch {
        if ($hadOld -and -not (Test-Path -LiteralPath $ModDir)) { [IO.Directory]::Move($previous, $ModDir) }
        throw
    }
}
catch {
    Write-Bad "Installing failed: $($_.Exception.Message)"
    if (Test-Path -LiteralPath $ModDir) { Write-Host '   The previously installed copy is unchanged.' }
    elseif (Test-Path -LiteralPath $previous) {
        # the old copy could not be put back: it is kept where it is, never deleted
        Write-Warn2 "The previously installed copy could not be put back; it is kept in $previous. Rename that folder to $ModName to use it again."
    }
    Remove-Dir $staging
    Remove-Dir $tmp
    Finish 1
}
finally {
    Remove-Dir $tmp
    Remove-Dir $staging
    # the old copy goes only once a copy is in place (the new one, or the old one put back)
    if (Test-Path -LiteralPath $ModDir) { Remove-Dir $previous }
}
$dll = Join-Path $ModDir 'SkylineTogether.dll'
$installedBuild = (Get-Sha256 $dll).Substring(0, 12)
Write-Ok "Installed to $ModDir"
Write-Ok "Build ID: $installedBuild  (every player must have the same build ID)"

# ------------------------------------------------------------------------------------------- other mods
Write-Step 'Checking your other mods'
$pdx = Join-Path $UserData '.cache\mods\pdx_mods'
$old = @()
if (Test-Path -LiteralPath $pdx) { $old = @(Get-ChildItem -LiteralPath $pdx -Directory -Filter "$($OldMultiplayerModId)_*" -ErrorAction SilentlyContinue) }
if ($old.Count -gt 0) {
    Write-Warn2 'The old "CS2 Multiplayer Mod" is subscribed. Disable it (or unsubscribe) in your playset before playing:'
    Write-Host  '        two multiplayer mods cannot run at the same time.'
} else {
    Write-Ok 'No conflicting multiplayer mod found'
}
# a Paradox Mods subscription of Skyline Together next to this local copy: the game loads only one of them
$subscribed = @()
if (Test-Path -LiteralPath $pdx) {
    $subscribed = @(Get-ChildItem -LiteralPath $pdx -Directory -ErrorAction SilentlyContinue | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'SkylineTogether.dll')
    })
}
if ($subscribed.Count -gt 0) {
    Write-Warn2 'Skyline Together is also subscribed on Paradox Mods. The game loads only one copy (this local one),'
    Write-Host  '        and two different builds can break your sessions. Keep one: unsubscribe on Paradox Mods,'
    Write-Host  '        or uninstall this copy (-Uninstall) and use the subscription. Every player needs the same build.'
}
$libs = @('0Harmony', 'Colossal.Mathematics', 'Markdig', 'Reinforced.Typings', 'Extensions', 'External')
$codeMods = @()
if (Test-Path -LiteralPath $pdx) {
    Get-ChildItem -LiteralPath $pdx -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -like "$($OldMultiplayerModId)_*") { return }
        if (Test-Path -LiteralPath (Join-Path $_.FullName 'SkylineTogether.dll')) { return }
        $names = Get-ChildItem -LiteralPath $_.FullName -Filter '*.dll' -File -Recurse -Depth 1 -ErrorAction SilentlyContinue |
            Where-Object { $_.BaseName -notlike '*_win_x86_64' -and $libs -notcontains $_.BaseName } |
            ForEach-Object { $_.BaseName }
        if ($names) { $codeMods += ($names | Select-Object -First 1) }
    }
}
if ($codeMods.Count -gt 0) {
    Write-Host '   Subscribed code mods (only enabled ones count):'
    $codeMods | Sort-Object -Unique | ForEach-Object { Write-Host "     - $_" }
    Write-Host '   Gameplay mods (e.g. Traffic, Move It, Anarchy) must be enabled on both PCs in the same version.'
    Write-Host '   Visual/UI-only mods (translations, tooltips, icon libraries, Skyve, overlays) may differ.'
    Write-Host '   If they differ, the game tells you exactly what to enable or disable when you join.'
} else {
    Write-Ok 'No other code mods subscribed'
}

# ------------------------------------------------------------------------------------------- next steps
Write-Step 'Ready'
Write-Host '   1. Start Cities: Skylines II.'
Write-Host '   2. On the title screen click "Multiplayer" (below "Load Game").'
Write-Host '   3. Click "Join a friend", enter the host''s Steam ID (or IP:port) and the password if any, click "Join".'
Write-Host '      The host''s city downloads and loads automatically.'
Write-Host '   4. Something wrong? In the Skyline Together panel click "Write diagnostics", run Collect-Logs.cmd'
Write-Host '      and attach the zip from your Desktop to a bug report: https://github.com/ljkx/SkylineTogether/issues'
Finish 0

# ------------------------------------------------------------------------------------------- embedded mod
# (base64 of the release zip; generated by tools/installer/make_installer.py)
<#PAYLOAD
__PAYLOAD__
PAYLOAD#>
