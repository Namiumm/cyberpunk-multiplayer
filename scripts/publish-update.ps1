<#
.SYNOPSIS
  Rebuilds dist/ChoomSync.exe, updates manifest.json with fresh SHA-256 checksums,
  pushes to GitHub, and publishes/updates the GitHub Release so every ChoomSync.exe
  auto-updates on next launch.
#>
[CmdletBinding()]
param(
  [string]$Version = "0.1.0",
  [switch]$PublishRelease
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
  # 1. Build ChoomSync.exe
  & (Join-Path $repoRoot 'installer\build.ps1')

  $exePath = Join-Path $repoRoot 'dist\ChoomSync.exe'
  $exeSha = (Get-FileHash $exePath -Algorithm SHA256).Hash.ToLower()
  $initSha = (Get-FileHash (Join-Path $repoRoot 'mod\cet\ChoomSync\init.lua') -Algorithm SHA256).Hash.ToLower()
  $sdkSha = (Get-FileHash (Join-Path $repoRoot 'mod\cet\ChoomSync\nightcoop_sdk.lua') -Algorithm SHA256).Hash.ToLower()

  # 2. Update manifest.json preserving existing third-party framework entries
  $manifestPath = Join-Path $repoRoot 'manifest.json'
  $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
  $manifest.version = $Version
  $manifest.launcher.version = $Version
  $manifest.launcher.url = "https://github.com/Namiumm/cyberpunk-multiplayer/releases/download/v$Version/ChoomSync.exe"
  $manifest.launcher.sha256 = $exeSha
  foreach ($mf in $manifest.modFiles) {
    if ($mf.name -eq 'init.lua') { $mf.sha256 = $initSha }
    if ($mf.name -eq 'nightcoop_sdk.lua') { $mf.sha256 = $sdkSha }
  }
  $json = $manifest | ConvertTo-Json -Depth 6
  [System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "Updated manifest.json for v$Version (exe=$exeSha)" -ForegroundColor Green

  if ($PublishRelease) {
    $tag = "v$Version"
    & gh release view $tag 2>$null
    if ($LASTEXITCODE -eq 0) {
      & gh release upload $tag $exePath --clobber
    } else {
      & gh release create $tag $exePath --title "ChoomSync $tag" --notes "ChoomSync $tag for Cyberpunk 2077 (Patch 2.5) + NightCoop 0.2.2-beta."
    }
    Write-Host "Published GitHub release $tag with ChoomSync.exe" -ForegroundColor Cyan
  }
} finally {
  Pop-Location
}
