# Starts Portaballs. The installer copies this file to the install folder (C:\Portaballs by default) and
# the desktop icons run it with no window:
#
#   launch.ps1 -mode vr        in the headset, through SteamVR (also starts the tracking helper)
#   launch.ps1 -mode flat      a 1600x900 window on the monitor
#   launch.ps1 -mode editor    the s&box editor with the project open
#
# Switches passed to the engine:
#   -mcp              needed for the engine to open a project file from the command line. It also opens
#                     a local pipe (same Windows user only) that Facepunch's sbox-cli tool can talk to.
#   +game <project>   play this project            -project <project>  open it in the editor
#   -nosteam          do not sign in to Steam
#   -vr / -novr       headset on or off
#
# VR: the game is pointed at SteamVR's OpenXR runtime (XR_RUNTIME_JSON, for this game only), because the
# helper reads SteamVR and some PCs have another runtime as their default (Oculus, Virtual Desktop).
# The helper listens on 127.0.0.1 port 8443 only (s&box lets games connect to this PC on 80, 443, 8080 or
# 8443). If it cannot start, the game still runs and throws from its own hand positions.
# What happened is written to logs\launch_vr.log.
param(
  [ValidateSet('flat', 'vr', 'editor')][string]$mode = 'flat'
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$engine = Join-Path $here 'sbox\game'
$proj = Join-Path $here 'game\portaballs\portaballs.sbproj'
$helper = Join-Path $here 'helper\PortaballsHelper.exe'
$helperPort = 8443
$vrLog = Join-Path $here 'logs\launch_vr.log'

$launches = @{
  flat   = @{ Exe = "$engine\sbox.exe";     Args = "-mcp +game `"$proj`" -nosteam -novr -sw -w 1600 -h 900" }
  vr     = @{ Exe = "$engine\sbox.exe";     Args = "-mcp +game `"$proj`" -nosteam -vr" }
  editor = @{ Exe = "$engine\sbox-dev.exe"; Args = "-project `"$proj`" -nosteam -novr" }
}

$l = $launches[$mode]
if (-not (Test-Path $l.Exe) -or -not (Test-Path $proj)) {
  Add-Type -AssemblyName PresentationFramework
  [void][System.Windows.MessageBox]::Show("Portaballs is not fully installed in $here.`n`nRun 'Install Portaballs.cmd' again.", 'Portaballs')
  exit 1
}

if ($mode -ne 'vr') {
  Start-Process -FilePath $l.Exe -ArgumentList $l.Args -WorkingDirectory $engine
  exit 0
}

function Write-VrLog([string]$text) {
  try {
    New-Item -ItemType Directory -Force (Split-Path -Parent $vrLog) | Out-Null
    Add-Content -Path $vrLog -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $text) -Encoding utf8
  }
  catch { }
}

function Get-Helpers {
  $found = @()
  foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='PortaballsHelper.exe'" -ErrorAction SilentlyContinue)) {
    $cmd = [string]$p.CommandLine
    if ($cmd -match '--(stop|probe|help)\b') { continue }
    $port = 28461
    if ($cmd -match '--port\s+(\d+)') { $port = [int]$Matches[1] }
    $found += [pscustomobject]@{ Id = [int]$p.ProcessId; Port = $port; Fake = ($cmd -match '--fake\b') }
  }
  return $found
}

function Stop-Helper([int]$onlyPid = 0) {
  $targets = { Get-Helpers | Where-Object { $_.Port -eq $helperPort -and ($onlyPid -eq 0 -or $_.Id -eq $onlyPid) } }
  if (-not (& $targets)) { return $true }
  try { Start-Process -FilePath $helper -ArgumentList "--stop --port $helperPort" -WindowStyle Hidden -Wait } catch { }
  for ($i = 0; $i -lt 25; $i++) {
    if (-not (& $targets)) { return $true }
    Start-Sleep -Milliseconds 200
  }
  foreach ($h in (& $targets)) { try { Stop-Process -Id $h.Id -Force -ErrorAction Stop } catch { } }
  Start-Sleep -Milliseconds 300
  return -not (& $targets)
}

Write-VrLog '---- VR launch ----'

$startedHelper = $false
$helperPid = 0
if (-not (Test-Path $helper)) {
  Write-VrLog "Helper not found at $helper. Starting the game without it."
}
else {
  $mine = @(Get-Helpers | Where-Object { $_.Port -eq $helperPort })
  if ($mine | Where-Object { $_.Fake }) {
    Write-VrLog "A test helper (--fake) is on port $helperPort. Stopping it."
    $null = Stop-Helper
    $mine = @(Get-Helpers | Where-Object { $_.Port -eq $helperPort })
  }
  if ($mine.Count -gt 0) {
    Write-VrLog "A helper is already running on port $helperPort (pid $($mine[0].Id)). Using it."
  }
  else {
    try {
      $h = Start-Process -FilePath $helper -ArgumentList "--port $helperPort" -WorkingDirectory (Split-Path -Parent $helper) -WindowStyle Hidden -PassThru
      Start-Sleep -Milliseconds 700
      if ($h.HasExited) {
        Write-VrLog "The helper stopped at once (exit code $($h.ExitCode); 2 = something else is using port $helperPort). Starting the game without it. See helper\helper.log."
      }
      else {
        $startedHelper = $true
        $helperPid = $h.Id
        Write-VrLog "Helper started on port $helperPort (pid $($h.Id))."
      }
    }
    catch { Write-VrLog "The helper could not be started: $($_.Exception.Message)" }
  }
}

try {
  . (Join-Path $here 'steamvr_runtime.ps1')
  $xr = Get-SteamVrRuntimeJson
  if ($xr) { $env:XR_RUNTIME_JSON = $xr; Write-VrLog "VR runtime: SteamVR ($xr)." }
  else { Write-VrLog "SteamVR's OpenXR runtime file was not found. Using the PC's default OpenXR runtime." }
}
catch { Write-VrLog "Could not pick SteamVR's runtime: $($_.Exception.Message)" }

$code = 0
try {
  $game = Start-Process -FilePath $l.Exe -ArgumentList $l.Args -WorkingDirectory $engine -PassThru
  Write-VrLog "Game started (pid $($game.Id))."
  $null = $game.Handle
  $game.WaitForExit()
  $code = $game.ExitCode
  Write-VrLog "Game closed (exit code $code)."
}
catch {
  Write-VrLog "The game could not be started: $($_.Exception.Message)"
  $code = 1
}
finally {
  if ($startedHelper) {
    if (Stop-Helper $helperPid) { Write-VrLog 'Helper stopped.' } else { Write-VrLog "The helper would not stop. Stop it with: helper\PortaballsHelper.exe --stop --port $helperPort" }
  }
}
exit $code
