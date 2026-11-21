param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [int]$Minutes = 65
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$outCsv = Join-Path $root "counters"
$duration = "00:{0:D2}:00:00" -f ([math]::Ceiling($Minutes / 60.0))
if ($Minutes -lt 60) { $duration = "00:00:{0:D2}:00" -f $Minutes }

Write-Host "Collecting counters from PID $ProcessId for $Minutes min → $outCsv*.csv"
& dotnet-counters collect `
    -p $ProcessId `
    --counters TransitPulse.Processor `
    --format csv `
    -o $outCsv `
    --duration $duration `
    --refresh-interval 5 `
    --maxHistograms 50
Write-Host "counters exit=$LASTEXITCODE"
