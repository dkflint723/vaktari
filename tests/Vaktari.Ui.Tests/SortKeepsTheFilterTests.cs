using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Sorting a filtered folder put every hidden row back.**
///
/// The resort rebuilt the row collection from the pane's UNFILTERED master
/// list, so clicking a column heading — or clicking the same heading again to
/// reverse it — in a folder narrowed to three files showed the whole folder
/// again, with the filter box still holding the words that were supposed to be
/// hiding them. Nothing on screen said the filter had stopped applying, and the
/// status line went on claiming a filtered count over an unfiltered listing.
///
/// Five places asked for that resort. Two spelled out "through the filter if
/// there is one" for themselves and three did not, which is what a method with
/// the wrong contract looks like from the outside — so the rule now lives in
/// the resort itself.
/// </summary>
public sealed class SortKeepsTheFilterTests : OwnedViewModels
{
    /// <summary>Distinct sizes, so sorting by size genuinely reorders and a
    /// test cannot pass on the name order it started in.</summary>
    private static FileEntry Entry(string name, long size)
        => new(name, "/s/" + name, size, DateTimeOffset.UnixEpoch, EntryFlags.None);

    private async Task<PaneViewModel> Pane(params FileEntry[] rows)
    {
        var pane = Own(new PaneViewModel(new Canned(rows), null, null)
        {
            ViewportWidth = 1400,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    /// <summary>
    /// **The filter is debounced by 120 ms**, so reading the listing straight
    /// after setting the text would test nothing at all.
    /// </summary>
    private static async Task Filter(PaneViewModel pane, string text)
    {
        pane.FilterText = text;

        await Task.Delay(250);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private async Task<PaneViewModel> FilteredToTheNotes()
    {
        var pane = await Pane(
            Entry("note-b.txt", 30),
            Entry("note-a.txt", 10),
            Entry("other.txt", 20));

        await Filter(pane, "note");

        Assert.Equal(2, pane.DetailsEntries.Count());

        return pane;
    }

    private static List<string> Names(PaneViewModel pane)
        => pane.DetailsEntries.Select(e => e.Name).ToList();

    // ---- clicking a heading -------------------------------------------------

    /// <summary>
    /// Clicking a different column heading. This is the resort SortBy asks for
    /// itself, once, with both of its own property writes suppressed.
    ///
    /// Biggest first, because the first click on size is descending — see
    /// SortDefaults. Unfiltered, "other.txt" would land between the two.
    /// </summary>
    [AvaloniaFact]
    public async Task Sorting_by_another_column_keeps_the_filter()
    {
        var pane = await FilteredToTheNotes();

        pane.SortByCommand.Execute("size");

        Assert.Equal(["note-b.txt", "note-a.txt"], Names(pane));
    }

    /// <summary>
    /// Clicking the SAME heading again, which only flips the direction — a
    /// different route to the same resort, through OnSortDescendingChanged.
    /// </summary>
    [AvaloniaFact]
    public async Task Reversing_the_sort_keeps_the_filter()
    {
        var pane = await FilteredToTheNotes();

        pane.SortByCommand.Execute("name");

        Assert.Equal(["note-b.txt", "note-a.txt"], Names(pane));
    }

    /// <summary>
    /// The third route: the sort field written on its own, which is what
    /// OnSortChanged answers. No control reaches it unsuppressed today — the
    /// headings and the menu both go through SortBy — so this is a GUARD on the
    /// remaining call site rather than a report of something a user hit.
    /// </summary>
    [AvaloniaFact]
    public async Task Setting_the_sort_field_on_its_own_keeps_the_filter()
    {
        // Its own fixture, three matching rows rather than the shared two:
        // with two, size ascending IS name ascending, so the assertion also
        // passed against a resort that did nothing whatever.
        var pane = await Pane(
            Entry("note-b.txt", 30),
            Entry("note-a.txt", 10),
            Entry("note-c.txt", 20),
            Entry("other.txt", 40));

        await Filter(pane, "note");
        Assert.Equal(3, pane.DetailsEntries.Count());

        pane.Sort = SortField.Size;

        Assert.Equal(["note-a.txt", "note-c.txt", "note-b.txt"], Names(pane));
    }

    // ---- and the line under it ----------------------------------------------

    /// <summary>
    /// The status line is the half of this a person actually reads, and it was
    /// the half that stayed HONEST: the resort never touched Status, so it went
    /// on saying "filtered to 2 of 3" while three rows sat under it. The two
    /// disagreeing is the fault stated as the user met it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_count_under_the_listing_agrees_with_it_after_a_sort()
    {
        var pane = await FilteredToTheNotes();

        pane.SortByCommand.Execute("size");

        Assert.Equal($"filtered to {Names(pane).Count:N0} of 3", pane.Status);
    }

    // ---- an unfiltered folder is untouched ----------------------------------

    /// <summary>
    /// A guard, not a bug report: sorting with no filter up must still sort the
    /// whole folder. The resort now consults the filter, and a resort that
    /// consulted it wrongly would empty every ordinary listing.
    /// </summary>
    [AvaloniaFact]
    public async Task Sorting_an_unfiltered_folder_still_sorts_all_of_it()
    {
        var pane = await Pane(
            Entry("note-b.txt", 30),
            Entry("note-a.txt", 10),
            Entry("other.txt", 20));

        pane.SortByCommand.Execute("size");

        Assert.Equal(["note-b.txt", "other.txt", "note-a.txt"], Names(pane));
        Assert.Equal("", pane.Status);
    }

    /// <summary>
    /// The delegation sits AHEAD of the empty-listing guard, and this is what
    /// tells the two orders apart. Narrow the filter to nothing and Entries is
    /// empty beside a full `_all`; widen it and the box's 120 ms debounce has
    /// not fired yet, so the listing is still empty while the filter now
    /// matches. A heading clicked in that window has to rebuild it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_sort_inside_the_debounce_rebuilds_an_emptied_listing()
    {
        var pane = await Pane(
            Entry("note-b.txt", 30),
            Entry("note-a.txt", 10),
            Entry("other.txt", 20));

        await Filter(pane, "zzz");
        Assert.Empty(pane.DetailsEntries);

        // Widened, but the debounce has NOT fired: still empty.
        pane.FilterText = "note";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Empty(pane.DetailsEntries);

        pane.SortByCommand.Execute("size");

        Assert.Equal(["note-b.txt", "note-a.txt"], Names(pane));
    }

    /// <summary>
    /// A GUARD, and the killing mutation for the condition itself. Status is
    /// the pane's message line — "nothing selected", "paste failed: …" — and
    /// ApplyFilter always writes it while the resort's own body never does. A
    /// resort that delegated unconditionally would wipe a message every time a
    /// heading was clicked in an ordinary folder.
    /// </summary>
    [AvaloniaFact]
    public async Task Sorting_an_unfiltered_folder_leaves_the_message_line_alone()
    {
        var pane = await Pane(
            Entry("note-b.txt", 30),
            Entry("note-a.txt", 10),
            Entry("other.txt", 20));

        pane.Status = "nothing selected";

        pane.SortByCommand.Execute("size");

        Assert.Equal("nothing selected", pane.Status);
    }

    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
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
