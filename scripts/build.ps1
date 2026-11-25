param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Run scripts/setup.ps1 first.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$outputPath = Join-Path $projectRoot 'artifacts\app'
Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        & $dotnetPath run --project 'tests/WinGlance.Tests' -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & $dotnetPath run --project 'tests/WinGlance.Telemetry.Tests' -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Telemetry tests failed.' }
        & $dotnetPath run --project 'tests/WinGlance.Statistics.Tests' -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Statistics tests failed.' }
        & $dotnetPath run --project 'tests/WinGlance.App.Tests' -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'App service tests failed.' }
    }
    & $dotnetPath publish 'src/WinGlance.Collector/WinGlance.Collector.csproj' -c $Configuration -o $outputPath --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Collector build failed.' }
    & $dotnetPath publish 'src/WinGlance.App/WinGlance.App.csproj' -c $Configuration -o $outputPath --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
    Write-Host "Ready: $outputPath\WinGlance.App.exe"
} finally { Pop-Location }
