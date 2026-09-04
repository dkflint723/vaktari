using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A search, read the way every other listing is read.
///
/// **The shape is the whole feature.** The pane already batches, sorts,
/// filters, virtualizes, selects, drags and drops whatever comes out of an
/// IAsyncEnumerable of batches; the popup had none of that and could not,
/// because it was a floating list drawn over the pane. Meeting the same
/// contract is what buys all of it at once.
/// </summary>
public sealed class SearchListingTests : OwnedViewModels
{
    private static readonly ListingOptions Visible = new() { IncludeHidden = false };
    private static readonly ListingOptions Everything = new() { IncludeHidden = true };

    private static FileEntry Entry(string name, EntryFlags flags = EntryFlags.None)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1, DateTimeOffset.UnixEpoch, flags);

    private static async Task<List<FileEntry>> ReadAsync(
        ISearchProvider? search, string path, ListingOptions? options = null)
    {
        var all = new List<FileEntry>();

        await foreach (var batch in SearchListing.EnumerateAsync(
            search, path, options ?? Visible, CancellationToken.None))
        {
            all.AddRange(batch);
        }

        return all;
    }

    /// <summary>
    /// **A desktop with no index still lets you type a question.** Every other
    /// provider on the pane is nullable for the same reason, and the rule
    /// throughout is that null draws nothing rather than throwing — a listing
    /// that threw here would take the navigation down with it.
    /// </summary>
    [Fact]
    public async Task With_no_backend_at_all_the_listing_is_empty_rather_than_broken()
    {
        var rows = await ReadAsync(null, VirtualPaths.Search("report", null, false));

        Assert.Empty(rows);
    }

    /// <summary>
    /// And a malformed path — one hand-edited into the session file, or
    /// truncated — asks the backend nothing at all rather than searching for
    /// the empty string, which on an index means every file on the machine.
    /// </summary>
    [Fact]
    public async Task An_empty_question_is_not_asked()
    {
        var backend = new Fake(Entry("a.txt"));

        Assert.Empty(await ReadAsync(backend, "vaktari:search:"));
        Assert.Null(backend.Asked);
    }

    [Fact]
    public async Task What_the_backend_finds_is_what_the_listing_shows()
    {
        var backend = new Fake(Entry("report.docx"), Entry("report.pdf"));

        var rows = await ReadAsync(backend, VirtualPaths.Search("report", null, false));

        Assert.Equal(["report.docx", "report.pdf"], rows.Select(r => r.Name));
    }

    /// <summary>
    /// The scope reaches the backend, so "this folder only" is answered by the
    /// index rather than by filtering a machine-wide result set afterwards.
    /// </summary>
    [Fact]
    public async Task A_scoped_search_asks_the_backend_for_that_folder()
    {
        var backend = new Fake();

        await ReadAsync(backend, VirtualPaths.Search("report", @"C:\Users\me", scoped: true));

        Assert.Equal(@"C:\Users\me", backend.Asked!.ScopePath);
        Assert.Equal("report", backend.Asked.Text);
    }

    /// <summary>
    /// **Everywhere is a null scope, not the folder it started from.** The
    /// origin is carried in the path so the box can be unticked back to it, and
    /// a listing that passed it as the scope regardless would make "everywhere"
    /// do nothing at all.
    /// </summary>
    [Fact]
    public async Task And_an_unscoped_one_asks_for_everywhere_despite_carrying_its_origin()
    {
        var backend = new Fake();
        var path = VirtualPaths.Search("report", @"C:\Users\me", scoped: false);

        await ReadAsync(backend, path);

        Assert.Equal(@"C:\Users\me", VirtualPaths.OriginOf(path));
        Assert.Null(backend.Asked!.ScopePath);
    }

    /// <summary>
    /// **The cap is the listing's, not the record type's default.** SearchQuery
    /// defaults MaxResults to 1000, so a listing that set nothing would silently
    /// truncate a real result set at a number chosen for a popup.
    /// </summary>
    [Fact]
    public async Task The_cap_is_the_one_a_pane_can_hold()
    {
        var backend = new Fake();

        await ReadAsync(backend, VirtualPaths.Search("e", null, false));

        // One MORE than the cap, deliberately. The extra row is never shown; it
        // is the only way to tell "there are more" from "there are exactly this
        // many", and without it a folder holding precisely ten thousand matches
        // would be reported as cut short.
        Assert.Equal(SearchListing.Limit + 1, backend.Asked!.MaxResults);
        Assert.True(SearchListing.Limit > new SearchQuery { Text = "x" }.MaxResults,
                    "the listing's cap must beat the record's popup-sized default");
    }

    /// <summary>
    /// **The listing ended at the cap without a word.** The pane sees an
    /// enumeration that finished; it cannot tell that from one that ran out.
    /// </summary>
    [Fact]
    public async Task Reaching_the_cap_is_reported_rather_than_swallowed()
    {
        var backend = new Fake([.. Enumerable.Range(0, 5).Select(i => Entry($"f{i}.txt"))]);

        var capped = false;
        var rows = new List<FileEntry>();

        await foreach (var batch in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("f", null, false), Visible, CancellationToken.None,
            limit: 3, onCapped: () => capped = true))
        {
            rows.AddRange(batch);
        }

        Assert.True(capped, "the listing was cut short and said nothing");

        // And the extra row the query asked for is not shown.
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// And exactly the cap is a complete answer, not a cut one.
    ///
    /// **Counting to the cap cannot tell the two apart.** A tree with precisely
    /// ten thousand matches would have been reported as truncated, and "there
    /// are more" that is sometimes false is worse than the silence it replaced.
    /// </summary>
    [Fact]
    public async Task Exactly_the_cap_is_a_complete_answer()
    {
        var backend = new Fake([.. Enumerable.Range(0, 3).Select(i => Entry($"f{i}.txt"))]);

        var capped = false;
        var rows = new List<FileEntry>();

        await foreach (var batch in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("f", null, false), Visible, CancellationToken.None,
            limit: 3, onCapped: () => capped = true))
        {
            rows.AddRange(batch);
        }

        Assert.False(capped, "a complete answer the size of the cap was called cut short");
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// **The backends return hidden and system files and the pane expects them
    /// already gone.** Everywhere else that rule belongs to the filesystem
    /// provider, applied before a row is ever seen; nothing downstream
    /// re-checks it. Without this the search shows dotfiles and ~$Word.docx
    /// while the folder beside it hides them, from one setting.
    /// </summary>
    [Fact]
    public async Task Hidden_and_system_files_obey_the_same_setting_as_a_folder()
    {
        var backend = new Fake(
            Entry("plain.txt"),
            Entry(".config", EntryFlags.Hidden),
            Entry("~$report.docx", EntryFlags.System));

        var path = VirtualPaths.Search("re", null, false);

        Assert.Equal(["plain.txt"], (await ReadAsync(backend, path)).Select(r => r.Name));

        Assert.Equal(
            ["plain.txt", ".config", "~$report.docx"],
            (await ReadAsync(backend, path, Everything)).Select(r => r.Name));
    }

    /// <summary>
    /// **A narrow query would otherwise show nothing until it finished.** The
    /// pane consults its own flush timer only when this yields, so batching on
    /// a count alone hides the case where progressive results matter most: four
    /// hits found slowly over ninety seconds, displayed all at once at the end.
    /// </summary>
    [Fact]
    public async Task A_trickle_of_results_is_shown_as_it_arrives_rather_than_at_the_end()
    {
        var backend = new Fake(Entry("one.txt"), Entry("two.txt")) { PauseMs = 220 };

        var batches = new List<int>();

        await foreach (var batch in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("t", null, false), Visible, CancellationToken.None))
        {
            batches.Add(batch.Count);
        }

        Assert.Equal([1, 1], batches);
    }

    /// <summary>
    /// And a flood is batched rather than yielded one row at a time — each
    /// yield costs the pane a dispatcher hop.
    /// </summary>
    [Fact]
    public async Task A_flood_arrives_in_batches()
    {
        var backend = new Fake([.. Enumerable.Range(0, 100).Select(i => Entry($"f{i}.txt"))]);

        var batches = new List<int>();

        await foreach (var batch in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("f", null, false), Visible, CancellationToken.None))
        {
            batches.Add(batch.Count);
        }

        Assert.Equal(100, batches.Sum());
        Assert.True(batches.Count < 10, $"100 rows came in {batches.Count} batches, one hop each");
    }

    /// <summary>
    /// Navigating away stops the walk AND unwinds the backend.
    ///
    /// **The disposal is the part that can be silently dropped.** An unindexed
    /// search reads every fixed drive and an indexed one holds a process or a
    /// socket; a pump that abandoned its enumerator on cancellation would leave
    /// every superseded search of the session still holding one. Nothing else
    /// disposes it — the async-foreach that would have is exactly what stops
    /// running here.
    /// </summary>
    [Fact]
    public async Task Navigating_away_stops_the_backend_and_lets_go_of_it()
    {
        var backend = new Fake([.. Enumerable.Range(0, 500).Select(i => Entry($"f{i}.txt"))]);

        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in SearchListing.EnumerateAsync(
                backend, VirtualPaths.Search("f", null, false), Visible, stop.Token))
            {
                await stop.CancelAsync();
            }
        });

        Assert.True(backend.Disposed, "the backend's enumerator was abandoned rather than closed");
    }

    /// <summary>
    /// **The backend's first step runs on the pool, not on the thread that
    /// navigated.** An async iterator runs on its CALLER until it reaches a
    /// genuine suspension, and both real backends do work before theirs —
    /// Baloo starts a process, the fallback reads a directory, Everything opens
    /// an IPC connection. A ConfigureAwait at the consuming end governs
    /// continuations after a suspension, not that prologue, so without the pump
    /// the freeze lands on the UI thread on every search.
    /// </summary>
    [Fact]
    public async Task The_backend_never_starts_on_the_thread_that_asked()
    {
        var backend = new Fake(Entry("a.txt"));
        var asking = Environment.CurrentManagedThreadId;

        await ReadAsync(backend, VirtualPaths.Search("a", null, false));

        Assert.NotNull(backend.RanOn);
        Assert.NotEqual(asking, backend.RanOn);
    }

    /// <summary>
    /// **And the pane really reaches it**, which is the branch the rest of this
    /// file cannot see. Everything above tests the listing in isolation; a
    /// source ternary that never named the search would leave every one of them
    /// passing while a search path fell through to the filesystem provider and
    /// asked the disk for a folder called "vaktari:search:report::everywhere".
    /// </summary>
    [AvaloniaFact]
    public async Task Navigating_to_a_search_shows_what_the_backend_found()
    {
        var backend = new Fake(Entry("report.docx"));

        UseSearch(backend);

        var pane = Own(new PaneViewModel(new NoDisk()));

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false));

        Assert.Equal(["report.docx"], pane.Entries.Select(e => e.Name));
        Assert.Equal("report", backend.Asked!.Text);
    }

    /// <summary>A disk that would fail the assertion above if it were consulted.</summary>
    private sealed class NoDisk : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return [Entry("from-the-disk.txt")];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// A fake index. Its prologue — everything before the first yield — is what
    /// the pump test watches, standing in for starting a process or opening a
    /// socket.
    /// </summary>
    private sealed class Fake(params FileEntry[] results) : ISearchProvider
    {
        public bool IsAvailable => true;
        public string BackendName => "fake";
        public bool SupportsContentSearch => false;

        public SearchQuery? Asked { get; private set; }
        public int? RanOn { get; private set; }
        public bool Disposed { get; private set; }
        public int PauseMs { get; init; }

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            Asked = query;
            RanOn = Environment.CurrentManagedThreadId;

            try
            {
                foreach (var entry in results)
                {
                    ct.ThrowIfCancellationRequested();

                    if (PauseMs > 0) await Task.Delay(PauseMs, ct).ConfigureAwait(false);

                    yield return entry;
                }
            }
            finally
            {
                // Where a real backend closes its process or its socket.
                Disposed = true;
            }
        }
    }
}
