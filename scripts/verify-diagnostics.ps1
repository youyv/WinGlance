param([string]$BinaryDirectory = 'artifacts\repair-baseline', [switch]$InitializeOnly)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SmokeNative {
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string m);
}
'@
$projectRoot = Split-Path -Parent $PSScriptRoot
$binary = Join-Path ([IO.Path]::GetFullPath((Join-Path $projectRoot $BinaryDirectory))) 'WinGlance.App.exe'
$runRoot = Join-Path $projectRoot ('artifacts\smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$tree = [System.Windows.Automation.TreeScope]::Descendants
function Assert($condition, $message) { if (-not $condition) { throw $message }; $checks.Add($message); Write-Output "PASS $message" }
function WaitFor([scriptblock]$test, [int]$seconds=30) {
 $until = [DateTime]::UtcNow.AddSeconds($seconds)
 do { $result = & $test; if ($result) { return $result }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $until)
 throw "Timed out: $test"
}
function Element($id) { $script:root.FindFirst($tree,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)) }
function Text($id) { $element = Element $id; if ($element) { $element.Current.Name } else { '' } }
function Invoke($id) { (Element $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
function SelectApp([string]$name) {
 (Element 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
 Start-Sleep -Milliseconds 350
 $row = (Element 'ProcessList').FindFirst($tree,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
 if (-not $row) { throw "Missing fixture $name" }
 $r = $row.Current.BoundingRectangle
 [SmokeNative]::SetWindowPos($script:handle,[IntPtr](-1),0,0,0,0,0x13) | Out-Null
 [SmokeNative]::SetForegroundWindow($script:handle) | Out-Null
 [SmokeNative]::SetCursorPos([int]($r.X+130),[int]($r.Y+$r.Height/2)) | Out-Null
 [SmokeNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [SmokeNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 350
}
function CountConnections {
 $list = Element 'ConnectionsList'
 if (-not $list) { return 0 }
 return $list.FindAll($tree,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)).Count
}
function StartFixture($name, [bool]$initialOverlay=$false, [string]$fault='', [bool]$minimized=$false) {
 $script:directory = Join-Path $runRoot $name
 New-Item -ItemType Directory -Path $script:directory | Out-Null
 @{Theme='Dark'; HideSystem=$false; CloseToTray=$true; ShowOverlay=$initialOverlay; OverlayClickThrough=$initialOverlay; OverlayOpacity=0.8} | ConvertTo-Json | Set-Content (Join-Path $script:directory 'settings.json') -Encoding utf8
 if ($fault) { New-Item -ItemType File -Path (Join-Path $script:directory ('fault-'+$fault)) | Out-Null }
 $info = [Diagnostics.ProcessStartInfo]::new($binary)
 $info.UseShellExecute=$false
 $info.EnvironmentVariables['WINGLANCE_DIAGNOSTICS_DIR']=$script:directory
 $info.EnvironmentVariables['WINGLANCE_DIAGNOSTIC_REPLAY']='1'
 if ($minimized) { $info.Arguments='--minimized' }
 $script:app = [Diagnostics.Process]::Start($info)
 $script:root = WaitFor { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:app.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'WinGlance'))) }
 $script:handle = [IntPtr]$script:root.Current.NativeWindowHandle
 [SmokeNative]::SetWindowPos($script:handle,[IntPtr](-1),0,0,0,0,0x13) | Out-Null
 [SmokeNative]::SetForegroundWindow($script:handle) | Out-Null
 WaitFor { (Text 'LiveLabel') -eq '实时 · 1 秒' } | Out-Null
}
function StopFixture {
 if ($script:app -and -not $script:app.HasExited) {
  if (-not $script:handle) { $script:app.Refresh(); $script:handle = $script:app.MainWindowHandle }
  # Only this owned diagnostic instance is addressed. Let its normal persistence path drain.
  [SmokeNative]::PostMessage($script:handle,0x11,[IntPtr]0,[IntPtr]0) | Out-Null
  [SmokeNative]::PostMessage($script:handle,0x16,[IntPtr]1,[IntPtr]0) | Out-Null
  Start-Sleep -Milliseconds 500
  [SmokeNative]::PostMessage($script:handle,0x10,[IntPtr]0,[IntPtr]0) | Out-Null
  if (-not $script:app.WaitForExit(6000)) {
   $cleanup.Add("Owned diagnostic PID $($script:app.Id) required forced cleanup after its normal exit deadline.")
   $script:app.Kill()
  }
 }
}
if ($InitializeOnly) { return }
try {
 StartFixture 'live'
 SelectApp '诊断应用 000'
 WaitFor { (CountConnections) -gt 0 } | Out-Null
 Assert ((Text 'ConnectionHint') -like '40 条端点*') 'native endpoint fixture displays 40 UDP bindings'
 Invoke 'PauseButton'
 $pausedStatus = Text 'StatusLabel'
 New-Item -ItemType File -Path (Join-Path $directory 'exit001') | Out-Null
 Start-Sleep -Seconds 3
 Assert ((Text 'StatusLabel') -eq $pausedStatus) 'paused observation time remains the displayed frame time'
 SelectApp '诊断应用 001'
 Assert ((Text 'DetailName') -eq '诊断应用 001' -and (CountConnections) -eq 0) 'paused owner switch clears prior application endpoints'
 Assert ((Text 'DetailPath') -eq 'C:\WinGlanceFixture\001.exe' -and (Text 'ConnectionHint') -like '*应用已退出*') 'paused list keeps application B identity after B exits in newer frames'
 Invoke 'CloseDetailButton'
 SelectApp '诊断应用 001'
 Assert ((CountConnections) -eq 0) 'reopening details while paused cannot restore prior owner rows'
 Invoke 'PauseButton'
 Assert ((Text 'CountLabel') -like '0 个应用*') 'resuming removes the exited application from the filtered list'
 Remove-Item -LiteralPath (Join-Path $directory 'exit001')
 SelectApp '诊断应用 000'
 WaitFor { (Text 'ConnectionHint') -like '40 条端点*' } | Out-Null
 Invoke 'OverlayButton'
 New-Item -ItemType File -Path (Join-Path $directory 'stall') | Out-Null
 WaitFor { (Text 'LiveLabel') -eq '等待新数据' } | Out-Null
 Assert ((Text 'ConnectionHint') -like '*上次观测*') 'stalled endpoint rows are labelled with last observation'
 Invoke 'StatisticsButton'
 Assert ((Text 'CollectionStatus') -like '*等待新数据*') 'statistics shares stalled collection state'
 Invoke 'OverviewButton'
 Remove-Item -LiteralPath (Join-Path $directory 'stall')
 WaitFor { (Text 'LiveLabel') -eq '实时 · 1 秒' } | Out-Null
 Assert $true 'pipe resumes from stall without reconnect or elevation'
 # Exercise production power-message handling without suspending the user's machine.
 New-Item -ItemType File -Path (Join-Path $directory 'stall') | Out-Null
 [SmokeNative]::PostMessage($handle,0x218,[IntPtr]4,[IntPtr]0) | Out-Null
 [SmokeNative]::PostMessage($handle,0x218,[IntPtr]18,[IntPtr]0) | Out-Null
 Start-Sleep -Milliseconds 350
 Assert ((Text 'LiveLabel') -eq '等待新数据') 'power resume waits for a real new frame'
 Remove-Item -LiteralPath (Join-Path $directory 'stall')
 WaitFor { (Text 'LiveLabel') -eq '实时 · 1 秒' } | Out-Null
 [SmokeNative]::PostMessage($handle,0x10,[IntPtr]0,[IntPtr]0) | Out-Null
 Start-Sleep -Milliseconds 350
 [SmokeNative]::PostMessage($handle,0x8001,[IntPtr]1,[IntPtr]0x202) | Out-Null
 Start-Sleep -Milliseconds 500
 Assert ((Text 'LiveLabel') -eq '实时 · 1 秒') 'tray restore retains current generation state'
 StopFixture
 StartFixture 'overlay-failure' $true 'overlay' $true
 Assert ((Text 'OverlayNotice') -like '*穿透*' -or (Element 'OverlayNotice').Current.IsOffscreen -eq $false) 'silent launch failure restores visible main window'
 Assert ((Get-Content (Join-Path $directory 'settings.json') -Raw -Encoding utf8 | ConvertFrom-Json).OverlayClickThrough) 'native failure preserves saved click-through preference'
 StopFixture
 StartFixture 'tray-failure' $true 'tray' $true
 Assert (-not $root.Current.IsOffscreen) 'tray unavailable prevents silent launch hiding the main window'
 Assert ((Get-Content (Join-Path $directory 'settings.json') -Raw -Encoding utf8 | ConvertFrom-Json).OverlayClickThrough) 'tray failure preserves saved click-through preference'
 StopFixture
} catch {
 Write-Output "FAILED $($_.Exception.Message) at $($_.ScriptStackTrace)"
 throw
} finally {
 StopFixture
 @{Checks=$checks.ToArray(); Count=$checks.Count; CleanupErrors=$cleanup.ToArray(); Binary=$binary; RunRoot=$runRoot; Finished=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $runRoot 'verification.json') -Encoding utf8
}
if ($cleanup.Count -gt 0) { throw 'Diagnostic cleanup did not complete normally; inspect verification.json.' }
