using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A drop the guard refuses tells its source None.** The merge of the
/// right-click menu redesign made MainWindow.DragDrop.cs return before it
/// reports an effect when the pane's paste, or the bin row's TrashPaths,
/// turned the drop away: a drive answered Copy or Move before, and on X11 a
/// Move tells the source to delete what it dragged. The pane-level answers
/// are pinned in VolumeRefusalTests; nothing drove the drop handler itself,
/// so taking either return away reddened nothing (fix-7 verification). A
/// temporary folder stands in for the drive through the mount-point seam, so
/// no volume is handed to anything.
/// </summary>
public sealed class RefusedDropTests : OwnedViewModels
{
    [AvaloniaTheory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Shift)]
    public async Task A_refused_drop_reports_none(KeyModifiers modifiers)
    {
        var drive = Outside("drive");
        var onIt = Path.Combine(drive, "on-the-drive.txt");
        File.WriteAllText(onIt, "x");
        var into = Outside("into");
        var from = Outside("from");
        var ordinary = Path.Combine(from, "plain.txt");
        File.WriteAllText(ordinary, "p");

        var before = VolumeRoots.MountPointsOverride;
        VolumeRoots.MountPointsOverride = () => ["/", drive];

        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            var pane = Own(ShellOf(window)).ActiveTab!;

            await pane.NavigateAsync(into);
            Pump();

            Assert.Equal(into, pane.CurrentPath);

            var listing = ListingOf(window, pane);

            var refused = Raise(window, listing, DragDrop.DropEvent, await Carrying(window, drive), modifiers);

            Assert.Equal(VolumeRoots.Refusal, pane.Status);
            Assert.Equal(DragDropEffects.None, refused.DragEffects);
            Assert.True(File.Exists(onIt));
            Assert.Empty(Directory.EnumerateFileSystemEntries(into));

            // The control: an ordinary file dropped the same way is taken, and says so.
            var taken = Raise(window, listing, DragDrop.DropEvent, await Carrying(window, ordinary), modifiers);

            var expected = modifiers == KeyModifiers.Control
                ? DragDropEffects.Copy
                : OperatingSystem.IsWindows() ? DragDropEffects.None : DragDropEffects.Move;

            Assert.Equal(expected, taken.DragEffects);
        }
        finally
        {
            VolumeRoots.MountPointsOverride = before;
            window.Close();
            Delete(drive);
            Delete(into);
            Delete(from);
        }
    }

    /// <summary>
    /// A drive dropped on the bin's sidebar row is refused by TrashPaths, and
    /// the drop must then report None — not MovedByUs, which on X11 is a Move
    /// that tells the source to delete what it dragged. On Windows MovedByUs is
    /// None already, so this discriminates only on Linux.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refused_bin_drop_reports_none()
    {
        var drive = Outside("bindrive");
        var onIt = Path.Combine(drive, "on-the-drive.txt");
        File.WriteAllText(onIt, "x");

        var before = VolumeRoots.MountPointsOverride;
        VolumeRoots.MountPointsOverride = () => ["/", drive];

        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            var pane = Own(ShellOf(window)).ActiveTab!;

            var bin = window.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.DataContext is PlaceItemViewModel p && PathRules.Same(p.Path, VirtualPaths.Trash));

            Assert.True(bin is not null, "the sidebar has no bin row to drop on");

            var refused = Raise(window, bin!, DragDrop.DropEvent, await Carrying(window, drive), KeyModifiers.None);

            Assert.Equal(VolumeRoots.Refusal, pane.Status);
            Assert.Equal(DragDropEffects.None, refused.DragEffects);
            Assert.True(File.Exists(onIt));
        }
        finally
        {
            VolumeRoots.MountPointsOverride = before;
            window.Close();
            Delete(drive);
        }
    }
    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception) { }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static ShellViewModel ShellOf(MainWindow window)
        => (ShellViewModel)typeof(MainWindow)
            .GetProperty("Shell", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    private static Control ListingOf(MainWindow window, PaneViewModel pane)
    {
        var listing = window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(l => ReferenceEquals(l.DataContext, pane));

        Assert.True(listing is not null, "no listing carries the active pane");

        return listing!;
    }

    private static async Task<DataTransfer> Carrying(TopLevel top, params string[] paths)
    {
        var data = new DataTransfer();

        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                : await top.StorageProvider.TryGetFileFromPathAsync(path);

            Assert.True(item is not null, "the drop could not be given " + path);

            data.Add(DataTransferItem.CreateFile(item!));
        }

        return data;
    }

    private static DragEventArgs Raise(
        MainWindow window, Control on, RoutedEvent<DragEventArgs> what,
        DataTransfer data, KeyModifiers modifiers)
    {
        var centre = new Point(on.Bounds.Width / 2, on.Bounds.Height / 2);
        var point = on.TranslatePoint(centre, window) ?? centre;

        var e = new DragEventArgs(what, data, on, point, modifiers);

        on.RaiseEvent(e);

        return e;
    }

    private static string Outside(string what)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "vaktari-refuseddrop-" + what + "-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(dir);

        Assert.False(DropStaging.IsVolatile(dir, Path.GetTempPath()), "the host runs from temp");

        return dir;
    }
}
