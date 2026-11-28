param([Parameter(Mandatory=$true)][string]$AppPath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [Alias('QuickTail')][switch]$TailOnly)
$onlyTail=$TailOnly
$chosenApp=$AppPath; $chosenOutput=$OutputDirectory
. (Join-Path $PSScriptRoot 'measure-diagnostics.ps1') -AppPath $chosenApp -OutputDirectory $chosenOutput -InitializeOnly
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class FeatureInput {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
 [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
 [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
 [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
 [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateFont(int h,int w,int escape,int orientation,int weight,uint italic,uint underline,uint strike,uint charset,uint output,uint clip,uint quality,uint family,string face);
 [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc,string text,int count,out Size result);
 [StructLayout(LayoutKind.Sequential)] public struct Size { public int Width,Height; }
 public static int Measure(string text,double scale){
  var dc=CreateCompatibleDC(IntPtr.Zero);var font=CreateFont(-(int)Math.Ceiling(12*scale),0,0,0,400,0,0,0,1,0,0,4,0,"Consolas");var old=SelectObject(dc,font);
  try { Size size; if(!GetTextExtentPoint32(dc,text,text.Length,out size))throw new InvalidOperationException("measure failed");return size.Width; }
  finally{SelectObject(dc,old);DeleteObject(font);DeleteDC(dc);}
 }
}
'@
$checks=[Collections.Generic.List[string]]::new(); $cleanup=[Collections.Generic.List[string]]::new(); $captures=[Collections.Generic.List[object]]::new()
$instance=$null; $completed=$false
function ReadUserRun {
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$false)
 try {
  if($null -eq $key -or $key.GetValueNames() -notcontains 'WinGlance'){return [pscustomobject]@{Exists=$false;Kind=$null;Value=$null}}
  return [pscustomobject]@{Exists=$true;Kind=$key.GetValueKind('WinGlance').ToString();Value=$key.GetValue('WinGlance',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
 }finally{if($null -ne $key){$key.Dispose()}}
}
$startupBefore=ReadUserRun
$desc=[Windows.Automation.TreeScope]::Descendants
function Check([bool]$condition,[string]$message) { if(-not $condition){throw $message}; $checks.Add($message); Write-Host "PASS $message" }
function OwnRoot { Assert-Window $instance $instance.Window; [Windows.Automation.AutomationElement]::FromHandle($instance.Window) }
function ByName([string]$name,[string]$kind='') {
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
 if($kind){ $condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::$kind)) }
 (OwnRoot).FindFirst($desc,$condition)
}
function ClickElement($element) {
 if($null -eq $element){throw 'Missing owned UI element'}
 Assert-Owned $instance.App
 $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Milliseconds 150
}
function ReadText([string]$id) { $element=Find-Control $instance $id; if($null -ne $element){return $element.Current.Name}; return '' }
function SelectCombo([string]$id,[string]$name) {
 Assert-Owned $instance.App
 $combo=Find-Control $instance $id
 if($null -eq $combo){
  Wait-Until { $script:pendingCombo=Find-Control $instance $id; $null -ne $script:pendingCombo } "settings control $id"
  $combo=$script:pendingCombo
 }
 $scroll=$null
 if($combo.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView()}
 $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Start-Sleep -Milliseconds 120
 $item=$null
 Wait-Until { $script:item=ByName $name 'ListItem'; $null -ne $script:item } "option $id/$name"
 $script:item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 Start-Sleep -Milliseconds 120
}
function OpenSettings {
 # ContentDialog close animation completes after Invoke returns. A click while
 # MainWindow still owns the old dialog is intentionally ignored by the product.
 Start-Sleep -Milliseconds 500
 [void][WinGlanceDiagnosticNative]::SetWindowPos($instance.Window,[IntPtr](-1),0,0,0,0,0x13)
 ClickElement (ByName '设置' 'Button')
 Wait-Until { $null -ne (Find-Control $instance 'OverlayPresetPicker') } 'settings content'
 Start-Sleep -Milliseconds 350
}
function Configure([string]$theme,[string]$preset,[bool]$native) {
 OpenSettings
 SelectCombo 'ThemePicker' $(if($theme -eq 'Dark'){'深色'}else{'浅色'})
 SelectCombo 'OverlayPresetPicker' $preset
 $toggle=(Find-Control $instance 'OverlayClickThroughToggle').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
 if(($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $native){$toggle.Toggle()}
 Invoke-Control $instance 'PrimaryButton'
 Wait-Overlay $instance $true $native
 Wait-Until { $state=Read-Backdrop $instance; $state.RequestedTheme -eq $theme } 'requested overlay theme'
 Start-Sleep -Milliseconds 800
}
function CaptureOverlay([string]$name) {
 $state=Read-Backdrop $instance
 $title=if($state.ClickThrough){'WinGlance 悬浮条（鼠标穿透）'}else{'WinGlance 悬浮条'}
 $windows=@([WinGlanceDiagnosticNative]::Windows($instance.App.Id,$title) | Where-Object { [WinGlanceDiagnosticNative]::IsWindowVisible($_) })
 if($windows.Count -ne 1){throw "Expected one owned overlay, found $($windows.Count)"}
 $window=$windows[0]; Assert-Window $instance $window
 [void][WinGlanceDiagnosticNative]::SetWindowPos($window,[IntPtr](-1),0,0,0,0,0x13)
 Start-Sleep -Milliseconds 200
 $bounds=[WinGlanceDiagnosticNative]::Bounds($window)
 $imagePath=Join-Path $OutputDirectory ($name+'.png')
 $bitmap=[Drawing.Bitmap]::new($bounds[2],$bounds[3]); $graphics=[Drawing.Graphics]::FromImage($bitmap)
 try { $graphics.CopyFromScreen($bounds[0],$bounds[1],0,0,$bitmap.Size); $bitmap.Save($imagePath) }
 finally { $graphics.Dispose(); $bitmap.Dispose() }
 $state=Read-Backdrop $instance
 $scale=[WinGlanceDiagnosticNative]::GetDpiForWindow($window)/96.0
 foreach($line in ($state.DisplayText -split "`n")){
  Check ([FeatureInput]::Measure($line,$scale) -le (($state.LayoutWidth-76)*$scale)) "$name line fits actual GDI Consolas metrics"
 }
 if(-not $state.ClickThrough){
  $overlayRoot=[Windows.Automation.AutomationElement]::FromHandle($window)
  $text=$overlayRoot.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'StatusText'))
  Check ($null -ne $text -and -not $text.Current.IsOffscreen -and $text.Current.BoundingRectangle.Right -le $bounds[0]+$bounds[2]) "$name XAML text bounds remain inside overlay"
 }
 $captures.Add([pscustomobject]@{Name=$name;Path=$imagePath;Bounds=$bounds;Dpi=[WinGlanceDiagnosticNative]::GetDpiForWindow($window);State=$state})
 Write-JsonFile $state (Join-Path $OutputDirectory ($name+'.json'))
}
try {
 if(-not $onlyTail){
 $instance=Start-Diagnostic 'features' 'Details'
 Invoke-Control $instance 'PauseButton'
 $sourceHint=ReadText 'ConnectionHint'
 Check ($sourceHint -like '*40*' -and $sourceHint -like '*上次观测*') 'pause preserves the original 40-endpoint observation hint'
 SelectCombo 'ConnectionProtocol' 'TCP'
 Check ((ReadText 'ConnectionFilterHint') -like '*40*0*' -and (ReadText 'ConnectionEmpty') -like '*没有符合*') 'TCP filter shows zero of 40 retrieved UDP rows'
 SelectCombo 'ConnectionProtocol' 'UDP'
 Check ((ReadText 'ConnectionFilterHint') -like '*40*40*') 'UDP filter restores the same retrieved 40-row dataset'
 SelectCombo 'ConnectionState' '已连接'
 Check ((ReadText 'ConnectionFilterHint') -like '*40*0*') 'connected state intersects UDP without inventing a remote connection'
 SelectCombo 'ConnectionState' 'UDP 绑定'
 Check ((ReadText 'ConnectionFilterHint') -like '*40*40*' -and (ReadText 'ConnectionHint') -eq $sourceHint) 'filtering preserves source observation and paused state'
 $list=Find-Control $instance 'ConnectionsList'
 $row=$list.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 $scroll=$null
 if($row.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView()}
 if($row.Current.IsOffscreen){
  $scrollers=(OwnRoot).FindAll($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty,$true))
  foreach($candidate in $scrollers){
   if($candidate.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'ConnectionProtocol'))){
    $candidate.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1,100)
   }
  }
  Start-Sleep -Milliseconds 300
 }
 $rectangle=$row.Current.BoundingRectangle
 Check (-not $row.Current.IsOffscreen -and $rectangle.Width -gt 0) 'owned UDP row is visible for the context-menu check'
 Assert-Window $instance $instance.Window
 [void][FeatureInput]::SetCursorPos([int]($rectangle.X+30),[int]($rectangle.Y+$rectangle.Height/2))
 [FeatureInput]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [FeatureInput]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
 Wait-Until { $null -ne (ByName '复制远端端点' 'MenuItem') } 'connection context menu'
 Check (-not (ByName '复制远端端点' 'MenuItem').Current.IsEnabled -and (ByName '复制本地端点' 'MenuItem').Current.IsEnabled -and (ByName '复制整行' 'MenuItem').Current.IsEnabled) 'UDP copy menu disables missing remote endpoint and allows local/full row'
 $savedClipboard=[Windows.Forms.Clipboard]::GetDataObject(); $clipboardBackup=[Windows.Forms.DataObject]::new(); $copied=$null
 if($null -ne $savedClipboard){foreach($format in $savedClipboard.GetFormats($false)){$clipboardBackup.SetData($format,$false,$savedClipboard.GetData($format,$false))}}
 try {
  ClickElement (ByName '复制整行' 'MenuItem')
  $copied=[Windows.Forms.Clipboard]::GetText()
  Check ($copied -match '^UDP4\t本地绑定\t\d+\t127\.0\.0\.1:\d+\t—$') 'actual full-row clipboard action copies the selected immutable endpoint'
 } finally {
  if($null -ne $copied -and [Windows.Forms.Clipboard]::GetText() -eq $copied){
   if($null -eq $savedClipboard -or $savedClipboard.GetFormats($false).Count -eq 0){[Windows.Forms.Clipboard]::Clear()}
   else{[Windows.Forms.Clipboard]::SetDataObject($clipboardBackup,$true)}
  }
 }
 [void][WinGlanceDiagnosticNative]::PostMessage($instance.Window,0x100,[IntPtr]27,[IntPtr]0)
 [void][WinGlanceDiagnosticNative]::PostMessage($instance.Window,0x101,[IntPtr]27,[IntPtr]0)
 Start-Sleep -Milliseconds 150
 Invoke-Control $instance 'PauseButton'
 Invoke-Control $instance 'CloseDetailButton'
 Invoke-Control $instance 'StatisticsButton'
 Start-Sleep -Seconds 2
 Assert-Window $instance $instance.Window
 $statsBounds=[WinGlanceDiagnosticNative]::Bounds($instance.Window)
 $statsBitmap=[Drawing.Bitmap]::new($statsBounds[2],$statsBounds[3]);$statsGraphics=[Drawing.Graphics]::FromImage($statsBitmap)
 try{$statsGraphics.CopyFromScreen($statsBounds[0],$statsBounds[1],0,0,$statsBitmap.Size);$statsBitmap.Save((Join-Path $OutputDirectory 'statistics-page.png'))}finally{$statsGraphics.Dispose();$statsBitmap.Dispose()}
 Invoke-Control $instance 'OverviewButton'
 Invoke-Control $instance 'OverlayButton'
 Wait-Overlay $instance $true $false
 Start-Sleep -Seconds 2
 foreach($theme in @('Dark','Light')) {
  foreach($native in @($false,$true)) {
   foreach($preset in @(@('仅网速','Network'),@('标准','Standard'),@('含内存','Memory'),@('全部','All'))) {
    Configure $theme $preset[0] $native
    $state=Read-Backdrop $instance
    Check ($state.Preset -eq $preset[1] -and $state.ClickThrough -eq $native) "$theme/$native/$($preset[1]) applies and persists preset"
    Check ($state.LayoutWidth -le 510 -and $state.LayoutHeight -ge 58) "$theme/$native/$($preset[1]) has bounded layout"
    if(-not $native){Check ($state.Status -eq 'Attached' -and $state.State -eq 'Active') "$theme/$($preset[1]) uses active Acrylic"}
    CaptureOverlay ("overlay-$theme-"+$(if($native){'native'}else{'acrylic'})+"-$($preset[1])")
   }
  }
 }
 $before=[IO.File]::ReadAllText((Join-Path $instance.Directory 'settings.json'))|ConvertFrom-Json
 OpenSettings; SelectCombo 'OverlayPresetPicker' '仅网速'; Invoke-Control $instance 'CloseButton'
 $after=[IO.File]::ReadAllText((Join-Path $instance.Directory 'settings.json'))|ConvertFrom-Json
 Check ($before.OverlayPreset -eq $after.OverlayPreset) 'cancelled preset edit does not change saved settings'
 New-Item -ItemType File -Path (Join-Path $instance.Directory 'large-overlay') | Out-Null
 Start-Sleep -Seconds 2
 foreach($native in @($false,$true)){
  Configure 'Dark' '全部' $native
  CaptureOverlay ("overlay-large-"+$(if($native){'native'}else{'acrylic'}))
  $state=Read-Backdrop $instance
  Check ($state.DisplayText -like '*1023.9 TB/s*' -and $state.DisplayText -like '*100.0%*') 'large fixture displays complete TB/s and percent values'
 }
 }else{
  $instance=Start-Diagnostic 'features-tail' 'Acrylic'
  Configure 'Dark' '全部' $false
  $settings=[IO.File]::ReadAllText((Join-Path $instance.Directory 'settings.json'))|ConvertFrom-Json
  Check ($settings.OverlayPreset -eq 3) 'All preset is persisted after a real settings save'
  OpenSettings; SelectCombo 'OverlayPresetPicker' '仅网速'; Invoke-Control $instance 'CloseButton'
  $settings=[IO.File]::ReadAllText((Join-Path $instance.Directory 'settings.json'))|ConvertFrom-Json
  Check ($settings.OverlayPreset -eq 3) 'cancelled preset edit preserves the persisted All preset'
  OpenSettings
  $startup=(Find-Control $instance 'StartupToggle').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  $startup.Toggle()
  $expectedStartup=$startup.Current.ToggleState
  Invoke-Control $instance 'PrimaryButton'
  OpenSettings
  $startup=(Find-Control $instance 'StartupToggle').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  Check ($startup.Current.ToggleState -eq $expectedStartup) 'diagnostic startup toggle round-trips using only isolated mock state'
  Invoke-Control $instance 'CloseButton'
 }
 New-Item -ItemType File -Path (Join-Path $instance.Directory 'stall') | Out-Null
 Wait-Until { (ReadText 'LiveLabel') -notlike '*1 秒*' } 'stale replay status' 20
 foreach($preset in @(@('仅网速','Network'),@('标准','Standard'),@('含内存','Memory'),@('全部','All'))) {
  Configure 'Dark' $preset[0] $true
  $state=Read-Backdrop $instance
  Check ($state.DisplayText -match '(最后|上次)观测' -and $state.DisplayText -notlike '*CPU*') "$($preset[1]) keeps stale data visibly distinct from live metrics"
 }
 $completed=$true
} catch {
 Write-Host ("FAILED " + $_.Exception.Message + " at " + $_.ScriptStackTrace)
 if($null -ne $instance -and $instance.Window -ne [IntPtr]::Zero){ try {
  Assert-Window $instance $instance.Window
  $bounds=[WinGlanceDiagnosticNative]::Bounds($instance.Window)
  $bitmap=[Drawing.Bitmap]::new($bounds[2],$bounds[3]); $graphics=[Drawing.Graphics]::FromImage($bitmap)
  try{$graphics.CopyFromScreen($bounds[0],$bounds[1],0,0,$bitmap.Size);$bitmap.Save((Join-Path $OutputDirectory 'failure.png'))}finally{$graphics.Dispose();$bitmap.Dispose()}
  $nodes=(OwnRoot).FindAll($desc,[Windows.Automation.Condition]::TrueCondition)
  $names=@(foreach($node in $nodes){[pscustomobject]@{Name=$node.Current.Name;Id=$node.Current.AutomationId;Type=$node.Current.ControlType.ProgrammaticName;Offscreen=$node.Current.IsOffscreen;Bounds=$node.Current.BoundingRectangle.ToString()}})
  Write-JsonFile $names (Join-Path $OutputDirectory 'failure-uia.json')
 }catch{Write-Warning ('Failure evidence capture: '+$_.Exception.Message)} }
 throw
} finally {
 if($null -ne $instance){foreach($error in @(Stop-Diagnostic $instance)){$cleanup.Add($error)}}
 $packageAfter=Get-PackageManifest
 $startupAfter=ReadUserRun
 $startupUnchanged=($startupBefore|ConvertTo-Json -Compress) -eq ($startupAfter|ConvertTo-Json -Compress)
 $unchanged=($packageBefore|ConvertTo-Json -Depth 4 -Compress) -eq ($packageAfter|ConvertTo-Json -Depth 4 -Compress)
 Write-JsonFile ([ordered]@{Completed=$completed;TailOnly=[bool]$onlyTail;Checks=$checks.ToArray();Count=$checks.Count;Captures=$captures.ToArray();CleanupErrors=$cleanup.ToArray();PackageUnchanged=$unchanged;UserStartupUnchanged=$startupUnchanged;FinishedUtc=[DateTimeOffset]::UtcNow}) (Join-Path $OutputDirectory 'report.json')
}
if($cleanup.Count -gt 0){throw 'Feature UI cleanup failed; inspect report.json'}
if(-not $startupUnchanged){throw 'The real user Run registration changed during isolated UI testing; inspect startup isolation.'}
