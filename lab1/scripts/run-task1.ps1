<#
.SYNOPSIS
    Runs Task 1a, 1b and 1c in every configuration and appends the results to lab1\results\task1*.csv.
.EXAMPLE
    .\run-task1.ps1                      # everything, 3 repetitions
    .\run-task1.ps1 -Tasks 1b            # only Task 1b
    .\run-task1.ps1 -Repetitions 5
#>
param(
    [string[]]$Tasks = @('1a', '1b', '1c'),
    [int]$Repetitions = 3,
    [int[]]$ThreadCounts = @(1, 2, 4, 8, 16, 32, 64),
    [switch]$IncludeInterlocked = $true
)

. "$PSScriptRoot\common.ps1"
Build-Lab

if (-not (Test-Path (Join-Path $Lab1Dir 'data\numbers.txt'))) {
    Invoke-Lab1 @('gen')
}

# Warm-up (not recorded): loads numbers.txt into the OS file cache, so every measured run of
# Task 1 reads from RAM and measures the CPU work, not the first read from the SSD.
Write-Host "`n=== Warm-up (not recorded) ==="
Invoke-Lab1 @('1a', '--threads', '16')

# Repetitions are the outer loop, so a slow moment (thermal throttling, a background task)
# hits one repetition of every configuration instead of all repetitions of one.
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    Write-Host "`n=== Repetition $rep of $Repetitions ==="

    if ($Tasks -contains '1a') {
        foreach ($n in $ThreadCounts) {
            Invoke-Lab1 @('1a', '--threads', "$n", '--csv', (Join-Path $ResultsDir 'task1a.csv'))
        }
    }

    if ($Tasks -contains '1b') {
        $modes = @('lock', 'local')
        if ($IncludeInterlocked) { $modes += 'interlocked' }
        foreach ($mode in $modes) {
            Invoke-Lab1 @('1b', '--mode', $mode, '--csv', (Join-Path $ResultsDir 'task1b.csv'))
        }
    }

    if ($Tasks -contains '1c') {
        foreach ($n in $ThreadCounts) {
            Invoke-Lab1 @('1c', '--workers', "$n", '--csv', (Join-Path $ResultsDir 'task1c.csv'))
        }
    }
}

# The lab asks for the unsynchronised version to be run exactly three times.
if ($Tasks -contains '1b') {
    Write-Host "`n=== Task 1b, unsynchronised, 3 runs ==="
    for ($i = 1; $i -le 3; $i++) {
        Invoke-Lab1 @('1b', '--mode', 'unsync', '--csv', (Join-Path $ResultsDir 'task1b.csv'))
    }
}

Write-Host "`nDone. Results are in $ResultsDir. Run .\summary.ps1 for medians and speed-ups."
