param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $projectRoot 'artifacts\app\WinGlance.App.exe'
if (-not (Test-Path -LiteralPath $applicationPath)) { throw 'Run scripts/build.ps1 first.' }
Start-Process -FilePath $applicationPath -WorkingDirectory (Split-Path -Parent $applicationPath)
