using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **A pin flushed places.json to the disk on the UI thread.** The Windows
/// twin of this class gives the reasons; the provider here had the same
/// synchronous write behind the same sidebar clicks. The queue is held shut by
/// a write of the test's own, so a pin written by the caller shows at once and
/// one that was queued waits for the gate.
///
/// A plain fact: nothing here depends on the platform it runs on.
/// </summary>
public sealed class PinWritesOffTheCallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-pin-writes").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public async Task A_pin_waits_for_the_disk_on_the_pool_not_in_the_caller()
    {
        var state = Path.Combine(_root, "state");
        var provider = new LinuxPlacesProvider(state)
        {
            MountLines = () => [],
            FilesystemDevices = () => [],
            SwapLines = () => [],
            VolumeLabels = () => new Dictionary<string, string>(),
        };
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
