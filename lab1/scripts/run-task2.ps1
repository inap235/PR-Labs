<#
.SYNOPSIS
    Runs Task 2 on the USB drive with 1, 2 and 4 threads. Before every run it waits for you to
    eject, unplug and replug the drive, so no data comes from the file cache.
.EXAMPLE
    .\run-task2.ps1 -UsbDir E:\lab1 -CopyFirst     # copy part_*.txt to the drive, then measure
    .\run-task2.ps1 -UsbDir E:\lab1
    .\run-task2.ps1 -UsbDir ..\data\parts -Label ssd -NoReplug   # optional: same task on the SSD
#>
param(
    [Parameter(Mandatory)][string]$UsbDir,
    [int[]]$ThreadCounts = @(1, 2, 4),
    [int]$Repetitions = 1,
    [string]$Label = 'usb',
    [switch]$CopyFirst,
    [switch]$NoReplug
)

. "$PSScriptRoot\common.ps1"
Build-Lab

if ($CopyFirst) {
    $parts = Join-Path $Lab1Dir 'data\parts'
    if (-not (Test-Path (Join-Path $parts 'part_1.txt'))) { Invoke-Lab1 @('split') }
    New-Item -ItemType Directory -Force $UsbDir | Out-Null
    Write-Host "Copying $parts\part_*.txt to $UsbDir ..."
    Copy-Item (Join-Path $parts 'part_*.txt') $UsbDir -Force
}

for ($rep = 1; $rep -le $Repetitions; $rep++) {
    foreach ($n in $ThreadCounts) {
        if (-not $NoReplug) {
            Write-Host "`nNext: $n thread(s). Eject the drive (Safely Remove), unplug it, plug it back in."
            Read-Host 'Press Enter when the drive is back'
            while (-not (Test-Path $UsbDir)) {
                Write-Host "Waiting for $UsbDir ..."
                Start-Sleep -Seconds 1
            }
        }
        Invoke-Lab1 @('2', '--dir', $UsbDir, '--threads', "$n", '--label', $Label,
                      '--csv', (Join-Path $ResultsDir 'task2.csv'))
    }
}

Write-Host "`nDone. Results are in $ResultsDir\task2.csv."
