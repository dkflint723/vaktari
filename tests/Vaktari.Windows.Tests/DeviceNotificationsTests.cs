using System.Diagnostics;
using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The hidden window that hears Windows announce a device change, in place of
/// a look every second.
///
/// **What a test can prove and what it cannot.** A real arrival needs a device
/// plugged in, which no test does; and WM_DEVICECHANGE's arrival messages
/// carry a pointer, so Windows refuses to let anyone post one
/// (ERROR_MESSAGE_SYNC_ONLY, measured). DBT_DEVNODES_CHANGED carries none and
/// is broadcast the same way, so posting it to the window's own handle drives
/// the same window procedure a broadcast does. That the broadcast reaches a
/// hidden top-level window is Windows' documented behaviour, and NOT yet
/// measured here: it is a hand check with a real stick, owed before release
/// (QA, qa/streamline — no commit or note records one having been done).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(DeviceNotificationsCollection.Name)]
public sealed class DeviceNotificationsTests
{
    private const nint DBT_DEVNODES_CHANGED = 0x0007;

    private static int Threads()
    {
        using var me = Process.GetCurrentProcess();
        return me.Threads.Count;
    }

    /// <summary>
    /// Every subscriber hears a change, and one that has left does not. One
    /// window for the process, however many subscribe (the first shape, a
    /// window and a thread per provider, leaked a thread for every window a
    /// test built).
    /// </summary>
    [WindowsFact]
    public void Every_subscriber_hears_a_device_change_and_a_departed_one_does_not()
    {
        using var first = new SemaphoreSlim(0);
        var departed = 0;

        using var a = DeviceNotifications.Start(() => first.Release());
        var b = DeviceNotifications.Start(() => Interlocked.Increment(ref departed));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(1, DeviceNotifications.WindowsMade);

        b.Dispose();

        Assert.True(DeviceNotifications.PostMessageW(DeviceNotifications.Hwnd, DeviceNotifications.WM_DEVICECHANGE, DBT_DEVNODES_CHANGED, 0));

        Assert.True(first.Wait(TimeSpan.FromSeconds(10)), "the window procedure never nudged");
        Thread.Sleep(200);
        Assert.Equal(0, Volatile.Read(ref departed));
    }

    [WindowsFact]
    public void The_window_is_never_shown()
    {
        using var listener = DeviceNotifications.Start(() => { });

        Assert.NotNull(listener);
        Assert.False(DeviceNotifications.IsWindowVisible(DeviceNotifications.Hwnd));
    }

    /// <summary>Twenty subscribers, one window and no thread each: the leak
    /// WindowThreadsTests found, pinned here where it is cheap to see.</summary>
    [WindowsFact]
    public void Many_subscribers_share_one_window_and_one_thread()
    {
        using (DeviceNotifications.Start(() => { })) { }

        var threads = Threads();
        var made = DeviceNotifications.WindowsMade;
        var subscriptions = Enumerable.Range(0, 20).Select(_ => DeviceNotifications.Start(() => { })).ToList();

        try
        {
            Assert.All(subscriptions, Assert.NotNull);
            Assert.Equal(made, DeviceNotifications.WindowsMade);
            var grown = Threads() - threads;
            Assert.True(grown < 10, $"twenty subscribers grew {grown} threads");
        }
        finally
        {
            foreach (var s in subscriptions) s?.Dispose();
        }
    }

    /// <summary>
    /// The provider wires it: a started provider runs the watch on the slow
    /// floor, which it does only when the window was made.
    /// </summary>
    [WindowsFact]
    public void A_started_provider_listens_and_slows_its_floor()
    {
        var state = Directory.CreateTempSubdirectory("vaktari-devnotify").FullName;

        try
        {
            using var places = new WindowsPlacesProvider(state);
            places.Start();

            Assert.Equal(TimeSpan.FromSeconds(30), places.WatchIntervalForTests);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }
}

/// <summary>
/// **Counts the process's threads, so it runs alone** (QA, qa/streamline).
/// Beside the other classes, a pool that grew for someone else's blocked
/// work — ThemeWatcherTests holds a pool thread in a raise on purpose — was
/// charged to twenty subscribers: "grew 5 threads" in the message, ten or
/// more at the assert, one full run in seven. The window count beside it is
/// the exact check; the thread count is the leak's own symptom, and only
/// means something with nothing else running.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeviceNotificationsCollection
{
    public const string Name = "DeviceNotifications";
}
