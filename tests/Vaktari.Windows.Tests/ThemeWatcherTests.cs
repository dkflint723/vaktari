using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **Every window started three threads that never ended.** Each
/// WindowsThemeProvider watched the three registry keys itself, one thread
/// each, blocked in RegNotifyChangeKeyValue — a wait nothing can call off — and
/// every window that built its own services built a provider. Measured:
/// opening and closing thirty windows took a test process from 14 threads to
/// 120 (batch-0.11.2 notes, item5), and a full Ui run ended near 740.
///
/// The watchers are now one set for the process, and the change event is
/// shared: whichever provider a window subscribed through, a change reaches it.
///
/// The real keys are the user's settings and are never written here: the
/// thread-to-event path is driven on a key this test makes under
/// HKCU\Software and deletes, and the fan-out through
/// <see cref="WindowsThemeProvider.Notify"/>, which is what a watcher calls.
/// One class, so the process-wide count is read by nothing running beside it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThemeWatcherTests
{
    private static int Threads()
    {
        using var me = Process.GetCurrentProcess();
        return me.Threads.Count;
    }

    [WindowsFact]
    public void Making_many_providers_starts_the_watchers_once()
    {
        _ = new WindowsThemeProvider();

        var started = WindowsThemeProvider.WatchersStarted;
        var threads = Threads();

        for (var i = 0; i < 50; i++) _ = new WindowsThemeProvider();

        Assert.Equal(started, WindowsThemeProvider.WatchersStarted);

        // And by the operating system's count, which the watchers do not keep:
        // fifty providers each starting their own would be 150 more threads.
        // The margin is for the pool, which grows on its own.
        var grown = Threads() - threads;
        Assert.True(grown < 30, $"the process grew {grown} threads for fifty providers");
    }

    /// <summary>
    /// Two windows, each subscribed through a provider of its own — the shape
    /// two windows with services of their own have — both hear one change,
    /// and a window that has closed and taken its handler back hears nothing.
    ///
    /// Only the calls made on this test's thread are counted. The event is the
    /// process's, so a watcher thread raises it too: the previous test's key,
    /// deleted as that test ends, wakes its watcher once more before the wait
    /// fails, and that late raise landed here on CI as [2, 2]. Notify raises on
    /// the thread that calls it, so the count is exactly this test's.
    /// </summary>
    [WindowsFact]
    public void A_change_reaches_every_subscriber_of_every_provider_and_no_departed_one()
    {
        var first = new WindowsThemeProvider();
        var second = new WindowsThemeProvider();

        var me = Environment.CurrentManagedThreadId;
        var heard = new int[2];
        EventHandler one = (_, _) => { if (Environment.CurrentManagedThreadId == me) Interlocked.Increment(ref heard[0]); };
        EventHandler two = (_, _) => { if (Environment.CurrentManagedThreadId == me) Interlocked.Increment(ref heard[1]); };

        first.Changed += one;
        second.Changed += two;

        try
        {
            WindowsThemeProvider.Notify();

            Assert.Equal([1, 1], heard);

            second.Changed -= two;

            WindowsThemeProvider.Notify();

            Assert.Equal([2, 1], heard);
        }
        finally
        {
            first.Changed -= one;
            second.Changed -= two;
        }
    }

    /// <summary>
    /// The watcher itself, on a key of this test's own: a value written there
    /// wakes it and reaches a provider's subscriber; deleting the key ends the
    /// wait, and the thread with it.
    /// </summary>
    [WindowsFact]
    public void A_watched_key_that_changes_reaches_the_subscribers()
    {
        var subKey = @"Software\Vaktari-tests\theme-" + Guid.NewGuid().ToString("N");
        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        var provider = new WindowsThemeProvider();
        provider.Changed += handler;
        Thread? watcher = null;

        try
        {
            using (Registry.CurrentUser.CreateSubKey(subKey)) { }

            watcher = WindowsThemeProvider.Watch(subKey);
            Assert.NotNull(watcher);

            // The thread arms its wait a moment after it starts; a write
            // before that is not seen, so write until one is.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var woke = false;

            for (var n = 0; !woke && DateTime.UtcNow < deadline; n++)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true)!)
                    key.SetValue("n", n);

                woke = heard.Wait(TimeSpan.FromMilliseconds(200));
            }

            Assert.True(woke, "a change to the watched key never reached the subscriber");
        }
        finally
        {
            provider.Changed -= handler;
            Forget(subKey, watcher);
        }
    }

    /// <summary>
    /// Deletes a key a test watched, and waits for its watcher to end: the
    /// delete wakes the wait once or twice before it fails, and each wake
    /// raises the process's event, which must not land in the next test.
    /// </summary>
    private static void Forget(string subKey, Thread? watcher)
    {
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);

        try
        {
            Assert.True(watcher?.Join(TimeSpan.FromSeconds(15)) ?? true, "the watcher outlived its deleted key");
        }
        finally
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Vaktari-tests", writable: true);
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\Vaktari-tests", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// **A subscriber that throws keeps the change from nobody after it.**
    /// Notify was a plain Invoke, so the throw left it at that handler: the
    /// windows subscribed later never heard the change. Counted on this test's
    /// thread only, as the test above counts.
    /// </summary>
    [WindowsFact]
    public void A_subscriber_that_throws_does_not_keep_the_change_from_the_ones_after_it()
    {
        var provider = new WindowsThemeProvider();

        var me = Environment.CurrentManagedThreadId;
        var heard = 0;
        EventHandler throws = (_, _) => throw new InvalidOperationException("a subscriber that throws");
        EventHandler after = (_, _) => { if (Environment.CurrentManagedThreadId == me) Interlocked.Increment(ref heard); };

        provider.Changed += throws;
        provider.Changed += after;

        try
        {
            WindowsThemeProvider.Notify();

            Assert.Equal(1, heard);
        }
        finally
        {
            provider.Changed -= throws;
            provider.Changed -= after;
        }
    }

    /// <summary>
    /// **A subscriber that throws does not end the watcher.** The throw used
    /// to escape Notify into the watcher thread's catch, which swallowed it
    /// and ended the thread, so the process stopped hearing that key for good.
    /// Here the subscriber that hears comes first and the one that throws
    /// after it, so the first change is heard either way; only a watcher still
    /// running hears the second.
    /// </summary>
    [WindowsFact]
    public void A_subscriber_that_throws_does_not_end_the_watcher()
    {
        var subKey = @"Software\Vaktari-tests\theme-" + Guid.NewGuid().ToString("N");
        using var heard = new SemaphoreSlim(0);
        EventHandler hears = (_, _) => heard.Release();
        EventHandler throws = (_, _) => throw new InvalidOperationException("a subscriber that throws");

        var provider = new WindowsThemeProvider();
        provider.Changed += hears;
        provider.Changed += throws;
        Thread? watcher = null;

        try
        {
            using (Registry.CurrentUser.CreateSubKey(subKey)) { }

            watcher = WindowsThemeProvider.Watch(subKey);
            Assert.NotNull(watcher);

            Assert.True(WriteUntilHeard(subKey, heard, "first"), "the first change never reached the subscriber");

            // Drained, so the second wake is the second change's own.
            while (heard.Wait(TimeSpan.FromMilliseconds(300))) { }

            Assert.True(WriteUntilHeard(subKey, heard, "second"), "the watcher stopped after a subscriber threw");
            Assert.True(watcher.IsAlive, "the watcher stopped after a subscriber threw");
        }
        finally
        {
            provider.Changed -= hears;
            provider.Changed -= throws;
            Forget(subKey, watcher);
        }
    }

    /// <summary>Writes the key until the watcher's raise is heard: it arms its
    /// wait a moment after it starts, and again after each raise.</summary>
    private static bool WriteUntilHeard(string subKey, SemaphoreSlim heard, string tag)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        for (var n = 0; DateTime.UtcNow < deadline; n++)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true)!)
                key.SetValue("n", $"{tag} {n}");

            if (heard.Wait(TimeSpan.FromMilliseconds(200))) return true;
        }

        return false;
    }
}