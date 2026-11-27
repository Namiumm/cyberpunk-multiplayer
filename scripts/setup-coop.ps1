<#
.SYNOPSIS
  Safe, reproducible Cyberpunk 2077 (Patch 2.5) Co-op setup for NightCoop + CET + redscript + ChoomSync.
  Downloads closed-source/third-party frameworks directly from their official hosts, verifies SHA-256,
  backs up saves first, and installs the local ChoomSync companion mod & launcher.
#>
[CmdletBinding()]
param(
  [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Download-FileSafe([string]$Url, [string]$OutFile) {
  if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    & curl.exe -fsSL --retry 2 -o $OutFile $Url
    if ($LASTEXITCODE -ne 0) { throw "curl.exe failed downloading $Url (exit $LASTEXITCODE)" }
  } else {
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
  }
}

$NIGHTCOOP_URL = "https://nightcoop.net/dl/0.2.2-beta/NightCoop-0.2.2-beta-2.5.zip"
$NIGHTCOOP_SHA256 = "71d846388ed41443d5dadefb776e85ddef3e2a76fd5d6f9ff0868e7dfd615669"
$CET_URL = "https://github.com/maximegmd/CyberEngineTweaks/releases/download/v1.38.1/cet_1_38_1.zip"
$REDSCRIPT_URL = "https://github.com/jac3km4/redscript/releases/download/v1.0.2/redscript-v1.0.2-windows.zip"

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Find-CyberpunkDir {
  if ($GameDir -and (Test-Path (Join-Path $GameDir "bin\x64\Cyberpunk2077.exe"))) { return $GameDir }
  $candidates = @(
    "C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077",
    "C:\Program Files\GOG Galaxy\Games\Cyberpunk 2077",
    "C:\GOG Games\Cyberpunk 2077",
    "C:\Program Files\Epic Games\Cyberpunk2077"
  )
  $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
  if (Test-Path $vdf) {
    Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object {
      $lib = $_.Matches.Groups[1].Value -replace '\\\\', '\'
      $candidates += Join-Path $lib "steamapps\common\Cyberpunk 2077"
    }
  }
  foreach ($c in ($candidates | Select-Object -Unique)) {
    if (Test-Path (Join-Path $c "bin\x64\Cyberpunk2077.exe")) { return $c }
  }
  throw "Could not find Cyberpunk 2077. Re-run with -GameDir 'C:\path\to\Cyberpunk 2077'"
}

$cpDir = Find-CyberpunkDir
$exe = Join-Path $cpDir "bin\x64\Cyberpunk2077.exe"
$ver = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "[ChoomSync] Found Cyberpunk 2077 ($ver) at: $cpDir" -ForegroundColor Cyan
if ($ver -ne "2.5") {
  Write-Warning "Detected ProductVersion=$ver (expected 2.5). Make sure Cyberpunk 2077 is updated to Patch 2.5."
}

# 1. Backup saves before touching anything
$savesSrc = Join-Path $env:USERPROFILE "Saved Games\CD Projekt Red\Cyberpunk 2077"
$backupRoot = Join-Path $env:USERPROFILE "Saved Games\CD Projekt Red\Cyberpunk 2077_Backups"
if (Test-Path $savesSrc) {
  $existing = Get-ChildItem $backupRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "pre-coop_*" } | Select-Object -First 1
  if (-not $existing) {
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $stamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $dest = Join-Path $backupRoot "pre-coop_$stamp"
    Write-Host "[ChoomSync] Backing up saves to $dest ..." -ForegroundColor Yellow
    Copy-Item -Path $savesSrc -Destination $dest -Recurse -Force
  } else {
    Write-Host "[ChoomSync] Initial save backup already exists: $($existing.FullName)" -ForegroundColor Green
  }
}

# 2. Download official archives
$dlDir = Join-Path $env:USERPROFILE "NightCoopSetup\downloads"
New-Item -ItemType Directory -Force -Path $dlDir | Out-Null

$ncZip = Join-Path $dlDir "NightCoop-0.2.2-beta-2.5.zip"
if (-not (Test-Path $ncZip)) {
  Write-Host "[ChoomSync] Downloading NightCoop 0.2.2-beta (official nightcoop.net)..."
  Download-FileSafe $NIGHTCOOP_URL $ncZip
}
$ncHash = (Get-FileHash $ncZip -Algorithm SHA256).Hash.ToLower()
if ($ncHash -ne $NIGHTCOOP_SHA256) {
  Remove-Item $ncZip -Force
  throw "NightCoop SHA-256 mismatch! Expected $NIGHTCOOP_SHA256, got $ncHash"
}
Write-Host "[ChoomSync] Verified NightCoop SHA-256 OK." -ForegroundColor Green

$cetZip = Join-Path $dlDir "cet_1_38_1.zip"
if (-not (Test-Path $cetZip)) {
  Write-Host "[ChoomSync] Downloading Cyber Engine Tweaks v1.38.1 (official GitHub)..."
  Download-FileSafe $CET_URL $cetZip
}

$rsZip = Join-Path $dlDir "redscript-v1.0.2-windows.zip"
if (-not (Test-Path $rsZip)) {
  Write-Host "[ChoomSync] Downloading redscript v1.0.2 (official GitHub)..."
  Download-FileSafe $REDSCRIPT_URL $rsZip
}

# 3. Extract in safe order (redscript -> CET -> NightCoop with bundled RED4ext 1.31.0)
Write-Host "[ChoomSync] Extracting redscript v1.0.2..."
Expand-Archive -Path $rsZip -DestinationPath $cpDir -Force

Write-Host "[ChoomSync] Extracting Cyber Engine Tweaks v1.38.1..."
Expand-Archive -Path $cetZip -DestinationPath $cpDir -Force

Write-Host "[ChoomSync] Extracting NightCoop 0.2.2-beta (includes RED4ext 1.31.0)..."
Expand-Archive -Path $ncZip -DestinationPath $cpDir -Force

# 4. Install ChoomSync CET Companion Mod
$modSrc = Join-Path $RepoRoot "mod\cet\ChoomSync"
$modDst = Join-Path $cpDir "bin\x64\plugins\cyber_engine_tweaks\mods\ChoomSync"
New-Item -ItemType Directory -Force -Path $modDst | Out-Null
Copy-Item -Path (Join-Path $modSrc "init.lua") -Destination $modDst -Force
Copy-Item -Path (Join-Path $modSrc "nightcoop_sdk.lua") -Destination $modDst -Force
if (-not (Test-Path (Join-Path $modDst "config.json"))) {
  Copy-Item -Path (Join-Path $modSrc "config.json") -Destination $modDst -Force
}

# 5. Clear stale modded script cache so redscript compiles cleanly on Patch 2.5
foreach ($cachePath in @(
  (Join-Path $cpDir "r6\cache\modded"),
  (Join-Path $cpDir "r6\cache\final.redscripts.modded")
)) {
  if (Test-Path $cachePath) { Remove-Item $cachePath -Recurse -Force }
}

Write-Host "`n[ChoomSync] All done! Launch via launcher\launch.bat" -ForegroundColor Green
