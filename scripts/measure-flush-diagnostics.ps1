param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(1, 10)][int]$FlushRounds = 5,
    [ValidateRange(90, 180)][int]$MinimumSeconds = 90,
    [ValidateRange(90, 180)][int]$MaximumSeconds = 180
)

# Reuses only identity-checked diagnostic launch/control helpers. The user instance and
# its settings/database are excluded. Measure separately from foreground CPU experiments.
$requestedRounds = $FlushRounds
$requestedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$requestedApp = [IO.Path]::GetFullPath($AppPath)
. (Join-Path $PSScriptRoot 'measure-diagnostics.ps1') -AppPath $requestedApp -OutputDirectory $requestedOutput -InitializeOnly
$live = Join-Path $projectRoot 'scripts\tools\diagnostic-live\bin\Release\net10.0-windows\diagnostic-live.dll'
$reader = Join-Path $projectRoot 'scripts\tools\diagnostic-reader\bin\Release\net10.0\diagnostic-reader.dll'
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $live) -or -not (Test-Path -LiteralPath $reader)) {
    throw 'Build scripts/tools/diagnostic-live and scripts/tools/diagnostic-reader first.'
}
$records = [Collections.Generic.List[object]]::new()
$instance = $null
try {
    for ($round = 1; $round -le $requestedRounds; $round++) {
        $name = 'flush-{0:00}' -f $round
        Write-Host "Starting $name; >= $MinimumSeconds seconds and >=3 actual periodic completions, cap $MaximumSeconds seconds"
        $instance = Start-Diagnostic $name 'Baseline'
        $trace = Join-Path $instance.Directory 'flush.nettrace'
        $before = Get-ProcessSnapshot $instance.App
        Invoke-Control $instance 'StatisticsButton'
        Wait-Until { $null -ne (Find-Control $instance 'SaveStatus') } 'visible statistics with periodic query'
        & $dotnet $live attach --pid $instance.App.Id --trace $trace --min-seconds $MinimumSeconds --max-seconds $MaximumSeconds --flushes 3
        $captureExit = $LASTEXITCODE
        $after = Get-ProcessSnapshot $instance.App
        & $dotnet $reader $trace (Join-Path $instance.Directory 'flush.diagnostics.json') --stacks
        $readerExit = $LASTEXITCODE
        $capture = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($trace, '.capture.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
        $record = [pscustomobject]@{ Round = $round; Instance = $instance; Before = $before; After = $after;
            Cpu = Get-CpuResult $before $after; Capture = $capture; CaptureExit = $captureExit; ReaderExit = $readerExit }
        $records.Add($record)
        $instance.CleanupErrors = @(Stop-Diagnostic $instance)
        $instance = $null
        Write-JsonFile $records.ToArray() (Join-Path $OutputDirectory 'flush-rounds.json')
        if ($captureExit -ne 0 -or $readerExit -ne 0) { throw "Flush round $round is incomplete; keep its trace and capture status." }
    }
} finally {
    if ($null -ne $instance) { @(Stop-Diagnostic $instance) | Out-Null }
    Write-JsonFile $records.ToArray() (Join-Path $OutputDirectory 'flush-rounds.json')
}
