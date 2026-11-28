param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
[void][IO.Directory]::CreateDirectory($output)
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$helper = Join-Path $projectRoot 'scripts/tools/save-dialog-smoke/bin/Release/net10.0-windows/save-dialog-smoke.dll'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SaveDialogVerifyNative {
  private delegate bool EnumProc(IntPtr h,IntPtr p);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h,StringBuilder text,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h,StringBuilder text,int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr p,uint flags,StringBuilder path,ref uint size);
  public static string ImagePath(IntPtr p) {var b=new StringBuilder(32768);uint n=(uint)b.Capacity;if(!QueryFullProcessImageName(p,0,b,ref n))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return b.ToString();}
  public static string Title(IntPtr h) {var b=new StringBuilder(512);GetWindowText(h,b,b.Capacity);return b.ToString();}
  public static IntPtr[] Dialogs(int id) {var list=new List<IntPtr>();EnumWindows((h,p)=>{uint owner;GetWindowThreadProcessId(h,out owner);var b=new StringBuilder(100);GetClassName(h,b,b.Capacity);if(owner==id&&IsWindowVisible(h)&&b.ToString()=="#32770")list.Add(h);return true;},IntPtr.Zero);return list.ToArray();}
}
'@
$utf8 = [Text.UTF8Encoding]::new($false)
$results = [Collections.Generic.List[object]]::new()
function Write-Json($value,[string]$path) { [IO.File]::WriteAllText($path,($value | ConvertTo-Json -Depth 10),$utf8) }
function Assert-Identity($instance) {
    $p=Get-Process -Id $instance.Id -ErrorAction Stop
    try { if($p.StartTime.ToUniversalTime().Ticks -ne $instance.StartTicks -or [SaveDialogVerifyNative]::ImagePath($p.Handle) -ne $dotnet) {throw 'Helper identity changed.'} }
    finally {$p.Dispose()}
}
function Dialogs($instance) { Assert-Identity $instance; return @([SaveDialogVerifyNative]::Dialogs($instance.Id)) }
function Assert-Dialog($instance,[IntPtr]$window) {
    Assert-Identity $instance
    [uint32]$owner=0
    [void][SaveDialogVerifyNative]::GetWindowThreadProcessId($window,[ref]$owner)
    if($owner -ne $instance.Id) {throw 'Dialog is not owned by the isolated helper.'}
}
function Wait-Condition([scriptblock]$condition,[string]$description,[int]$seconds=12) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {if(& $condition){return};Start-Sleep -Milliseconds 100}while($clock.Elapsed.TotalSeconds -lt $seconds)
    throw "Timed out: $description"
}
function Find-Control([IntPtr]$window,[string]$id) {
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($window)
    $automationId = if($id -in @('6','7')) {'CommandButton_'+$id} else {$id}
    $found=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$automationId))
    foreach($control in $found) {
        if($id -eq '1' -and $control.Current.Name -notmatch '^(保存|导出|Save|Export)$'){continue}
        if($id -eq '2' -and $control.Current.Name -notmatch '^(取消|Cancel)$'){continue}
        return $control
    }
    return $null
}
function Command($instance,[IntPtr]$window,[int]$id) {
    Assert-Dialog $instance $window
    $control=Find-Control $window ([string]$id)
    $ready=[Diagnostics.Stopwatch]::StartNew()
    while(($null -eq $control -or -not $control.Current.IsEnabled) -and $ready.Elapsed.TotalSeconds -lt 3) {
        Start-Sleep -Milliseconds 100
        $control=Find-Control $window ([string]$id)
    }
    if($null -eq $control -or -not $control.Current.IsEnabled) {throw "Expected enabled dialog control $id is absent."}
    $message = if($id -in @(6,7)) {0x466} else {0x111} # TDM_CLICK_BUTTON / WM_COMMAND
    [void][SaveDialogVerifyNative]::PostMessage($window,$message,[IntPtr]$id,[IntPtr]::Zero)
}
function Save-DialogTree($instance,[string]$label) {
    $items=foreach($window in (Dialogs $instance)) {
        $root=[System.Windows.Automation.AutomationElement]::FromHandle($window)
        $children=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
        $controls=foreach($item in $children) {try{[pscustomobject]@{Id=$item.Current.AutomationId;Name=$item.Current.Name;Type=$item.Current.ControlType.ProgrammaticName;Enabled=$item.Current.IsEnabled}}catch{}}
        [pscustomobject]@{Window=$window.ToInt64();Title=[SaveDialogVerifyNative]::Title($window);Enabled=[SaveDialogVerifyNative]::IsWindowEnabled($window);Controls=@($controls)}
    }
    Write-Json @($items) (Join-Path $instance.Directory ($label+'.json'))
}
function Start-Case([string]$name,[string]$target,[string[]]$extra=@()) {
    $directory=Join-Path $output $name
    [void][IO.Directory]::CreateDirectory($directory)
    $resultPath=Join-Path $directory 'result.json'
    $info=[Diagnostics.ProcessStartInfo]::new($dotnet)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach($arg in @($helper,$target,$resultPath)+$extra){$info.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($info)
    try {$instance=[pscustomobject]@{Id=$p.Id;StartTicks=$p.StartTime.ToUniversalTime().Ticks;Directory=$directory;Result=$resultPath;Target=$target}}
    finally{$p.Dispose()}
    Write-Json $instance (Join-Path $directory 'identity.json')
    return $instance
}
function Wait-Result($instance) {
    Wait-Condition {Test-Path -LiteralPath $instance.Result} 'helper result' 10
    Wait-Condition {-not(Get-Process -Id $instance.Id -ErrorAction SilentlyContinue)} 'helper normal exit' 5
    return Get-Content -LiteralPath $instance.Result -Raw | ConvertFrom-Json
}
function Wait-SaveWindow($instance) {
    Wait-Condition {@(Dialogs $instance).Count -gt 0} 'save dialog'
    $windows=Dialogs $instance
    return $windows[0]
}
function Cleanup($instance) {
    if($null -eq $instance -or -not(Get-Process -Id $instance.Id -ErrorAction SilentlyContinue)){return}
    Assert-Identity $instance
    [IO.File]::WriteAllText((Join-Path $instance.Directory 'cancel.signal'),'cancel')
    foreach($window in (Dialogs $instance)) {
        if(-not [SaveDialogVerifyNative]::IsWindowEnabled($window)){continue}
        foreach($button in @(7,2)) {if($null -ne (Find-Control $window ([string]$button))) {Command $instance $window $button;break}}
    }
    Start-Sleep -Milliseconds 500
    if(Get-Process -Id $instance.Id -ErrorAction SilentlyContinue) {
        foreach($window in (Dialogs $instance)){Assert-Dialog $instance $window;[void][SaveDialogVerifyNative]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}
        Start-Sleep -Milliseconds 500
    }
    if(Get-Process -Id $instance.Id -ErrorAction SilentlyContinue){Assert-Identity $instance;Stop-Process -Id $instance.Id -Force;throw 'Failed helper required forced cleanup.'}
}
function Run-Case([string]$name,[scriptblock]$action) {
    $script:current=$null
    $started=[DateTimeOffset]::UtcNow
    try {$details=& $action;$results.Add([pscustomobject]@{Name=$name;Passed=$true;StartedUtc=$started;Details=$details});Write-Host "PASS $name"}
    catch {$results.Add([pscustomobject]@{Name=$name;Passed=$false;StartedUtc=$started;Error=$_.Exception.Message});Write-Warning "$name : $($_.Exception.Message)";if($script:current -and (Get-Process -Id $script:current.Id -ErrorAction SilentlyContinue)){Save-DialogTree $script:current 'failure-tree'}}
    finally {try{Cleanup $script:current}finally{Write-Json $results.ToArray() (Join-Path $output 'report.json')}}
}
$existing=Join-Path $output 'existing.json'
[IO.File]::WriteAllText($existing,'{"fixture":"keep exactly"}',$utf8)
$originalHash=(Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash
foreach($cancelMilliseconds in @(0,1)) {
    Run-Case "token-$cancelMilliseconds" {
        $script:current=Start-Case "token-$cancelMilliseconds" (Join-Path $output "token-$cancelMilliseconds.json") @([string]$cancelMilliseconds,'--no-diagnostics')
        $result=Wait-Result $script:current
        if($result.Status -ne 'TokenCancelled'){throw 'Expected cancellation.'};$result
    }
}
Run-Case 'user-cancel' {
    $script:current=Start-Case 'user-cancel' (Join-Path $output 'user-cancel.csv')
    $window=Wait-SaveWindow $script:current
    Save-DialogTree $script:current 'visible-tree'
    Command $script:current $window 2
    $result=Wait-Result $script:current
    if($result.Status -ne 'UserCancelled'){throw 'Expected user cancellation.'};$result
}
Run-Case 'token-visible-no-diagnostics' {
    $signal=Join-Path $output 'token-visible-no-diagnostics/cancel.signal'
    $script:current=Start-Case 'token-visible-no-diagnostics' (Join-Path $output 'token-visible.json') @('--cancel-file',$signal,'--no-diagnostics')
    [void](Wait-SaveWindow $script:current)
    [IO.File]::WriteAllText($signal,'cancel')
    $result=Wait-Result $script:current
    if($result.Status -ne 'TokenCancelled'){throw 'Expected cancellation without diagnostic callback.'};$result
}
Run-Case 'new-path' {
    $target=Join-Path $output 'new-export.csv'
    $script:current=Start-Case 'new-path' $target
    $window=Wait-SaveWindow $script:current
    Command $script:current $window 1
    $result=Wait-Result $script:current
    if($result.Status -ne 'Selected' -or $result.Path -ne $target -or (Test-Path -LiteralPath $target)){throw 'New target was not selected without file creation.'};$result
}
Run-Case 'existing-path' {
    $script:current=Start-Case 'existing-path' $existing
    $window=Wait-SaveWindow $script:current
    Command $script:current $window 1
    Wait-Condition {@(Dialogs $script:current).Count -gt 1} 'overwrite confirmation'
    Save-DialogTree $script:current 'overwrite-tree'
    $confirm=@(Dialogs $script:current | Where-Object {$_ -ne $window})[0]
    Command $script:current $confirm 6
    $result=Wait-Result $script:current
    if($result.Status -ne 'Selected' -or $result.Path -ne $existing -or (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash -ne $originalHash){throw 'Existing target changed during picker selection.'};$result
}
Run-Case 'overwrite-user-decline' {
    $script:current=Start-Case 'overwrite-user-decline' $existing
    $window=Wait-SaveWindow $script:current
    Command $script:current $window 1
    Wait-Condition {@(Dialogs $script:current).Count -gt 1} 'overwrite confirmation'
    $confirm=@(Dialogs $script:current | Where-Object {$_ -ne $window})[0]
    Command $script:current $confirm 7
    Wait-Condition {@(Dialogs $script:current).Count -eq 1} 'return to picker after decline'
    Command $script:current $window 2
    $result=Wait-Result $script:current
    if($result.Status -ne 'UserCancelled' -or (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash -ne $originalHash){throw 'Declining overwrite must preserve target.'};$result
}
Run-Case 'overwrite-token-cancel' {
    $signal=Join-Path $output 'overwrite-token-cancel/cancel.signal'
    $script:current=Start-Case 'overwrite-token-cancel' $existing @('--cancel-file',$signal,'--no-diagnostics')
    $window=Wait-SaveWindow $script:current
    Command $script:current $window 1
    Wait-Condition {@(Dialogs $script:current).Count -gt 1} 'overwrite confirmation'
    [IO.File]::WriteAllText($signal,'cancel')
    $result=Wait-Result $script:current
    if($result.Status -ne 'TokenCancelled' -or (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash -ne $originalHash){throw 'Cancelling overwrite must preserve target.'};$result
}
if(@($results | Where-Object {-not $_.Passed}).Count){throw 'Save dialog verification has failed cases; inspect report.json.'}
