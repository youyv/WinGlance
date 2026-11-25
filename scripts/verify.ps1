param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
Push-Location $projectRoot
try {
    & $dotnetPath run --project tests/WinGlance.Tests -- --windows-smoke
    if ($LASTEXITCODE -ne 0) { throw 'Regression or identity smoke tests failed.' }
    & $dotnetPath run --project tests/WinGlance.Telemetry.Tests
    if ($LASTEXITCODE -ne 0) { throw 'Telemetry lifecycle tests failed.' }
    & $dotnetPath run --project src/WinGlance.Collector -- --samples 3
    if ($LASTEXITCODE -ne 0) { throw 'Collector smoke test failed.' }
} finally { Pop-Location }
