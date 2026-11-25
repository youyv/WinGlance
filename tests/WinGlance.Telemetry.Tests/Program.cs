using WinGlance.Core;
using WinGlance.Windows.Telemetry;
static void Equal(object? actual, object? expected, string test)
{
    if (!Equals(actual, expected)) throw new Exception($"{test}: {actual} != {expected}");
    Console.WriteLine("PASS " + test);
}

var map = new ProcessLifetimeMap();
var old = new ProcessIdentity(42, 100);
var next = new ProcessIdentity(42, 200);
map.ObserveSnapshot([old], 150);
Equal(map.Resolve(42, 99), null, "before process creation stays unassigned");
Equal(map.Resolve(42, 150), old, "existing snapshot process");

map.ObserveStart(42, 202, 2, next);
Equal(map.Resolve(42, 199), old, "late old event stays old generation");
Equal(map.Resolve(42, 201), next, "new traffic gets new generation");
map.ObserveStop(42, 190, 1);
Equal(map.Resolve(42, 195), null, "stopped old lifetime cannot receive traffic");
Equal(map.Resolve(42, 210), next, "late old stop cannot close new lifetime");

map.ObserveStart(42, 300, 3, null);
Equal(map.Resolve(42, 301), null, "unresolved new process fences previous generation");
map.ObserveSnapshot([next], 310);
Equal(map.Resolve(42, 301), null, "stale snapshot cannot resurrect old process");
var third = new ProcessIdentity(42, 299);
map.ObserveSnapshot([third], 315);
Equal(map.Resolve(42, 301), third, "exact creation time upgrades unknown generation");

map.ObserveStart(42, 400, 4, new ProcessIdentity(42, 500));
Equal(map.Resolve(42, 401), null, "delayed start opening replacement PID stays unknown");
map.Invalidate();
Equal(map.Resolve(42, 301), null, "event loss invalidates attribution");

var retained = new ProcessIdentity(44, 100);
map.ObserveSnapshot([retained], TimeSpan.FromSeconds(20).Ticks);
map.ObserveSnapshot([], TimeSpan.FromSeconds(36).Ticks);
Equal(map.Resolve(44, TimeSpan.FromSeconds(35).Ticks), null, "missing stop cannot retain stale ownership forever");
map.ObserveSnapshot([old], 150);
map.ObserveStart(42, 202, 2, next);
map.ObserveStop(42, 250, 2);
map.ObserveSnapshot([next], 260);
Equal(map.Resolve(42, 260), null, "snapshot cannot reopen a known stopped lifetime");
