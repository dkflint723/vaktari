using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **Every window left an inotify instance behind it, for good.** Each
/// KdeThemeProvider made a FileSystemWatcher of its own on the config folder —
/// one inotify instance on Linux — and nothing disposed it, while LinuxPlatform
/// builds a provider for every window that builds its own services. Measured
/// (batch-0.11.2 QA, item G): thirty windows opened and closed with a config
/// folder present took a test process from 0 instances to 30, each still
/// raising, against a per-user ceiling of 128.
///
/// The watcher is now one for the process and the change event is shared, the
/// shape WindowsThemeProvider took for its registry threads: whichever
/// provider a window subscribed through, a change reaches it, and a handler
/// taken back hears nothing more.
///
/// The user's own config folder is never watched or written: every provider
/// here is built over a temporary folder, and the one test that sets
/// XDG_CONFIG_HOME sets it to one and puts it back. This assembly runs its
/// classes one at a time (Parallelism.cs), so nothing reads the variable
/// meanwhile, and nothing else counts the process's inotify instances.
/// </summary>
public sealed class KdeThemeWatcherTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-kdewatch").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static int InotifyInstances() => InotifyCount.Instances();

    [PosixFact]
    public void Making_many_providers_does_not_grow_the_inotify_instances()
    {
        // The count can see an instance at all: one opened here is one more,
        // and gone again once its last watch is disposed. Without this, a
        // count that read zero whatever happened would pass the assertion
        // below. (The watcher itself shares the process's one instance with
        // the panes now, so making it need not add one.)
        var before = InotifyInstances();

        var own = Inotify.Open();

        using (own.Add(_root, _ => { }))
        {
            Assert.Equal(before + 1, InotifyInstances());
        }

        Assert.True(own.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
        Assert.Equal(before, InotifyInstances());

        // Whatever starts once per process has started after this one.
        _ = new KdeThemeProvider(_root);

        var started = KdeThemeProvider.WatchersStarted;
        var instances = InotifyInstances();

        // Kept, as a window keeps its provider for as long as it lives: a
        // watcher nothing holds is closed by its finalizer at the next
        // collection, which would hide the very thing counted here.
        var providers = new List<KdeThemeProvider>();

        for (var i = 0; i < 30; i++) providers.Add(new KdeThemeProvider(_root));

        Assert.Equal(instances, InotifyInstances());
        Assert.Equal(started, KdeThemeProvider.WatchersStarted);

        GC.KeepAlive(providers);
    }

    /// <summary>
    /// Two windows, each subscribed through a provider of its own — the shape
    /// two windows with services of their own have — both hear one change,
    /// and one that has taken its handler back hears nothing.
    ///
    /// Only the calls made on this test's thread are counted. The event is the
    /// process's, so a watcher raises it too — a late change from the tests
    /// beside this one, which write kdeglobals and delete their folder. The
    /// Windows twin of this test failed on CI that way, [2, 2]. Notify raises
    /// on the thread that calls it, so the count is exactly this test's.
    /// </summary>
    [Fact]
    public void A_change_reaches_every_subscriber_of_every_provider_and_no_departed_one()
    {
        var first = new KdeThemeProvider(_root);
        var second = new KdeThemeProvider(_root);

        var me = Environment.CurrentManagedThreadId;
        var heard = new int[2];
        EventHandler one = (_, _) => { if (Environment.CurrentManagedThreadId == me) Interlocked.Increment(ref heard[0]); };
        EventHandler two = (_, _) => { if (Environment.CurrentManagedThreadId == me) Interlocked.Increment(ref heard[1]); };

        first.Changed += one;
        second.Changed += two;

        try
        {
            KdeThemeProvider.Notify();

            Assert.Equal([1, 1], heard);

            second.Changed -= two;

            KdeThemeProvider.Notify();

            Assert.Equal([2, 1], heard);
        }
        finally
        {
            first.Changed -= one;
            second.Changed -= two;
        }
    }

    /// <summary>
    /// **A subscriber that throws keeps the change from nobody after it.**
    /// Notify was a plain Invoke, so the throw left it at that handler and the
    /// windows subscribed later never heard Plasma change. Counted on this
    /// test's thread only, as the test above counts.
    /// </summary>
    [Fact]
    public void A_subscriber_that_throws_does_not_keep_the_change_from_the_ones_after_it()
    {
        var provider = new KdeThemeProvider(_root);

        var me = Environment.CurrentManagedThreadId;
        var heard = 0;
        EventHandler throws = (_, _) => throw new InvalidOperationException("a subscriber that throws");
        EventHandler after = (_, _) => { if (Environment.CurrentManagedThreadId == me) Interlocked.Increment(ref heard); };

        provider.Changed += throws;
        provider.Changed += after;

        try
        {
            KdeThemeProvider.Notify();

            Assert.Equal(1, heard);
        }
        finally
        {
            provider.Changed -= throws;
            provider.Changed -= after;
        }
    }

    /// <summary>
    /// **The watcher a provider starts is the one that tells its
    /// subscribers** (batch-0.11.2b QA). Every other test here makes a
    /// watcher of its own through Watch, so the provider's constructor could
    /// stop starting one at all — and the theme stop following Plasma — with
    /// every test still green. The process's watcher is set aside for the
    /// length of this test, so the provider made here is the first, starts it
    /// over a temporary folder, and a kdeglobals written there reaches a
    /// subscriber with nothing else watching. Put back after, and the one
    /// made here disposed.
    /// </summary>
    [PosixFact]
    public void The_first_provider_starts_the_watcher_its_subscribers_hear()
    {
        var field = typeof(KdeThemeProvider).GetField(
            "_watcher", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var gate = (Lock)typeof(KdeThemeProvider).GetField(
            "Gate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;

        var configHome = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var kdeglobals = Path.Combine(configHome, "kdeglobals");

        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        object? kept;
        lock (gate)
        {
            kept = field.GetValue(null);
            field.SetValue(null, null);
        }

        ConfigFolderWatch? started = null;

        try
        {
            var provider = new KdeThemeProvider(configHome);

            started = (ConfigFolderWatch?)field.GetValue(null);
            Assert.NotNull(started);
            Assert.Equal(configHome, started.Folder);

            provider.Changed += handler;

            try
            {
                var deadline = DateTime.UtcNow + Ceiling;
                var woke = false;

                for (var n = 0; !woke && DateTime.UtcNow < deadline; n++)
                {
                    File.WriteAllLines(kdeglobals, ["[KDE]", "SingleClick=true", "# " + n]);
                    woke = heard.Wait(TimeSpan.FromMilliseconds(200));
                }

                Assert.True(woke, "the provider's own watcher never told its subscriber");
            }
            finally
            {
                provider.Changed -= handler;
            }
        }
        finally
        {
            lock (gate)
            {
                field.SetValue(null, kept);
            }

            started?.Dispose();
        }
    }

    /// <summary>
    /// The watcher itself, over a temporary XDG_CONFIG_HOME: kdeglobals
    /// written there wakes it and reaches the subscriber of a provider that
    /// read that folder from the variable, and that provider reads what was
    /// written. The process's own watcher may already be on another folder
    /// (the first provider made in this process chose it), so this one is
    /// made here, as the first provider would make it, and disposed after.
    /// </summary>
    [PosixFact]
    public void A_written_kdeglobals_reaches_the_subscribers()
    {
        var configHome = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        var kdeglobals = Path.Combine(configHome, "kdeglobals");
        var was = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        try
        {
            var provider = new KdeThemeProvider();
            provider.Changed += handler;

            try
            {
                using var watcher = KdeThemeProvider.Watch(configHome);
                Assert.NotNull(watcher);

                // inotify is armed by the time EnableRaisingEvents returns, but
                // write until heard rather than trust one write to land.
                var deadline = DateTime.UtcNow + Ceiling;
                var woke = false;

                for (var n = 0; !woke && DateTime.UtcNow < deadline; n++)
                {
                    File.WriteAllLines(kdeglobals,
                    [
                        "[Colors:View]",
                        "BackgroundNormal=20,22,24",
                        "[KDE]",
                        "SingleClick=true",
                        "# " + n,
                    ]);

                    woke = heard.Wait(TimeSpan.FromMilliseconds(200));
                }

                Assert.True(woke, "writing kdeglobals never reached the subscriber");

                var palette = provider.Read();

                Assert.NotNull(palette);
                Assert.True(palette.IsDark);
                Assert.True(palette.SingleClick);
            }
            finally
            {
                provider.Changed -= handler;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", was);
        }
    }

    // ---- the config folder going and coming back (batch-0.11.2b QA, F4) -------

    private static readonly System.Reflection.FieldInfo WatcherField = typeof(KdeThemeProvider).GetField(
        "_watcher", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

    private static readonly Lock WatcherGate = (Lock)typeof(KdeThemeProvider).GetField(
        "Gate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;

    /// <summary>Sets the process's watcher aside, so the next provider made
    /// is the first; what was there is handed back for <see cref="PutBack"/>.</summary>
    private static object? SetAside()
    {
        lock (WatcherGate)
        {
            var kept = WatcherField.GetValue(null);
            WatcherField.SetValue(null, null);
            return kept;
        }
    }

    /// <summary>Puts the process's watcher back, disposing the one a test
    /// started in its place.</summary>
    private static void PutBack(object? kept)
    {
        ConfigFolderWatch? made;

        lock (WatcherGate)
        {
            made = (ConfigFolderWatch?)WatcherField.GetValue(null);
            WatcherField.SetValue(null, kept);
        }

        if (!ReferenceEquals(made, kept)) made?.Dispose();
    }

    private static ConfigFolderWatch? Current()
    {
        lock (WatcherGate) return (ConfigFolderWatch?)WatcherField.GetValue(null);
    }

    /// <summary>Writes kdeglobals in <paramref name="configHome"/> until the
    /// subscriber hears, and answers whether it did.</summary>
    private static bool Hears(string configHome, SemaphoreSlim heard)
    {
        while (heard.Wait(0)) { }

        var deadline = DateTime.UtcNow + Ceiling;

        for (var n = 0; DateTime.UtcNow < deadline; n++)
        {
            File.WriteAllLines(Path.Combine(configHome, "kdeglobals"), ["[KDE]", "SingleClick=true", "# " + n]);

            if (heard.Wait(TimeSpan.FromMilliseconds(200))) return true;
        }

        return false;
    }

    private static bool Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Ceiling;

        while (!done())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(20);
        }

        return true;
    }

    /// <summary>
    /// **A config folder deleted and made again is heard from again**
    /// (batch-0.11.2b QA, KdeGoneProbe: heard before, never after, by the same
    /// watcher or a new window's). The watch hears the folder go, waits for it
    /// in the folder above, and watches it again when it is back — the same
    /// watcher, on the process's one inotify instance, with no instance left
    /// behind.
    /// </summary>
    [PosixFact]
    public void A_config_folder_deleted_and_made_again_is_heard_again()
    {
        var configHome = Directory.CreateDirectory(Path.Combine(_root, "gone", "config")).FullName;

        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        var kept = SetAside();

        try
        {
            var provider = new KdeThemeProvider(configHome);
            var started = Current();

            Assert.NotNull(started);
            provider.Changed += handler;

            try
            {
                Assert.True(Hears(configHome, heard), "not heard before the folder went");

                var instances = InotifyInstances();

                Directory.Delete(configHome, recursive: true);
                Assert.True(Until(() => !started.OnFolder), "the folder going was not heard");

                Directory.CreateDirectory(configHome);
                Assert.True(Until(() => started.OnFolder), "the folder coming back was not heard");

                Assert.True(Hears(configHome, heard), "not heard after the folder was made again");

                _ = new KdeThemeProvider(configHome);

                Assert.Same(started, Current());
                Assert.False(started.Lapsed);
                Assert.Equal(instances, InotifyInstances());
            }
            finally
            {
                provider.Changed -= handler;
            }
        }
        finally
        {
            PutBack(kept);
        }
    }

    /// <summary>
    /// **One whose folder above went too lapses, and the next provider starts
    /// a new one** — as a provider did when there was none. Here the folder
    /// above the config folder is deleted with it, so there is nothing left
    /// to wait in.
    /// </summary>
    [PosixFact]
    public void A_lapsed_watcher_is_started_again_by_the_next_provider()
    {
        var above = Path.Combine(_root, "above");
        var configHome = Directory.CreateDirectory(Path.Combine(above, "config")).FullName;

        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        var kept = SetAside();

        try
        {
            var provider = new KdeThemeProvider(configHome);
            var started = Current();

            Assert.NotNull(started);
            provider.Changed += handler;

            try
            {
                Directory.Delete(above, recursive: true);
                Assert.True(Until(() => started.Lapsed), "the watcher did not lapse with both folders gone");

                Directory.CreateDirectory(configHome);
                _ = new KdeThemeProvider(configHome);

                var again = Current();

                Assert.NotNull(again);
                Assert.NotSame(started, again);
                Assert.True(Hears(configHome, heard), "the new watcher was not heard");
            }
            finally
            {
                provider.Changed -= handler;
            }
        }
        finally
        {
            PutBack(kept);
        }
    }

    /// <summary>
    /// **A kdeglobals renamed into place is heard** — the way an atomic save
    /// lands a new copy over the old, and a rename the FileSystemWatcher's
    /// Changed and Created handlers never answered.
    /// </summary>
    [PosixFact]
    public void A_kdeglobals_renamed_into_place_is_heard()
    {
        var configHome = Directory.CreateDirectory(Path.Combine(_root, "renamed")).FullName;

        using var heard = new SemaphoreSlim(0);
        EventHandler handler = (_, _) => heard.Release();

        var provider = new KdeThemeProvider(configHome);
        provider.Changed += handler;

        try
        {
            using var watcher = KdeThemeProvider.Watch(configHome);
            Assert.NotNull(watcher);

            var temporary = Path.Combine(configHome, "kdeglobals.Xa1b2c");
            File.WriteAllLines(temporary, ["[KDE]", "SingleClick=true"]);
            while (heard.Wait(TimeSpan.FromMilliseconds(300))) { }

            File.Move(temporary, Path.Combine(configHome, "kdeglobals"), overwrite: true);

            Assert.True(heard.Wait(Ceiling), "the rename into place was not heard");
        }
        finally
        {
            provider.Changed -= handler;
        }
    }
}
