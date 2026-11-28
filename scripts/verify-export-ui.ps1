param([Parameter(Mandatory=$true)][string]$BinaryDirectory)
$selectedBinary = $BinaryDirectory
. (Join-Path $PSScriptRoot 'verify-diagnostics.ps1') -BinaryDirectory $selectedBinary -InitializeOnly
function SetValue([string]$id, [string]$value) {
 (Element $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)
 Start-Sleep -Milliseconds 250
}
function BeginExport([string]$format) {
 Invoke 'ExportButton'
 Invoke ('Export'+$format)
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
