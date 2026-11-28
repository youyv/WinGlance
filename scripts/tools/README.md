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
