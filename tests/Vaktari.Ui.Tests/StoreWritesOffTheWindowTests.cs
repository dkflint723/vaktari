using Vaktari.Core.Settings;
using Vaktari.Core.Sharing;
using Vaktari.Ui.Settings;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where a store's save waits for the disk: on the pool, never in the
/// window's own handler.
///
/// **Every store flushed to the disk on the UI thread.** The flush before the
/// rename is what keeps a power cut from leaving a file of zeros, and it waits
/// for the device to confirm — on a portable copy's stick, a network share or
/// a busy ext4 journal, long enough to freeze the window at the end of a
/// column drag, after a settings OK or after a link was made. Only the session
/// store had moved its flush off that thread.
///
/// Each test holds the store's queue shut with a write of its own, then
/// saves: a save written by the caller lands at once, and one that goes
/// through the queue waits for the gate. Nothing here can be satisfied by a
/// fast disk.
/// </summary>
public sealed class StoreWritesOffTheWindowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-store-writes-" + Guid.NewGuid().ToString("N")[..12]);

    public StoreWritesOffTheWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a temp dir is not worth failing over */ }
    }

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private static readonly DriveLink Link =
        new(@"D:\Proton-Drive\a.txt", "/my-files/a.txt", "https://drive.proton.me/urls/A#k");

    [Fact]
    public async Task A_settings_save_waits_for_the_disk_on_the_pool_not_in_the_caller()
    {
        var store = new JsonSettingsStore(_root);
        using var gate = new ManualResetEventSlim();
        _ = store.Writes.Enqueue(() => gate.Wait(Ceiling));

        var saving = store.SaveAsync(new SettingsState());

        Assert.False(File.Exists(store.FilePath), "the caller wrote the settings itself, disk wait and all");

        gate.Set();
        await saving;

        Assert.True(File.Exists(store.FilePath), "the queued save never landed");
    }

    [Fact]
    public async Task A_drive_link_save_waits_for_the_disk_on_the_pool_not_in_the_caller()
    {
        var store = new JsonDriveLinkStore(_root);
        var file = Path.Combine(_root, "drive-links.json");
        using var gate = new ManualResetEventSlim();
        _ = store.Writes.Enqueue(() => gate.Wait(Ceiling));

        var saving = store.SaveAsync([Link]);

        Assert.False(File.Exists(file), "the caller wrote the links itself, disk wait and all");

        gate.Set();
        await saving;

        Assert.True(File.Exists(file), "the queued save never landed");
    }

    /// <summary>
    /// **A window opened a moment after a save read the list from before it.**
    /// Each window reads the links file as it opens, and with the save still
    /// in flight it would read the older list — and write that back over the
    /// new link with its own next save. Loading waits for what is queued.
    /// </summary>
    [Fact]
    public async Task Reading_the_links_waits_for_a_save_still_in_flight()
    {
        var store = new JsonDriveLinkStore(_root);
        using var gate = new ManualResetEventSlim();
        _ = store.Writes.Enqueue(() => gate.Wait(Ceiling));

        var saving = store.SaveAsync([Link]);
        var loading = Task.Run(() => store.Load());

        await Task.WhenAny(loading, Task.Delay(300, TestContext.Current.CancellationToken));

        Assert.False(loading.IsCompleted, "the links were read before the save had landed");

        gate.Set();
        await saving;

        Assert.Equal(new[] { Link }, await loading);
    }

    /// <summary>
    /// The window's own handlers are what ran on the UI thread, and the way
    /// out is what must still find everything on the disk. Read from the
    /// source, because each is one call in a constructor or a close that no
    /// view model can be asked about.
    /// </summary>
    [Fact]
    public void The_window_saves_through_the_queue_and_the_way_out_waits_for_it()
    {
        var window = RepoSource.Ui("MainWindow.axaml.cs") + RepoSource.Ui("MainWindow.Settings.cs");

        Assert.DoesNotContain("SettingsStore.Save(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("DriveLinkStore.Save(", window, StringComparison.Ordinal);

        var services = RepoSource.Ui("WindowServices.cs");

        Assert.Contains("await SettingsStore.Writes.Idle;", services, StringComparison.Ordinal);
        Assert.Contains("await DriveLinkStore.Writes.Idle;", services, StringComparison.Ordinal);
        Assert.Contains("await Platform.Places.Written;", services, StringComparison.Ordinal);

        var flushes = services.IndexOf("FolderViews.Flush();", StringComparison.Ordinal);
        var pool = services.LastIndexOf("await Task.Run(() =>", flushes, StringComparison.Ordinal);

        Assert.True(flushes > 0 && pool > 0 && flushes - pool < 80,
            "the per-application stores are flushed on the UI thread on the way out");
    }
}
