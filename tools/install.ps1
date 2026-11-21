# Portaballs installer. Start it by double-clicking "Install Portaballs.cmd" in the folder above this one.
#
# What it does, in order:
#   1. checks for Git and the .NET 10 SDK, and offers to get them if they are missing
#   2. asks you to agree to Facepunch's terms (the s&box engine's native files are theirs)
#   3. downloads s&box's public source into <InstallDir>\sbox at the commit Portaballs was made with,
#      and runs Facepunch's own Setup.bat, which downloads the engine files (about 10 GB) and builds it
#   4. puts in Portaballs' small engine changes (engine-patches\) and rebuilds the engine's C# part
#   5. copies the game into <InstallDir>\game and builds the tracking helper into <InstallDir>\helper
#   6. makes "Portaballs VR" and "Portaballs flat screen" icons on your desktop
#
# Running it again is safe: it picks up where it stopped and only redoes what is needed.
# Everything goes in <InstallDir> (default C:\Portaballs). To remove Portaballs, delete that folder and the icons.
#
# Options (for running it from PowerShell yourself):
#   -InstallDir D:\Somewhere     put it somewhere else (keep the path short, without spaces)
#   -GameOnly                    only refresh the game and the helper (after you pull a new version)
#   -NoShortcuts                 do not make icons
#   -ShortcutDir <folder>        make the icons in this folder instead of the desktop and Start menu
#   -AcceptFacepunchTerms        you have read Facepunch's terms and agree (skips that question)
#   -Yes                         answer yes when asked to install Git or the .NET SDK
param(
  [string]$InstallDir = 'C:\Portaballs',
  [switch]$GameOnly,
  [switch]$NoShortcuts,
  [string]$ShortcutDir = '',
  [switch]$AcceptFacepunchTerms,
  [switch]$Yes,
  [switch]$LocalDotnet   # always use a private copy of the .NET SDK inside InstallDir (used for testing)
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$SboxRepo   = 'https://github.com/Facepunch/sbox-public.git'
$SboxCommit = 'a8a63b3ad2ad5dcc0712eac863810b70d94a26a0'   # the engine version Portaballs was made and tested with
$TermsUrl   = 'https://store.steampowered.com/eula/590830_eula_0'

$repo     = Split-Path -Parent $PSScriptRoot
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$sbox     = Join-Path $InstallDir 'sbox'
$gameDir  = Join-Path $InstallDir 'game\portaballs'
$helperDir = Join-Path $InstallDir 'helper'
$logDir   = Join-Path $InstallDir 'logs'
$stamp    = Get-Date -Format 'yyyyMMdd_HHmmss'
$log      = Join-Path $logDir "install_$stamp.log"

New-Item -ItemType Directory -Force $logDir | Out-Null

function Write-Log([string]$text) {
  Add-Content -Path $log -Value ((Get-Date -Format 'HH:mm:ss') + '  ' + $text) -Encoding utf8
}
function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color; Write-Log $text }
function Step([string]$text) { Write-Host ''; Say "== $text" 'Cyan' }
function Fail([string]$text) {
  Write-Host ''
  Say "STOPPED: $text" 'Red'
  Say "The full log is $log" 'Red'
  exit 1
}
function Ask([string]$question) {
  if ($Yes) { Say "$question  (yes, from -Yes)"; return $true }
  $a = Read-Host "$question [y/n]"
  Write-Log "$question -> $a"
  return ($a -match '^\s*y')
}

