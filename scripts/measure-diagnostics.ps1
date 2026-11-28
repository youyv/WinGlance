param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$OutputDirectory,
    [ValidateRange(1, 20)][int]$Rounds = 5,
    [ValidateRange(20, 600)][int]$Seconds = 60,
    [ValidateRange(0, 60)][int]$WarmupSeconds = 10,
    [ValidateSet('Baseline', 'Acrylic', 'ClickThrough', 'Details', 'TrayClickThrough', 'NoCharts', 'NoMotion')]
    [string[]]$Scenarios = @('Baseline', 'Acrylic', 'ClickThrough', 'Details', 'TrayClickThrough', 'NoCharts', 'NoMotion'),
    [ValidateRange(1, 500)][int]$Cycles = 100,
    [switch]$SkipCycles,
    [switch]$CyclesOnly,
    [switch]$InitializeOnly,
    [int[]]$ProtectedProcessIds = @(18984, 17224)
)

# Use only a frozen diagnostic-capable publish directory. Every process launched here gets
# independent settings, SQLite database and single-instance ownership. No user app is selected.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($CyclesOnly -and $SkipCycles) { throw 'CyclesOnly and SkipCycles cannot be combined.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$AppPath = (Resolve-Path -LiteralPath $AppPath).Path
if ([IO.Path]::GetFileName($AppPath) -ne 'WinGlance.App.exe') { throw 'AppPath must identify WinGlance.App.exe.' }
$packageDirectory = Split-Path -Parent $AppPath
$collectorPath = Join-Path $packageDirectory 'WinGlance.Collector.exe'
if (-not (Test-Path -LiteralPath $collectorPath)) { throw 'The complete published app and collector are required.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts\diagnostics-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($OutputDirectory.Equals($packageDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($packageDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep diagnostic output outside the frozen publish directory.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; existing runs are never overwritten.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$protected = [Collections.Generic.HashSet[int]]::new()
foreach ($processId in $ProtectedProcessIds) { [void]$protected.Add($processId) }
foreach ($existing in @(Get-Process -Name WinGlance.App, WinGlance.Collector -ErrorAction SilentlyContinue)) {
    [void]$protected.Add($existing.Id)
    $existing.Dispose()
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('WinGlanceDiagnosticNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinGlanceDiagnosticNative {
    private delegate bool EnumProc(IntPtr h, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder text, int capacity);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong32(IntPtr h, int index);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    public static long ExStyle(IntPtr h) => IntPtr.Size == 8 ? GetWindowLongPtr64(h, -20).ToInt64() : GetWindowLong32(h, -20);
    public static string ImagePath(IntPtr process) {
        var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }
    public static int[] Bounds(IntPtr h) { Rect r; return GetWindowRect(h, out r) ? new[]{r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top} : null; }
    public static IntPtr[] Windows(int processId, string exactTitle) {
        var result = new List<IntPtr>();
        EnumWindows((h, _) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == processId) {
                var title = new StringBuilder(256); GetWindowText(h, title, title.Capacity);
                if (String.IsNullOrEmpty(exactTitle) || title.ToString() == exactTitle) result.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@
}

function Write-JsonFile($value, [string]$path) {
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 15), $utf8)
}
function Get-PackageManifest {
    @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($packageDirectory.Length + 1); Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Assert-Owned($identity) {
    if ($protected.Contains($identity.Id)) { throw "Refusing to operate protected PID $($identity.Id)." }
    $candidate = Get-Process -Id $identity.Id -ErrorAction Stop
    try {
        $observedTicks = $candidate.StartTime.ToUniversalTime().Ticks
        # Query the kernel image path once through the opened process handle. Process.Path
        # enumerates modules and can transiently return null while WinUI loads/unloads DLLs.
        $observedPath = [WinGlanceDiagnosticNative]::ImagePath($candidate.Handle)
        if ($observedTicks -ne $identity.StartTicks -or $observedPath -ne $identity.Path) {
            throw "Process identity changed for PID $($identity.Id): expected ticks $($identity.StartTicks), path '$($identity.Path)'; observed ticks $observedTicks, path '$observedPath'."
        }
    } finally { $candidate.Dispose() }
}
function Assert-Window($instance, [IntPtr]$handle) {
    Assert-Owned $instance.App
    [uint32]$owner = 0
    [void][WinGlanceDiagnosticNative]::GetWindowThreadProcessId($handle, [ref]$owner)
    if ($handle -eq [IntPtr]::Zero -or $owner -ne $instance.App.Id) { throw 'The window does not belong to this diagnostic instance.' }
}
function Find-Control($instance, [string]$id) {
    Assert-Window $instance $instance.Window
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($instance.Window)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Invoke-Control($instance, [string]$id) {
    $control = Find-Control $instance $id
    if ($null -eq $control) {
        # Concurrent diagnostic windows may temporarily occlude WinUI's automation tree.
        # Reorder only this verified visible window, then allow its tree to become available.
        Assert-Window $instance $instance.Window
        if ([WinGlanceDiagnosticNative]::IsWindowVisible($instance.Window)) {
            [void][WinGlanceDiagnosticNative]::SetWindowPos($instance.Window, [IntPtr](-1), 0, 0, 0, 0, 0x13)
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            while ($null -eq $control -and $deadline.Elapsed.TotalSeconds -lt 3) {
                Start-Sleep -Milliseconds 100
                $control = Find-Control $instance $id
            }
        }
    }
    if ($null -eq $control) { throw "Diagnostic control $id is unavailable." }
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-Until([scriptblock]$predicate, [string]$description, [int]$timeoutSeconds = 15) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $predicate) { return }
        Start-Sleep -Milliseconds 100
    } while ($deadline.Elapsed.TotalSeconds -lt $timeoutSeconds)
    throw "Timed out: $description"
}
function Read-Backdrop($instance) {
    $path = Join-Path $instance.Directory 'overlay-backdrop.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return [IO.File]::ReadAllText($path) | ConvertFrom-Json } catch { return $null }
}
function Wait-Overlay($instance, [bool]$open, [bool]$clickThrough) {
    Wait-Until {
        $state = Read-Backdrop $instance
        if ($null -eq $state -or $state.ProcessId -ne $instance.App.Id) { return $false }
        if (-not $open) { return $state.Status -in @('Closed', 'Hidden') }
        return $state.Status -notin @('Closed', 'Hidden') -and $state.ClickThrough -eq $clickThrough -and
            @([WinGlanceDiagnosticNative]::Windows($instance.App.Id, $null) | Where-Object {
                $_ -ne $instance.Window -and [WinGlanceDiagnosticNative]::IsWindowVisible($_)
            }).Count -gt 0
    } 'overlay state change' 10
}
function Get-WindowState($instance) {
    Assert-Window $instance $instance.Window
    $handle = $instance.Window
    [pscustomobject]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Visible = [WinGlanceDiagnosticNative]::IsWindowVisible($handle)
        Minimized = [WinGlanceDiagnosticNative]::IsIconic($handle)
        Topmost = ([WinGlanceDiagnosticNative]::ExStyle($handle) -band 8) -ne 0
        Foreground = [WinGlanceDiagnosticNative]::GetForegroundWindow() -eq $handle
        Dpi = [WinGlanceDiagnosticNative]::GetDpiForWindow($handle)
        Bounds = [WinGlanceDiagnosticNative]::Bounds($handle)
    }
}
function Get-ProcessSnapshot($identity) {
    Assert-Owned $identity
    $process = Get-Process -Id $identity.Id
    try {
        [pscustomobject]@{
            TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
            StopwatchTicks = [Diagnostics.Stopwatch]::GetTimestamp()
            ProcessId = $identity.Id; StartTicks = $identity.StartTicks
            CpuSeconds = $process.TotalProcessorTime.TotalSeconds
            WorkingSetBytes = $process.WorkingSet64; PrivateBytes = $process.PrivateMemorySize64
            KernelHandles = $process.HandleCount
            GdiHandles = [WinGlanceDiagnosticNative]::GetGuiResources($process.Handle, 0)
            UserHandles = [WinGlanceDiagnosticNative]::GetGuiResources($process.Handle, 1)
            ThreadCount = $process.Threads.Count
        }
    } finally { $process.Dispose() }
}
function Get-Identity($process, [string]$path) {
    [pscustomobject]@{ Id = $process.Id; StartTicks = $process.StartTime.ToUniversalTime().Ticks; Path = $path }
}
function Stop-Diagnostic($instance) {
    if ($null -eq $instance) { return }
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    if ($null -ne $instance.App) {
        try {
            if (Get-Process -Id $instance.App.Id -ErrorAction SilentlyContinue) {
                Assert-Owned $instance.App
                if ($instance.Window -ne [IntPtr]::Zero) {
                    Assert-Window $instance $instance.Window
                    [void][WinGlanceDiagnosticNative]::PostMessage($instance.Window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
                }
                $deadline = [Diagnostics.Stopwatch]::StartNew()
                while ((Get-Process -Id $instance.App.Id -ErrorAction SilentlyContinue) -and $deadline.Elapsed.TotalSeconds -lt 10) {
                    Start-Sleep -Milliseconds 100
                }
                if (Get-Process -Id $instance.App.Id -ErrorAction SilentlyContinue) {
                    Assert-Owned $instance.App
                    Stop-Process -Id $instance.App.Id -Force
                    $cleanupErrors.Add('App required forced termination after the graceful exit deadline.')
                }
            }
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if ($null -ne $instance.Collector) {
        try {
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            while ((Get-Process -Id $instance.Collector.Id -ErrorAction SilentlyContinue) -and $deadline.Elapsed.TotalSeconds -lt 5) {
                Start-Sleep -Milliseconds 100
            }
            if (Get-Process -Id $instance.Collector.Id -ErrorAction SilentlyContinue) {
                Assert-Owned $instance.Collector
                Stop-Process -Id $instance.Collector.Id -Force
                $cleanupErrors.Add('Diagnostic collector required forced termination.')
            }
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    foreach ($cleanupError in $cleanupErrors) { Write-Warning $cleanupError }
    return @($cleanupErrors.ToArray())
}
function Start-Diagnostic([string]$name, [string]$scenario, [bool]$cyclesOnly = $false) {
    $directory = Join-Path $OutputDirectory $name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $clickThrough = $scenario -in @('ClickThrough', 'TrayClickThrough')
    $showOverlay = -not $cyclesOnly -and $scenario -in @('Acrylic', 'ClickThrough', 'TrayClickThrough')
    Write-JsonFile ([ordered]@{ Theme = 'Dark'; HideSystem = $true; CloseToTray = $false; ShowOverlay = $showOverlay;
        OverlayClickThrough = $clickThrough; OverlayOpacity = 0.8; OverlayX = 24; OverlayY = 24 }) (Join-Path $directory 'settings.json')
    $info = [Diagnostics.ProcessStartInfo]::new($AppPath)
    $info.WorkingDirectory = $packageDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.EnvironmentVariables['WINGLANCE_DIAGNOSTICS_DIR'] = $directory
    $info.EnvironmentVariables['WINGLANCE_DIAGNOSTIC_REPLAY'] = '1'
    $info.EnvironmentVariables['WINGLANCE_DISABLE_CHARTS'] = $(if ($scenario -eq 'NoCharts') { '1' } else { '0' })
    $info.EnvironmentVariables['WINGLANCE_DISABLE_MOTION'] = $(if ($scenario -eq 'NoMotion') { '1' } else { '0' })
    $instance = [pscustomobject]@{ Name = $name; Scenario = $scenario; Directory = $directory; App = $null; Collector = $null;
        Window = [IntPtr]::Zero; MainThreadId = 0; CleanupErrors = @() }
    try {
        $process = [Diagnostics.Process]::Start($info)
        try { $instance.App = Get-Identity $process $AppPath } finally { $process.Dispose() }
        Write-JsonFile $instance.App (Join-Path $directory 'app-identity.json')
        Assert-Owned $instance.App
        Wait-Until {
            $windows = @([WinGlanceDiagnosticNative]::Windows($instance.App.Id, 'WinGlance'))
            if ($windows.Count -eq 1) { $instance.Window = $windows[0]; return $true }
            return $false
        } 'diagnostic main window'
        Wait-Until {
            $count = Find-Control $instance 'CountLabel'
            $live = Find-Control $instance 'LiveLabel'
            return $null -ne $count -and $null -ne $live -and $count.Current.Name -match '^100 ' -and $live.Current.Name -match '1 秒'
        } '100 fixture applications and fresh replay data' 25
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($instance.App.Id)" | Where-Object {
            $_.Name -eq 'WinGlance.Collector.exe' -and $_.ExecutablePath -eq $collectorPath -and
            $_.CommandLine -match ('--parent ' + $instance.App.Id + '(?:\s|$)') -and
            $_.CommandLine -match ('--parent-start ' + $instance.App.StartTicks + '(?:\s|$)')
        })
        if ($children.Count -ne 1) { throw 'Could not identify exactly one collector owned by this diagnostic app.' }
        $collector = Get-Process -Id $children[0].ProcessId
        try { $instance.Collector = Get-Identity $collector $collectorPath } finally { $collector.Dispose() }
        Assert-Owned $instance.Collector
        [uint32]$owner = 0
        $instance.MainThreadId = [WinGlanceDiagnosticNative]::GetWindowThreadProcessId($instance.Window, [ref]$owner)
        if (-not [WinGlanceDiagnosticNative]::SetWindowPos($instance.Window, [IntPtr](-1), 0, 0, 0, 0, 0x13)) { throw 'Failed to keep diagnostic main window visible.' }
        if ($showOverlay) {
            Wait-Overlay $instance $true $clickThrough
            if (-not $clickThrough) {
                $appearance = Read-Backdrop $instance
                if ($appearance.Status -ne 'Attached' -or -not $appearance.AdvancedEffectsEnabled -or $appearance.HighContrast) {
                    throw 'Acrylic is unavailable in the current desktop configuration; do not report fallback rendering as Acrylic.'
                }
            }
            # Keep the overlay above the topmost main window, without synthesizing input.
            foreach ($window in [WinGlanceDiagnosticNative]::Windows($instance.App.Id, $null)) {
                if ($window -ne $instance.Window -and [WinGlanceDiagnosticNative]::IsWindowVisible($window)) {
                    Assert-Window $instance $window
                    [void][WinGlanceDiagnosticNative]::SetWindowPos($window, [IntPtr](-1), 0, 0, 0, 0, 0x13)
                }
            }
        }
        if ($scenario -eq 'Details') {
            $list = Find-Control $instance 'ProcessList'
            $row = $list.FindFirst([System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
            if ($null -eq $row) { throw 'Fixture application row is missing.' }
            $row.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Wait-Until {
                $nameControl = Find-Control $instance 'DetailName'
                $hint = Find-Control $instance 'ConnectionHint'
                return $null -ne $nameControl -and $nameControl.Current.Name -eq '诊断应用 000' -and $null -ne $hint -and $hint.Current.Name -match '\b40\b'
            } 'fixture application 000 and its 40 native UDP endpoints'
        }
        if ($scenario -eq 'TrayClickThrough') {
            Assert-Window $instance $instance.Window
            [void][WinGlanceDiagnosticNative]::PostMessage($instance.Window, 0x112, [IntPtr]0xF020, [IntPtr]::Zero)
            Wait-Until { -not [WinGlanceDiagnosticNative]::IsWindowVisible($instance.Window) } 'minimize to tray'
        }
        return $instance
    } catch {
        $launchFailure = $_
        $instance.CleanupErrors = @(Stop-Diagnostic $instance)
        foreach ($cleanupError in $instance.CleanupErrors) { $cleanup.Add("${name}: $cleanupError") }
        throw $launchFailure
    }
}
function Get-CpuResult($before, $after) {
    $duration = ($after.StopwatchTicks - $before.StopwatchTicks) / [double][Diagnostics.Stopwatch]::Frequency
    $cpu = $after.CpuSeconds - $before.CpuSeconds
    if ($duration -le 0 -or $cpu -lt 0 -or $before.StartTicks -ne $after.StartTicks) { throw 'Invalid CPU measurement endpoints.' }
    [pscustomobject]@{ DurationSeconds = $duration; CpuSeconds = $cpu; CpuPercent = 100 * $cpu / $duration / [Environment]::ProcessorCount }
}
function Get-Quantile([double[]]$values, [double]$fraction) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $index = ($sorted.Count - 1) * $fraction
    $low = [int][Math]::Floor($index); $high = [int][Math]::Ceiling($index)
    return $sorted[$low] + ($sorted[$high] - $sorted[$low]) * ($index - $low)
}

$started = [DateTimeOffset]::UtcNow
$packageBefore = @(Get-PackageManifest)
$results = [Collections.Generic.List[object]]::new()
$resourceCycles = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$failure = $null
$current = $null
$powerBefore = (& powercfg /getactivescheme | Out-String).Trim()
$environment = [pscustomobject]@{
    OS = [Environment]::OSVersion.VersionString; LogicalProcessors = [Environment]::ProcessorCount
    Processor = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    Computer = Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory
    PowerPlan = $powerBefore; Theme = 'Dark'; SampleIntervalSeconds = 1
    Fixture = '100 synthetic applications at 1 Hz; application 000 owns 40 real UDP loopback sockets'
    DatabaseTemplate = 'A new empty writable database per instance; no user data or prior history'
    PackageDirectory = $packageDirectory; GitCommit = (& git -C $projectRoot rev-parse HEAD | Out-String).Trim()
    ProtectedProcessIds = @($protected | Sort-Object); Package = $packageBefore
    TraceEnabled = $false
}
# Dot-source with -InitializeOnly to reuse the same identity-checked launch/cleanup helpers
# in independent trace/flush experiments. Initialization does not launch or control an app.
if ($InitializeOnly) {
    Write-JsonFile $environment (Join-Path $OutputDirectory 'environment.json')
    return
}
try {
    if (-not $CyclesOnly) {
    for ($round = 0; $round -lt $Rounds; $round++) {
        # Latin-square-style rotation avoids running all copies of one mode together.
        for ($position = 0; $position -lt $Scenarios.Count; $position++) {
            $scenario = $Scenarios[($position + $round) % $Scenarios.Count]
            $name = 'round-{0:00}-{1:00}-{2}' -f ($round + 1), ($position + 1), $scenario
            Write-Host "Starting $name ($WarmupSeconds s warmup, $Seconds s measured)"
            $current = Start-Diagnostic $name $scenario
            if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
            $observations = [Collections.Generic.List[object]]::new()
            $windowBefore = Get-WindowState $current
            $appBefore = Get-ProcessSnapshot $current.App
            $collectorBefore = Get-ProcessSnapshot $current.Collector
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
                $remaining = [Math]::Ceiling(1000 * ($Seconds - $timer.Elapsed.TotalSeconds))
                if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Min(1000, $remaining)) }
                $observations.Add([pscustomobject]@{ ElapsedSeconds = $timer.Elapsed.TotalSeconds;
                    Window = Get-WindowState $current; App = Get-ProcessSnapshot $current.App; Collector = Get-ProcessSnapshot $current.Collector })
            }
            $collectorAfter = Get-ProcessSnapshot $current.Collector
            $appAfter = Get-ProcessSnapshot $current.App
            $windowAfter = Get-WindowState $current
            if ($scenario -eq 'TrayClickThrough') {
                # Hidden WinUI windows may omit descendants from UIA. Restore only after all
                # timing/resource/window endpoints, then inspect the same collection status.
                Assert-Window $current $current.Window
                [void][WinGlanceDiagnosticNative]::PostMessage($current.Window, 0x8001, [IntPtr]1, [IntPtr]0x202)
                Wait-Until { [WinGlanceDiagnosticNative]::IsWindowVisible($current.Window) } 'post-measurement tray restoration' 5
            }
            $liveAfter = Find-Control $current 'LiveLabel'
            if ($null -eq $liveAfter -or $liveAfter.Current.Name -notmatch '1 秒') {
                throw "Replay data was not fresh at the end of $name; the sample is invalid."
            }
            $backdropAfter = Read-Backdrop $current
            if ($scenario -in @('Acrylic', 'ClickThrough', 'TrayClickThrough') -and
                ($null -eq $backdropAfter -or $backdropAfter.Status -eq 'Closed' -or
                $backdropAfter.ClickThrough -ne ($scenario -ne 'Acrylic'))) {
                throw "Overlay mode changed during $name; the sample is invalid."
            }
            $appCpu = Get-CpuResult $appBefore $appAfter
            $collectorCpu = Get-CpuResult $collectorBefore $collectorAfter
            $states = @($windowBefore) + @($observations | ForEach-Object Window) + @($windowAfter)
            $invalid = if ($scenario -eq 'TrayClickThrough') { @($states | Where-Object Visible).Count -gt 0 }
                else { @($states | Where-Object { -not $_.Visible -or $_.Minimized -or -not $_.Topmost }).Count -gt 0 }
            if ($invalid) { throw "Window visibility changed during $name; the sample is invalid." }
            if (@($states | Where-Object { $_.Dpi -ne $windowBefore.Dpi -or ($_.Bounds -join ',') -ne ($windowBefore.Bounds -join ',') }).Count -gt 0) {
                throw "Window size, position or DPI changed during $name; the sample is invalid."
            }
            $result = [pscustomobject]@{
                Round = $round + 1; Position = $position + 1; Scenario = $scenario; Name = $name
                AppIdentity = $current.App; CollectorIdentity = $current.Collector; MainUiThreadId = $current.MainThreadId
                AppCpu = $appCpu; CollectorCpu = $collectorCpu
                AppBefore = $appBefore; AppAfter = $appAfter; CollectorBefore = $collectorBefore; CollectorAfter = $collectorAfter
                WindowBefore = $windowBefore; WindowAfter = $windowAfter; Backdrop = $backdropAfter
                RestoredAfterMeasurementForValidation = $scenario -eq 'TrayClickThrough'
                Observations = @($observations.ToArray()); AllocationBytes = $null
                AllocationNote = 'Allocation and stack evidence are collected in separate EventPipe runs to avoid perturbing CPU comparisons.'
            }
            $results.Add($result)
            Write-JsonFile $result (Join-Path $current.Directory 'measurement.json')
            Write-JsonFile @($results.ToArray()) (Join-Path $OutputDirectory 'rounds.json')
            Write-Host ('Completed {0}: app {1:N3}% / collector {2:N3}% CPU; app private {3:N1} MiB' -f $name, $appCpu.CpuPercent, $collectorCpu.CpuPercent, ($appAfter.PrivateBytes / 1MB))
            foreach ($cleanupError in @(Stop-Diagnostic $current)) { $cleanup.Add("${name}: $cleanupError") }
            $current = $null
        }
    }
    }
    if (-not $SkipCycles) {
        foreach ($mode in @('Acrylic', 'ClickThrough')) {
            Write-Host "Starting $Cycles open/close cycles: $mode"
            $current = Start-Diagnostic ('cycles-' + $mode) $mode $true
            $cold = Get-ProcessSnapshot $current.App
            $cycleWarmupSeconds = [Math]::Max(60, $WarmupSeconds)
            Write-Host "Resource warmup: $mode ${cycleWarmupSeconds}s plus one uncounted open/close"
            $warmupTimer = [Diagnostics.Stopwatch]::StartNew()
            while ($warmupTimer.Elapsed.TotalSeconds -lt $cycleWarmupSeconds) { Start-Sleep -Milliseconds 250 }
            Invoke-Control $current 'OverlayButton'
            Wait-Overlay $current $true ($mode -eq 'ClickThrough')
            Start-Sleep -Milliseconds 500
            Invoke-Control $current 'OverlayButton'
            Wait-Overlay $current $false ($mode -eq 'ClickThrough')
            Start-Sleep -Seconds 2
            $points = [Collections.Generic.List[object]]::new()
            $points.Add([pscustomobject]@{ Cycle = 0; IdleSeconds = 0; App = Get-ProcessSnapshot $current.App })
            $checkpoints = @(25, 50, 75, 100, $Cycles | Sort-Object -Unique)
            for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
                Invoke-Control $current 'OverlayButton'
                Wait-Overlay $current $true ($mode -eq 'ClickThrough')
                Start-Sleep -Milliseconds 250
                Invoke-Control $current 'OverlayButton'
                Wait-Overlay $current $false ($mode -eq 'ClickThrough')
                Start-Sleep -Milliseconds 250
                if ($cycle -in $checkpoints) {
                    $points.Add([pscustomobject]@{ Cycle = $cycle; IdleSeconds = 0; App = Get-ProcessSnapshot $current.App })
                    Write-JsonFile @($points.ToArray()) (Join-Path $current.Directory 'cycle-points.json')
                    Write-Host "Resource checkpoint: $mode $cycle/$Cycles"
                }
            }
            $idle = [Diagnostics.Stopwatch]::StartNew()
            foreach ($idleSeconds in @(15, 30, 60)) {
                while ($idle.Elapsed.TotalSeconds -lt $idleSeconds) { Start-Sleep -Milliseconds 250 }
                $points.Add([pscustomobject]@{ Cycle = $Cycles; IdleSeconds = $idleSeconds; App = Get-ProcessSnapshot $current.App })
                Write-JsonFile @($points.ToArray()) (Join-Path $current.Directory 'cycle-points.json')
                Write-Host "Resource idle checkpoint: $mode +$idleSeconds s"
            }
            $cycleResult = [pscustomobject]@{ Mode = $mode; Cycles = $Cycles; Cold = $cold;
                WarmupSeconds = $cycleWarmupSeconds; UncountedWarmupOpenCloseCycles = 1; Points = @($points.ToArray());
                Note = 'Hidden/closed-overlay checkpoints and idle observations reveal trends. A single positive delta neither proves a leak nor demonstrates its absence.' }
            $resourceCycles.Add($cycleResult)
            Write-JsonFile $cycleResult (Join-Path $current.Directory 'cycles.json')
            foreach ($cleanupError in @(Stop-Diagnostic $current)) { $cleanup.Add("cycles-${mode}: $cleanupError") }
            $current = $null
        }
    }
} catch { $failure = $_ }
finally {
    if ($null -ne $current) { foreach ($cleanupError in @(Stop-Diagnostic $current)) { $cleanup.Add($cleanupError) } }
}
$packageAfter = @(Get-PackageManifest)
$packageUnchanged = ($packageBefore | ConvertTo-Json -Compress) -eq ($packageAfter | ConvertTo-Json -Compress)
$powerAfter = (& powercfg /getactivescheme | Out-String).Trim()
$summary = @($results | Group-Object Scenario | ForEach-Object {
    $app = @($_.Group | ForEach-Object { $_.AppCpu.CpuPercent })
    $collector = @($_.Group | ForEach-Object { $_.CollectorCpu.CpuPercent })
    [pscustomobject]@{ Scenario = $_.Name; N = $_.Count
        AppCpuMedian = Get-Quantile $app 0.5; AppCpuQ1 = Get-Quantile $app 0.25; AppCpuQ3 = Get-Quantile $app 0.75
        AppCpuIqr = (Get-Quantile $app 0.75) - (Get-Quantile $app 0.25)
        CollectorCpuMedian = Get-Quantile $collector 0.5; CollectorCpuQ1 = Get-Quantile $collector 0.25; CollectorCpuQ3 = Get-Quantile $collector 0.75
        CollectorCpuIqr = (Get-Quantile $collector 0.75) - (Get-Quantile $collector 0.25)
        AppPrivateMedianBytes = Get-Quantile @($_.Group | ForEach-Object { $_.AppAfter.PrivateBytes }) 0.5
        AppWorkingSetMedianBytes = Get-Quantile @($_.Group | ForEach-Object { $_.AppAfter.WorkingSetBytes }) 0.5
        AppKernelHandlesMedian = Get-Quantile @($_.Group | ForEach-Object { $_.AppAfter.KernelHandles }) 0.5
        AppGdiHandlesMedian = Get-Quantile @($_.Group | ForEach-Object { $_.AppAfter.GdiHandles }) 0.5
        AppUserHandlesMedian = Get-Quantile @($_.Group | ForEach-Object { $_.AppAfter.UserHandles }) 0.5 }
})
$report = [pscustomobject]@{
    StartedUtc = $started.ToString('o'); FinishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Completed = $null -eq $failure -and $packageUnchanged -and $cleanup.Count -eq 0
    FormalDuration = -not $CyclesOnly -and $Seconds -ge 60 -and $Rounds -ge 5 -and $WarmupSeconds -ge 10
    CyclesOnly = [bool]$CyclesOnly
    RequestedRounds = $Rounds; RequestedSeconds = $Seconds; WarmupSeconds = $WarmupSeconds
    Environment = $environment; PackageUnchanged = $packageUnchanged; PowerPlanAfter = $powerAfter
    PowerPlanUnchanged = $powerBefore -eq $powerAfter; Summary = $summary
    Rounds = @($results.ToArray()); ResourceCycles = @($resourceCycles.ToArray()); CleanupErrors = @($cleanup.ToArray())
    Failure = $(if ($failure) { $failure.Exception.Message } else { $null })
    Notes = 'CPU is normalized by logical processor count, using per-process monotonic endpoint intervals. The collector is a deterministic replay peer, so its CPU does not represent native telemetry cost. Topmost reduces normal occlusion; external topmost windows, power changes and system workload can still affect results. No p95 is inferred from five rounds. EventPipe allocation/stack, actual flush, native collector and platform coverage are separate experiments.'
}
Write-JsonFile $report (Join-Path $OutputDirectory 'report.json')
Write-Host "Saved: $(Join-Path $OutputDirectory 'report.json')"
if ($failure) { throw $failure }
if (-not $packageUnchanged) { throw 'Published files changed during measurement; results cannot be treated as a frozen-build comparison.' }
if ($cleanup.Count -gt 0) { throw 'Measurement completed with cleanup problems; inspect CleanupErrors.' }
