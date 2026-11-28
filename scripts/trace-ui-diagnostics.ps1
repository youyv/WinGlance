param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(20, 180)][int]$TraceSeconds = 60
)
$requestedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$requestedApp = [IO.Path]::GetFullPath($AppPath)
. (Join-Path $PSScriptRoot 'measure-diagnostics.ps1') -AppPath $requestedApp -OutputDirectory $requestedOutput -InitializeOnly
$live = Join-Path $projectRoot 'scripts\tools\diagnostic-live\bin\Release\net10.0-windows\diagnostic-live.dll'
$reader = Join-Path $projectRoot 'scripts\tools\diagnostic-reader\bin\Release\net10.0\diagnostic-reader.dll'
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$records = [Collections.Generic.List[object]]::new()
$instance = $null
try {
    foreach ($scenario in @('Baseline', 'NoCharts', 'NoMotion', 'Details')) {
        $instance = Start-Diagnostic ('trace-' + $scenario) $scenario
        Start-Sleep -Seconds 10
        $trace = Join-Path $instance.Directory 'ui.nettrace'
        $before = Get-ProcessSnapshot $instance.App
        $window = Get-WindowState $instance
        $duration = $(if ($scenario -eq 'Details') { [Math]::Max(65, $TraceSeconds) } else { $TraceSeconds })
        & $dotnet $live attach --pid $instance.App.Id --trace $trace --min-seconds $duration --max-seconds $duration --flushes 0
        $captureExit = $LASTEXITCODE
        $after = Get-ProcessSnapshot $instance.App
        & $dotnet $reader $trace (Join-Path $instance.Directory 'ui.diagnostics.json') --stacks
        $readerExit = $LASTEXITCODE
        if ($scenario -eq 'Details') {
            $analysis = Get-Content -LiteralPath (Join-Path $instance.Directory 'ui.diagnostics.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $endpointStage = @($analysis.StageSummary | Where-Object Name -eq 'app.endpointQuery')
            if ($endpointStage.Count -ne 1 -or $endpointStage[0].Samples -lt 20) {
                throw 'Details trace has fewer than 20 actual endpoint query stages; do not report it as complete.'
            }
        }
        $records.Add([pscustomobject]@{ Scenario = $scenario; Instance = $instance; Window = $window;
            Before = $before; After = $after; Cpu = Get-CpuResult $before $after; CaptureExit = $captureExit; ReaderExit = $readerExit })
        $instance.CleanupErrors = @(Stop-Diagnostic $instance)
        $instance = $null
        Write-JsonFile $records.ToArray() (Join-Path $OutputDirectory 'trace-rounds.json')
        if ($captureExit -ne 0 -or $readerExit -ne 0) { throw "Trace $scenario is incomplete." }
    }
} finally {
    if ($null -ne $instance) { @(Stop-Diagnostic $instance) | Out-Null }
    Write-JsonFile $records.ToArray() (Join-Path $OutputDirectory 'trace-rounds.json')
}