# Runs a program, shows its output and copies it to the log. Its stderr goes through cmd so that
# Windows PowerShell 5.1 does not turn each line into an error. Returns the exit code.
function Invoke-Logged([string]$what, [string]$commandLine, [string]$cwd) {
  Say "   $what"
  Write-Log "   > $commandLine   (in $cwd)"
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  Push-Location $cwd
  try {
    # cd inside cmd too: Push-Location moves PowerShell, not the folder a started program begins in.
    & cmd.exe /d /c "(cd /d `"$cwd`" && $commandLine) 2>&1" | ForEach-Object {
      Write-Host "     $_" -ForegroundColor DarkGray
      Add-Content -Path $log -Value "     $_" -Encoding utf8
    }
    $code = $LASTEXITCODE
  }
  finally {
    Pop-Location
    $ErrorActionPreference = $old
  }
  Write-Log "   exit code $code"
  return $code
}

Say "Portaballs installer, $(Get-Date -Format 'yyyy-MM-dd HH:mm')" 'White'
Say "From:  $repo"
Say "Into:  $InstallDir"
Write-Log "PowerShell $($PSVersionTable.PSVersion), Windows $([Environment]::OSVersion.Version)"

if (-not (Test-Path (Join-Path $repo 'game\portaballs\portaballs.sbproj'))) {
  Fail "This script has to stay in the Portaballs folder (tools\install.ps1). game\portaballs is missing next to it."
}
if (-not [Environment]::Is64BitOperatingSystem) { Fail 's&box needs 64-bit Windows.' }
if ($InstallDir -match ' ') { Fail "Pick a folder without spaces in its name (asked for: $InstallDir)." }
if ($InstallDir.Length -gt 60) { Fail "Pick a shorter folder (asked for: $InstallDir). Long paths break the engine build." }

$running = @(Get-Process -Name 'sbox', 'sbox-dev' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($sbox, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) { Fail 'Portaballs (or the s&box editor) is running from this folder. Close it, then run the installer again.' }

# ------------------------------------------------------------------------------------------------
Step 'Checking for Git and the .NET 10 SDK'

$git = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $git) {
  foreach ($c in "$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe") {
    if (Test-Path $c) { $env:PATH = (Split-Path -Parent $c) + ';' + $env:PATH; $git = Get-Command git.exe; break }
  }
}
if (-not $git) {
  Say 'Git is not installed. It is needed to download the s&box source.' 'Yellow'
  $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
  if ($winget -and (Ask 'Install Git for Windows now with winget (Microsoft''s app installer)?')) {
    $code = Invoke-Logged 'Installing Git' 'winget install --id Git.Git -e --source winget' $env:TEMP
    foreach ($c in "$env:ProgramFiles\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe") {
      if (Test-Path $c) { $env:PATH = (Split-Path -Parent $c) + ';' + $env:PATH; break }
    }
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
  }
  if (-not $git) { Fail 'Install Git from https://git-scm.com/download/win , then run this installer again.' }
}
Say "   Git: $($git.Source)"

function Test-Sdk10([string]$dotnetExe) {
  if (-not (Test-Path $dotnetExe)) { return $false }
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $sdks = & $dotnetExe --list-sdks 2>$null } finally { $ErrorActionPreference = $old }
  return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}

$privateDotnet = Join-Path $InstallDir 'dotnet'
$candidates = @()
if (-not $LocalDotnet) {
  $onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
  if ($onPath) { $candidates += $onPath.Source }
  $candidates += "$env:ProgramFiles\dotnet\dotnet.exe", "$env:USERPROFILE\.dotnet\dotnet.exe"
}
$candidates += (Join-Path $privateDotnet 'dotnet.exe')
$dotnet = $null
foreach ($c in $candidates) { if (Test-Sdk10 $c) { $dotnet = $c; break } }

if (-not $dotnet) {
  Say 'The .NET 10 SDK was not found. The engine and the helper are built with it.' 'Yellow'
  if (-not (Ask "Download Microsoft's .NET 10 SDK into $privateDotnet (about 250 MB, only Portaballs uses this copy)?")) {
    Fail 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download , then run this installer again.'
  }
  New-Item -ItemType Directory -Force $privateDotnet | Out-Null
  $installer = Join-Path $privateDotnet 'dotnet-install.ps1'
  Say '   Downloading dotnet-install.ps1 from https://dot.net/v1/dotnet-install.ps1'
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
  $code = Invoke-Logged 'Installing the .NET 10 SDK' "powershell -NoProfile -ExecutionPolicy Bypass -File `"$installer`" -Channel 10.0 -InstallDir `"$privateDotnet`" -NoPath" $privateDotnet
  $dotnet = Join-Path $privateDotnet 'dotnet.exe'
  if (-not (Test-Sdk10 $dotnet)) { Fail 'The .NET 10 SDK did not install. See the log.' }
}
$sdkDir = Split-Path -Parent $dotnet
Say "   .NET SDK: $dotnet"
$env:PATH = "$sdkDir;$env:PATH"
$env:DOTNET_ROOT = $sdkDir
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

# ------------------------------------------------------------------------------------------------
if (-not $GameOnly) {
  Step "Facepunch's terms"
  Say 'Portaballs runs on s&box, Facepunch''s engine built on Valve''s Source 2.'
  Say 'Its C# source is MIT licensed, but the native engine files that Setup downloads are Facepunch''s'
  Say 'and come under their terms of service. Portaballs does not include or hand out those files:'
  Say 'they come straight from Facepunch (artifacts.sbox.game) to you.'
  Say "Read the terms here:  $TermsUrl"
  if ($AcceptFacepunchTerms) {
    Say '   Agreed (from -AcceptFacepunchTerms).'
  }
  else {
    $a = Read-Host 'Type YES if you have read and agree to Facepunch''s terms'
    Write-Log "terms answer: $a"
    if ($a.Trim() -ne 'YES' -and $a.Trim() -ne 'yes') { Fail 'You did not agree to Facepunch''s terms, so nothing was downloaded.' }
  }

  # ----------------------------------------------------------------------------------------------
  Step "Getting the s&box source (commit $($SboxCommit.Substring(0, 8)))"
  if (Test-Path (Join-Path $sbox '.git')) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $origin = & git -C $sbox remote get-url origin 2>$null
    $ErrorActionPreference = $old
    if ($origin -notmatch 'Facepunch/sbox-public') { Fail "$sbox already holds something else (its origin is '$origin'). Pick another -InstallDir." }
    Say "   Already downloaded: $sbox"
  }
  elseif (Test-Path $sbox) {
    if (@(Get-ChildItem -LiteralPath $sbox -Force).Count -gt 0) { Fail "$sbox exists and is not an s&box download. Move it away or pick another -InstallDir." }
  }
  if (-not (Test-Path (Join-Path $sbox '.git'))) {
    $code = Invoke-Logged 'Cloning github.com/Facepunch/sbox-public (about 150 MB)' "git clone --no-checkout `"$SboxRepo`" `"$sbox`"" $InstallDir
    if ($code -ne 0) { Fail 'Downloading the s&box source failed. Check your internet connection and run the installer again.' }
  }

  # Put the pinned commit in place. --force also undoes Portaballs' engine changes from an earlier run;
  # PortalViews.cs is the one new file they add, so it goes too. Nothing else is touched (the
  # downloaded engine files are not part of git and stay).
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  $null = & git -C $sbox cat-file -e "$SboxCommit^{commit}" 2>$null
  $have = ($LASTEXITCODE -eq 0)
  $ErrorActionPreference = $old
  if (-not $have) {
    $code = Invoke-Logged 'Fetching the commit' "git -C `"$sbox`" fetch origin $SboxCommit" $InstallDir
    if ($code -ne 0) { Fail "Commit $SboxCommit could not be fetched from Facepunch's repository." }
  }
  $newFile = Join-Path $sbox 'engine\Sandbox.Engine\Systems\Render\PortalViews.cs'
  if (Test-Path $newFile) { Remove-Item -LiteralPath $newFile -Force }
  $code = Invoke-Logged 'Checking out that commit' "git -C `"$sbox`" -c advice.detachedHead=false checkout --force $SboxCommit" $InstallDir
  if ($code -ne 0) { Fail 'Checking out the s&box source failed.' }

  # ----------------------------------------------------------------------------------------------
  Step 'Running Facepunch''s Setup.bat: downloads the engine files (about 10 GB) and builds the engine'
  Say '   This is the long part: from 15 minutes to an hour, depending on your connection.'
  $code = Invoke-Logged 'Setup.bat' '.\Setup.bat' $sbox
  if ($code -ne 0) { Fail "Facepunch's Setup.bat stopped with exit code $code. Run the installer again to retry; it carries on where it stopped." }
  if (-not (Test-Path (Join-Path $sbox 'game\sbox.exe'))) { Fail 'Setup.bat finished but game\sbox.exe is missing.' }

  # ----------------------------------------------------------------------------------------------
  Step 'Adding Portaballs'' engine changes (see engine-patches\README.md)'
  $patchFiles = Join-Path $repo 'engine-patches\files'
  $changed = @(Get-ChildItem -LiteralPath $patchFiles -Recurse -File)
  if ($changed.Count -eq 0) { Fail 'engine-patches\files is empty.' }
  foreach ($f in $changed) {
    $rel = $f.FullName.Substring($patchFiles.Length + 1)
    $dest = Join-Path $sbox $rel
    if (-not (Test-Path (Split-Path -Parent $dest))) { Fail "The engine has no folder for $rel. Is this the right s&box commit?" }
    Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
    Say "   $rel"
  }

  Step 'Rebuilding the engine''s C# with those changes (a few minutes)'
  $code = Invoke-Logged 'SboxBuild build --skip-native' 'dotnet run --verbosity quiet --project engine\Tools\SboxBuild\SboxBuild.csproj -- build --skip-native' $sbox
  if ($code -ne 0) { Fail "The engine rebuild stopped with exit code $code." }
  if (-not (Test-Path (Join-Path $sbox 'game\bin\managed\Sandbox.Engine.dll'))) { Fail 'The engine rebuild finished but Sandbox.Engine.dll is missing.' }
}
elseif (-not (Test-Path (Join-Path $sbox 'game\sbox.exe'))) {
  Fail "-GameOnly needs an engine that is already installed, and $sbox\game\sbox.exe is missing. Run the installer without -GameOnly."
}

# ------------------------------------------------------------------------------------------------
Step 'Copying the game'
# A plain copy: the game's maths (core\src) goes inside the project as ordinary files.
$gameNew = "$gameDir.new"
if (Test-Path $gameNew) { Remove-Item -LiteralPath $gameNew -Recurse -Force }
& robocopy (Join-Path $repo 'game\portaballs') $gameNew /E /XJ /XD .sbox .vscode obj bin /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "Copying the game failed (robocopy code $LASTEXITCODE)." }
& robocopy (Join-Path $repo 'core\src') (Join-Path $gameNew 'Code\Core') /E /XJ /XD obj bin /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "Copying the game's maths failed (robocopy code $LASTEXITCODE)." }
# Keep the engine's caches from an earlier install (they make the first start faster).
if (Test-Path (Join-Path $gameDir '.sbox')) {
  & robocopy (Join-Path $gameDir '.sbox') (Join-Path $gameNew '.sbox') /E /XJ /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
}
if (Test-Path $gameDir) { Remove-Item -LiteralPath $gameDir -Recurse -Force }
Rename-Item -LiteralPath $gameNew -NewName (Split-Path -Leaf $gameDir)
$count = @(Get-ChildItem -LiteralPath (Join-Path $gameDir 'Code') -Recurse -File -Filter *.cs).Count
Say "   $gameDir ($count code files)"

