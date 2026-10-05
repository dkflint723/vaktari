using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Core;
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
/// The watches were then made one set for the process. Now they are no
/// threads at all — an asynchronous, thread-agnostic notification waited on by
/// the pool's shared wait thread (KeyWatch) — and only the keys a setting
/// follows are watched (Follow), so a key can be stopped as well as started.
///
/// The real keys are the user's settings and are never written here: the
/// event path is driven on a key this test makes under HKCU\Software and
/// deletes, and the fan-out through <see cref="WindowsThemeProvider.Notify"/>,
/// which is what a watch calls. One class, so the process-wide state is read
/// by nothing running beside it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThemeWatcherTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    public void Dispose() => new WindowsThemeProvider().Follow(ThemeNeeds.None);

    private static int Threads()
    {
        using var me = Process.GetCurrentProcess();
        return me.Threads.Count;
    }

    /// <summary>
    /// Fifty providers each asking to follow everything — the shape fifty
    /// windows have — arm each key once (review M3: with nothing armed by the
    /// constructor any more, a test that only built providers would compare
    /// zero with zero).
    /// </summary>
    [WindowsFact]
    public void Making_many_providers_starts_the_watchers_once()
    {
        new WindowsThemeProvider().Follow(ThemeNeeds.All);

        var started = WindowsThemeProvider.WatchersStarted;
        var threads = Threads();

        Assert.Equal(3, WindowsThemeProvider.Watched.Count);

        for (var i = 0; i < 50; i++) new WindowsThemeProvider().Follow(ThemeNeeds.All);

        Assert.Equal(started, WindowsThemeProvider.WatchersStarted);

        // And by the operating system's count, which the watches do not keep.
        // The margin is for the pool, which grows on its own.
        var grown = Threads() - threads;
        Assert.True(grown < 30, $"the process grew {grown} threads for fifty providers");
    }

    /// <summary>
    /// **Only the keys a setting follows are watched.** Personalize carries
    /// light or dark, which the desktop's own colours bring with them; DWM
    /// carries the accent, wanted only with those colours; Accessibility
    /// carries the text size.
    /// </summary>
    [WindowsFact]
    public void Following_watches_only_what_is_needed()
    {
        var provider = new WindowsThemeProvider();
        const string personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string dwm = @"Software\Microsoft\Windows\DWM";
        const string accessibility = @"Software\Microsoft\Accessibility";

        provider.Follow(ThemeNeeds.None);
        Assert.Empty(WindowsThemeProvider.Watched);

        provider.Follow(ThemeNeeds.Lightness);
        Assert.Equal([personalize], WindowsThemeProvider.Watched.Order());

        provider.Follow(ThemeNeeds.Lightness | ThemeNeeds.TextSize);
        Assert.Equal(new[] { accessibility, personalize }.Order(), WindowsThemeProvider.Watched.Order());

        provider.Follow(ThemeNeeds.Colours);
        Assert.Equal(new[] { dwm, personalize }.Order(), WindowsThemeProvider.Watched.Order());

        provider.Follow(ThemeNeeds.None);
        Assert.Empty(WindowsThemeProvider.Watched);
    }

    /// <summary>
    /// Two windows, each subscribed through a provider of its own, both hear
    /// one change, and a window that has closed and taken its handler back
    /// hears nothing. Only the calls made on this test's thread are counted:
    /// the event is the process's, and a watch raises it too.
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
    /// The watch itself, on a key of this test's own: a value written there
    /// reaches a provider's subscriber; deleting the key ends the watch.
    /// </summary>
    [WindowsFact]
    public async Task A_watched_key_that_changes_reaches_the_subscribers()
    {
        var subKey = NewKey();
        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        var provider = new WindowsThemeProvider();
        provider.Changed += handler;
        KeyWatch? watcher = null;

        try
        {
            watcher = WindowsThemeProvider.Watch(subKey);
            Assert.NotNull(watcher);

            Assert.True(await WriteUntilHeard(subKey, heard, "first"), "a change to the watched key never reached the subscriber");

            // A deleted key ends the watch by itself: its re-arm fails.
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            await watcher.Ended.WaitAsync(Ceiling);
        }
        finally
        {
            provider.Changed -= handler;
            await Forget(subKey, watcher);
        }
    }

    /// <summary>
    /// **A stopped watch hears nothing.** A blocked wait could not be called
    /// off, which is why every watch used to live for the process; a key the
    /// settings stop following must stop raising.
    /// </summary>
    [WindowsFact]
    public async Task Stop_silences_a_key()
    {
        var subKey = NewKey();
        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        var provider = new WindowsThemeProvider();
        provider.Changed += handler;
        KeyWatch? watcher = null;

        try
        {
            watcher = WindowsThemeProvider.Watch(subKey);
            Assert.NotNull(watcher);

            Assert.True(await WriteUntilHeard(subKey, heard, "before"), "the watch never heard the key");

            await watcher.Stop().WaitAsync(Ceiling);
            while (await heard.WaitAsync(TimeSpan.FromMilliseconds(200))) { }

            Write(subKey, "after");

            Assert.False(await heard.WaitAsync(TimeSpan.FromMilliseconds(500)), "a stopped watch still raised");
        }
        finally
        {
            provider.Changed -= handler;
            await Forget(subKey, watcher);
        }
    }

    /// <summary>
    /// **Arming from a thread that then ends must raise nothing** (review M4).
    /// Without REG_NOTIFY_THREAD_AGNOSTIC the registration belongs to the
    /// thread that made it, and the system signals the event when that thread
    /// exits — a raise for a change nobody made. Every re-arm happens on a pool
    /// thread, and pool threads retire, so in the application that is a
    /// palette re-read on an idle machine. Then a real write is still heard,
    /// once.
    /// </summary>
    [WindowsFact]
    public async Task Arming_from_a_thread_that_ends_raises_nothing()
    {
        var subKey = NewKey();
        var raised = 0;
        EventHandler handler = (_, _) => Interlocked.Increment(ref raised);

        var provider = new WindowsThemeProvider();
        provider.Changed += handler;
        KeyWatch? watcher = null;

        try
        {
            var arming = new Thread(() => watcher = WindowsThemeProvider.Watch(subKey));
            arming.Start();
            arming.Join();
            Assert.NotNull(watcher);

            await Task.Delay(500);
            Assert.Equal(0, Volatile.Read(ref raised));

            Write(subKey, "real");

            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref raised) == 0 && clock.Elapsed < Ceiling) await Task.Delay(10);
            await Task.Delay(300);

            Assert.Equal(1, Volatile.Read(ref raised));
        }
        finally
        {
            provider.Changed -= handler;
            await Forget(subKey, watcher);
        }
    }

    /// <summary>
    /// **A subscriber that throws keeps the change from nobody after it.**
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
    /// **A subscriber that throws does not end the watch.** The second change
    /// is heard only by a watch still running.
    /// </summary>
    [WindowsFact]
    public async Task A_subscriber_that_throws_does_not_end_the_watcher()
    {
        var subKey = NewKey();
        using var heard = new SemaphoreSlim(0);
        EventHandler hears = (_, _) => heard.Release();
        EventHandler throws = (_, _) => throw new InvalidOperationException("a subscriber that throws");

        var provider = new WindowsThemeProvider();
        provider.Changed += hears;
        provider.Changed += throws;
        KeyWatch? watcher = null;

        try
        {
            watcher = WindowsThemeProvider.Watch(subKey);
            Assert.NotNull(watcher);

            Assert.True(await WriteUntilHeard(subKey, heard, "first"), "the first change never reached the subscriber");

            while (await heard.WaitAsync(TimeSpan.FromMilliseconds(300))) { }

            Assert.True(await WriteUntilHeard(subKey, heard, "second"), "the watch stopped after a subscriber threw");
            Assert.False(watcher.Ended.IsCompleted, "the watch stopped after a subscriber threw");
        }
        finally
        {
            provider.Changed -= hears;
            provider.Changed -= throws;
            await Forget(subKey, watcher);
        }
    }

    private static string NewKey()
    {
        var subKey = @"Software\Vaktari-tests\theme-" + Guid.NewGuid().ToString("N");
        using (Registry.CurrentUser.CreateSubKey(subKey)) { }
        return subKey;
    }

    private static void Write(string subKey, string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true)!;
        key.SetValue("n", value);
    }

    /// <summary>Writes the key until the watch's raise is heard.</summary>
    private static async Task<bool> WriteUntilHeard(string subKey, SemaphoreSlim heard, string tag)
    {
        var deadline = DateTime.UtcNow + Ceiling;

        for (var n = 0; DateTime.UtcNow < deadline; n++)
        {
            Write(subKey, $"{tag} {n}");

            if (await heard.WaitAsync(TimeSpan.FromMilliseconds(200))) return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes a key a test watched, and waits for its watch to end: a raise
    /// from it must not land in the next test.
    /// </summary>
    private static async Task Forget(string subKey, KeyWatch? watcher)
    {
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);

        try
        {
            if (watcher is not null)
            {
                _ = watcher.Stop();
                await watcher.Ended.WaitAsync(Ceiling);
            }
        }
        finally
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Vaktari-tests", writable: true);
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\Vaktari-tests", throwOnMissingSubKey: false);
        }
    }
}
