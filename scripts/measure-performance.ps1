param(
    [ValidateRange(5, 120)][int]$Seconds = 15,
    [string]$OutputName = 'performance-current.json'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName UIAutomationClient
if (-not ('PerfWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PerfWindow {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint processId);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr h,int n);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong32(IntPtr h,int n);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
    public static bool IsTopmost(IntPtr h) => ((IntPtr.Size == 8 ? GetWindowLongPtr64(h,-20).ToInt64() : GetWindowLong32(h,-20)) & 8) != 0;
}
'@
}
function Set-Topmost([bool]$value) {
    $insertAfter = if ($value) { [IntPtr](-1) } else { [IntPtr](-2) }
    # NOMOVE | NOSIZE | NOACTIVATE; raising the window does not steal keyboard input.
    if (-not [PerfWindow]::SetWindowPos($hwnd,$insertAfter,0,0,0,0,0x13)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
}
function Get-Paused {
    switch ($pause.Current.Name) {
        '恢复显示' { return $true }
        '暂停显示' { return $false }
        default { throw 'Cannot determine the current pause state.' }
    }
}
function Set-Paused([bool]$value) {
    if ((Get-Paused) -eq $value) { return }
    $pause.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 50
        if ((Get-Paused) -eq $value) { return }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The pause state did not change within three seconds.'
}
function Get-WindowState([bool]$includePause = $false) {
    $foreground = [PerfWindow]::GetForegroundWindow()
    $state = [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Visible = [PerfWindow]::IsWindowVisible($hwnd)
        Minimized = [PerfWindow]::IsIconic($hwnd)
        Topmost = [PerfWindow]::IsTopmost($hwnd)
        Foreground = $foreground -eq $hwnd
        ForegroundWindowHandle = $foreground.ToInt64()
    }
    if ($includePause) { $state.Paused = Get-Paused }
    return [pscustomobject]$state
}
function Get-ProcessSnapshot([int]$processId, [bool]$includeThreads = $false) {
    $errors = [Collections.Generic.List[string]]::new()
    $snapshot = [ordered]@{
        ProcessId=$processId; Process=''; StartTimeUtcTicks=$null; CPUSeconds=$null
        WorkingSetMiB=$null; PrivateMiB=$null; Threads=@{}; Errors=$errors
    }
    try {
        $measuredProcess = Get-Process -Id $processId
        $snapshot.Process = $measuredProcess.ProcessName
        try { $snapshot.StartTimeUtcTicks = $measuredProcess.StartTime.ToUniversalTime().Ticks } catch { $errors.Add("Process identity: $($_.Exception.Message)") }
        try { $snapshot.CPUSeconds = $measuredProcess.TotalProcessorTime.TotalSeconds } catch { $errors.Add("Process CPU: $($_.Exception.Message)") }
        try {
            $snapshot.WorkingSetMiB = [Math]::Round($measuredProcess.WorkingSet64/1MB,1)
            $snapshot.PrivateMiB = [Math]::Round($measuredProcess.PrivateMemorySize64/1MB,1)
        } catch { $errors.Add("Process memory: $($_.Exception.Message)") }
        if ($includeThreads) {
            try {
                foreach ($thread in $measuredProcess.Threads) {
                    try {
                        $snapshot.Threads[$thread.Id] = [pscustomobject]@{
                            ThreadId=$thread.Id; StartTimeUtcTicks=$thread.StartTime.ToUniversalTime().Ticks
                            CPUSeconds=$thread.TotalProcessorTime.TotalSeconds
                        }
                    } catch { $errors.Add("Thread $($thread.Id) unavailable: $($_.Exception.Message)") }
                    finally { $thread.Dispose() }
                }
            } catch { $errors.Add("Thread enumeration: $($_.Exception.Message)") }
        }
        $measuredProcess.Dispose()
    } catch { $errors.Add("Process unavailable: $($_.Exception.Message)") }
    return [pscustomobject]$snapshot
}
function Get-ThreadDeltas($before, $after, [double]$duration, [long]$windowStartTicks) {
    foreach ($threadId in @(@($before.Threads.Keys) + @($after.Threads.Keys) | Sort-Object -Unique)) {
        $first = $before.Threads[$threadId]
        $last = $after.Threads[$threadId]
        $delta = $null
        $status = 'ExitedOrUnreadable'
        if ($null -ne $first -and $null -ne $last) {
            if ($first.StartTimeUtcTicks -eq $last.StartTimeUtcTicks) {
                $delta = $last.CPUSeconds-$first.CPUSeconds
                $status = 'ObservedAtBothEndpoints'
            } else { $status = 'ThreadIdReused' }
        } elseif ($null -ne $last) {
            if ($last.StartTimeUtcTicks -ge $windowStartTicks) {
                $delta = $last.CPUSeconds
                $status = 'StartedDuringMeasurement'
            } else { $status = 'MissingBaseline' }
        }
        if ($null -ne $delta -and $delta -lt 0) { $delta=$null; $status='InvalidCounter' }
        [pscustomobject]@{
            ThreadId=$threadId; IsMainUIThread=$threadId -eq $mainThreadId
            CPUSecondsDelta=$delta
            CPUPercent=if ($null -ne $delta) { [Math]::Round(100*$delta/$duration/[Environment]::ProcessorCount,3) } else { $null }
            Status=$status
        }
    }
}

$appPath = Join-Path $projectRoot 'artifacts\app\WinGlance.App.exe'
$applications = @(Get-Process WinGlance.App -ErrorAction SilentlyContinue | Where-Object {
    $candidatePath = $null
    try { $candidatePath = $_.Path } catch { }
    # An inaccessible executable path is not evidence that the process is unrelated.
    (-not $candidatePath -or $candidatePath -eq $appPath) -and $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId
})
if ($applications.Count -ne 1) { throw 'Start exactly one published WinGlance application in this session before measuring.' }
$appProcess = $applications[0]
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'WinGlance')))
if ($null -eq $root) { throw 'Restore the main WinGlance window before measuring.' }
$hwnd = [IntPtr]$root.Current.NativeWindowHandle
if ($hwnd -eq 0 -or -not [PerfWindow]::IsWindowVisible($hwnd) -or [PerfWindow]::IsIconic($hwnd)) {
    throw 'Restore the main WinGlance window before measuring.'
}
[uint32]$windowProcessId = 0
$mainThreadId = [PerfWindow]::GetWindowThreadProcessId($hwnd,[ref]$windowProcessId)
if ($windowProcessId -ne $appProcess.Id) { throw 'The main window no longer belongs to the selected process.' }
$pause = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'PauseButton'))
if ($null -eq $pause) { throw 'The selected window is not the WinGlance main window.' }
$settingsPath = Join-Path $env:LOCALAPPDATA 'WinGlance\settings.json'
if ((Test-Path -LiteralPath $settingsPath) -and (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).CloseToTray -eq $false) {
    throw 'Enable close-to-tray before measuring all three modes.'
}
$initialState = Get-WindowState $true
$initialForeground = [PerfWindow]::GetForegroundWindow()
$collectors = @(Get-Process WinGlance.Collector -ErrorAction SilentlyContinue | Where-Object SessionId -eq $appProcess.SessionId)
if ($collectors.Count -gt 1) { throw 'Multiple collectors are running in this session; cannot identify the one to measure.' }
$processIds = @($appProcess.Id) + @($collectors | ForEach-Object Id)
$samples = [Collections.Generic.List[object]]::new()
$modeStates = [Collections.Generic.List[object]]::new()
$restorationErrors = [Collections.Generic.List[string]]::new()
$failure = $null
try {
    foreach ($mode in @('Visible','Paused','Tray')) {
        if ($mode -eq 'Tray') {
            Set-Paused $false
            [PerfWindow]::PostMessage($hwnd,0x10,0,0) | Out-Null
        } else {
            Set-Topmost $true
            [PerfWindow]::SetForegroundWindow($hwnd) | Out-Null
            Set-Paused ($mode -eq 'Paused')
        }
        Start-Sleep -Seconds 2
        $windowBefore = Get-WindowState $true
        if ($mode -eq 'Tray' -and $windowBefore.Visible) { throw 'The application did not enter tray mode.' }
        if ($mode -ne 'Tray' -and (-not $windowBefore.Visible -or $windowBefore.Minimized -or -not $windowBefore.Topmost)) {
            throw 'The window must remain visible and topmost for Visible/Paused measurements.'
        }
        $baseline = @{}
        $endpoint = @{}
        $observation = [Collections.Generic.List[object]]::new()
        $startedUtc = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        foreach ($processId in $processIds) { $baseline[$processId] = Get-ProcessSnapshot $processId ($processId -eq $appProcess.Id) }
        $baselineDuration = $timer.Elapsed.TotalSeconds
        while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
            $observation.Add((Get-WindowState))
            $remainingMs = [Math]::Ceiling(1000*($Seconds-$timer.Elapsed.TotalSeconds))
            if ($remainingMs -gt 0) { Start-Sleep -Milliseconds ([Math]::Min(1000,$remainingMs)) }
        }
        foreach ($processId in $processIds) { $endpoint[$processId] = Get-ProcessSnapshot $processId ($processId -eq $appProcess.Id) }
        $timer.Stop()
        # Freeze one denominator before formatting/output so every process/thread uses the same window.
        $duration = $timer.Elapsed.TotalSeconds
        $windowAfter = Get-WindowState $true
        $observation.Add($windowAfter)
        $modeStates.Add([pscustomobject]@{
            Mode=$mode; StartedUtc=$startedUtc.ToString('o'); DurationSeconds=$duration
            BaselineSnapshotSeconds=$baselineDuration; Before=$windowBefore; After=$windowAfter; During=@($observation.ToArray())
        })
        $expectedPaused = $mode -eq 'Paused'
        if ($windowAfter.Paused -ne $expectedPaused) { throw "Pause state changed during $mode measurement." }
        if ($mode -eq 'Tray' -and @($observation | Where-Object Visible).Count -gt 0) {
            throw 'The window became visible during Tray measurement.'
        }
        if ($mode -ne 'Tray' -and @($observation | Where-Object { -not $_.Visible -or $_.Minimized -or -not $_.Topmost }).Count -gt 0) {
            throw "Window visibility/topmost state changed during $mode measurement."
        }
        foreach ($processId in $processIds) {
            $first=$baseline[$processId]; $last=$endpoint[$processId]
            $cpuDelta=$null
            if ($null -ne $first.CPUSeconds -and $null -ne $last.CPUSeconds -and
                $null -ne $first.StartTimeUtcTicks -and $first.StartTimeUtcTicks -eq $last.StartTimeUtcTicks) {
                $cpuDelta=$last.CPUSeconds-$first.CPUSeconds
                if ($cpuDelta -lt 0) { $cpuDelta=$null }
            }
            $sample = [pscustomobject]@{
                Mode=$mode; Process=$first.Process; ProcessId=$processId
                CPUSecondsDelta=$cpuDelta
                CPUPercent=if ($null -ne $cpuDelta) { [Math]::Round(100*$cpuDelta/$duration/[Environment]::ProcessorCount,3) } else { $null }
                WorkingSetMiB=$last.WorkingSetMiB; PrivateMiB=$last.PrivateMiB
                DurationSeconds=$duration
                MainUIThreadId=if ($processId -eq $appProcess.Id) { $mainThreadId } else { $null }
                Threads=if ($processId -eq $appProcess.Id) { @(Get-ThreadDeltas $first $last $duration $startedUtc.UtcTicks | Sort-Object CPUSecondsDelta -Descending) } else { @() }
                Errors=@($first.Errors.ToArray()) + @($last.Errors.ToArray())
            }
            $samples.Add($sample)
            Write-Host "$mode / $($sample.Process): CPU $($sample.CPUPercent)%, WS $($sample.WorkingSetMiB) MiB"
        }
    }
} catch { $failure=$_ }
finally {
    try {
        if (-not [PerfWindow]::IsWindowVisible($hwnd)) {
            [PerfWindow]::PostMessage($hwnd,0x8001,1,0x202) | Out-Null
            $restoreDeadline=[DateTime]::UtcNow.AddSeconds(3)
            while (-not [PerfWindow]::IsWindowVisible($hwnd) -and [DateTime]::UtcNow -lt $restoreDeadline) { Start-Sleep -Milliseconds 50 }
        }
        Set-Paused $initialState.Paused
    } catch { $restorationErrors.Add("Pause/visibility: $($_.Exception.Message)") }
    try { Set-Topmost $initialState.Topmost } catch { $restorationErrors.Add("Topmost: $($_.Exception.Message)") }
    if ($initialForeground -ne [IntPtr]::Zero) { [PerfWindow]::SetForegroundWindow($initialForeground) | Out-Null }
}
$report = [pscustomobject]@{
    Timestamp=[DateTimeOffset]::Now.ToString('o'); LogicalProcessors=[Environment]::ProcessorCount
    RequestedDurationSeconds=$Seconds; Completed=$null -eq $failure; MainUIThreadId=$mainThreadId
    InitialWindowState=$initialState; ModeStates=@($modeStates.ToArray()); Samples=@($samples.ToArray())
    RestorationErrors=@($restorationErrors.ToArray())
    Failure=if ($failure) { $failure.Exception.Message } else { $null }
    Notes='CPU is normalized across logical processors. Endpoint snapshots are sequential and their bounded overhead is included in the common duration. Threads that exit between snapshots cannot contribute a final CPU counter; null means unavailable. Topmost prevents ordinary window occlusion; other topmost windows and user intervention can still affect the run.'
}
$outputPath = Join-Path $projectRoot (Join-Path 'artifacts' $OutputName)
$report | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $outputPath -Encoding utf8
Write-Host "Saved: $outputPath"
foreach ($restoreError in $restorationErrors) { Write-Warning $restoreError }
if ($failure) { throw $failure }
if ($restorationErrors.Count -gt 0) { throw 'Measurement finished, but restoring the initial window state failed. See RestorationErrors.' }
