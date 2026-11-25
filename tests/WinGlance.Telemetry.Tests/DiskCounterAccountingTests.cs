using WinGlance.Core;
using WinGlance.Windows.Telemetry;

internal static class DiskCounterAccountingTests
{
    internal static void Run(Action<object?, object?, string> equal)
    {
        Dictionary<string, DiskCounterValue> Pair(DiskCounterValue other, bool reverse, bool baseline = false)
        {
            var healthy = baseline ? new DiskCounterValue(1000, 1000) : new DiskCounterValue(1500, 2000);
            return reverse ? new() { ["other"] = other, ["healthy"] = healthy }
                : new() { ["healthy"] = healthy, ["other"] = other };
        }

        var cases = new (string Name, DiskCounterValue Old, DiskCounterValue Current, MetricState Device, MetricState Total)[]
        {
            ("byte reset", new(1000, 1000), new(900, 2000), MetricState.WarmingUp, MetricState.WarmingUp),
            ("clock reset", new(1000, 1000), new(1500, 900), MetricState.WarmingUp, MetricState.WarmingUp),
            ("stationary clock", new(1000, 1000), new(1500, 1000), MetricState.WarmingUp, MetricState.WarmingUp),
            ("long interval", new(1000, 1000), new(1500, 7001), MetricState.WarmingUp, MetricState.WarmingUp),
            ("different valid intervals", new(1000, 1000), new(1500, 3000), MetricState.Ready, MetricState.WarmingUp),
            ("invalid current status", new(1000, 1000), new(1500, 2000, 0xc0000bc6), MetricState.Error, MetricState.Error),
            ("invalid old status", new(1000, 1000, 0xc0000bc6), new(1500, 2000), MetricState.WarmingUp, MetricState.WarmingUp),
            ("invalid current numerator", new(1000, 1000), new(-1, 2000), MetricState.Error, MetricState.Error),
            ("invalid old numerator", new(-1, 1000), new(1500, 2000), MetricState.WarmingUp, MetricState.WarmingUp),
            ("timestamp subtraction overflow", new(0, long.MinValue), new(500, long.MaxValue), MetricState.Error, MetricState.Error),
            ("aggregate byte overflow", new(0, 1000), new(long.MaxValue, 2000), MetricState.Ready, MetricState.Error),
            ("valid zero", new(1000, 1000), new(1000, 2000), MetricState.Ready, MetricState.Ready),
        };
        foreach (var test in cases)
        {
            MetricAccounting? firstTotal = null;
            Dictionary<string, MetricAccounting>? firstDevices = null;
            foreach (bool reverse in new[] { false, true })
            {
                var total = DiskCounterAccounting.Calculate(Pair(test.Current, reverse), Pair(test.Old, !reverse, true),
                    1000, 1, true, out var devices);
                if (!reverse)
                {
                    equal(devices["healthy"].State, MetricState.Ready, $"{test.Name}: healthy disk stays ready");
                    equal(devices["healthy"].BytesDelta, 500L, $"{test.Name}: healthy delta survives");
                    equal(devices["other"].State, test.Device, $"{test.Name}: affected disk status");
                    equal(total.State, test.Total, $"{test.Name}: complete total status");
                    if (test.Total != MetricState.Ready)
                        equal(total.BytesDelta, null, $"{test.Name}: incomplete total cannot become zero");
                    if (test.Device != MetricState.Ready)
                        equal(devices["other"].BytesDelta, null, $"{test.Name}: invalid device cannot become zero");
                    firstTotal = total;
                    firstDevices = devices;
                }
                else
                {
                    equal(total, firstTotal, $"{test.Name}: aggregate is independent of enumeration order");
                    equal(devices["healthy"], firstDevices!["healthy"], $"{test.Name}: healthy result is order independent");
                    equal(devices["other"], firstDevices["other"], $"{test.Name}: affected result is order independent");
                }
            }
        }

        var before = Pair(new(1000, 1000), false, true);
        var now = Pair(new(1500, 2000), false);
        var onlyHealthyBefore = new Dictionary<string, DiskCounterValue> { ["healthy"] = before["healthy"] };
        var onlyHealthyNow = new Dictionary<string, DiskCounterValue> { ["healthy"] = now["healthy"] };
        var added = DiskCounterAccounting.Calculate(now, onlyHealthyBefore, 1000, 1, true, out var addedDevices);
        equal(added.State, MetricState.WarmingUp, "new disk prevents incomplete aggregate");
        equal(addedDevices["other"].State, MetricState.WarmingUp, "new disk establishes its own baseline");
        equal(addedDevices["healthy"].BytesDelta, 500L, "new disk preserves existing disk delta");
        var removed = DiskCounterAccounting.Calculate(onlyHealthyNow, before, 1000, 1, true, out var removedDevices);
        equal(removed.State, MetricState.WarmingUp, "removed disk prevents incomplete aggregate");
        equal(removedDevices["healthy"].BytesDelta, 500L, "removed disk preserves existing disk delta");
        equal(removedDevices.ContainsKey("other"), false, "removed disk is not a zero observation");

        var paused = DiskCounterAccounting.Calculate(now, before, 1000, 1, false, out var pausedDevices);
        equal(paused.State, MetricState.WarmingUp, "global discontinuity invalidates total");
        equal(pausedDevices.Values.All(x => x.State == MetricState.WarmingUp && x.BytesDelta is null), true,
            "global discontinuity invalidates all device intervals");
        var zero = DiskCounterAccounting.Calculate(before, before.ToDictionary(x => x.Key, x => x.Value with { Timestamp = 0 }),
            1000, 1, true, out var zeroDevices);
        equal(zero.State, MetricState.Ready, "all-zero traffic is a valid observation");
        equal(zero.BytesDelta, 0L, "all-zero traffic retains exact zero total");
        equal(zeroDevices.Values.All(x => x.State == MetricState.Ready && x.BytesDelta == 0), true,
            "all-zero disks retain valid zero deltas");
        var reset = Pair(new(0, 2000), false);
        var recovery = new Dictionary<string, DiskCounterValue> { ["healthy"] = new(2000, 3000), ["other"] = new(300, 3000) };
        var recovered = DiskCounterAccounting.Calculate(recovery, reset, 1000, 1, true, out var recoveredDevices);
        equal(recoveredDevices["other"].BytesDelta, 300L, "reset disk recovers from next baseline");
        equal(recovered.State, MetricState.Ready, "all disks recover together after new baselines");
        equal(recovered.BytesDelta, 800L, "recovered aggregate includes both disks");
    }
}
