# Shared helpers for the Lab 1 scripts. Dot-source it: . "$PSScriptRoot\common.ps1"

$ErrorActionPreference = 'Stop'
$Lab1Dir    = Split-Path -Parent $PSScriptRoot
$RepoDir    = Split-Path -Parent $Lab1Dir
$ResultsDir = Join-Path $Lab1Dir 'results'
$Exe        = Join-Path $RepoDir 'bin\Release\net9.0\Labs.exe'

function Build-Lab {
    Write-Host 'Building (Release)...'
    dotnet build (Join-Path $RepoDir 'Labs.csproj') -c Release --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    New-Item -ItemType Directory -Force $ResultsDir | Out-Null
}

# Every measurement is a separate process, so the peak memory belongs to that run only.
function Invoke-Lab1([string[]]$Arguments) {
    & $Exe lab1 @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Failed: lab1 $($Arguments -join ' ')" }
}
