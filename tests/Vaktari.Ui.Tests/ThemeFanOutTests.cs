using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Win32;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The theme watchers became one set per process, and the event they raise
/// became static** (batch-0.11.2). That traded one risk for another: a static
/// event holds every handler given to it, so a window that closed and did not
/// take its handler back would be kept alive by it and repainted on every
/// desktop change — and, the other way round, a window that is open has to
/// hear a change made while several others are open beside it, each of which
/// built its own provider.
///
/// Measured end to end, with real windows: four are opened, two closed, and
/// a change is made to a registry key this test owns, through the same
/// watcher thread the desktop's keys use. Each window's handler, when it
/// runs, re-reads the palette and tells every pane to re-ask its click
/// setting (Shell.RefreshActivation), so a pane raising OpensOnSingleClick is
/// the window having heard it — nothing else in the test raises it. The open
/// windows must hear it; the closed ones must not, and their handlers must
/// be gone from the event's list.
///
/// The real theme keys are the user's and are never written: the key is
/// HKCU\Software\Vaktari-tests\theme-fanout-&lt;guid&gt;, deleted afterwards.
/// Reached by reflection because this assembly is built against one platform
/// assembly at a time and the provider's type exists only on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThemeFanOutTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

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

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_change_reaches_every_open_window_and_no_closed_one()
    {
        UseSearch(PaneViewModel.Search);

        var windows = new List<MainWindow>();
        var heard = new Dictionary<MainWindow, int>();
        var subKey = @"Software\Vaktari-tests\theme-fanout-" + Guid.NewGuid().ToString("N");
        using var woke = new SemaphoreSlim(0);
        EventHandler probe = (_, _) => woke.Release();
        object? provider = null;

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
            Assert.Equal("WindowsThemeProvider", type.Name);

            // The static list, as the watcher threads will read it.
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

            using (Registry.CurrentUser.CreateSubKey(subKey)) { }

            type.GetMethod("Watch", Any)!.Invoke(null, [subKey]);

            // The watcher arms its wait a moment after it starts, and a write
            // before that is not seen — so write until one is.
            var clock = Stopwatch.StartNew();
            var fired = false;

            for (var n = 0; !fired && clock.Elapsed < Ceiling; n++)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true)!)
                    key.SetValue("n", n);

                fired = woke.Wait(TimeSpan.FromMilliseconds(200));
            }

            Assert.True(fired, "a change to the watched key never reached a subscriber");

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
            if (provider is not null) provider.GetType().GetEvent("Changed")!.RemoveEventHandler(provider, probe);

            foreach (var window in windows) window.Close();
            Dispatcher.UIThread.RunJobs();

            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);

            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Vaktari-tests", writable: true);
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
                Registry.CurrentUser.DeleteSubKey(@"Software\Vaktari-tests", throwOnMissingSubKey: false);
        }
    }
}
