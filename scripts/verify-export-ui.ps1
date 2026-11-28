param([Parameter(Mandatory=$true)][string]$BinaryDirectory)
$selectedBinary = $BinaryDirectory
. (Join-Path $PSScriptRoot 'verify-diagnostics.ps1') -BinaryDirectory $selectedBinary -InitializeOnly
function SetValue([string]$id, [string]$value) {
 (Element $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)
 Start-Sleep -Milliseconds 250
}
function BeginExport([string]$format) {
 Invoke 'ExportButton'
 $command = WaitFor { Element ('Export'+$format) }
 $command.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 return WaitFor {
  $script:root.FindFirst([Windows.Automation.TreeScope]::Descendants,
    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'导出运行统计'))
 }
}
function DialogElement($dialog,[string]$id) {
 $matches=$dialog.FindAll($tree,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
 foreach($control in $matches) {
  if($id -eq '1' -and $control.Current.Name -notmatch '^(导出|保存|Export|Save)$') { continue }
  if($id -eq '2' -and $control.Current.Name -notmatch '^(取消|Cancel)$') { continue }
  return $control
 }
}
function DialogCommand($dialog,[string]$id) {
 $control=DialogElement $dialog $id
 if(-not $control -or -not $control.Current.IsEnabled) { throw "Dialog button $id is not ready" }
 [SmokeNative]::PostMessage([IntPtr]$dialog.Current.NativeWindowHandle,0x111,[IntPtr]([int]$id),[IntPtr]0) | Out-Null
}
function SaveExport([string]$format,[string]$path) {
 $dialog = BeginExport $format
 (DialogElement $dialog '1001').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($path)
 DialogCommand $dialog '1'
 WaitFor { (Text 'ExportNotice') -like '*导出完成*' -or (Text 'ExportResultText') -like ('*'+$path+'*') } | Out-Null
 Assert (Test-Path -LiteralPath $path) ($format+' export creates the selected destination')
}
function ChooseExisting([string]$path) {
 $dialog = BeginExport 'Json'
 (DialogElement $dialog '1001').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($path)
 DialogCommand $dialog '1'
 WaitFor { Element 'ExportOverwriteDialog' } | Out-Null
 Assert ((Element 'ExportOverwriteDialog').Current.IsOffscreen -eq $false) 'existing file requires visible application overwrite confirmation'
 Assert (-not (Element 'ResetButton').Current.IsEnabled) 'reset is disabled during export destination selection'
}
function ConfirmOverwrite([string]$choice) {
 $confirm = Element 'ExportOverwriteDialog'
 $button = $confirm.FindFirst($tree,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$choice))
 if (-not $button) { throw "Missing overwrite decision: $choice" }
 $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 WaitFor { -not (Element 'ExportOverwriteDialog') } | Out-Null
 Start-Sleep -Milliseconds 350
}
try {
 StartFixture 'export'
 Invoke 'StatisticsButton'
 WaitFor { (Text 'ApplicationCount') -like '100 个应用*' } | Out-Null
 SetValue 'StatsSearch' '诊断应用 000'
 $jsonPath = Join-Path $directory '统计.json'
 SaveExport 'Json' $jsonPath
 $json = Get-Content -LiteralPath $jsonPath -Raw -Encoding utf8 | ConvertFrom-Json
 Assert ($json.Applications.Count -eq 1 -and $json.Scope.Search -eq '诊断应用 000') 'JSON export keeps the clicked search scope'
 Assert ($json.Scope.AllApplicationsCount -eq 100) 'filtered export preserves the all-application denominator scope'
 $notice = Text 'ExportResultText'
 Start-Sleep -Seconds 3
 Assert ((Text 'ExportResultText') -eq $notice) 'regular statistics refresh preserves the export result'
 Invoke 'OverviewButton'
 Invoke 'StatisticsButton'
 Assert ((Text 'ExportResultText') -eq $notice) 'navigation retains the export result'
 SetValue 'StatsSearch' '诊断应用 00'
 $csvPath = Join-Path $directory '统计.csv'
 SaveExport 'Csv' $csvPath
 $csv = [IO.File]::ReadAllBytes($csvPath)
 Assert ($csv.Length -gt 3 -and $csv[0] -eq 239 -and $csv[1] -eq 187 -and $csv[2] -eq 191) 'CSV export writes UTF-8 BOM'
 $dialog = BeginExport 'Json'
 DialogCommand $dialog '2'
 WaitFor { (Element 'ExportButton').Current.IsEnabled -and (Text 'ExportResultText') -eq '未提交文件。' } | Out-Null
 Assert $true 'manual dialog cancellation returns the statistics page to a usable state'
 $existing = Join-Path $directory '已有统计.json'
 [IO.File]::WriteAllText($existing,'independent content')
 $originalHash = (Get-FileHash -LiteralPath $existing).Hash
 ChooseExisting $existing
 ConfirmOverwrite '取消'
 WaitFor { (Element 'ExportButton').Current.IsEnabled } | Out-Null
 Assert ((Get-FileHash -LiteralPath $existing).Hash -eq $originalHash) 'declining overwrite preserves the existing file exactly'
 ChooseExisting $existing
 ConfirmOverwrite '另选位置'
 $dialog = WaitFor { $root.FindFirst($tree,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'导出运行统计')) }
 $suggestedName = (DialogElement $dialog '1001').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 Assert ($suggestedName -eq [IO.Path]::GetFileName($existing)) 'reselection suggests only the filename so choosing another folder remains effective'
 # Re-selecting the same path must ask again instead of reusing prior permission.
 (DialogElement $dialog '1001').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($existing)
 DialogCommand $dialog '1'
 WaitFor { Element 'ExportOverwriteDialog' } | Out-Null
 Assert $true 'reselecting the same file asks for fresh overwrite consent'
 ConfirmOverwrite '另选位置'
 $dialog = WaitFor { $root.FindFirst($tree,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'导出运行统计')) }
 $reselected = Join-Path $directory '另选统计.json'
 (DialogElement $dialog '1001').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($reselected)
 DialogCommand $dialog '1'
 WaitFor { (Element 'ExportButton').Current.IsEnabled -and (Test-Path -LiteralPath $reselected) } | Out-Null
 Assert ((Get-FileHash -LiteralPath $existing).Hash -eq $originalHash) 'reselecting a new target preserves the declined file'
 ChooseExisting $existing
 ConfirmOverwrite '替换'
 # InfoBar's Automation Name need not contain its visual title; the result text
 # exposes the committed path, and the file schema is checked independently below.
 WaitFor { (Element 'ExportButton').Current.IsEnabled -and (Text 'ExportResultText') -like ('*'+$existing+'*') } | Out-Null
 Assert ((Get-Content -LiteralPath $existing -Raw -Encoding utf8 | ConvertFrom-Json).schemaVersion -eq 1) 'explicit overwrite produces a valid export'
 Assert (@(Get-ChildItem -LiteralPath $directory -Filter '.winglance-recovery-*' -Directory).Count -eq 0) 'successful replacement cleans its recovery directory'
 StopFixture
 StartFixture 'export-exit-confirmation'
 Invoke 'StatisticsButton'
 WaitFor { (Text 'ApplicationCount') -like '100 个应用*' } | Out-Null
 $exitTarget = Join-Path $directory '退出保护.json'
 [IO.File]::WriteAllText($exitTarget,'preserve on exit')
 ChooseExisting $exitTarget
 [SmokeNative]::PostMessage($handle,0x16,[IntPtr]1,[IntPtr]0) | Out-Null
 WaitFor { -not (Element 'ExportOverwriteDialog') } | Out-Null
 Assert ([IO.File]::ReadAllText($exitTarget) -eq 'preserve on exit') 'session end closes application confirmation without replacing the file'
 StopFixture
 StartFixture 'export-exit-picker'
 Invoke 'StatisticsButton'
 WaitFor { (Text 'ApplicationCount') -like '100 个应用*' } | Out-Null
 $dialog = BeginExport 'Json'
 [SmokeNative]::PostMessage($handle,0x11,[IntPtr]0,[IntPtr]0) | Out-Null
 Start-Sleep -Milliseconds 300
 [SmokeNative]::PostMessage($handle,0x16,[IntPtr]0,[IntPtr]0) | Out-Null
 Start-Sleep -Milliseconds 300
 Assert (-not $app.HasExited -and -not $dialog.Current.IsOffscreen) 'cancelled session query leaves export selection open'
 [SmokeNative]::PostMessage($handle,0x16,[IntPtr]1,[IntPtr]0) | Out-Null
 WaitFor { -not $script:root.FindFirst($tree,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'导出运行统计')) } | Out-Null
 Assert $true 'confirmed session end cancels the picker without waiting for a UI continuation'
} catch {
 Write-Output "FAILED $($_.Exception.Message) at $($_.ScriptStackTrace)"
 Write-Output ('Export phase: '+(Text 'ExportPhaseLabel')+'; result: '+(Text 'ExportResultText'))
 throw
}
finally {
 StopFixture
 @{Checks=$checks.ToArray();Count=$checks.Count;CleanupErrors=$cleanup.ToArray();Binary=$binary;RunRoot=$runRoot;Finished=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $runRoot 'export-verification.json') -Encoding utf8
}
if ($cleanup.Count -gt 0) { throw 'Export UI cleanup did not complete normally; inspect export-verification.json.' }
