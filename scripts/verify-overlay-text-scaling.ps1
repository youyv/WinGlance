param([Parameter(Mandatory=$true)][string]$AppPath, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$chosenApp=$AppPath; $chosenOutput=$OutputDirectory
. (Join-Path $PSScriptRoot 'measure-diagnostics.ps1') -AppPath $chosenApp -OutputDirectory $chosenOutput -OverlayPreset 3 -InitializeOnly
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class OverlayScaleMeasurement {
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
 public static void NotifySettingChange(IntPtr window){
  // WM_SETTINGCHANGE is a system pointer-bearing message; PostMessage rejects it.
  // Deliver only to this verified diagnostic HWND, with a bounded synchronous send.
  UIntPtr result;
  if(SendMessageTimeout(window,0x1A,IntPtr.Zero,IntPtr.Zero,2,2000,out result)==IntPtr.Zero)
   throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Owned WM_SETTINGCHANGE delivery failed");
 }
 [StructLayout(LayoutKind.Sequential)] public struct Size { public int Width,Height; }
 [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
 [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
 [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
 [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
 [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateFont(int h,int w,int escape,int orientation,int weight,uint italic,uint underline,uint strike,uint charset,uint output,uint clip,uint quality,uint family,string face);
 [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc,string text,int count,out Size result);
 public static Size Measure(string text,int fontPixels){
  var dc=CreateCompatibleDC(IntPtr.Zero); if(dc==IntPtr.Zero)throw new InvalidOperationException("measurement DC failed");
  var font=CreateFont(-fontPixels,0,0,0,400,0,0,0,1,0,0,4,0,"Consolas");
  if(font==IntPtr.Zero){DeleteDC(dc);throw new InvalidOperationException("measurement font failed");}
  var old=SelectObject(dc,font);
  try { Size size; if(!GetTextExtentPoint32(dc,text,text.Length,out size))throw new InvalidOperationException("measure failed");return size; }
  finally{SelectObject(dc,old);DeleteObject(font);DeleteDC(dc);}
 }
}
'@
$checks=[Collections.Generic.List[string]]::new(); $captures=[Collections.Generic.List[object]]::new()
$cleanup=[Collections.Generic.List[string]]::new(); $instance=$null; $completed=$false; $failure=$null
$desc=[Windows.Automation.TreeScope]::Descendants
function Check([bool]$condition,[string]$message) { if(-not $condition){throw $message}; $checks.Add($message); Write-Host "PASS $message" }
function OverlayHandle {
 $state=Read-Backdrop $instance
 $title=if($state.ClickThrough){'WinGlance 悬浮条（鼠标穿透）'}else{'WinGlance 悬浮条'}
 $windows=@([WinGlanceDiagnosticNative]::Windows($instance.App.Id,$title) | Where-Object { [WinGlanceDiagnosticNative]::IsWindowVisible($_) })
 if($windows.Count -ne 1){throw "Expected one owned overlay, found $($windows.Count)"}
 Assert-Window $instance $windows[0]
 return $windows[0]
}
function SetScale([double]$factor,[bool]$notify=$true) {
 Assert-Owned $instance.App
 [IO.File]::WriteAllText((Join-Path $instance.Directory 'overlay-text-scale.txt'),$factor.ToString([Globalization.CultureInfo]::InvariantCulture),$utf8)
 if($notify){
  # WM_SETTINGCHANGE is sent only to our owned HWND, never HWND_BROADCAST.
  $state=Read-Backdrop $instance
  $target=if($state.ClickThrough){OverlayHandle}else{$instance.Window}
  Assert-Window $instance $target
  [OverlayScaleMeasurement]::NotifySettingChange($target)
  Wait-Until { $state=Read-Backdrop $instance; $null -ne $state -and $state.TextScaleFactor -eq $factor } "dynamic text scale $factor"
 }
}
function Capture([string]$name,[double]$factor) {
 $window=OverlayHandle
 [void][WinGlanceDiagnosticNative]::SetWindowPos($window,[IntPtr](-1),0,0,0,0,0x13)
 Start-Sleep -Milliseconds 250
 $state=Read-Backdrop $instance
 $bounds=[WinGlanceDiagnosticNative]::Bounds($window)
 $dpi=[WinGlanceDiagnosticNative]::GetDpiForWindow($window)/96.0
 $expectedFont=[Math]::Round(12*$factor,2);$expectedLine=[Math]::Ceiling(20*$factor)
 Check ($state.TextScaleFactor -eq $factor -and $state.FontSizeDip -eq $expectedFont -and $state.LineHeightDip -eq $expectedLine) "$name uses the explicit shared font and line scale"
 Check (-not $state.TextAutomaticScaleEnabled -and -not $state.CloseAutomaticScaleEnabled -and $state.CloseFontSizeDip -eq 16 -and $state.CloseWidthDip -eq 36) "$name disables duplicate automatic scaling and keeps the close target fixed"
 Check (-not $state.SpaceLimited -and $bounds[2] -le [Math]::Ceiling($state.AvailableWidthDip*$dpi) -and $bounds[3] -le [Math]::Ceiling($state.AvailableHeightDip*$dpi)) "$name window fits its workarea with complete content"
 $fontPixels=[int][Math]::Ceiling($expectedFont*$dpi);$linePixels=[int][Math]::Ceiling($expectedLine*$dpi)
 foreach($line in ($state.DisplayText -split "`n")){
  $actual=[OverlayScaleMeasurement]::Measure($line,$fontPixels)
  Check ($actual.Width -le ($bounds[2]-[Math]::Ceiling(16*$dpi)-[Math]::Ceiling(60*$dpi)) -and $actual.Height -le $linePixels) "$name actual GDI glyphs fit the allocated text line"
 }
 if($state.ClickThrough){
  Check ($state.NativeFontPixelHeight -eq $fontPixels -and $state.NativeLinePixelHeight -eq $linePixels) "$name native font and line cache follow dynamic text scale"
 }else{
  $root=[Windows.Automation.AutomationElement]::FromHandle($window)
  $text=$root.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'StatusText'))
  $close=$root.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'CloseButton'))
  Check ($null -ne $text -and -not $text.Current.IsOffscreen -and $text.Current.BoundingRectangle.Right -le $bounds[0]+$bounds[2] -and $text.Current.BoundingRectangle.Bottom -le $bounds[1]+$bounds[3]) "$name WinUI text remains inside the window"
  Check ($null -ne $close -and -not $close.Current.IsOffscreen -and [Math]::Abs($close.Current.BoundingRectangle.Width-36*$dpi) -le 2) "$name WinUI close target remains visible at fixed size"
 }
 $imagePath=Join-Path $OutputDirectory ($name+'.png')
 $bitmap=[Drawing.Bitmap]::new($bounds[2],$bounds[3]);$graphics=[Drawing.Graphics]::FromImage($bitmap)
 try{$graphics.CopyFromScreen($bounds[0],$bounds[1],0,0,$bitmap.Size);$bitmap.Save($imagePath)}finally{$graphics.Dispose();$bitmap.Dispose()}
 $captures.Add([pscustomobject]@{Name=$name;Path=$imagePath;Bounds=$bounds;DpiScale=$dpi;State=$state})
 Write-JsonFile $state (Join-Path $OutputDirectory ($name+'.json'))
}
try {
 foreach($mode in @('Acrylic','ClickThrough')){
  $instance=Start-Diagnostic ('text-scale-'+$mode) $mode
  [IO.File]::WriteAllText((Join-Path $instance.Directory 'large-overlay'),'',$utf8)
  Start-Sleep -Seconds 2
  SetScale 2.25
  foreach($factor in @(1.0,1.5,2.25,1.0)){
   SetScale $factor
   Capture ($mode+'-dynamic-'+$captures.Count+'-'+[int]($factor*100)) $factor
  }
  Invoke-Control $instance 'OverlayButton'; Wait-Overlay $instance $false ($mode -eq 'ClickThrough')
  SetScale 2.25 $false
  Invoke-Control $instance 'OverlayButton'; Wait-Overlay $instance $true ($mode -eq 'ClickThrough')
  Wait-Until { (Read-Backdrop $instance).TextScaleFactor -eq 2.25 } 'reopen rereads text scale without a notification'
  Capture ($mode+'-reopen-225') 2.25
  [IO.File]::WriteAllText((Join-Path $instance.Directory 'stall'),'',$utf8)
  Wait-Until { $label=Find-Control $instance 'LiveLabel'; $null -ne $label -and $label.Current.Name -notlike '*1 秒*' } 'stale replay status' 20
  SetScale 1.5
  Capture ($mode+'-stale-150') 1.5
  Check ((Read-Backdrop $instance).DisplayText -match '(最后|上次)观测') "$mode scaled stale status keeps its observation timestamp"
  foreach($cleanupError in @(Stop-Diagnostic $instance)){$cleanup.Add($cleanupError)}
  $instance=$null
 }
 $completed=$true
}catch{
 $failure=$_
 Write-Host ("FAILED "+$_.Exception.Message+" at "+$_.ScriptStackTrace)
 if($null -ne $instance){try{
  Write-JsonFile (Read-Backdrop $instance) (Join-Path $OutputDirectory 'failure-backdrop.json')
  $window=OverlayHandle;$bounds=[WinGlanceDiagnosticNative]::Bounds($window)
  $bitmap=[Drawing.Bitmap]::new($bounds[2],$bounds[3]);$graphics=[Drawing.Graphics]::FromImage($bitmap)
  try{$graphics.CopyFromScreen($bounds[0],$bounds[1],0,0,$bitmap.Size);$bitmap.Save((Join-Path $OutputDirectory 'failure.png'))}finally{$graphics.Dispose();$bitmap.Dispose()}
 }catch{Write-Warning ('Failure evidence: '+$_.Exception.Message)}}
}finally{
 if($null -ne $instance){foreach($cleanupError in @(Stop-Diagnostic $instance)){$cleanup.Add($cleanupError)}}
 $packageAfter=Get-PackageManifest
 $unchanged=($packageBefore|ConvertTo-Json -Depth 4 -Compress) -eq ($packageAfter|ConvertTo-Json -Depth 4 -Compress)
 Write-JsonFile ([ordered]@{Completed=$completed -and $unchanged -and $cleanup.Count -eq 0;Checks=$checks.ToArray();Count=$checks.Count;Captures=$captures.ToArray();CleanupErrors=$cleanup.ToArray();PackageUnchanged=$unchanged;Failure=$(if($failure){$failure.Exception.Message}else{$null});FinishedUtc=[DateTimeOffset]::UtcNow;Scope='Isolated per-instance text-factor injection and owned WM_SETTINGCHANGE; real Windows text-size/DPI/high-contrast changes remain separate platform acceptance tests.'}) (Join-Path $OutputDirectory 'report.json')
}
if($failure){throw $failure}
if(-not $unchanged){throw 'Frozen publish changed during verification'}
if($cleanup.Count -gt 0){throw 'Text scaling cleanup failed; inspect report.json'}
