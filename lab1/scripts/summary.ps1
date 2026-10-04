<#
.SYNOPSIS
    Reads lab1\results\task*.csv and writes lab1\results\summary.csv: per task, variant and
    thread count the median time, the speed-up against 1 thread and the median peak memory.
    These are the numbers behind the charts of the report.
#>
. "$PSScriptRoot\common.ps1"

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $n = $sorted.Count
    if ($n % 2 -eq 1) { return $sorted[[int][math]::Floor($n / 2)] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

$rows = foreach ($file in Get-ChildItem (Join-Path $ResultsDir 'task*.csv')) { Import-Csv $file.FullName }
if (-not $rows) { throw "No results in $ResultsDir." }

$summary = foreach ($group in ($rows | Group-Object task, variant)) {
    $byThreads = $group.Group | Group-Object { [int]$_.threads } | Sort-Object { [int]$_.Name }
    $baseline = $byThreads | Where-Object { [int]$_.Name -eq 1 } | Select-Object -First 1
    $baseTime = if ($baseline) { Get-Median ($baseline.Group | ForEach-Object { [double]$_.time_ms }) } else { $null }

    foreach ($t in $byThreads) {
        $times = $t.Group | ForEach-Object { [double]$_.time_ms }
        $median = Get-Median $times
        [pscustomobject]@{
            task              = $t.Group[0].task
            variant           = $t.Group[0].variant
            threads           = [int]$t.Name
            runs              = $times.Count
            median_time_ms    = [math]::Round($median, 1)
            min_time_ms       = [math]::Round(($times | Measure-Object -Minimum).Minimum, 1)
            max_time_ms       = [math]::Round(($times | Measure-Object -Maximum).Maximum, 1)
            speedup           = if ($baseTime) { [math]::Round($baseTime / $median, 2) } else { '' }
            median_peak_ws_mb = [math]::Round((Get-Median ($t.Group | ForEach-Object { [double]$_.peak_ws_mb })), 1)
            median_peak_private_mb = [math]::Round((Get-Median ($t.Group | ForEach-Object { [double]$_.peak_private_mb })), 1)
            checks            = (($t.Group.check | Sort-Object -Unique) -join '/')
        }
    }
}

$out = Join-Path $ResultsDir 'summary.csv'
$summary | Sort-Object task, variant, threads | Export-Csv $out -NoTypeInformation
$summary | Sort-Object task, variant, threads | Format-Table -AutoSize | Out-Host
Write-Host "Written to $out"
