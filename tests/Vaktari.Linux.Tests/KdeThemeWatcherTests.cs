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

    /// <summary>
    /// The process's inotify instances, by the kernel's own account: each is
    /// a descriptor whose link reads "anon_inode:inotify". Nothing in the
    /// application keeps this count, so nothing it does can satisfy it by
    /// accident.
    /// </summary>
    private static int InotifyInstances()
    {
        var count = 0;

        foreach (var fd in Directory.GetFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == "anon_inode:inotify") count++;
            }
            catch (IOException)
            {
                // The descriptor the listing itself used, closed since.
            }
        }

        return count;
    }

    [PosixFact]
    public void Making_many_providers_does_not_grow_the_inotify_instances()
    {
        // The count can see a watcher at all: one made here is one more, and
        // gone again once disposed. Without this, a count that read zero
        // whatever happened would pass the assertion below.
        var before = InotifyInstances();

        using (var probe = KdeThemeProvider.Watch(_root))
        {
            Assert.NotNull(probe);
            Assert.Equal(before + 1, InotifyInstances());
        }

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
    /// </summary>
    [Fact]
    public void A_change_reaches_every_subscriber_of_every_provider_and_no_departed_one()
    {
        var first = new KdeThemeProvider(_root);
        var second = new KdeThemeProvider(_root);

        var heard = new int[2];
        EventHandler one = (_, _) => Interlocked.Increment(ref heard[0]);
        EventHandler two = (_, _) => Interlocked.Increment(ref heard[1]);

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

        FileSystemWatcher? started = null;

        try
        {
            var provider = new KdeThemeProvider(configHome);

            started = (FileSystemWatcher?)field.GetValue(null);
            Assert.NotNull(started);
            Assert.Equal(configHome, started.Path);

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
}
