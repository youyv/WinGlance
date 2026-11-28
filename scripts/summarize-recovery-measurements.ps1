param([string]$Artifacts = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'))
$ErrorActionPreference = 'Stop'
function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Distribution($values) {
    $ordered = @($values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    function Quantile([double]$fraction) {
        $index = ($ordered.Count - 1) * $fraction
        $lower = [int][Math]::Floor($index); $upper = [int][Math]::Ceiling($index)
        $ordered[$lower] + ($ordered[$upper] - $ordered[$lower]) * ($index - $lower)
    }
    [pscustomobject]@{ N = $ordered.Count; Median = Quantile .5; Q1 = Quantile .25; Q3 = Quantile .75;
        IQR = (Quantile .75) - (Quantile .25); P95 = Quantile .95; Min = $ordered[0]; Max = $ordered[-1] }
}
$ui = foreach ($name in @('Baseline', 'NoCharts', 'NoMotion', 'Details')) {
    $data = Read-Json (Join-Path $Artifacts "trace-ui-v2\trace-$name\ui.diagnostics.json")
    $normalizedStages = foreach ($group in ($data.Stages | Group-Object Name)) {
        [pscustomobject]@{ Name = $group.Name; Time = Distribution @($group.Group | ForEach-Object Milliseconds);
            AllocatedBytes = $(if ($group.Name -in @('app.render', 'app.endpointQuery')) { ($group.Group | Measure-Object AllocatedBytes -Sum).Sum } else { $null }) }
    }
    [pscustomobject]@{ Scenario = $name; Stages = $normalizedStages;
        AllocationSamples = $data.AllocationSamples; CpuStacks = $data.CpuStacks }
}
$allFlushEvents = [Collections.Generic.List[object]]::new()
$allWorkEvents = [Collections.Generic.List[object]]::new()
$flush = foreach ($index in 1..5) {
    $data = Read-Json (Join-Path $Artifacts ('flush-v2\flush-{0:00}\flush.diagnostics.json' -f $index))
    foreach ($operation in $data.FlushOperations) {
        foreach ($event in $operation.Events) { $allFlushEvents.Add($event) }
    }
    foreach ($event in $data.WorkEvents) { $allWorkEvents.Add($event) }
    $sampleQueue = @($data.WorkEvents | Where-Object { $_.Operation -eq 'sample' -and $_.Phase -eq 'start' } | ForEach-Object Milliseconds)
    $queryQueue = @($data.WorkEvents | Where-Object { $_.Operation -eq 'query' -and $_.Phase -eq 'start' } | ForEach-Object Milliseconds)
    $queryExecution = @($data.WorkEvents | Where-Object { $_.Operation -eq 'query' -and $_.Phase -eq 'finished' } | ForEach-Object Milliseconds)
    $completed = @($data.FlushOperations | Where-Object { $_.Finished -and $_.Reason -eq 'periodic' })
    [pscustomobject]@{ Round = $index; Completed = $completed.Count;
        FlushMilliseconds = @($completed | ForEach-Object { @($_.Events | Where-Object Phase -eq 'finished')[0].Milliseconds });
        Phases = $data.FlushSummary; QueryQueue = Distribution $queryQueue; QueryExecution = Distribution $queryExecution;
        SampleQueue = Distribution $sampleQueue; Failures = @($data.WorkEvents | Where-Object Phase -eq 'failed').Count }
}
$flushAggregate = [pscustomobject]@{
    Completed = @($allFlushEvents | Where-Object { $_.Reason -eq 'periodic' -and $_.Phase -eq 'finished' }).Count;
    Phases = @(foreach ($group in ($allFlushEvents | Where-Object Reason -eq 'periodic' | Group-Object Phase)) {
        [pscustomobject]@{ Phase = $group.Name; Time = Distribution @($group.Group | ForEach-Object Milliseconds) }
    });
    QueryQueue = Distribution @($allWorkEvents | Where-Object { $_.Operation -eq 'query' -and $_.Phase -eq 'start' } | ForEach-Object Milliseconds);
    QueryExecution = Distribution @($allWorkEvents | Where-Object { $_.Operation -eq 'query' -and $_.Phase -eq 'finished' } | ForEach-Object Milliseconds);
    SampleQueue = Distribution @($allWorkEvents | Where-Object { $_.Operation -eq 'sample' -and $_.Phase -eq 'start' } | ForEach-Object Milliseconds);
    Failures = @($allWorkEvents | Where-Object Phase -eq 'failed').Count
}
$collectors = foreach ($mode in @('ordinary', 'enhanced')) {
    foreach ($trace in @($true, $false)) {
        $folder = 'collector-' + $mode + '-v2' + $(if ($trace) { '' } else { '-no-trace' })
        $directory = Join-Path $Artifacts $folder
        if (-not (Test-Path -LiteralPath (Join-Path $directory 'rounds.json'))) { continue }
        $environment = Read-Json (Join-Path $directory 'environment.json')
        $rounds = @(Read-Json (Join-Path $directory 'rounds.json'))
        $excluded = @()
        if (Test-Path -LiteralPath (Join-Path $directory 'exclusions.json')) {
            $exclusions = Read-Json (Join-Path $directory 'exclusions.json')
            $excluded = @($rounds | Where-Object { $_.Round -in $exclusions.ExcludedRounds })
            $rounds = @($rounds | Where-Object { $_.Round -notin $exclusions.ExcludedRounds })
            $replacement = @(Read-Json (Join-Path $exclusions.ReplacementDirectory 'rounds.json'))
            $replacement[0].Round = 'replacement-1'
            $rounds += $replacement
        }
        $metrics = foreach ($round in $rounds) {
            $first = $round.Observations[10]; $last = $round.Observations[-1]
            $quality = @($round.Observations | ForEach-Object { if ($_.PSObject.Properties.Name -contains 'EtwQuality' -and $null -ne $_.EtwQuality) { $_.EtwQuality } })
            [pscustomobject]@{ Round = $round.Round; Frames = $round.Frames; TraceError = $round.TraceError;
                MeasuredSeconds = $last.ElapsedSeconds - $first.ElapsedSeconds;
                CpuMachinePercent = 100 * ($last.CpuSeconds - $first.CpuSeconds) / ($last.ElapsedSeconds - $first.ElapsedSeconds) / $environment.LogicalProcessors;
                PrivateMiB = $last.PrivateBytes / 1MB; WorkingSetMiB = $last.WorkingSetBytes / 1MB;
                PrivateChangeMiB = ($last.PrivateBytes - $first.PrivateBytes) / 1MB;
                HandlesStart = $first.KernelHandles; HandlesEnd = $last.KernelHandles;
                ProcessesStart = $first.Processes; ProcessesEnd = $last.Processes;
                NetworkStates = @($round.Observations | ForEach-Object { $_.Capabilities.network.State } | Sort-Object -Unique);
                DiskStates = @($round.Observations | ForEach-Object { $_.Capabilities.disk.State } | Sort-Object -Unique);
                EtwStates = @($quality | ForEach-Object State | Sort-Object -Unique);
                QualityFrames = $quality.Count;
                LostEvents = ($quality | Measure-Object -Property LostEvents -Sum).Sum;
                UnattributedEvents = ($quality | Measure-Object -Property UnattributedEvents -Sum).Sum;
                UnattributedDownloadBytes = ($quality | Measure-Object -Property UnattributedDownloadBytes -Sum).Sum;
                UnattributedUploadBytes = ($quality | Measure-Object -Property UnattributedUploadBytes -Sum).Sum;
                UnattributedReadBytes = ($quality | Measure-Object -Property UnattributedDiskReadBytes -Sum).Sum;
                UnattributedWriteBytes = ($quality | Measure-Object -Property UnattributedDiskWriteBytes -Sum).Sum }
        }
        [pscustomobject]@{ Mode = $mode; TraceEnabled = $trace; CpuMachinePercent = Distribution @($metrics | ForEach-Object CpuMachinePercent);
            PrivateMiB = Distribution @($metrics | ForEach-Object PrivateMiB); Rounds = $metrics; ExcludedRounds = $excluded;
            Stages = $(if ($trace) { foreach ($index in 1..$rounds.Count) {
                $analysis = Read-Json (Join-Path $directory ('collector-{0:00}.diagnostics.json' -f $index))
                $warmStages = foreach ($group in ($analysis.Stages | Group-Object Name)) {
                    $warm = @($group.Group | Select-Object -Skip 10)
                    [pscustomobject]@{ Name = $group.Name; Time = Distribution @($warm | ForEach-Object Milliseconds);
                        ThreadAllocatedBytes = $(if ($group.Name -in @('collector.sample', 'collector.serialize')) { ($warm | Measure-Object -Property AllocatedBytes -Sum).Sum } else { $null }) }
                }
                [pscustomobject]@{ Round = $index; Summary = $analysis.StageSummary; WarmStageSummary = $warmStages;
                    CpuStacks = $analysis.CpuStacks; AllocationSamples = $analysis.AllocationSamples }
            } } else { @() }) }
    }
}
$report = [pscustomobject]@{ CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o'); UI = @($ui); Flush = @($flush); FlushAggregate = $flushAggregate; Collectors = @($collectors);
    Notes = @('Collector CPU warm interval is frame 11 through frame 60 (49 intervals), excluding first ten frames consistently.',
        'Trace-on and no-trace are separate measurements. GC allocation bytes are estimates and nested stage wall times overlap.',
        'Stage allocatedBytes is instrumented only for app.render, app.endpointQuery, collector.sample and collector.serialize. Zero for acceptGate/acceptPrepare/other stages means not measured, not zero allocation.',
        'All distributions in this aggregate use linearly interpolated quantiles; original parser files used lower-order discrete quantiles.',
        'Five-round medians/IQR are meaningful descriptive summaries; p95 fields for five rounds are not reliable tail-latency claims.',
        'Ordinary ETW quality was recorded only in the replacement round and was PermissionRequired; missing quality is not evidence of zero losses.',
        'Host workload other than the fixed UDP loopback fixture remains uncontrolled. IsElevated alone does not establish ETW coverage.') }
[IO.File]::WriteAllText((Join-Path $Artifacts 'c-recovery-measurements.json'), ($report | ConvertTo-Json -Depth 25), [Text.UTF8Encoding]::new($false))
