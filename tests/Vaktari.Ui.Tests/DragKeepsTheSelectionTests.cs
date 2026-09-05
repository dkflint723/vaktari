using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What is still selected once a drag that started on a selected row is over.
///
/// **Pressing one of three selected rows collapsed the visible selection.** The
/// press is how a drag begins, and Avalonia's list reduces a multiple selection
/// to the row under the pointer on the press rather than on the release — so
/// the window already snapshots the three on the tunnelled press and builds the
/// payload from that, which is why the drag itself carried all three. Nothing
/// put them back: measured, the gesture ended with one row selected whether the
/// drag was cancelled with Escape or refused outright by the bin, leaving two
/// rows to pick out again with no sign of what had happened.
///
/// Two halves, and each is pinned on its own: the window restores the snapshot
/// on the way out of the drag, and the pane refuses to restore it over a
/// listing the rows have left.
///
/// Three gestures reach the first half here, because the routes out of the
/// drag differ: one cancelled with Escape, one the BIN refuses before the drag
/// ever starts, and one begun with Ctrl held.
/// </summary>
public sealed class DragKeepsTheSelectionTests : OwnedViewModels
{
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;

    public override void Dispose()
    {
        PaneViewModel.Trash = _trashBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    // ---- the whole gesture, in the window it has to work in -----------------

    /// <summary>
    /// Whether a file drag is in flight, read off the window's own flag.
    ///
    /// Through reflection, and deliberately: the flag is a private detail of
    /// how the window sequences one gesture, and widening it for a test would
    /// invite the rest of the application to start asking.
    /// </summary>
    private static bool Dragging(MainWindow window)
        => (bool)typeof(MainWindow)
            .GetField("_dragging", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    /// <summary>
    /// Waits for the assertion's OWN subject — how many rows the pane says are
    /// selected — under a wall-clock ceiling.
    ///
    /// Not a fixed number of dispatcher turns: the drag is an async method that
    /// awaits the storage provider before it reaches the restore, so how many
    /// turns that takes is not this test's to know. And the transition cannot
    /// satisfy the condition either, because the caller asserts the collapse to
    /// one row first: three selected can only mean the restore ran.
    /// </summary>
    private static async Task<int> SelectionSettlesAt(PaneViewModel pane, int count)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < until)
        {
            Settle();

            if (pane.Selection.Count == count) return count;

            await Task.Delay(10);
        }

        Settle();

        return pane.Selection.Count;
    }

    /// <summary>
    /// The pane's own multiple-selection list, laid out and ready to be
    /// pressed. Shared by the three window tests, which differ only in what
    /// they navigate to and which modifier they hold.
    /// </summary>
    private static ListBox LaidOut(MainWindow window, PaneViewModel pane)
    {
        // Details, explicitly: the view a new tab opens in is remembered across
        // windows, so a layout test that ran earlier in this assembly would
        // otherwise decide what these ones measure.
        pane.View = ViewMode.Details;

        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Settle();

        return window.GetVisualDescendants().OfType<ListBox>()
            .First(l => l.IsVisible
                        && ReferenceEquals(l.DataContext, pane)
                        && l.SelectionMode.HasFlag(SelectionMode.Multiple));
    }

    /// <summary>Selects every row and hands back the middle one's centre in
    /// window coordinates.</summary>
    private static Point AllSelectedAndPressing(
        MainWindow window, PaneViewModel pane, ListBox list, int index)
    {
        foreach (var row in pane.DetailsEntries) list.SelectedItems!.Add(row);

        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Settle();

        var container = (Control)list.ContainerFromIndex(index)!;

        return container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
    }

    /// <summary>
    /// **The finding itself, driven through the real window.** Three files
    /// selected, a press on one of them, a move past the drag threshold and an
    /// Escape to end the drag — the press's collapse to one row is asserted in
    /// between, so this cannot pass because nothing was ever collapsed.
    ///
    /// A REAL drag, and it really runs: with no platform drag source under it
    /// Avalonia falls back to its own in-process one, which tracks the pointer
    /// and takes Escape as a cancel — so <c>DoDragDropAsync</c> is entered and
    /// returns here exactly as it does on a desktop. Escape rather than a
    /// release over a folder, because this test must not move a file to prove
    /// something about a selection, and cancelling is the case the finding
    /// names: the drag achieved nothing and the rows it carried should still be
    /// picked out. The files are counted at the end to say so.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_leaves_the_rows_it_carried_still_selected()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-dragsel-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(root, name), name);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            Settle();

