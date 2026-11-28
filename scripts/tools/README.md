# Diagnostic tools

These opt-in helpers observe isolated diagnostic instances. They are not packaged with WinGlance. Build using the .NET 10 SDK in `.tools/dotnet` (or substitute your installed `dotnet` executable):

```powershell
.tools/dotnet/dotnet.exe build scripts/tools/diagnostic-reader/diagnostic-reader.csproj -c Release
.tools/dotnet/dotnet.exe build scripts/tools/diagnostic-live/diagnostic-live.csproj -c Release
```

`diagnostic-live attach` writes an EventPipe trace with WinGlance stage/flush/work events, GC allocation sampling and managed SampleProfiler events. `--flushes` counts actual successful periodic completions; `--min-seconds` and `--max-seconds` bound capture. `diagnostic-live collector` starts only its own collector child, validates its identity, isolates data and captures 60 frames by default. It uses the harness token: an enhanced run requires launching the harness with the appropriate Windows elevation.

```powershell
& scripts/trace-ui-diagnostics.ps1 -AppPath artifacts/repair-baseline-v2/WinGlance.App.exe -OutputDirectory artifacts/new-ui-trace
& scripts/measure-flush-diagnostics.ps1 -AppPath artifacts/repair-baseline-v2/WinGlance.App.exe -OutputDirectory artifacts/new-flush-trace
& scripts/measure-real-collector.ps1 -CollectorPath artifacts/repair-baseline-v2/WinGlance.Collector.exe -OutputDirectory artifacts/new-collector
& scripts/measure-real-collector.ps1 -CollectorPath artifacts/repair-baseline-v2/WinGlance.Collector.exe -OutputDirectory artifacts/new-collector-no-trace -NoTrace
```

Use a fresh output directory. Run experiments sequentially while avoiding builds, tests and large file scans. Always compare trace-enabled measurements with separate untraced CPU measurements. Preserve partial output and disclose exclusions rather than overwriting failed/contaminated runs.

The reader reports linearly interpolated median/p95 timings. Stage wall times can overlap; allocation ticks estimate allocation and do not measure retained memory. Only managed samples with meaningful managed frames enter the managed hotspot lists. External samples can include native waiting and must not be labeled CPU work or renderer work without additional evidence. Fields with uninstrumented zero allocation are unknown; the product currently measures thread allocation only for `app.render`, `app.endpointQuery`, `collector.sample` and `collector.serialize`.

`endpoint-benchmark` exercises synthetic native-table inputs, per-process selection, process removal and explicit buffer capacity. It excludes operating-system table enumeration and process-handle validation. `handle-snapshot` reads handle-type counts for an explicitly selected owned diagnostic PID; pair counts with process identity, cycle number and idle duration before interpreting a trend.

`save-dialog-smoke` links the production save-location dialog into a small STA host. `scripts/verify-save-dialog.ps1` checks new/existing destination selection, overwrite prompts and cancellation without writing the selected destination. `scripts/verify-export-ui.ps1 -BinaryDirectory artifacts/<fresh-publish-directory>` drives the complete statistics export flow using an isolated replay instance. These are interactive desktop checks: run them sequentially, without another diagnostic window covering the target. Test failures and any forced helper cleanup must remain in the recorded results.

`scripts/verify-features-ui.ps1` checks bounded connection filters, clipboard actions and both overlay renderers. `-QuickTail` checks settings and stale-state behavior without repeating the screenshot matrix. It reads the real startup registration before and after and requires it to remain unchanged. Diagnostic-mode startup settings use an in-process substitute; normal runs use the Windows registry.

`scripts/measure-preset-diagnostics.ps1` compares Standard / All in both overlay renderers, alternating order across rounds. The default is three rounds, 10 seconds of warmup and 30 seconds measured per case. This is a short incremental check, not a replacement for the five-round formal baseline. Per-second checks require the actual overlay to stay visible in the expected renderer, size and DPI; every process has a fresh writable database. The `large-overlay` diagnostic marker is for text-layout validation only and must remain absent during performance measurements.
