using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The KDE theme watcher became one per process, and the event it raises
/// became static** (batch-0.11.2b) — what ThemeFanOutTests pins for the
/// Windows provider, here for Linux. Each KdeThemeProvider made a watcher of
/// its own, one inotify instance, and nothing disposed it: every window that
/// built its own services left one behind (batch-0.11.2 QA, item G).
///
/// Two things are measured with real windows. Opening and closing windows
/// does not grow the process's inotify instances, by the kernel's own count,
/// nor leave a pane watching, by the one instance's own count of listeners —
/// every watch shares that instance now, so the instance count alone could
/// not see a pane that kept its watcher after its window closed (batch-0.11.2c
/// QA). And a change reaches
/// every open window and no closed one: four are opened, two closed, and
/// kdeglobals is written in the folder they read it from. Each window's
/// handler re-reads the palette and tells every pane to re-ask its click
/// setting (Shell.RefreshActivation), so a pane raising OpensOnSingleClick is
/// the window having heard it.
///
/// The user's own config is never read or written: XDG_CONFIG_HOME points at a
/// temporary folder while each test runs, and is put back after. Reached by
/// reflection because this assembly is built against one platform assembly at
/// a time and the provider's type exists only on Linux.
/// </summary>
public sealed class KdeThemeFanOutTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly string _configHome = Directory.CreateTempSubdirectory("vaktari-kdefan").FullName;
    private readonly string? _was = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public KdeThemeFanOutTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
        Scheme(singleClick: false);
    }

    public override void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _was);

        try { Directory.Delete(_configHome, recursive: true); } catch { }

        base.Dispose();
    }

    private void Scheme(bool singleClick, int n = 0)
        => File.WriteAllLines(Path.Combine(_configHome, "kdeglobals"),
        [
            "[Colors:View]",
            "BackgroundNormal=239,240,241",
            "[KDE]",
            "SingleClick=" + (singleClick ? "true" : "false"),
            "# " + n,
        ]);

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, Any)!.GetValue(owner)!;

    private static async Task Until(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();

        while (!done())
        {
            Assert.True(clock.Elapsed < Ceiling, $"waited {Ceiling.TotalSeconds:N0} s for {what}");

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    /// <summary>The process's inotify instances, by the kernel's account: a
    /// descriptor whose link reads "anon_inode:inotify".</summary>
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

    private static async Task OpenAndClose(int windows)
    {
        for (var i = 0; i < windows; i++)
        {
            var window = new MainWindow();

            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.Close();

            // Close() only starts the teardown in headless; the pane's
            // watcher goes with the pane.
            await Until(() => !window.IsVisible, "the window to close");
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// **Ten windows opened and closed leave no inotify instance behind.**
    /// Two first, so whatever starts once per process has started. Before
    /// the watcher was shared, each founder left one: ten more here.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task Opening_and_closing_windows_does_not_grow_the_inotify_instances()
    {
        UseSearch(PaneViewModel.Search);

        await OpenAndClose(2);

        var before = InotifyInstances();

        await OpenAndClose(10);

        // Settled: a pane's watcher is let go on the pool once its window is
        // gone, so the count is read until it stops above the start.
        var clock = Stopwatch.StartNew();

        while (InotifyInstances() > before && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        var grown = InotifyInstances() - before;

        Assert.True(grown < 3, $"ten windows opened and closed left {grown} more inotify instances than before them");
    }

    /// <summary>
    /// The listeners the process's one inotify instance holds — every pane's
    /// folder watch and repository watch, and the theme watcher — read from
    /// Vaktari.Linux.Inotify by reflection. Zero when no instance is open.
    /// </summary>
    private static int InotifyListeners()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Vaktari.Linux.Inotify"))
            .FirstOrDefault(t => t is not null);

        Assert.NotNull(type);

        var gate = (Lock)type.GetField("Gate", Any)!.GetValue(null)!;

        lock (gate)
        {
            return type.GetField("_shared", Any)!.GetValue(null) is { } shared
                ? (int)type.GetField("_listeners", Any)!.GetValue(shared)!
                : 0;
        }
    }

    /// <summary>
    /// **Ten windows opened and closed leave no pane watching** (batch-0.11.2c
    /// QA). The count of instances above cannot say so any more: every watch
    /// in the process shares one instance now, so a pane that kept its watch
    /// after its window closed adds nothing to it — and its listener, held by
    /// the instance, keeps the pane, and through it the window, alive. Counted
    /// here where the watches are: the instance's own listeners.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task Opening_and_closing_windows_leaves_no_pane_watching()
    {
        UseSearch(PaneViewModel.Search);

        await OpenAndClose(2);

        var before = InotifyListeners();

        // The count can see a window's watches at all: one open is more.
        var open = new MainWindow();
        open.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            await Until(() => InotifyListeners() > before, "an open window's pane to be watching");
        }
        finally
        {
            open.Close();
            await Until(() => !open.IsVisible, "the window to close");
        }

        await OpenAndClose(10);

        var clock = Stopwatch.StartNew();

        while (InotifyListeners() > before && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        var grown = InotifyListeners() - before;

        Assert.True(grown < 3, $"eleven windows opened and closed left {grown} more inotify listeners than before them");
    }

    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task A_change_reaches_every_open_window_and_no_closed_one()
    {
        UseSearch(PaneViewModel.Search);

        var windows = new List<MainWindow>();
        var heard = new Dictionary<MainWindow, int>();
        using var woke = new SemaphoreSlim(0);
        EventHandler probe = (_, _) => woke.Release();
        object? provider = null;
        IDisposable? watcher = null;

        try
        {
            for (var i = 0; i < 4; i++)
            {
                var window = new MainWindow();

                window.Show();
                Dispatcher.UIThread.RunJobs();

                windows.Add(window);
                heard[window] = 0;

                var pane = window.Shell.ActiveTab!;
                pane.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PaneViewModel.OpensOnSingleClick)) heard[window]++;
                };
            }

            provider = Field<object>(windows[0], "_theme");
            var type = provider.GetType();
            Assert.Equal("KdeThemeProvider", type.Name);

            // Each window has a provider of its own, as a founder does.
            Assert.NotSame(provider, Field<object>(windows[1], "_theme"));

            // The static list, as the watcher will read it.
            Delegate[] Subscribed()
                => (type.GetField("_changed", Any)!.GetValue(null) as Delegate)?.GetInvocationList() ?? [];

            foreach (var window in windows)
                Assert.Contains(Field<EventHandler>(window, "_onThemeChanged"), Subscribed());

            var closed = windows.Take(2).ToList();
            var open = windows.Skip(2).ToList();

            foreach (var window in closed) window.Close();

            // Close() only starts the teardown in headless; the handler comes
            // off in OnClosed.
            await Until(
                () => closed.All(w => !Subscribed().Contains(Field<EventHandler>(w, "_onThemeChanged"))),
                "the closed windows to take their theme handlers back");

            foreach (var window in open)
                Assert.Contains(Field<EventHandler>(window, "_onThemeChanged"), Subscribed());

            foreach (var window in windows) heard[window] = 0;

            type.GetEvent("Changed")!.AddEventHandler(provider, probe);

            // The process's own watcher was made by the first provider in this
            // process, over whatever folder that one read; this one is made over
            // the folder these windows read, as the first would make it.
            watcher = (IDisposable?)type.GetMethod("Watch", Any)!.Invoke(null, [_configHome]);
            Assert.NotNull(watcher);

            var clock = Stopwatch.StartNew();
            var fired = false;

            for (var n = 1; !fired && clock.Elapsed < Ceiling; n++)
            {
                Scheme(singleClick: true, n);
                fired = woke.Wait(TimeSpan.FromMilliseconds(200));
            }

            Assert.True(fired, "writing kdeglobals never reached a subscriber");

            await Until(() => open.All(w => heard[w] > 0), "every open window to hear the change");

            // Whatever a closed window's handler would have done was queued by
            // the same Notify, behind the same 150 ms settle; give it the same
            // time again before saying it never came.
            var settle = Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < 600)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.All(closed, w => Assert.Equal(0, heard[w]));
        }
        finally
        {
            watcher?.Dispose();

            if (provider is not null) provider.GetType().GetEvent("Changed")!.RemoveEventHandler(provider, probe);

            foreach (var window in windows) window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
