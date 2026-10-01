using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A folder deleted while a pane showed it left an inotify instance behind,
/// for the life of the process** (batch-0.11.2b QA, WatchedFolderGoneProbe).
/// .NET 10's FileSystemWatcher on Linux cannot close its instance once the
/// kernel has dropped the watch on a deleted folder, and disposing it does not
/// help. One window, a folder opened, deleted from outside, and the person
/// going home: once per folder, measured at +20 for twenty rounds, unchanged
/// by a full collection — against a per-user ceiling of 128, past which every
/// pane reads its folder on a timer and other programs cannot watch at all.
///
/// The same rounds now, counted by the kernel's own account (each instance is
/// a descriptor whose link reads "anon_inode:inotify" in /proc/self/fd), must
/// leave the count where it was. Linux only: the instance is the Linux
/// provider's. Every folder is a temporary one.
/// </summary>
public sealed class WatchedFolderGoneTests : OwnedViewModels
{
    private const int Rounds = 20;

    private static int Inotify()
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

    private static async Task Until(Func<bool> done, int ms = 5000)
    {
        var clock = Stopwatch.StartNew();

        while (!done() && clock.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private static async Task Settle(int ms)
    {
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    [AvaloniaFact(Skip = OnlyOn.Linux, SkipUnless = nameof(OnlyOn.IsLinux), SkipType = typeof(OnlyOn))]
    public async Task A_folder_deleted_while_shown_leaves_no_inotify_instance_behind()
    {
        UseSearch(PaneViewModel.Search);

        var root = Directory.CreateTempSubdirectory("vaktari-gone").FullName;
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;

        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            if (window.Shell.IsSplit)
            {
                window.Shell.ToggleSplit();
                Dispatcher.UIThread.RunJobs();
            }

            var pane = window.Shell.Left.ActiveTab!;

            await pane.NavigateAsync(home);
            await Until(() => pane.IsLoaded && !pane.IsLoading && pane.CurrentPath == home);
            await Settle(300);

            var start = Inotify();

            // The count can see the pane's watch at all: something is open
            // while a folder is shown. Without this, a count that read zero
            // whatever happened would pass the assertion below.
            Assert.True(start >= 1, "no inotify instance is open while a folder is shown");

            var noticed = 0;

            for (var i = 0; i < Rounds; i++)
            {
                var shown = Directory.CreateDirectory(Path.Combine(root, "gone" + i)).FullName;
                File.WriteAllText(Path.Combine(shown, "a.txt"), "a");

                await pane.NavigateAsync(shown);
                await Until(() => pane.IsLoaded && !pane.IsLoading && pane.CurrentPath == shown);
                await Settle(100);

                // Deleted from outside — a terminal, another program — while
                // the pane shows it. The pane hears it go and reloads.
                Directory.Delete(shown, recursive: true);

                await Until(() => pane.LoadError is not null, 3000);
                if (pane.LoadError is not null) noticed++;

                await pane.NavigateAsync(home);
                await Until(() => pane.IsLoaded && !pane.IsLoading && pane.CurrentPath == home);
            }

            await Settle(500);

            Assert.Equal(Rounds, noticed);
            Assert.Equal(start, Inotify());
        }
        finally
        {
            window.Close();
            await Until(() => !window.IsVisible);
            await Settle(300);

            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
