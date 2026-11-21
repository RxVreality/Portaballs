# Finds SteamVR's OpenXR runtime file (steamxr_win64.json), so the VR launch can use SteamVR whatever the PC's
# default OpenXR runtime is. Dot-source it, then call Get-SteamVrRuntimeJson (returns a path, or $null).
#
# Why: s&box opens VR through OpenXR. If the PC's default OpenXR runtime is another one (Oculus, Virtual Desktop),
# the game talks to that instead of SteamVR, and the tracking helper only reads SteamVR. Setting XR_RUNTIME_JSON
# for the game alone fixes that without changing the PC's default for other games. A Quest through Virtual
# Desktop or Steam Link still works this way, because those also feed SteamVR.
#
# Where it looks, in order: Steam's library folders, then the path file SteamVR keeps in Local AppData.

function Get-SteamVrRuntimeJson {
  $candidates = New-Object System.Collections.Generic.List[string]

  $steam = $null
  foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam', 'HKCU:\SOFTWARE\Valve\Steam') {
    try {
      $item = Get-ItemProperty -Path $key -ErrorAction Stop
      $v = if ($item.InstallPath) { $item.InstallPath } else { $item.SteamPath }
      if ($v) { $steam = $v -replace '/', '\'; break }
    } catch { }
  }
  if (-not $steam) { $steam = "${env:ProgramFiles(x86)}\Steam" }

  $libraries = @($steam)
  $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
  if (Test-Path $vdf) {
    foreach ($m in [regex]::Matches((Get-Content -Raw $vdf), '"path"\s+"([^"]+)"')) {
      $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
    }
  }
  foreach ($lib in $libraries) { $candidates.Add((Join-Path $lib 'steamapps\common\SteamVR\steamxr_win64.json')) }

  $vrpath = Join-Path $env:LOCALAPPDATA 'openvr\openvrpaths.vrpath'
  if (Test-Path $vrpath) {
    try {
      foreach ($r in @((Get-Content -Raw $vrpath | ConvertFrom-Json).runtime)) { $candidates.Add((Join-Path $r 'steamxr_win64.json')) }
    } catch { }
  }

  foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path } }
  return $null
}
