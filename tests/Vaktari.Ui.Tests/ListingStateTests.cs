using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Three things the listing is supposed to keep track of, and did not.
///
///  - **A failed load left the previous success standing.** IsLoaded is only
///    ever set true, at the end of a load that worked, so after a failure it
///    still said yes. Two guards read it: one returned early from "navigate
///    somewhere you are already at", so plugging the drive back in and retyping
///    the same path did nothing at all; the other recorded the dead path in
///    Recent locations, which is the exact thing that check exists to prevent.
///
///  - **The selection did not survive a rebuild.** ReplaceAll clears the
///    collection the view binds its selection to, so F5 lost your place and
///    re-sorting quietly deselected everything.
///
///  - **The item count and the empty state did not follow live changes.** Both
///    are computed from Entries.Count and nothing raised them when the watcher
///    changed the rows — only a navigation did.
/// </summary>
public sealed class ListingStateTests : OwnedViewModels
{
    private static FileEntry Entry(string name)
        => new(name, "/listing/" + name, 4, DateTimeOffset.UnixEpoch, EntryFlags.None);

    // ---- a failed load does not look like a loaded one ----------------------

    /// <summary>
    /// The drive was missing, then it came back. Retyping the same path has to
    /// try again rather than answering "you are already there".
    /// </summary>
    [AvaloniaFact]
    public async Task Retyping_a_path_that_failed_tries_again()
    {
        var fs = new Flaky("/gone");
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync("/here");
        Assert.True(pane.IsLoaded);

        await pane.NavigateAsync("/gone");
        Assert.False(pane.IsLoaded, "a failed load still claimed to be loaded");

        var attempts = fs.Attempts;

        // The drive is back.
        fs.Dead = null;
        await pane.NavigateAsync("/gone");

        Assert.True(fs.Attempts > attempts, "the second attempt never reached the filesystem");
        Assert.True(pane.IsLoaded);
    }

    /// <summary>
    /// Navigating to where you already are is still a no-op, which is what the
    /// guard is for — a reload flashes the folder in readdir order.
    /// </summary>
    [AvaloniaFact]
    public async Task Navigating_to_where_you_already_are_still_does_nothing()
    {
        var fs = new Flaky(null);
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync("/here");
        var attempts = fs.Attempts;

        await pane.NavigateAsync("/here");

        Assert.Equal(attempts, fs.Attempts);
    }

    // ---- the selection survives a rebuild -----------------------------------

    /// <summary>
    /// **The clearing is done by the ListBox, not by the view model**, so this
    /// stands in for it: Avalonia's SelectingItemsControl empties its selection
    /// when the bound collection raises Reset, which is exactly what ReplaceAll
    /// raises. A test that only added to DetailsSelection and re-sorted passed
    /// against the bug, because nothing in a headless view model ever cleared
    /// the collection the real list clears.
    /// </summary>
    [AvaloniaFact]
    public async Task Sorting_keeps_what_was_selected()
    {
        var fs = new Flaky(null) { Rows = [Entry("a.txt"), Entry("b.txt"), Entry("c.txt")] };
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync("/here");

        // What the list does with its selection when the rows are replaced.
        pane.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                pane.DetailsSelection.Clear();
        };

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "b.txt"));
        Assert.Single(pane.Selection);

        pane.SortByCommand.Execute("name");   // flips to descending, rebuilding the rows

        Assert.Single(pane.Selection);
        Assert.Equal("b.txt", pane.Selection[0].Name);
        Assert.Equal("b.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>Nothing selected stays nothing selected — the restore must not
    /// invent a selection where there was none.</summary>
    [AvaloniaFact]
    public async Task Sorting_with_nothing_selected_selects_nothing()
    {
        var fs = new Flaky(null) { Rows = [Entry("a.txt"), Entry("b.txt")] };
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync("/here");

        pane.SortByCommand.Execute("name");

        Assert.Empty(pane.Selection);
    }

    // ---- the count follows the rows -----------------------------------------

    /// <summary>
    /// A download finishing, or something else deleting a file, changes the
    /// rows without any navigation. The count has to notice.
    /// </summary>
    [AvaloniaFact]
    public async Task The_item_count_follows_a_change_from_underneath()
    {
        var fs = new Flaky(null) { Rows = [Entry("a.txt"), Entry("b.txt")] };
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync("/here");

        var raised = new List<string?>();
        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.Entries.Add(Entry("c.txt"));

        Assert.Contains(nameof(PaneViewModel.Summary), raised);
        Assert.Contains(nameof(PaneViewModel.IsEmpty), raised);
        Assert.Contains("3", pane.Summary);
    }

    /// <summary>
    /// And the other direction, which reads worse on screen: "this folder is
    /// empty" printed across the middle of a folder that now has files in it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_empty_state_goes_away_when_a_file_appears()
    {
        var fs = new Flaky(null) { Rows = [] };
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync("/here");
        Assert.True(pane.IsEmpty);

        pane.Entries.Add(Entry("arrived.txt"));

        Assert.False(pane.IsEmpty, "the empty message stayed over real rows");
    }

    private sealed class Flaky(string? dead) : IFileSystemProvider
    {
        public string? Dead { get; set; } = dead;
        public int Attempts { get; private set; }
        public IReadOnlyList<FileEntry> Rows { get; init; } = [Entry("a.txt")];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Attempts++;

            // Contains, not equality: LoadListingAsync normalises the path
            // before it reaches a provider, so "/gone" arrives as "\gone" on
            // Windows and an exact comparison never matched — which made this
            // fake succeed and the test pass against the bug.
            if (Dead is { } gone && path.Contains(gone.Trim('/'), StringComparison.Ordinal))
                throw new DirectoryNotFoundException("that folder is not there any more");

            await Task.CompletedTask;
            yield return Rows;
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
