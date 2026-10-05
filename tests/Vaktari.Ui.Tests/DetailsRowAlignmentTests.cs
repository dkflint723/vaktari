using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Every listing that draws details rows keeps them under its headings once
/// the name has a width of its own** (columns-resize QA). The heading's grid
/// and each row's grid read the name's width from one per-tab resource, and a
/// nested row takes its indent off it; the listings that add a Path column —
/// Recent files and the bin — and search results draw their rows from the same
/// template, so each is measured here as well as a folder with a row opened in
/// place: the right edge of every column, name to created, on every row, with
/// the name filling, pinned, and giving way to a row narrower than the one it
/// was chosen in, at 100% and at 125%, with enough rows for the list to scroll.
/// </summary>
public sealed class DetailsRowAlignmentTests : OwnedViewModels
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-rowalign").FullName;
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;
    private readonly IRecentStore? _recentsBefore = PaneViewModel.Recents;
    private MainWindow? _window;

    public DetailsRowAlignmentTests(ITestOutputHelper output) => _out = output;

    public override void Dispose()
    {
        if (_window?.DataContext is ShellViewModel shell) shell.ActiveTab?.ResetColumnWidthsCommand.Execute(null);
        _window?.Close();
        Dispatcher.UIThread.RunJobs();
        base.Dispose();
        PaneViewModel.Trash = _trashBefore;
        PaneViewModel.Recents = _recentsBefore;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Bin(params string[] names) : ITrashMaintenance
    {
        public IReadOnlyList<TrashedItem> List() =>
            [.. names.Select(n => new TrashedItem(n, Path.Combine(Path.GetTempPath(), "vaktari-gone", n), "payload/" + n,
                DateTimeOffset.UnixEpoch, 1, false))];
        public void Delete(string trashName) { }
        public string Restore(string trashName) => trashName;
        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct) => ValueTask.FromResult(TrashSweepResult.Nothing);
        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct) => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    private sealed class Remembered(string file) : IRecentStore
    {
        public void Record(string path, RecentKind kind) { }
        public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count)
            => kind == RecentKind.File ? [new RecentEntry(file, RecentKind.File, DateTimeOffset.Now)] : [];
        public void Forget(string path) { }
        public int Count => 1;
        public int ForgetAll() => 0;
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class Finds(params FileEntry[] hits) : ISearchProvider
    {
        public string BackendName => "test";
        public bool SupportsContentSearch => false;
        public async IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var hit in hits) yield return hit;
        }
    }

    /// <summary>A device scaling for the headless window, through the platform's
    /// own setter, asserted to have taken: a renamed setter would otherwise
    /// leave the scaled cases quietly running at 100%.</summary>
    private static void SetScaling(Window w, double scaling)
    {
        var impl = w.PlatformImpl!;
        impl.GetType().GetProperty("RenderScaling",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance)!.SetValue(impl, scaling);
        (impl.GetType().GetProperty("ScalingChanged")?.GetValue(impl) as Action<double>)?.Invoke(scaling);

        Assert.Equal(scaling, w.RenderScaling);
    }

    private static void Pump(Window w)
    {
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
    }

    private static Grid? HeadingGrid(Window w, PaneViewModel pane)
        => w.GetVisualDescendants().OfType<Thumb>()
            .FirstOrDefault(t => t.Classes.Contains("columnGrip") && Equals(t.Tag, "Name") && ReferenceEquals(t.DataContext, pane))
            ?.Parent as Grid;

    private static List<(string Name, Grid Grid)> RowGrids(Window w, PaneViewModel pane)
        => [.. w.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsVisible && i.DataContext is FileEntry && i.FindAncestorOfType<ListBox>()?.DataContext == pane)
            .Select(i => (((FileEntry)i.DataContext!).Name,
                          i.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Margin == new Thickness(12, 0, 18, 0))))
            .Where(x => x.Item2 is not null)
            .Select(x => (x.Name, x.Item2!))];

    private static double Right(Grid g, int col, Visual root)
        => g.TranslatePoint(default, root)!.Value.X + g.ColumnDefinitions.Take(col + 1).Sum(c => c.ActualWidth);

    private async Task<(MainWindow W, PaneViewModel Pane)> Open(int width = 1600, double scaling = 1.0)
    {
        UseSearch(PaneViewModel.Search);
        var w = _window = new MainWindow { Width = width, Height = 800 };
        w.Show();
        if (scaling != 1.0) SetScaling(w, scaling);
        Pump(w);
        var shell = Assert.IsType<ShellViewModel>(w.DataContext);
        if (shell.IsSplit) { shell.ToggleSplit(); Pump(w); }
        var pane = shell.ActiveTab!;
        pane.View = ViewMode.Details;
        pane.ShowTypeColumn = true;
        pane.ShowCreatedColumn = true;
        pane.ResetColumnWidthsCommand.Execute(null);
        await Task.Yield();
        return (w, pane);
    }

    private async Task<List<string>> Compare(MainWindow w, PaneViewModel pane, string what)
    {
        var bad = new List<string>();
        List<(string Name, Grid Grid)> rows = [];
        for (var i = 0; i < 300 && rows.Count == 0; i++) { Pump(w); rows = RowGrids(w, pane); if (rows.Count == 0) await Task.Delay(10); }
        var head = HeadingGrid(w, pane);
        Assert.NotNull(head);
        Assert.NotEmpty(rows);

        foreach (var (name, row) in rows)
        {
            var parts = new List<string>();
            for (var c = 1; c <= 6; c++)
            {
                var h = Right(head!, c, w);
                var r = Right(row, c, w);
                parts.Add($"c{c} {h:N1}/{r:N1}");
                if (Math.Abs(h - r) > 0.6) bad.Add($"{what} row '{name}' column {c}: heading {h:N1} row {r:N1}");
            }
            _out.WriteLine($"{what} '{name}': {string.Join("  ", parts)}");
        }
        return bad;
    }

    [AvaloniaFact]
    public async Task Every_details_listing_lines_up_filling_pinned_and_giving_way()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "child.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "n");

        // Enough rows for the listing to scroll, so a scroll bar that took
        // room from the rows and not the heading would show here.
        for (var i = 0; i < 120; i++) File.WriteAllText(Path.Combine(_root, $"row{i:000}.txt"), "r");

        var (w, pane) = await Open();
        var bad = new List<string>();

        foreach (var zoom in new[] { 1.0, 1.25 })
        {
            pane.FontScale = zoom;

            foreach (var state in new[] { "filling", "pinned", "giving way" })
            {
                pane.ResetColumnWidthsCommand.Execute(null);
                if (state == "pinned")
                {
                    pane.SetColumnWidth(DetailsColumn.Name, 320);
                    pane.SetColumnWidth(DetailsColumn.Size, 150);
                }
                else if (state == "giving way")
                {
                    // A name chosen in a row much wider than this one: the
                    // heading and every row each work out how far it gives way
                    // from their own width, so a row narrower than the heading
                    // would draw its name narrower.
                    pane.ColumnWidths = pane.ColumnWidths with { Name = 700, Span = 1500, Size = 150 };
                }
                var tag = $"zoom {zoom} {state}";

                await pane.NavigateAsync(_root);
                await pane.RefreshAsync();
                var sub = pane.Entries.First(e => e.Name == "sub");
                if (!pane.Indents.ContainsKey(Path.Combine(_root, "sub", "child.txt"))) await pane.ToggleExpandAsync(sub);
                bad.AddRange(await Compare(w, pane, "folder+nested " + tag));

                PaneViewModel.Recents = new Remembered(Path.Combine(_root, "notes.txt"));
                await pane.NavigateAsync(VirtualPaths.Files);
                bad.AddRange(await Compare(w, pane, "recent " + tag));

                PaneViewModel.Trash = new Bin("gone.txt", "also.txt");
                await pane.NavigateAsync(VirtualPaths.Trash);
                bad.AddRange(await Compare(w, pane, "bin " + tag));

                UseSearch(new Finds(new FileEntry("notes.txt", Path.Combine(_root, "notes.txt"), 1, DateTimeOffset.Now, EntryFlags.None)));
                await pane.NavigateAsync(VirtualPaths.Search("notes", _root, scoped: false));
                bad.AddRange(await Compare(w, pane, "search " + tag));
                UseSearch(PaneViewModel.Search);
            }
        }

        foreach (var b in bad) _out.WriteLine("MISALIGNED " + b);
        Assert.Empty(bad);
    }

    /// <summary>
    /// **A row nested under an opened folder may sit up to a pixel off at a
    /// fractional device scaling, scrolled or not.** Its indent is a Border in
    /// the first column, rounded to the device pixel on its own, while the name
    /// column is narrowed by the indent unrounded; at 115% device scaling and
    /// 125% zoom that measured 0.96 at offset 0, where nothing has scrolled. It
    /// belongs to the indent, not to the scrolling, and is held here so it
    /// cannot grow unseen.
    /// </summary>
    private const double NestedTolerance = 1.0;

    /// <summary>
    /// The rows' scroller and the headings' (ColumnScroll).
    /// </summary>
    private static ScrollViewer RowsScroller(Window w, PaneViewModel pane)
        => w.GetVisualDescendants().OfType<ListBox>()
            .First(l => ReferenceEquals(l.DataContext, pane) && l.ItemsSource == pane.DetailsEntries)
            .GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>
    /// **Every listing stays under its headings while the columns are scrolled
    /// sideways**, at device scaling as well as zoom — 115% is where QA saw
    /// the drift and the jitter of the earlier rounds, and the headless window
    /// can be given it. Each offset is set and the window laid out ONCE, with
    /// nothing run on the dispatcher, because that is what a frame shows: a
    /// heading that caught up a job later would pass a test that settled
    /// first. The two scrollers must also agree on everything the offset
    /// depends on — the extent, the viewport and the offset itself; with docked
    /// rather than overlaid scroll bars the rows' viewport would be a bar
    /// narrower than the headings'.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.15, 1.0)]
    [InlineData(1.25, 1.0)]
    [InlineData(1.15, 1.25)]
    [InlineData(1.25, 1.25)]
    public async Task Every_details_listing_lines_up_while_scrolled(double scaling, double zoom)
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "child.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "n");

        for (var i = 0; i < 60; i++) File.WriteAllText(Path.Combine(_root, $"row{i:000}.txt"), "r");

        var (w, pane) = await Open(scaling: scaling);

        pane.FontScale = zoom;

        var bad = new List<string>();

        foreach (var state in new[] { "pinned wide", "a column past the edge", "filling with wide columns" })
        {
            pane.ResetColumnWidthsCommand.Execute(null);

            pane.ColumnWidths = state switch
            {
                "pinned wide" => pane.ColumnWidths with { Name = 1400, Size = 133.3, Type = 97.7 },
                "a column past the edge" => pane.ColumnWidths with { Name = 500, Span = 1300, Size = 600 },
                _ => pane.ColumnWidths with { Size = 600, Modified = 600 },
            };

            foreach (var listing in new[] { "folder", "recent", "bin", "search" })
            {
                var tag = $"{scaling}x{zoom} {state} {listing}";

                switch (listing)
                {
                    case "folder":
                        await pane.NavigateAsync(_root);
                        await pane.RefreshAsync();
                        var sub = pane.Entries.First(e => e.Name == "sub");
                        if (!pane.Indents.ContainsKey(Path.Combine(_root, "sub", "child.txt"))) await pane.ToggleExpandAsync(sub);
                        break;

                    case "recent":
                        PaneViewModel.Recents = new Remembered(Path.Combine(_root, "notes.txt"));
                        await pane.NavigateAsync(VirtualPaths.Files);
                        break;

                    case "bin":
                        PaneViewModel.Trash = new Bin("gone.txt", "also.txt");
                        await pane.NavigateAsync(VirtualPaths.Trash);
                        break;

                    default:
                        UseSearch(new Finds(new FileEntry("notes.txt", Path.Combine(_root, "notes.txt"), 1, DateTimeOffset.Now, EntryFlags.None)));
                        await pane.NavigateAsync(VirtualPaths.Search("notes", _root, scoped: false));
                        UseSearch(PaneViewModel.Search);
                        break;
                }

                // Rows on screen, settled.
                for (var i = 0; i < 300 && RowGrids(w, pane).Count == 0; i++) { Pump(w); await Task.Delay(10); }
                Pump(w);

                if (!pane.ColumnsOverflow)
                {
                    bad.Add($"{tag}: the columns do not overflow, so nothing scrolls");
                    continue;
                }

                var rows = RowsScroller(w, pane);
                var headings = HeadingGrid(w, pane)!.FindAncestorOfType<ScrollViewer>()!;
                var max = rows.Extent.Width - rows.Viewport.Width;

                foreach (var offset in new[] { new Vector(0, 0), new Vector(0.45, 0), new Vector(max / 2, 0),
                                               new Vector(max, 0), new Vector(max / 2, 77.7) })
                {
                    rows.Offset = offset;
                    w.UpdateLayout();

                    var at = $"{tag} at {offset}";

                    if (rows.Extent.Width != headings.Extent.Width)
                        bad.Add($"{at}: extents {rows.Extent.Width} and {headings.Extent.Width}");
                    if (rows.Viewport.Width != headings.Viewport.Width)
                        bad.Add($"{at}: viewports {rows.Viewport.Width} and {headings.Viewport.Width}");
                    if (rows.Offset.X != headings.Offset.X)
                        bad.Add($"{at}: offsets {rows.Offset.X} and {headings.Offset.X}");

                    var head = HeadingGrid(w, pane)!;

                    foreach (var (name, row) in RowGrids(w, pane))
                        for (var c = 1; c <= 6; c++)
                            if (Math.Abs(Right(head, c, w) - Right(row, c, w)) > (DetailsColumns.GetIndent(row) > 0 ? NestedTolerance : 0.6))
                                bad.Add($"{at} row '{name}' column {c}: heading {Right(head, c, w):N2} row {Right(row, c, w):N2} [{string.Join(" ", head.ColumnDefinitions.Select(d => d.ActualWidth.ToString("0.00")))}] vs [{string.Join(" ", row.ColumnDefinitions.Select(d => d.ActualWidth.ToString("0.00")))}] x {head.TranslatePoint(default, w)!.Value.X:0.00}/{row.TranslatePoint(default, w)!.Value.X:0.00}");
                }
            }
        }

        foreach (var b in bad) _out.WriteLine("MISALIGNED " + b);
        Assert.Empty(bad);
    }
}