# ------------------------------------------------------------------------------------------------
Step 'Building the tracking helper (reads your hands and eyes from SteamVR, 500 times a second)'
$helperRunning = @(Get-Process -Name PortaballsHelper -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($helperDir, [StringComparison]::OrdinalIgnoreCase) })
foreach ($h in $helperRunning) { try { Stop-Process -Id $h.Id -Force } catch { } }
$proj = Join-Path $repo 'helper\app\PortaballsHelper.csproj'
$code = Invoke-Logged 'dotnet publish' "dotnet publish `"$proj`" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o `"$helperDir`" --nologo -v q" $repo
if ($code -ne 0 -or -not (Test-Path (Join-Path $helperDir 'PortaballsHelper.exe'))) { Fail 'Building the helper failed. See the log.' }
Say "   $helperDir\PortaballsHelper.exe"

# ------------------------------------------------------------------------------------------------
Step 'Putting the launcher in place'
foreach ($f in 'launch.ps1', 'steamvr_runtime.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination (Join-Path $InstallDir $f) -Force }
$launcher = Join-Path $InstallDir 'launch.ps1'

if (-not $NoShortcuts) {
  $targets = @()
  if ($ShortcutDir) { $targets += $ShortcutDir }
  else {
    $targets += [Environment]::GetFolderPath('Desktop')
    $targets += Join-Path ([Environment]::GetFolderPath('Programs')) 'Portaballs'
  }
  $shell = New-Object -ComObject WScript.Shell
  $sboxExe = Join-Path $sbox 'game\sbox.exe'
  $engineDir = Join-Path $sbox 'game'
  $proj = Join-Path $gameDir 'portaballs.sbproj'
  # The flat and editor icons start the engine directly (the same switches as launch.ps1). Only the VR icon
  # needs the script, because it also starts and stops the tracking helper.
  $icons = @(
    @{ Name = 'Portaballs VR';          Mode = 'vr';     Tip = 'Play Portaballs in your headset (SteamVR)' },
    @{ Name = 'Portaballs flat screen'; Mode = 'flat';   Tip = 'Play Portaballs in a window, with mouse and keyboard'
       Exe = $sboxExe; Args = "-mcp +game `"$proj`" -nosteam -novr -sw -w 1600 -h 900" },
    @{ Name = 'Portaballs editor';      Mode = 'editor'; Tip = 'Open Portaballs in the s&box editor'
       Exe = (Join-Path $engineDir 'sbox-dev.exe'); Args = "-project `"$proj`" -nosteam -novr" }
  )
  foreach ($t in $targets) {
    New-Item -ItemType Directory -Force $t | Out-Null
    foreach ($i in $icons) {
      # The editor icon only goes in the Start menu (or the -ShortcutDir folder), not on the desktop.
      if ($i.Mode -eq 'editor' -and -not $ShortcutDir -and $t -eq [Environment]::GetFolderPath('Desktop')) { continue }
      $s = $shell.CreateShortcut((Join-Path $t ($i.Name + '.lnk')))
      if ($i.Exe) {
        $s.TargetPath = $i.Exe
        $s.Arguments = $i.Args
        $s.WorkingDirectory = $engineDir
      }
      else {
        # conhost --headless: PowerShell runs with no window at all, so nothing flashes up.
        $s.TargetPath = Join-Path $env:SystemRoot 'System32\conhost.exe'
        $s.Arguments = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launcher`" -mode $($i.Mode)"
        $s.WorkingDirectory = $InstallDir
        $s.WindowStyle = 7
      }
      $s.IconLocation = "$sboxExe,0"
      $s.Description = $i.Tip
      $s.Save()
    }
    Say "   Icons in $t"
  }
}

Write-Host ''
Say 'Done.' 'Green'
Say 'Put your headset on with SteamVR running, then double-click "Portaballs VR" on your desktop.' 'Green'
Say '"Portaballs flat screen" plays it in a window without a headset.' 'Green'
Say "Log: $log"
exit 0
