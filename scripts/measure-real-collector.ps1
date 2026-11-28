param(
    [Parameter(Mandatory = $true)][string]$CollectorPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(1, 10)][int]$Rounds = 5,
    [ValidateRange(5, 120)][int]$Frames = 60,
    [switch]$NoTrace
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$live = Join-Path $projectRoot 'scripts\tools\diagnostic-live\bin\Release\net10.0-windows\diagnostic-live.dll'
$reader = Join-Path $projectRoot 'scripts\tools\diagnostic-reader\bin\Release\net10.0\diagnostic-reader.dll'
$CollectorPath = (Resolve-Path -LiteralPath $CollectorPath).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Path]::GetFileName($CollectorPath) -ne 'WinGlance.Collector.exe') { throw 'Specify the frozen WinGlance.Collector.exe.' }
# The harness uses its current security token, launches only its own collector children,
# checks exact parent identity, and never finds/stops an existing user collector.
$captureArguments = @($live, 'collector', '--exe', $CollectorPath, '--output', $OutputDirectory, '--rounds', $Rounds, '--frames', $Frames)
if ($NoTrace) { $captureArguments += '--no-trace' }
& $dotnet @captureArguments
if ($LASTEXITCODE -ne 0) { throw 'The collector capture did not complete; preserve its partial evidence.' }
foreach ($trace in Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.nettrace' -File) {
    & $dotnet $reader $trace.FullName ([IO.Path]::ChangeExtension($trace.FullName, '.diagnostics.json')) --stacks
    if ($LASTEXITCODE -ne 0) { throw "The trace parser reported an error: $($trace.Name)" }
}
