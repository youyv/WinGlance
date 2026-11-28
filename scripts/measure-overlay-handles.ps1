param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('Acrylic', 'ClickThrough')][string]$Mode = 'Acrylic',
    [ValidateRange(1, 100)][int]$Cycles = 25
)
$requestedCycles = $Cycles
$requestedApp = [IO.Path]::GetFullPath($AppPath)
$requestedOutput = [IO.Path]::GetFullPath($OutputDirectory)
. (Join-Path $PSScriptRoot 'measure-diagnostics.ps1') -AppPath $requestedApp -OutputDirectory $requestedOutput -InitializeOnly
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$handleReader = Join-Path $projectRoot 'scripts\tools\handle-snapshot\bin\Release\net10.0-windows\handle-snapshot.dll'
$instance = $null
$points = [Collections.Generic.List[object]]::new()
function Save-HandlePoint([string]$label, [int]$cycle, [int]$idle) {
    $point = [pscustomobject]@{ Label = $label; Cycle = $cycle; IdleSeconds = $idle; Resources = Get-ProcessSnapshot $instance.App }
    $points.Add($point)
    & $dotnet $handleReader $instance.App.Id $instance.App.StartTicks $instance.App.Path (Join-Path $instance.Directory ($label + '-types.json'))
    if ($LASTEXITCODE -ne 0) { throw 'Handle type snapshot failed.' }
    Write-JsonFile @($points.ToArray()) (Join-Path $instance.Directory 'points.json')
}
try {
    $instance = Start-Diagnostic ('handles-' + $Mode) $Mode $true
    Write-Host ('Isolated PID {0}, warmup 60 seconds.' -f $instance.App.Id)
    Start-Sleep -Seconds 60
    Invoke-Control $instance 'OverlayButton'
    Wait-Overlay $instance $true ($Mode -eq 'ClickThrough')
    Start-Sleep -Milliseconds 500
    Invoke-Control $instance 'OverlayButton'
    Wait-Overlay $instance $false ($Mode -eq 'ClickThrough')
    Start-Sleep -Seconds 2
    Save-HandlePoint 'warm' 0 0
    for ($index = 1; $index -le $requestedCycles; $index++) {
        Invoke-Control $instance 'OverlayButton'
        Wait-Overlay $instance $true ($Mode -eq 'ClickThrough')
        Start-Sleep -Milliseconds 250
        Invoke-Control $instance 'OverlayButton'
        Wait-Overlay $instance $false ($Mode -eq 'ClickThrough')
        Start-Sleep -Milliseconds 250
        if ($index % 25 -eq 0 -or $index -eq $requestedCycles) { Save-HandlePoint ('cycles-' + $index) $index 0 }
    }
    Start-Sleep -Seconds 15
    Save-HandlePoint 'idle-15' $requestedCycles 15
    Start-Sleep -Seconds 45
    Save-HandlePoint 'idle-60' $requestedCycles 60
} finally {
    if ($null -ne $instance) {
        $cleanup = @(Stop-Diagnostic $instance)
        Write-JsonFile $cleanup (Join-Path $instance.Directory 'cleanup.json')
    }
}
