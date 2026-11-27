$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
  throw "Built-in .NET Framework csc.exe not found at $csc"
}

$distDir = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$outExe = Join-Path $distDir 'ChoomSync.exe'
$srcFile = Join-Path $PSScriptRoot 'ChoomSync.cs'
$modDir = Join-Path $repoRoot 'mod\cet\ChoomSync'

& $csc `
  /nologo `
  /target:winexe `
  /optimize+ `
  /platform:anycpu `
  "/out:$outExe" `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /reference:System.Net.Http.dll `
  /reference:System.IO.Compression.dll `
  /reference:System.IO.Compression.FileSystem.dll `
  /reference:System.Web.Extensions.dll `
  "/resource:$(Join-Path $modDir 'init.lua'),ChoomSync.init.lua" `
  "/resource:$(Join-Path $modDir 'nightcoop_sdk.lua'),ChoomSync.nightcoop_sdk.lua" `
  "/resource:$(Join-Path $modDir 'config.json'),ChoomSync.config.json" `
  "$srcFile"

if ($LASTEXITCODE -ne 0) {
  throw "csc.exe build failed with exit code $LASTEXITCODE"
}

$info = Get-Item $outExe
$sha = (Get-FileHash $outExe -Algorithm SHA256).Hash.ToLower()
Write-Host "Built $outExe ($([math]::Round($info.Length / 1KB, 1)) KB)" -ForegroundColor Green
Write-Host "SHA-256: $sha" -ForegroundColor Cyan
