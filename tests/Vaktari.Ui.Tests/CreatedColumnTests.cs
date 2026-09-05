using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The Created column.
///
/// **The listing could say four things about a file and "when was this made"
/// was not one of them** — name, type, size, modified, and nothing else
/// anywhere in the window. The entry the OS hands back for every row has
/// carried a creation time all along and <see cref="FileEntry"/> dropped it on
/// the floor, so the question could not be answered by turning something on:
/// there was nothing to turn on.
///
/// Everything here is the same shape as the type column beside it — off until
/// asked for, per pane, in the session, under the width rule — so these tests
/// are about the parts that are NEW rather than about the chooser, which
/// <see cref="ColumnChooserTests"/> already holds:
///
///   * the date reaching the cell is the creation date and not the modified
///     one, which is the whole point and the easiest thing to get silently
///     wrong;
///   * the heading sorts by it, and hides with it;
///   * the seventh slot really exists in both of the two hand-matched grids.
/// </summary>
public sealed class CreatedColumnTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private PaneViewModel Pane(double width = 1400)
        => Own(new PaneViewModel(new Canned([]), null, null) { ViewportWidth = width });

    // ---- off until it is asked for ------------------------------------------

    /// <summary>Room for a column is not a request for it — the rule the type
    /// column established, and a new column appearing uninvited is the one way
    /// this could annoy somebody who never wanted it.</summary>
    [AvaloniaFact]
    public void The_created_column_is_off_until_it_is_asked_for()
    {
        var pane = Pane(width: 2400);

        Assert.False(pane.ShowCreated);
        Assert.False(pane.IsCreatedColumnShown);
    }

    [AvaloniaFact]
    public void Choosing_it_puts_it_on_screen()
    {
        var pane = Pane(width: 2400);

        pane.ToggleCreatedColumnCommand.Execute(null);

        Assert.True(pane.ShowCreated);
        Assert.True(pane.IsCreatedColumnShown);
    }

    /// <summary>
    /// **The width rule keeps the last word, and of the four thresholds the
    /// chooser can be held to this one is the highest.** 700px is wide enough
    /// for size and modified and not for this one, so a pane that has dropped
    /// ONLY the new column is the state under test — a threshold that stopped
    /// applying, or one copied from the column to its left, both fail here.
    /// </summary>
    [AvaloniaFact]
    public void A_chosen_created_column_still_gives_way_in_a_narrow_pane()
    {
        var pane = Pane(width: 700);

        pane.ToggleCreatedColumnCommand.Execute(null);

        Assert.False(pane.ShowCreated, "a chosen column ignored the width rule");

        // The pane is only too narrow for THIS column, so the assertion above
        // cannot be passing because everything is off.
        Assert.True(pane.ShowSize);
        Assert.True(pane.ShowModified);

        // And the tick still says what was chosen, so the menu explains the gap
        // rather than hiding it.
        Assert.True(pane.IsCreatedColumnShown);
    }

    /// <summary>
    /// The visibility is computed, so nothing raises it on its own. Miss the
    /// fan-out and the tick moves while the column does not.
    /// </summary>
    [AvaloniaFact]
    public void Toggling_tells_the_view()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.ToggleCreatedColumnCommand.Execute(null);

        Assert.Contains(nameof(PaneViewModel.ShowCreated), raised);
        Assert.Contains(nameof(PaneViewModel.IsCreatedColumnShown), raised);
    }

    // ---- it travels with the tab --------------------------------------------

    [AvaloniaFact]
    public void The_choice_round_trips_through_the_session()
    {
        var before = Pane();

        before.ToggleCreatedColumnCommand.Execute(null);

        var after = Pane();

        after.RestoreFrom(before.ToTabState());

        Assert.True(after.ShowCreated);
    }

    /// <summary>
    /// **A session written before this column existed must not grow one.** An
    /// absent key deserialises as default(T) — which is why ShowCreated is
    /// phrased so that false is what the tab was already showing. Through the
    /// same source-generated context the real store uses.
    /// </summary>
    [AvaloniaFact]
    public void A_session_that_never_heard_of_it_does_not_grow_a_column()
    {
        var json = "{\"version\":13,\"windows\":[{\"panes\":[{\"tabs\":[{\"path\":\"" +
                   Path.GetTempPath().Replace("\\", "\\\\") + "\"}]}]}]}";

        var session = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState);

        Assert.NotNull(session);

        var pane = Pane();

        pane.RestoreFrom(session!.Windows[0].Panes[0].Tabs[0]);

        Assert.False(pane.ShowCreated, "a column appeared for everyone who upgraded");
    }

    /// <summary>
    /// **A property ToTabState writes but the shell never marks dirty only
    /// persists when something else changes first.** So this goes through the
    /// shell rather than the pane, and asks the store whether it heard.
    /// </summary>
    [AvaloniaFact]
    public void Changing_the_choice_is_worth_saving()
    {
        var store = new Listening();
        var shell = Own(new ShellViewModel(new Canned([]), store: store));

        shell.Start(null, Path.GetTempPath());

        var heard = store.Heard;

        shell.ActiveTab!.ToggleCreatedColumnCommand.Execute(null);

        Assert.True(store.Heard > heard, "the session store was not told");
        Assert.True(store.Last!.Windows[0].Panes[0].Tabs[0].ShowCreated,
                    "it was told, but not the new value");
    }

    // ---- the date in the cell is the creation date --------------------------

    /// <summary>
    /// **The one thing a Created column can get silently wrong is showing the
    /// modified date.** Both are DateTimeOffsets on the same record, one line
    /// apart in the markup, and every row in a folder full of files copied in
    /// one go would look plausible either way.
    ///
    /// So the two rows here have the SAME modified time and different creation
    /// times, and their NAMES run the other way — the newest file sorts first
    /// alphabetically and last by every other key here. Both halves matter and
    /// the second was learned the hard way: named the obvious way round, this
    /// test went on passing with the creation comparer deleted, because the
    /// name tie-break underneath it produced the very order it was asserting.
    /// </summary>
    [AvaloniaFact]
    public async Task Sorting_by_created_reads_the_creation_date()
    {
        var touched = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var pane = Own(new PaneViewModel(new Canned([
            Row("z-made-first.txt",  touched, made: new DateTimeOffset(2019, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            Row("a-made-second.txt", touched, made: new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero)),
        ]), null, null)
        {
            ViewportWidth = 1400,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SortByCommand.Execute("created");

        // Newest first, because a date column's first click is descending
        // everywhere people learned it — see SortDefaults.
        Assert.True(pane.SortDescending);
        Assert.Equal("a-made-second.txt", pane.Entries[0].Name);

        pane.SortByCommand.Execute("created");

        Assert.Equal("z-made-first.txt", pane.Entries[0].Name);
    }

    /// <summary>The heading needs an arrow like the four beside it, and an
    /// arrow that never moves is worse than none.</summary>
    [AvaloniaFact]
    public void The_created_heading_gets_a_sort_arrow()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.SortByCommand.Execute("created");

        Assert.True(pane.IsSortedByCreated);
        Assert.NotEqual("", pane.CreatedSortGlyph);
        Assert.Contains(nameof(PaneViewModel.CreatedSortGlyph), raised);

        // The radio dot beside "Sort by > Created" reads the same fact through
        // a OneWay binding, so it moves only if this is raised too.
        Assert.Contains(nameof(PaneViewModel.IsSortedByCreated), raised);
    }

    /// <summary>
    /// The menu route is the same sort as the heading, direction included. A
    /// second copy of SortBy's body is exactly where the first-click direction
    /// gets forgotten — the menu resetting to oldest-first while the heading it
    /// mirrors had learned better.
    /// </summary>
    [AvaloniaFact]
    public void The_menu_sorts_the_way_the_heading_does()
    {
        var pane = Pane();

        pane.SortByCreatedCommand.Execute(null);

        Assert.True(pane.IsSortedByCreated);
        Assert.True(pane.SortDescending, "the menu sorted oldest-first");
    }

    // ---- the two grids nobody was checking ----------------------------------

    /// <summary>
    /// The header and the rows are two separate grids kept in step by hand, and
    /// this column is the seventh slot in both. Renumbering — or adding the
    /// cell to one grid and not the other — is the edit the row template warns
    /// goes wrong quietly. <see cref="ColumnChooserTests"/> pins that the two
    /// column-definition lists still match; this pins that something actually
    /// landed in the new slot on both sides.
    /// </summary>
    [AvaloniaFact]
    public void The_created_column_took_the_seventh_slot_in_both_grids()
    {
        var inColumnSix = Markup()
            .Descendants()
            .Count(e => (string?)e.Attribute("Grid.Column") == "6");

        Assert.Equal(2, inColumnSix);
    }

    /// <summary>
    /// **The heading is a control and not a caption: it hides with its column,
    /// it sorts when clicked, it says what it is to a screen reader, and its
    /// arrow is its own.** Four things on one Button, none of which anything
    /// else in the suite reads — the sort tests above reach <c>SortByCommand</c>
    /// directly rather than through the markup, and <see cref="LabelCasingTests"/>
    /// finds this button by its CommandParameter alone and then only looks at
    /// the word inside it.
    ///
    /// The visibility is the one that costs, and it costs most when the column
    /// is OFF — which is what every upgraded session starts as. The seventh
    /// ColumnDefinition is in both grids either way, so a heading that does not
    /// hide fills a slot its cells leave empty. Measured in a headless window
    /// at 1800px with the column off: with the binding, the Modified heading
    /// and the Modified cells both sat at x=1632 and the name column was 1261
    /// wide; with it deleted the heading moved to x=1482 — a whole column left
    /// of the dates it names — and the name column, the only one that
    /// stretches, gave up those 150px to a heading with nothing under it.
    /// </summary>
    [AvaloniaFact]
    public void The_created_heading_hides_with_its_column_and_sorts()
    {
        var heading = Markup()
            .Descendants(Avalonia + "Button")
            .Single(b => (string?)b.Attribute("CommandParameter") == "created");

        Assert.Equal("{Binding ShowCreated}", (string?)heading.Attribute("IsVisible"));
        Assert.Equal("{Binding SortByCommand}", (string?)heading.Attribute("Command"));
        Assert.Equal("Sort by date created",
                     (string?)heading.Attribute("AutomationProperties.Name"));

        // And the arrow is this column's own. The_created_heading_gets_a_sort_arrow
        // above reads the view model, which would go on being right while the
        // heading drew the neighbouring column's glyph.
        Assert.Contains(heading.Descendants(Avalonia + "Run"),
                        r => (string?)r.Attribute("Text") == "{Binding CreatedSortGlyph}");
    }

    /// <summary>
    /// **The cell draws the creation date and not the modified one.** They are
    /// two DateTimeOffsets on the same record, one line apart in the markup and
    /// through the same converter, so the wrong one here is a column that looks
    /// entirely reasonable and duplicates its neighbour.
    ///
    /// The view model test above cannot see this: it reads the order the rows
    /// are in, which is the comparer's business, and the comparer would be
    /// right while the cell was wrong.
    /// </summary>
    [AvaloniaFact]
    public void The_cell_shows_the_creation_date()
    {
        var cell = Markup()
            .Descendants(Avalonia + "TextBlock")
            .Single(e => (string?)e.Attribute("Grid.Column") == "6");

        Assert.Contains("CreationTime", (string?)cell.Attribute("Text"));
        Assert.DoesNotContain("LastWriteTime", (string?)cell.Attribute("Text"));

        // And it appears exactly when the HEADING does. Spelled out in full
        // rather than looked for as a substring, because "ShowCreatedColumn"
        // contains "ShowCreated": a substring check cannot tell the width-ruled
        // property from the raw chooser flag, which is the one distinction this
        // binding exists to make. Measured with the flag in its place, in a
        // headless 900px window with the column chosen: the cells kept drawing
        // while the heading was correctly gone, and that put the Modified cells
        // at x=582 under a Modified heading still at x=732.
        Assert.Equal(
            "{Binding $parent[ListBox].((vm:PaneViewModel)DataContext).ShowCreated}",
            (string?)cell.Attribute("IsVisible"));
    }

    /// <summary>
    /// **A row with no creation date must draw an empty cell**, not a date in
    /// year one. This PC's drives, both Recent listings and the path bar build
    /// their entries from something other than a directory and have no creation
    /// date to give, so they carry default.
    ///
    /// The converter's "no answer" rule was written for the epoch a drive
    /// carries in its modified slot; default is below the epoch and lands in
    /// the same branch, which is why the new column needed no rule of its own —
    /// and this is what says so.
    /// </summary>
    [AvaloniaFact]
    public void A_row_with_no_creation_date_draws_nothing()
        => Assert.Equal("", FileConverters.Modified.Convert(
            default(DateTimeOffset), typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Sorting by it is reachable from the menu as well as the heading, the way
    /// the other four are — the heading is only in Details, and the menu is
    /// where sorting is reachable from the other two layouts.
    /// </summary>
    [AvaloniaFact]
    public void The_sort_menu_offers_it_too()
        => Assert.Contains(
            Markup().Descendants(Avalonia + "MenuItem"),
            m => (string?)m.Attribute("Command") == "{Binding ActiveTab.SortByCreatedCommand}");

    /// <summary>
    /// **A width the resource dictionary does not define draws as nothing.**
    /// Both cells ask for their width by name, and PaneScale is where those
    /// names are answered — a key that is spelled differently in the two places
    /// is a column with no width and no error anywhere.
    /// </summary>
    [AvaloniaFact]
    public void The_width_the_cells_ask_for_is_a_width_PaneScale_computes()
    {
        var keys = Markup()
            .Descendants()
            .Where(e => (string?)e.Attribute("Grid.Column") == "6")
            .Select(e => (string?)e.Attribute("Width"))
            .ToList();

        Assert.Equal(2, keys.Count);

        foreach (var key in keys)
        {
            Assert.Equal("{DynamicResource ColCreated}", key);

            Assert.Contains(PaneScale.Compute(1.0, 1.0), m => m.Key == "ColCreated");
        }

        // And it scales with the text, which is the whole reason these live in
        // PaneScale rather than as literals in the markup.
        Assert.Equal(
            PaneScale.Compute(1.0, 1.0).Single(m => m.Key == "ColCreated").Value * 2,
            PaneScale.Compute(2.0, 1.0).Single(m => m.Key == "ColCreated").Value);
    }

    // ---- helpers -------------------------------------------------------------

    private static FileEntry Row(string name, DateTimeOffset touched, DateTimeOffset made)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1, touched, EntryFlags.None, made);

    private static XDocument Markup()
        => XDocument.Load(Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }

    private sealed class Listening : ISessionStore
    {
        public int Heard { get; private set; }
        public SessionState? Last { get; private set; }

        public SessionState? Load() => null;

        public void NotifyChanged(SessionState state)
        {
            Heard++;
            Last = state;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Canned(IReadOnlyList<FileEntry> rows) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return rows;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
