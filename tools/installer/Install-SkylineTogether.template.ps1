<#
  Skyline Together - player setup (Cities: Skylines II co-op)

  Installs the mod into the game's local Mods folder, checks for common problems, and collects logs for bug reports.
  The mod itself is embedded in this script, so this one file is all you need.

    Install:        double-click Install.cmd
                    (or: powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-SkylineTogether.ps1)
    Collect logs:   double-click Collect-Logs.cmd   (or add -CollectLogs)
    Uninstall:      add -Uninstall
#>
param(
    [switch]$CollectLogs,
    [switch]$Uninstall,
    [switch]$NoPause,
    [string]$OutDir = '',
    [switch]$SkipGameCheck   # testing only
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
function Finish([int]$code) {
    if (-not $NoPause) { Write-Host ''; [void](Read-Host 'Press Enter to close') }
    exit $code
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
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $patterns = @(
        @{ Dir = $LogsDir; Filter = 'SkylineTogether*' },
        @{ Dir = $LogsDir; Filter = 'Modding.log' },
        @{ Dir = $LogsDir; Filter = 'UI.log' },
        @{ Dir = $UserData; Filter = 'Player.log' },
        @{ Dir = $UserData; Filter = 'Player-prev.log' }
    )
    $count = 0
    foreach ($p in $patterns) {
        if (-not (Test-Path $p.Dir)) { continue }
        Get-ChildItem -Path $p.Dir -Filter $p.Filter -File -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName -Destination $stage -Force
            $count++
        }
    }
    $recordings = Join-Path $UserData 'ModsData\SkylineTogether\Recordings'
    if (Test-Path $recordings) {
        Get-ChildItem -Path $recordings -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
            $dst = Join-Path $stage ('recording-' + $_.Name)
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
            Get-ChildItem -Path $_.FullName -Filter 'verdict*' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $dst -Force
        }
    }
    # Unity crash reports (error.log + crash.dmp) of the last two crashes
    $crashRoots = @(
        (Join-Path $env:TEMP 'Colossal Order\Cities Skylines II\Crashes'),
        (Join-Path $env:LOCALAPPDATA 'Temp\Colossal Order\Cities Skylines II\Crashes'),
        (Join-Path $UserData 'Crashes')
    ) | Select-Object -Unique
    foreach ($root in $crashRoots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2 | ForEach-Object {
            $dst = Join-Path $stage ('crash-' + $_.Name)
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
            Get-ChildItem -Path $_.FullName -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -in '.log', '.dmp', '.txt', '.json' -and $_.Length -lt 200MB } |
                ForEach-Object { Copy-Item $_.FullName -Destination $dst -Force; $count++ }
        }
    }
    if ($count -eq 0) {
        Write-Bad "No logs found in $LogsDir. Start the game with the mod once, then try again."
        Finish 1
    }
    $desktop = if ($OutDir) { $OutDir } else { [Environment]::GetFolderPath('Desktop') }
    $zip = Join-Path $desktop "SkylineTogether-logs-$stamp.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "Saved $count log file(s) to:"
    Write-Host "        $zip"
    Write-Host '   Send this zip to the person who made the mod (passwords and chat are not in it).'
    Finish 0
}

# ------------------------------------------------------------------------------------------- checks
Write-Step 'Checking the game'
if (-not (Test-Path $UserData)) {
    Write-Bad "Cities: Skylines II user folder not found ($UserData)."
    Write-Host '   Start the game once (until the main menu), close it, and run this again.'
    Finish 1
}
Write-Ok 'Cities: Skylines II user folder found'

$game = if ($SkipGameCheck) { $null } else { Get-Process -Name 'Cities2' -ErrorAction SilentlyContinue }
while ($game) {
    Write-Warn2 'Cities: Skylines II is running. Close the game completely, then press Enter.'
    [void](Read-Host)
    $game = Get-Process -Name 'Cities2' -ErrorAction SilentlyContinue
}
Write-Ok 'The game is not running'

# ------------------------------------------------------------------------------------------- uninstall
if ($Uninstall) {
    Write-Step 'Removing Skyline Together'
    if (Test-Path $ModDir) { Remove-Item $ModDir -Recurse -Force; Write-Ok "Removed $ModDir" }
    else { Write-Ok 'Skyline Together was not installed' }
    Finish 0
}

