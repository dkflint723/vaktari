using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A pin flushed places.json to the disk on the UI thread.** Pin, unpin,
/// rename and reorder are sidebar clicks and the end of a drag, and each one
/// waited there for the device to confirm the write. They go through the
/// provider's queue on the pool now, and the task they return completes when
/// the file is down — which a caller awaiting it learns no later than before.
///
/// The queue is held shut by a write of the test's own, so a pin written by
/// the caller shows at once and one that was queued waits for the gate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PinWritesOffTheCallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-pin-writes").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a temp dir is not worth failing over */ }
    }

    [WindowsFact]
    public async Task A_pin_waits_for_the_disk_on_the_pool_not_in_the_caller()
    {
        var state = Path.Combine(_root, "state");
        var provider = new WindowsPlacesProvider(state);
        var file = Path.Combine(state, "places.json");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Later")).FullName;

        using var gate = new ManualResetEventSlim();
        _ = provider.Writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));

        var pinning = provider.PinAsync(folder, null, CancellationToken.None).AsTask();

        Assert.False(File.Exists(file), "the caller wrote places.json itself, disk wait and all");

        gate.Set();
        await pinning;

        Assert.Contains("Later", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