            var list = LaidOut(window, pane);

            Assert.Equal(3, pane.Entries.Count);

            var at = AllSelectedAndPressing(window, pane, list, 0);

            Assert.Equal(3, pane.Selection.Count);

            window.MouseDown(at, MouseButton.Left);
            Settle();

            // The premise, and the fault in one line: without it a green run
            // here would only mean the list never collapsed anything.
            Assert.Single(pane.Selection);

            // Past the six-pixel threshold, with the button still down — below
            // it the window reads a click that wobbled and starts no drag.
            window.MouseMove(
                new Point(at.X + 40, at.Y + 40), RawInputModifiers.LeftMouseButton);

            // The drag really is in flight, so what follows measures a drag
            // ending rather than a press that armed nothing. Read straight
            // after the move with no wait, because the flag is set before the
            // method's first await.
            Assert.True(Dragging(window), "no drag started, so there is nothing to come back from");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);

            Assert.Equal(3, await SelectionSettlesAt(pane, 3));

            window.MouseUp(new Point(at.X + 40, at.Y + 40), MouseButton.Left);
            Settle();

            // And it was a cancelled drag that put them back, not a refresh
            // after an operation: nothing on disk moved.
            Assert.Equal(
                new[] { "a.txt", "b.txt", "c.txt" },
                Directory.GetFiles(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// **The bin refuses the drag before it starts, and used to keep the
    /// collapse.** CanDragOut answers false for a trash listing and the method
    /// returned there — above the try, so the restore in the finally was never
    /// reached and the one listing where a drag can NEVER succeed was the one
    /// that always ended with two of three rows silently deselected.
    ///
    /// Measured through the same window and the same press as the cancelled
    /// drag above; the difference is only the listing under it. No Escape and
    /// no release is needed, because no drag ever goes into flight — the flag
    /// is read to say so.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_the_bin_refuses_leaves_every_row_selected()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            // After the window, which assigns the platform's own bin to the
            // static in its constructor.
            PaneViewModel.Trash = new Bin("a.txt", "b.txt", "c.txt");

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(VirtualPaths.Trash);
            Settle();

            var list = LaidOut(window, pane);

            Assert.Equal(3, pane.Entries.Count);

            var at = AllSelectedAndPressing(window, pane, list, 0);

            Assert.Equal(3, pane.Selection.Count);

            window.MouseDown(at, MouseButton.Left);
            Settle();

            // The premise: the press really did collapse the three to one, so a
            // green run cannot mean the listing never collapsed anything.
            Assert.Single(pane.Selection);

            window.MouseMove(
                new Point(at.X + 40, at.Y + 40), RawInputModifiers.LeftMouseButton);

            Assert.Equal(3, await SelectionSettlesAt(pane, 3));

            // And it really was the refusal that ended it, not a drag that ran:
            // nothing went into flight, and the status bar says why.
            Assert.False(Dragging(window), "the bin is supposed to refuse this drag");
            Assert.Contains("use Restore", pane.Status);

            window.MouseUp(new Point(at.X + 40, at.Y + 40), MouseButton.Left);
            Settle();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A press held with Ctrl arms the snapshot too, and the whole selection
    /// comes back.** Avalonia's list drops the pressed row out of the selection
    /// on the PRESS, so a Ctrl-drag of three carried the pre-press three while
    /// the window showed two — and the fix is to make the window agree, not to
    /// stop snapshotting.
    ///
    /// Measured both ways before choosing. Gating the snapshot on the modifier
    /// instead — the obvious alternative — was run against a real drop: three
    /// files selected, Ctrl held, dropped on a folder row in the same pane, and
    /// only a.txt and b.txt arrived while c.txt stayed behind. Ctrl-drag is the
    /// copy gesture by volume, and losing a file out of it is worse than any
    /// question about what the rows look like. Ungated, the same drop carried
    /// all three. The deselect a bare Ctrl-click means is untouched either way:
    /// below the six-pixel threshold no drag runs, so nothing is restored.
    /// </summary>
    [AvaloniaFact]
    public async Task A_ctrl_press_that_becomes_a_drag_keeps_the_whole_selection()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-dragctrl-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(root, name), name);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            Settle();

            var list = LaidOut(window, pane);

            Assert.Equal(3, pane.Entries.Count);

            var at = AllSelectedAndPressing(window, pane, list, 1);

            Assert.Equal(3, pane.Selection.Count);

            window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
            Settle();

            // The premise, and it is a different collapse from the plain
            // press's: Ctrl takes the pressed row OUT rather than leaving it
            // alone, so two remain and b.txt is the one that went.
            Assert.Equal(
                new[] { "a.txt", "c.txt" },
                pane.Selection.Select(e => e.Name).Order(StringComparer.Ordinal));

            window.MouseMove(
                new Point(at.X + 40, at.Y + 40),
                RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);

            Assert.True(Dragging(window), "no drag started, so there is nothing to come back from");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);

            Assert.Equal(3, await SelectionSettlesAt(pane, 3));

            Assert.Equal(
                new[] { "a.txt", "b.txt", "c.txt" },
                pane.Selection.Select(e => e.Name).Order(StringComparer.Ordinal));

            window.MouseUp(new Point(at.X + 40, at.Y + 40), MouseButton.Left,
                RawInputModifiers.Control);
            Settle();
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>A bin holding the names it was built with, and nothing
    /// else.</summary>
    private sealed class Bin(params string[] names) : ITrashMaintenance
    {
        public IReadOnlyList<TrashedItem> List() =>
            [.. names.Select(n => new TrashedItem(
                n, Path.Combine("C:\\gone", n), "payload/" + n,
                DateTimeOffset.UnixEpoch, 1, false))];

        public void Delete(string trashName) { }
        public string Restore(string trashName) => trashName;

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    // ---- and the rule the pane applies to a remembered selection ------------

    private async Task<PaneViewModel> Listing(params string[] names)
    {
        var fs = new Folder(names);
        var pane = Own(new PaneViewModel(fs) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Folder.Root);

        Assert.Equal(names.Length, pane.Entries.Count);

        return pane;
    }

    private static string At(string name) => Path.Combine(Folder.Root, name);

    /// <summary>The half the window leans on: the rows named come back, not
    /// just the one the press had left behind.</summary>
    [AvaloniaFact]
    public async Task The_named_rows_come_back()
    {
        var pane = await Listing("a.txt", "b.txt", "c.txt");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "a.txt"));

        Assert.Single(pane.Selection);

        pane.ReselectPaths([At("a.txt"), At("b.txt"), At("c.txt")]);

        Assert.Equal(
            new[] { "a.txt", "b.txt", "c.txt" },
            pane.Selection.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// **And a listing the rows have left keeps whatever it has now.** Reselect
    /// clears the selection before it re-adds, so handing it two paths the
    /// listing no longer has would empty what the listing holds now instead of
    /// putting anything back.
    ///
    /// Driven straight at <c>ReselectPaths</c>, and it has to be: the real drag
    /// route was measured NOT to reach this state. A drop hands the move to a
    /// background operation and returns at once, so when the window's finally
    /// called this after three files were dropped on a folder row in the same
    /// pane, all four rows were still listed and all three wanted paths were
    /// among them. This pins the floor under that call rather than a case the
    /// drag was seen in.
    /// </summary>
    [AvaloniaFact]
    public async Task A_selection_of_rows_that_have_gone_is_not_restored()
    {
        var pane = await Listing("a.txt", "b.txt", "c.txt");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "c.txt"));

        pane.ReselectPaths([At("moved-one.txt"), At("moved-two.txt")]);

        Assert.Equal("c.txt", Assert.Single(pane.Selection).Name);
    }

    /// <summary>A listing that lists what it was built with, and nothing
    /// else.</summary>
    private sealed class Folder(params string[] names) : IFileSystemProvider
    {
        public static string Root => Path.Combine(Path.GetTempPath(), "vaktari-dragsel-rows");

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return [.. names.Select(n => new FileEntry(
                n, Path.Combine(path, n), 4, DateTimeOffset.UnixEpoch, EntryFlags.None))];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
