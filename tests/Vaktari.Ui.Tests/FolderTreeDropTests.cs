using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dropping onto a row of the folder tree.
///
/// **The tree took no drop of any kind.** Its rows are buttons, the sidebar's
/// ground stops at a button, and nothing else claimed one — so a drag over the
/// tree was answered None all the way down it, and since Windows drops only
/// where the last answer was yes, a drag let go over a tree row simply ended.
/// That is one of the ways "dragging out of the zip sometimes fails": the
/// pointer came to rest on a folder in the tree. A tree row is a place that
/// was not pinned, so it now takes a drop by the place rows' rules and washes
/// the way they do.
///
/// Driven on a real window with the tree switched on and rooted at folders of
/// the test's own, so the row, its markup and its hit test are the shipped
/// ones.
/// </summary>
public sealed class FolderTreeDropTests : OwnedViewModels
{
    private readonly Vaktari.Core.Settings.SettingsState _settingsBefore = AppSettings.Current;

    private readonly List<string> _made = [];

    private bool? _foldedBefore;
    private SidebarViewModel? _sidebar;

    public override void Dispose()
    {
        if (_sidebar is not null && _foldedBefore is { } folded) _sidebar.IsFoldersCollapsed = folded;

        AppSettings.Apply(_settingsBefore);

        foreach (var dir in _made)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }

