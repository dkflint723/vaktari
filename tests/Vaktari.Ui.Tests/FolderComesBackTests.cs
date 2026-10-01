using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A folder that went away and came back left the pane that showed it
/// saying it was not there** (batch-0.11.2c QA, GoneFlowProbe, MEDIUM). The
/// watcher heard the folder go — renamed from the other pane, sent to the bin,
/// deleted — and the reload found nothing; when Undo, a rename back or
/// anything outside Vaktari put it back, the pane went on saying "that folder
/// is not there any more", watching nothing, until F5. On 3a3746c, which never
/// heard a folder go, the pane went on showing the old rows and came back to
/// life with the folder.
///
/// Now a pane that cannot open its folder because it is not there waits for
/// it from the nearest folder above (PaneViewModel.WaitForReturn,
/// FolderReturnWatch) and reads it again when it is back. Each flow here ends
/// the same way: the folder is back, the pane lists it with no error, and a
/// file written in it afterwards arrives — the pane is watching it again, not
/// merely showing it. Both platforms: on Windows a FileSystemWatcher is silent
/// about its folder being renamed, so there the pane never errs over a rename
/// at all, and the end state is what is asserted.
///
/// "The bin" is a folder of the test's own that the folder is moved into and
/// back out of — what a trash and its undo do to the disk — so no real trash
/// or Recycle Bin is touched. Every folder is a temporary one.
/// </summary>
public sealed class FolderComesBackTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-comesback").FullName;

    private readonly List<MainWindow> _windows = [];

    public override void Dispose()
    {
        foreach (var window in _windows) window.Close();
        Dispatcher.UIThread.RunJobs();

        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<bool> Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();

        while (!done())
        {
            if (clock.Elapsed > Ceiling) return false;

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        return true;
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

    private string Dir(params string[] names) => Directory.CreateDirectory(Path.Combine([_root, .. names])).FullName;

    /// <summary>A split window, the left side on <paramref name="left"/> and the
    /// right on <paramref name="right"/>, both read to the end.</summary>
    private async Task<(MainWindow Window, PaneViewModel Left, PaneViewModel Right)> Open(string left, string right)
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 800 };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        if (!window.Shell.IsSplit)
        {
            window.Shell.ToggleSplit();
            await Settle(100);
        }

        var l = window.Shell.Left.ActiveTab!;
        var r = window.Shell.Right!.ActiveTab!;

        await l.NavigateAsync(left);
        await r.NavigateAsync(right);

        Assert.True(await Until(() => Shown(l, left) && Shown(r, right)), "the sides did not load");

        window.Shell.ActiveGroup = window.Shell.Left;

        return (window, l, r);
    }

    private static bool Shown(PaneViewModel pane, string path)
        => pane.CurrentPath == path && pane.IsLoaded && !pane.IsLoading && !pane.HasLoadError;

    private static bool Lists(PaneViewModel pane, string name) => pane.Entries.Any(e => e.Name == name);

    /// <summary>The folder at <paramref name="path"/> is listed again, with no
    /// error, and a file written there afterwards arrives: the pane is
    /// watching it again.</summary>
    private static async Task BackAndLive(PaneViewModel pane, string path, string listed, string what)
    {
        Assert.True(await Until(() => Shown(pane, path) && Lists(pane, listed)),
            $"{what}: the pane did not come back to the folder (error='{pane.LoadError}', rows={pane.Entries.Count})");

        var later = "later-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        File.WriteAllText(Path.Combine(path, later), "later");

        Assert.True(await Until(() => Lists(pane, later)),
            $"{what}: a file written after the folder came back did not arrive — it is shown, not watched");
    }

    [AvaloniaFact]
    public async Task Renamed_from_the_other_pane_and_undone_the_folder_is_live_again()
    {
        var x = Dir("x");
        File.WriteAllText(Path.Combine(x, "a.txt"), "a");

        var (_, left, right) = await Open(_root, x);

        await left.RenameAsync(left.Entries.Single(e => e.Name == "x"), "y");
        Assert.True(await Until(() => Directory.Exists(Path.Combine(_root, "y"))), "the rename did not happen");
        await Settle(500);

        await left.UndoAsync();
        Assert.True(await Until(() => Directory.Exists(x)), "the undo did not put x back");

        await BackAndLive(right, x, "a.txt", "renamed and undone");
    }

    [AvaloniaFact]
    public async Task Renamed_away_and_back_from_outside_the_folder_is_live_again()
    {
        var x = Dir("x");
        File.WriteAllText(Path.Combine(x, "a.txt"), "a");

        var (_, _, right) = await Open(_root, x);

        Directory.Move(x, x + "-away");
        await Settle(800);
        Directory.Move(x + "-away", x);

        await BackAndLive(right, x, "a.txt", "renamed away and back");
    }

    [AvaloniaFact]
    public async Task Sent_to_the_bin_and_restored_the_folder_is_live_again()
    {
        var bin = Dir("bin");
        var x = Dir("place", "x");
        File.WriteAllText(Path.Combine(x, "a.txt"), "a");

        var (_, _, right) = await Open(Path.Combine(_root, "place"), x);

        Directory.Move(x, Path.Combine(bin, "x"));
        await Settle(800);
        Directory.Move(Path.Combine(bin, "x"), x);

        await BackAndLive(right, x, "a.txt", "sent to the bin and restored");
    }

    [AvaloniaFact]
    public async Task Deleted_and_made_again_from_outside_the_folder_is_live_again()
    {
        var x = Dir("x");
        File.WriteAllText(Path.Combine(x, "a.txt"), "a");

        var (_, _, right) = await Open(_root, x);

        Directory.Delete(x, recursive: true);
        Assert.True(await Until(() => right.HasLoadError), "the pane did not notice its folder deleted");

        Directory.CreateDirectory(x);
        File.WriteAllText(Path.Combine(x, "b.txt"), "b");

        await BackAndLive(right, x, "b.txt", "deleted and made again");
    }

    /// <summary>The folder above it deleted too: the wait moves up to the
    /// nearest folder that is there, and down again as they come back.</summary>
    [AvaloniaFact]
    public async Task With_the_folder_above_deleted_too_it_comes_back_with_both()
    {
        var other = Dir("other");
        var p = Dir("p");
        var x = Dir("p", "x");
        File.WriteAllText(Path.Combine(x, "a.txt"), "a");

        var (_, _, right) = await Open(other, x);

        Directory.Delete(p, recursive: true);
        Assert.True(await Until(() => right.HasLoadError), "the pane did not notice its folder deleted");
        await Settle(300);

        Directory.CreateDirectory(p);
        await Settle(300);
        Directory.CreateDirectory(x);
        File.WriteAllText(Path.Combine(x, "b.txt"), "b");

        await BackAndLive(right, x, "b.txt", "both made again");
    }

    /// <summary>
    /// **A pane that moves on while it waits lets the wait go.** It is in the
    /// watch's slot, so the navigation's own prologue disposes it: the folder
    /// coming back afterwards brings the pane nowhere, and on Linux the kernel
    /// is watching exactly what it was before the folder went, in one inotify
    /// instance.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pane_that_moves_on_while_it_waits_leaves_no_wait_behind()
    {
        var other = Dir("other");
        var p = Dir("p");
        var x = Dir("p", "x");

        var (_, _, right) = await Open(other, x);
        await Settle(300);

        var watches = KernelWatches();
        var instances = InotifyInstances();

        Directory.Delete(x);
        Assert.True(await Until(() => right.HasLoadError), "the pane did not notice its folder deleted");
        await Settle(300);

        // Waiting at p, which nothing else watches: one watch each, as before.
        if (OperatingSystem.IsLinux()) Assert.Equal(watches, KernelWatches());

        // Asked again and again while it is not there: still one wait.
        for (var i = 0; i < 3; i++)
        {
            await right.RefreshAsync();
            await Settle(100);
        }

        Assert.True(await Until(() => right.HasLoadError && !right.IsLoading), "the refresh did not fail");
        await Settle(300);

        if (OperatingSystem.IsLinux()) Assert.Equal(watches, KernelWatches());

        await right.NavigateAsync(other);
        Assert.True(await Until(() => Shown(right, other)), "the pane did not move on");
        await Settle(300);

        // Both panes on "other" now, which is one watch; p is watched by no one.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(watches - 1, KernelWatches());
            Assert.Equal(instances, InotifyInstances());
        }

        Directory.CreateDirectory(x);
        await Settle(800);

        Assert.Equal(other, right.CurrentPath);
        Assert.False(right.HasLoadError);
    }

    /// <summary>The kernel's watches across this process's inotify instances:
    /// one "inotify wd:" line each in their fdinfo. Linux only.</summary>
    private static int KernelWatches()
    {
        if (!OperatingSystem.IsLinux()) return 0;

        var count = 0;

        foreach (var fd in Directory.GetFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget != "anon_inode:inotify") continue;

                count += File.ReadAllLines("/proc/self/fdinfo/" + Path.GetFileName(fd))
                    .Count(l => l.StartsWith("inotify wd:", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                // Closed since it was listed.
            }
        }

        return count;
    }

    private static int InotifyInstances()
    {
        if (!OperatingSystem.IsLinux()) return 0;

        var count = 0;

        foreach (var fd in Directory.GetFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == "anon_inode:inotify") count++;
            }
            catch (IOException)
            {
            }
        }

        return count;
    }
}
