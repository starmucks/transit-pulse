# Packages the committed hsl-tram-9-hour fixture into deploy/demo/data for compose mounts.
# Run from anywhere; paths are resolved relative to this script.
$ErrorActionPreference = "Stop"

$demoRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $demoRoot "..\..")).Path
$fixture = Join-Path $repoRoot "tests\TransitPulse.Replay.Tests\Fixtures\hsl-tram-9-hour"
$data = Join-Path $demoRoot "data"
$archiveDest = Join-Path $data "archive"
$gtfsDest = Join-Path $data "gtfs\hsl"
$stamp = "2026-11-03T1433Z"
$zipPath = Join-Path $gtfsDest "$stamp.zip"
$shaPath = "$zipPath.sha256"

if (-not (Test-Path $fixture)) {
    throw "Missing fixture at $fixture"
}

New-Item -ItemType Directory -Force -Path $archiveDest | Out-Null
New-Item -ItemType Directory -Force -Path $gtfsDest | Out-Null

Write-Host "Copying Parquet archive..."
Copy-Item -Path (Join-Path $fixture "archive\*") -Destination $archiveDest -Recurse -Force

Write-Host "Zipping GTFS slice..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $fixture "gtfs\*") -DestinationPath $zipPath -Force

$hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
Set-Content -Path $shaPath -Value $hash -NoNewline

Write-Host "Golden ready under $data"
Write-Host "  archive -> $archiveDest"
Write-Host "  gtfs    -> $zipPath"
Write-Host "  sha256  -> $hash"