        base.Dispose();
    }

    /// <summary>
    /// The fault, the rule and the mark in one gesture. A copy (Ctrl) from
    /// another folder onto a tree row: the cursor says Copy rather than None,
    /// the row is washed while it would take the drop and not after the drag
    /// has left, and the file lands in the row's folder.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tree_row_takes_a_drop_into_its_folder()
    {
        var into = Folder("tree");
        var from = Folder("from");

        var file = Path.Combine(from, "onto-the-tree.txt");
        File.WriteAllText(file, "x");

        var (window, rows) = await Shown(into);

        try
        {
            var row = rows[0];
            var node = (FolderNode)row.DataContext!;

            Assert.True(DragDrop.GetAllowDrop(row), "the tree row is not a drop target, so no drag reaches it");

            var data = await Carrying(window, file);

            var over = Raise(row, DragDrop.DragOverEvent, data, KeyModifiers.Control);
            Pump();

            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            Assert.True(node.IsDropTarget, "the row the drop would land in is not marked");
            Assert.True(row.Background is ISolidColorBrush { Color.A: > 0 },
                "the row is marked in its view model and draws nothing");

            Raise(row, DragDrop.DragLeaveEvent, data, KeyModifiers.Control);
            Pump();

            Assert.False(node.IsDropTarget, "the mark stayed on the row after the drag left it");

            var drop = Raise(row, DragDrop.DropEvent, data, KeyModifiers.Control);

            Assert.Equal(DragDropEffects.Copy, drop.DragEffects);

            await Arrives(Path.Combine(into, "onto-the-tree.txt"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The place rows' rules, not a rule of its own.** A drag that began in
    /// this window, within one volume and with no key held, is a move onto a
    /// place row — Explorer's default — and so it is onto a tree row.
    /// </summary>
    [AvaloniaFact]
    public async Task An_internal_drag_onto_a_tree_row_moves_as_onto_a_place()
    {
        var into = Folder("tree");
        var from = Folder("from");

        var file = Path.Combine(from, "moving.txt");
        File.WriteAllText(file, "x");

        var (window, rows) = await Shown(into);

        try
        {
            typeof(MainWindow)
                .GetField("_internalDrag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(window, true);

            var over = Raise(rows[0], DragDrop.DragOverEvent, await Carrying(window, file), KeyModifiers.None);

            Assert.Equal(DragDropEffects.Move, over.DragEffects);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The drop goes where the ring was.** Avalonia delivers the drop to the
    /// element the last drag-over was over, and in between the edge scroll can
    /// recycle that element for another item — the ring said one folder and
    /// the element now says another. Staged here by giving the row another
    /// folder between the drag-over and the drop, which is what recycling
    /// does to it: the file must land in the folder the drag-over marked.
    /// </summary>
    [AvaloniaFact]
    public async Task The_drop_lands_where_the_drag_over_pointed()
    {
        var marked = Folder("marked");
        var recycled = Folder("recycled");
        var from = Folder("from");

        var file = Path.Combine(from, "aimed.txt");
        File.WriteAllText(file, "x");

        var (window, rows) = await Shown(marked, recycled);

        try
        {
            var row = rows.First(r => ((FolderNode)r.DataContext!).Path == marked);
            var other = rows.First(r => ((FolderNode)r.DataContext!).Path == recycled).DataContext;

            var data = await Carrying(window, file);

            Assert.Equal(DragDropEffects.Copy, Raise(row, DragDrop.DragOverEvent, data, KeyModifiers.Control).DragEffects);

            row.DataContext = other;

            Raise(row, DragDrop.DropEvent, data, KeyModifiers.Control);

            await Arrives(Path.Combine(marked, "aimed.txt"));

            Assert.False(File.Exists(Path.Combine(recycled, "aimed.txt")),
                "the drop went into the folder the element was recycled for");
        }
        finally
        {
            window.Close();
        }
    }

    // ---- the harness ----------------------------------------------------------

    /// <summary>
    /// A shown window with the folder tree on, unfolded, and rooted at
    /// <paramref name="roots"/> — the rows returned in the tree's order.
    /// </summary>
    private async Task<(MainWindow Window, List<Button> Rows)> Shown(params string[] roots)
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        window.Show();
        Pump();

        var shell = Own((ShellViewModel)window.DataContext!);

        SidebarReady(window);

        // After the window, which applies the settings it loads from disk.
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { ShowFolderTree = true },
        });

        _sidebar = shell.Sidebar;
        _foldedBefore = _sidebar.IsFoldersCollapsed;
        _sidebar.IsFoldersCollapsed = false;
        _sidebar.RefreshFolderTreeVisibility();

        Assert.True(_sidebar.Tree is not null, "this platform's sidebar has no folder tree");

        // The pane somewhere none of the roots is, so revealing it opens
        // nothing under them.
        await shell.ActiveTab!.NavigateAsync(Folder("pane"));
        Pump();

        _sidebar.Tree!.SetRoots(roots.Select(r => (r, Path.GetFileName(r))));

        List<Button> rows = [];

        for (var i = 0; i < 500; i++)
        {
            Pump();

            // Not the triangle, which is a button inside the row and carries
            // the same node.
            rows = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b is not Avalonia.Controls.Primitives.ToggleButton
                            && b.DataContext is FolderNode node && roots.Contains(node.Path)
                            && b.IsEffectivelyVisible)
                .ToList();

            if (rows.Count == roots.Length) break;

            await Task.Delay(10);
        }

        Assert.True(rows.Count == roots.Length,
            $"the tree drew {rows.Count} of its {roots.Length} rows — ShowFolderTree {_sidebar.ShowFolderTree}, "
            + $"folded {_sidebar.IsFoldersCollapsed}, rows [{string.Join(", ", _sidebar.Tree.Rows.Select(r => r.Path))}]");

        return (window, rows);
    }

    private static async Task<IDataTransfer> Carrying(TopLevel top, string path)
    {
        var data = new DataTransfer();
        var item = await top.StorageProvider.TryGetFileFromPathAsync(path);

        Assert.True(item is not null, "the drop could not be given " + path);

        data.Add(DataTransferItem.CreateFile(item!));
        return data;
    }

    private static DragEventArgs Raise(
        Control on, RoutedEvent<DragEventArgs> what, IDataTransfer data, KeyModifiers modifiers)
    {
        var e = new DragEventArgs(what, data, on, new Point(on.Bounds.Width / 2, on.Bounds.Height / 2), modifiers)
        {
            DragEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link,
        };

        on.RaiseEvent(e);

        return e;
    }

    private string Folder(string what)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vaktari-tree-drop-{what}-{Guid.NewGuid():N}"[..40]);

        Directory.CreateDirectory(dir);
        _made.Add(dir);

        return dir;
    }

    /// <summary>Waits for the drop's operation to put the file in place, and
    /// then for the pane's refresh that follows it.</summary>
    private static async Task Arrives(string path)
    {
        for (var i = 0; i < 500 && !File.Exists(path); i++)
        {
            await Task.Delay(10);
            Pump();
        }

        Assert.True(File.Exists(path), "the drop's files never arrived at " + path);

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(10);
            Pump();
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }
}
