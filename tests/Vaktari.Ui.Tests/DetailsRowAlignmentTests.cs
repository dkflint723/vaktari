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
/// the name filling and with it pinned, at 100% and at 125%.
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

    private async Task<(MainWindow W, PaneViewModel Pane)> Open(int width = 1600)
    {
        UseSearch(PaneViewModel.Search);
        var w = _window = new MainWindow { Width = width, Height = 800 };
        w.Show();
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
    public async Task Every_details_listing_lines_up_filling_and_pinned()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "child.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "n");

        var (w, pane) = await Open();
        var bad = new List<string>();

        foreach (var zoom in new[] { 1.0, 1.25 })
        {
            pane.FontScale = zoom;

            foreach (var pinned in new[] { false, true })
            {
                pane.ResetColumnWidthsCommand.Execute(null);
                if (pinned)
                {
                    pane.SetColumnWidth(DetailsColumn.Name, 320);
                    pane.SetColumnWidth(DetailsColumn.Size, 150);
                }
                var tag = $"zoom {zoom} {(pinned ? "pinned" : "filling")}";

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
}
