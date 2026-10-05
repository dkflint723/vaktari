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
/// hidden top-level window is Windows' documented behaviour, checked by hand
/// with a stick (see the release notes).
/// </summary>
[SupportedOSPlatform("windows")]
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
            Assert.True(Threads() - threads < 10, $"twenty subscribers grew {Threads() - threads} threads");
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
