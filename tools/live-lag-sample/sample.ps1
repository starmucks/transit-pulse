# Samples Kafka consumer-group lag and Processor RSS every minute.
param(
    [int]$Minutes = 65,
    [string]$OutFile = (Join-Path $PSScriptRoot "samples.jsonl"),
    [string]$GroupId = "transitpulse-processor",
    [string]$Topic = "positions.raw"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
if (Test-Path $OutFile) { Remove-Item $OutFile }

function Find-ProcessorPid {
    $p = Get-CimInstance Win32_Process -Filter "Name='TransitPulse.Processor.exe'" |
        Sort-Object CreationDate -Descending |
        Select-Object -First 1
    if ($p) { return [int]$p.ProcessId }
    return $null
}

function Sample-KafkaLag {
    $name = docker ps --format '{{.Names}}' | Where-Object { $_ -match 'redpanda' -and $_ -notmatch 'console' } | Select-Object -First 1
    if (-not $name) { return $null }
    $json = docker exec $name rpk group describe $GroupId --format json 2>$null
    if (-not $json) { return $null }
    try {
        $g = $json | ConvertFrom-Json
        $parts = @()
        $total = 0L
        foreach ($p in $g.partitions) {
            if ($p.topic -ne $Topic) { continue }
            $lag = [long]$p.lag
            $total += $lag
            $parts += @{ partition = [int]$p.partition; lag = $lag; current = [long]$p.current_offset; end = [long]$p.end_offset }
        }
        return @{ totalLag = $total; partitions = $parts }
    }
    catch { return $null }
}

$deadline = (Get-Date).AddMinutes($Minutes)
$n = 0
Write-Host "Sampling until $deadline → $OutFile"

while ((Get-Date) -lt $deadline) {
    $n++
    $procId = Find-ProcessorPid
    $row = [ordered]@{
        utc = (Get-Date).ToUniversalTime().ToString('o')
        sample = $n
        processorPid = $procId
    }
    if ($procId) {
        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($p) {
            $row.cpuSec = [math]::Round($p.CPU, 2)
            $row.workingSetMiB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        }
    }
    $k = Sample-KafkaLag
    if ($k) {
        $row.kafkaTotalLag = $k.totalLag
        $row.kafkaPartitions = $k.partitions
    }
    ($row | ConvertTo-Json -Compress -Depth 6) | Add-Content -Path $OutFile -Encoding utf8
    Write-Host ("[{0}] #{1} pid={2} kafkaLag={3} rss={4} MiB" -f $row.utc, $n, $procId, $row.kafkaTotalLag, $row.workingSetMiB)
    Start-Sleep -Seconds 60
}

Write-Host "Done. Samples: $n"