# ------------------------------------------------------------------------------------------- install
Write-Step 'Installing Skyline Together'
$tmp = Join-Path $env:TEMP ("SkylineTogether-setup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
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
    $actual = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $PayloadSha256) {
        Write-Bad 'The embedded mod is damaged (checksum mismatch). Download the setup again.'
        Finish 1
    }
    Expand-Archive -Path $zipPath -DestinationPath (Join-Path $tmp 'x') -Force
    $source = Join-Path $tmp "x\$ModName"
    if (-not (Test-Path (Join-Path $source 'SkylineTogether.dll'))) {
        Write-Bad 'The embedded mod is incomplete. Download the setup again.'
        Finish 1
    }
    New-Item -ItemType Directory -Force -Path $ModsDir | Out-Null
    if (Test-Path $ModDir) { Remove-Item $ModDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ModDir | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $ModDir -Recurse -Force
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
$dll = Join-Path $ModDir 'SkylineTogether.dll'
$installedBuild = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
Write-Ok "Installed to $ModDir"
Write-Ok "Build id: $installedBuild  (the host must show the same build id)"

# ------------------------------------------------------------------------------------------- other mods
Write-Step 'Checking your other mods'
$pdx = Join-Path $UserData '.cache\mods\pdx_mods'
$old = @()
if (Test-Path $pdx) { $old = @(Get-ChildItem -Path $pdx -Directory -Filter "$($OldMultiplayerModId)_*" -ErrorAction SilentlyContinue) }
if ($old.Count -gt 0) {
    Write-Warn2 'The old "CS2 Multiplayer Mod" is subscribed. Disable it (or unsubscribe) in Paradox Mods /'
    Write-Host  '        your playset before playing - two multiplayer mods at once conflict.'
} else {
    Write-Ok 'No conflicting multiplayer mod found'
}
# a Paradox Mods subscription of Skyline Together next to this local copy: the game loads only one of them
$subscribed = @()
if (Test-Path $pdx) {
    $subscribed = @(Get-ChildItem -Path $pdx -Directory -ErrorAction SilentlyContinue | Where-Object {
        Test-Path (Join-Path $_.FullName 'SkylineTogether.dll')
    })
}
if ($subscribed.Count -gt 0) {
    Write-Warn2 'Skyline Together is also subscribed on Paradox Mods. Two copies at once can break the game, and'
    Write-Host  '        the game loads only one of them (this local copy). Keep one: unsubscribe on Paradox Mods,'
    Write-Host  '        or uninstall this copy (-Uninstall) and use the subscription. Every player needs the same build.'
}
$libs = @('0Harmony', 'Colossal.Mathematics', 'Markdig', 'Reinforced.Typings', 'Extensions', 'External')
$codeMods = @()
if (Test-Path $pdx) {
    Get-ChildItem -Path $pdx -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -like "$($OldMultiplayerModId)_*") { return }
        if (Test-Path (Join-Path $_.FullName 'SkylineTogether.dll')) { return }
        $names = Get-ChildItem -Path $_.FullName -Filter '*.dll' -File -Recurse -Depth 1 -ErrorAction SilentlyContinue |
            Where-Object { $_.BaseName -notlike '*_win_x86_64' -and $libs -notcontains $_.BaseName } |
            ForEach-Object { $_.BaseName }
        if ($names) { $codeMods += ($names | Select-Object -First 1) }
    }
}
if ($codeMods.Count -gt 0) {
    Write-Host '   Subscribed code mods (only ENABLED ones matter):'
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
Write-Host '   3. Open the "Join" tab, enter the host''s Steam ID (or IP:port) and the password if any, click "Join".'
Write-Host '      The host''s city downloads and loads automatically.'
Write-Host '   4. Something wrong? In the multiplayer panel click "Write diagnostics", then run Collect-Logs.cmd'
Write-Host '      and send the zip from your Desktop.'
Finish 0

# ------------------------------------------------------------------------------------------- embedded mod
# (base64 of the release zip; generated by tools/installer/make_installer.py)
<#PAYLOAD
__PAYLOAD__
PAYLOAD#>
