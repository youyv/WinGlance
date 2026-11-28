param(
 [Parameter(Mandatory=$true)][string]$AppPath,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [ValidateRange(1,5)][int]$ComparisonRounds=3,
 [ValidateRange(20,120)][int]$SampleSeconds=30,
 [ValidateRange(5,30)][int]$WarmSeconds=10
)
$selectedApp=$AppPath; $selectedOutput=$OutputDirectory
. (Join-Path $PSScriptRoot 'measure-diagnostics.ps1') -AppPath $selectedApp -OutputDirectory $selectedOutput -InitializeOnly
$environment | Add-Member -NotePropertyName ComparedPresets -NotePropertyValue @(0,3)
$environment | Add-Member -NotePropertyName ComparedModes -NotePropertyValue @('Acrylic','ClickThrough')
$environment.OverlayPreset=$null
Write-JsonFile $environment (Join-Path $OutputDirectory 'environment.json')
$samples=[Collections.Generic.List[object]]::new()
$problems=[Collections.Generic.List[string]]::new()
$instance=$null; $failure=$null
function Check-OverlayState($target,[string]$mode,[int]$preset) {
 $state=Read-Backdrop $target
 if($null -eq $state -or $state.ProcessId -ne $target.App.Id -or $state.Status -in @('Hidden','Closed') -or
    $state.ClickThrough -ne ($mode -eq 'ClickThrough')){throw 'Overlay mode or visibility changed.'}
 if($mode -eq 'Acrylic' -and ($state.Status -ne 'Attached' -or -not $state.AdvancedEffectsEnabled -or $state.HighContrast)){throw 'Acrylic is unavailable.'}
 $title=if($mode -eq 'ClickThrough'){'WinGlance 悬浮条（鼠标穿透）'}else{'WinGlance 悬浮条'}
 $windows=@([WinGlanceDiagnosticNative]::Windows($target.App.Id,$title) | Where-Object {[WinGlanceDiagnosticNative]::IsWindowVisible($_)})
 if($windows.Count -ne 1){throw 'Expected one visible owned overlay.'}
 Assert-Window $target $windows[0]
 if($preset -eq 3 -and $state.DisplayText -notmatch '内存' -or $preset -eq 0 -and $state.DisplayText -match '内存'){throw 'Preset did not apply.'}
 return [pscustomobject]@{State=$state;Bounds=[WinGlanceDiagnosticNative]::Bounds($windows[0]);Dpi=[WinGlanceDiagnosticNative]::GetDpiForWindow($windows[0])}
}
try {
 for($round=1;$round -le $ComparisonRounds;$round++) {
  $presets=if($round % 2 -eq 1){@(0,3)}else{@(3,0)}
  $modes=if($round % 2 -eq 1){@('Acrylic','ClickThrough')}else{@('ClickThrough','Acrylic')}
  foreach($mode in $modes) {
   foreach($preset in $presets) {
    $OverlayPreset=$preset
    $name='round-{0:00}-{1}-preset-{2}' -f $round,$mode,$preset
    Write-Host "Starting $name"
    $instance=Start-Diagnostic $name $mode
    Start-Sleep -Seconds $WarmSeconds
    $overlayBefore=Check-OverlayState $instance $mode $preset
    $before=Get-ProcessSnapshot $instance.App
    $windowBefore=Get-WindowState $instance
    $observations=[Collections.Generic.List[object]]::new()
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while($clock.Elapsed.TotalSeconds -lt $SampleSeconds) {
     Start-Sleep -Milliseconds 1000
     $observations.Add([pscustomobject]@{Window=Get-WindowState $instance;App=Get-ProcessSnapshot $instance.App;Overlay=Check-OverlayState $instance $mode $preset})
    }
    $after=Get-ProcessSnapshot $instance.App
    $overlayAfter=Check-OverlayState $instance $mode $preset
    $state=$overlayAfter.State
    $live=Find-Control $instance 'LiveLabel'
    if($null -eq $live -or $live.Current.Name -notmatch '1 秒'){throw 'Replay was not fresh.'}
    foreach($point in $observations) {
     if(-not $point.Window.Visible -or $point.Window.Minimized -or -not $point.Window.Topmost -or
        $point.Window.Dpi -ne $windowBefore.Dpi -or ($point.Window.Bounds -join ',') -ne ($windowBefore.Bounds -join ',')) {throw 'Window state changed.'}
     if($point.Overlay.Dpi -ne $overlayBefore.Dpi -or ($point.Overlay.Bounds -join ',') -ne ($overlayBefore.Bounds -join ',')){throw 'Overlay bounds changed.'}
    }
    $sample=[pscustomobject]@{Round=$round;Mode=$mode;Preset=$preset;AppIdentity=$instance.App;Cpu=Get-CpuResult $before $after;Before=$before;After=$after;Backdrop=$state;Observations=$observations.ToArray()}
    $samples.Add($sample)
    Write-JsonFile $sample (Join-Path $instance.Directory 'measurement.json')
    Write-JsonFile $samples.ToArray() (Join-Path $OutputDirectory 'rounds.json')
    Write-Host ('Completed {0}: {1:N3}% CPU, {2:N1} MiB private' -f $name,$sample.Cpu.CpuPercent,($after.PrivateBytes/1MB))
    foreach($problem in @(Stop-Diagnostic $instance)){$problems.Add($problem)}
    $instance=$null
   }
  }
 }
} catch {$failure=$_}
finally {if($null -ne $instance){foreach($problem in @(Stop-Diagnostic $instance)){$problems.Add($problem)}}}
$unchanged=($packageBefore | ConvertTo-Json -Compress) -eq (@(Get-PackageManifest) | ConvertTo-Json -Compress)
$summary=@($samples | Group-Object Mode,Preset | ForEach-Object {
 $values=@($_.Group | ForEach-Object {$_.Cpu.CpuPercent})
 [pscustomobject]@{Mode=$_.Group[0].Mode;Preset=$_.Group[0].Preset;N=$_.Count;CpuMedian=Get-Quantile $values .5;CpuIqr=(Get-Quantile $values .75)-(Get-Quantile $values .25);PrivateMiBMedian=(Get-Quantile @($_.Group | ForEach-Object {$_.After.PrivateBytes}) .5)/1MB}
})
Write-JsonFile ([pscustomobject]@{Completed=$null -eq $failure -and $problems.Count -eq 0 -and $unchanged;Rounds=$ComparisonRounds;WarmupSeconds=$WarmSeconds;Seconds=$SampleSeconds;PackageUnchanged=$unchanged;CleanupErrors=$problems.ToArray();Summary=$summary;Failure=$(if($failure){$failure.Exception.Message}else{$null});Note='Short incremental preset comparison, not the formal C baseline. Fixed replay and fresh writable database per process. CPU normalized to logical processors; user background load remains uncontrolled. No trace, no forced GC.'}) (Join-Path $OutputDirectory 'preset-report.json')
if($failure){throw $failure}
if(-not $unchanged -or $problems.Count -gt 0){throw 'Package or cleanup validation failed.'}
