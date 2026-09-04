using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Four things the listing showed wrongly.
///
///  - **Descending reversed the rows and left the bands alone**, so a
///    descending listing read Today, Yesterday, This week downwards while the
///    files inside each ran the other way. Two directions in one list.
///  - **Headers did not follow live changes.** They are computed once per
///    rebuild, and a watcher event is not one — so deleting the first row of a
///    band took its heading with it, and a file arriving at the top of one got
///    none.
///  - **A grouping chosen in Details went on reordering grid and compact.**
///    Hiding the menu there stopped it being changed and not applied, so the
///    tiles came up in band order with no headings to explain them and no row
///    on screen to clear it with.
///  - **Hidden files looked exactly like real content** once "show hidden
///    files" was on, which is the whole reason turning it on is survivable in
///    both references and was not here.
/// </summary>
public sealed class GroupingAndGhostingTests : OwnedViewModels
{
    private static FileEntry At(string name, DateTimeOffset when, EntryFlags flags = EntryFlags.None)
        => new(name, "/g/" + name, 1, when, flags);

    private async Task<PaneViewModel> Pane(GroupMode mode, params FileEntry[] entries)
    {
        var pane = Own(new PaneViewModel(new Canned(entries), null, null)
        {
            ViewportWidth = 1400,
            GroupBy = mode,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    private static List<string> Headers(PaneViewModel pane)
        => pane.DetailsEntries.Select(e => pane.HeaderFor(e.FullPath)).OfType<string>().ToList();

    // ---- descending turns the bands over too --------------------------------

    [AvaloniaFact]
    public async Task Descending_reverses_the_bands_as_well_as_the_rows()
    {
        var now = DateTimeOffset.Now;

        var pane = await Pane(
            GroupMode.Modified,
            At("today.txt", now),
            At("older.txt", now.AddDays(-3)));

        var ascending = Headers(pane);

        // Newest first: the first click on a date column is descending, which
        // is what Explorer does and what SortDefaults now encodes.
        pane.SortByCommand.Execute("modified");
        var first = Headers(pane).First();

        pane.SortByCommand.Execute("modified");   // and now oldest first
        var flipped = Headers(pane).First();

        Assert.NotEqual(first, flipped);
        Assert.Equal(2, ascending.Count);
    }

    // ---- headers follow the watcher -----------------------------------------

    /// <summary>
    /// A file arriving at the top of a band is precisely when a heading has to
    /// appear. It did not, so any download into a grouped folder left the
    /// bands wrong until a manual refresh.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_arriving_gets_its_band_heading()
    {
        var now = DateTimeOffset.Now;
        var fs = new Canned([At("beta.txt", now)]);

        var pane = Own(new PaneViewModel(fs, null, null)
        {
            ViewportWidth = 1400,
            GroupBy = GroupMode.Name,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        Assert.Single(Headers(pane));

        // Through the watcher, which is the path that was not recomputing.
        fs.Arriving = At("alpha.txt", now);
        fs.Raise(new FileSystemChange(ChangeKind.Added,
                                      Path.Combine(pane.CurrentPath, "alpha.txt")));

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(60);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var headers = Headers(pane);

        Assert.Equal(2, headers.Count);
        Assert.Equal(["A", "B"], headers);
    }

    // ---- grouping stops at the edge of the details view ----------------------

    /// <summary>
    /// **A grouping chosen in Details went on reordering grid and compact.** The
    /// menu that sets it is hidden outside Details, because only the details row
    /// template can draw a heading — but hiding it only stopped the grouping
    /// being CHANGED there. The ordering was never gated, so the tiles came up in
    /// band order with nothing to say what the runs were, and the row that would
    /// have cleared it was not on screen to click.
    /// </summary>
    [AvaloniaFact]
    public async Task Grouping_does_not_reorder_the_grid()
    {
        var now = DateTimeOffset.Now;

        // Banded by date these two run newest-first and by name they run the
        // other way, so neither order can be mistaken for the other.
        var pane = await Pane(
            GroupMode.Modified,
            At("alpha.txt", now.AddDays(-3)),
            At("zulu.txt", now));

        var banded = pane.DetailsEntries.Select(e => e.Name).ToList();

        Assert.Equal(["zulu.txt", "alpha.txt"], banded);

        pane.ShowAsGrid();

        var tiles = pane.GridEntries.Select(e => e.Name).ToList();

        Assert.Equal(["alpha.txt", "zulu.txt"], tiles);

        // Compact too, and back through Details to get there: only a switch
        // that CROSSES the details boundary resorts, so grid to compact would
        // have carried the grid's order across and asserted nothing. Going the
        // long way round is what makes a fix that special-cased the grid fail
        // here.
        pane.ShowAsDetails();
        pane.ShowAsCompact();

        Assert.Equal(
            ["alpha.txt", "zulu.txt"],
            pane.CompactEntries.Select(e => e.Name).ToList());
    }

    /// <summary>
    /// And is not left holding the headings either. The map is what a row asks,
    /// so a layout that cannot draw one must not have one to hand out.
    /// </summary>
    [AvaloniaFact]
    public async Task The_grid_carries_no_band_headings()
    {
        var now = DateTimeOffset.Now;

        var pane = await Pane(
            GroupMode.Modified,
            At("alpha.txt", now.AddDays(-3)),
            At("zulu.txt", now));

        Assert.Equal(2, Headers(pane).Count);

        pane.ShowAsGrid();

        var headings = pane.GridEntries
            .Select(e => pane.HeaderFor(e.FullPath))
            .OfType<string>()
            .ToList();

        Assert.Empty(headings);
    }

    /// <summary>
    /// Ignored outside Details, not forgotten. Coming back has to bring the
    /// bands with it — clearing the grouping instead would make a look at the
    /// tiles a silent way to lose a setting, and the menu would come back
    /// showing None for something you never set to None.
    /// </summary>
    [AvaloniaFact]
    public async Task Details_gets_its_grouping_back()
    {
        var now = DateTimeOffset.Now;

        var pane = await Pane(
            GroupMode.Modified,
            At("alpha.txt", now.AddDays(-3)),
            At("zulu.txt", now));

        pane.ShowAsGrid();
        pane.ShowAsDetails();

        var order = pane.DetailsEntries.Select(e => e.Name).ToList();

        Assert.Equal(GroupMode.Modified, pane.GroupBy);
        Assert.Equal(["zulu.txt", "alpha.txt"], order);
        Assert.Equal(2, Headers(pane).Count);
    }

    /// <summary>
    /// The resort that carries the change across has to go through the filter,
    /// and ResortInPlace now does that for itself. It used to rebuild the
    /// listing from the UNFILTERED master list, so switching to tiles in a
    /// filtered, grouped folder showed the whole folder again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_filter_survives_the_switch_to_tiles()
    {
        var now = DateTimeOffset.Now;

        var pane = await Pane(
            GroupMode.Modified,
            At("alpha.txt", now.AddDays(-3)),
            At("beta.txt", now.AddDays(-3)),
            At("zulu.txt", now));

        // **Debounced by 120 ms**, so reading the listing straight after setting
        // the text would test nothing.
        pane.FilterText = "zulu";
        await Task.Delay(250);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Single(pane.DetailsEntries);

        pane.ShowAsGrid();

        var tiles = pane.GridEntries.Select(e => e.Name).ToList();

        Assert.Equal(["zulu.txt"], tiles);
    }

    // ---- hidden files are ghosted -------------------------------------------

    [AvaloniaFact]
    public void A_hidden_file_is_ghosted()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("desktop.ini", DateTimeOffset.Now, EntryFlags.Hidden),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0.55, Assert.IsType<double>(faded), 3);
    }

    [AvaloniaFact]
    public void A_system_file_is_ghosted_too()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("thumbs.db", DateTimeOffset.Now, EntryFlags.System),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0.55, Assert.IsType<double>(faded), 3);
    }

    /// <summary>
    /// **A drive that was not there was drawn like a live one.** This PC lists
    /// an unmounted volume and a disconnected mapped drive in place, on purpose
    /// — a row that vanishes is worse than a row you cannot open — and nothing
    /// said which was which until you clicked it and waited out the timeout.
    /// </summary>
    [AvaloniaFact]
    public void A_drive_that_cannot_be_reached_is_ghosted()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("work (Z:)", DateTimeOffset.UnixEpoch,
               EntryFlags.Directory | EntryFlags.Volume | EntryFlags.Unreadable),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0.55, Assert.IsType<double>(faded), 3);
    }

    [AvaloniaFact]
    public void An_ordinary_file_is_not()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("report.txt", DateTimeOffset.Now),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1.0, Assert.IsType<double>(faded), 3);
    }

    // ---- and in every layout, not just the default one ----------------------

    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    /// <summary>
    /// **The ghosting was bound in the details rows and nowhere else.** The
    /// converter was right and its tests passed, while compact and grid drew
    /// desktop.ini, thumbs.db and everything else the filesystem marks hidden at
    /// full strength beside real content — so "show hidden files" was survivable
    /// in the default view and not in the other two, which is the state the
    /// setting exists to avoid.
    ///
    /// The listings are discovered from the markup rather than listed here, so
    /// a fourth layout added later is held to this without anybody remembering.
    /// </summary>
    [AvaloniaFact]
    public void Every_listing_ghosts_a_hidden_name()
    {
        var listings = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ListBox")
            .Where(l => (string?)l.Attribute("ItemsSource")
                        is "{Binding DetailsEntries}" or "{Binding CompactEntries}"
                           or "{Binding GridEntries}")
            .ToList();

        // A guard, not decoration: a renamed listing must fail here rather than
        // silently drop out of the check below.
        Assert.Equal(3, listings.Count);

        var full = listings
            .Select(l => (
                List: (string)l.Attribute("ItemsSource")!,
                // The name cell is the one bound through DisplayName — the same
                // cell the name tooltip hangs on.
                Name: l.Descendants(Xaml + "TextBlock")
                       .Single(t => ((string?)t.Attribute("Text"))
                                    ?.Contains("FileConverters.DisplayName",
                                               StringComparison.Ordinal) == true)))
            .Where(x => ((string?)x.Name.Attribute("Opacity"))
                        ?.Contains("FileConverters.HiddenFade", StringComparison.Ordinal) != true)
            .Select(x => x.List)
            .ToList();

        Assert.True(full.Count == 0,
            "these listings draw a hidden file at full strength: " + string.Join(", ", full));
    }

    /// <summary>
    /// The menu that sets a grouping stays out of the other two layouts. The
    /// pane ignores a grouping there now rather than applying it invisibly,
    /// which is exactly what makes offering the entry worse than hiding it:
    /// every row would tick and none of them would do anything.
    /// </summary>
    [AvaloniaFact]
    public void Group_by_is_offered_only_in_details()
    {
        var entry = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "MenuItem")
            .Single(m => (string?)m.Attribute("Header") == "Group by");

        Assert.Equal("{Binding ActiveTab.IsDetailsView}", (string?)entry.Attribute("IsVisible"));
    }

    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        private Action<FileSystemChange>? _onChange;

        /// <summary>What the next stat should return, for a watcher event.</summary>
        public FileEntry? Arriving { get; set; }

        public void Raise(FileSystemChange change) => _onChange?.Invoke(change);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(Arriving);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            _onChange = onChange;
            return new Nothing();
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
