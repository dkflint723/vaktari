using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a burst of watcher events costs the listing.
///
/// **Every single event was a full pass over the folder.** A removal ran
/// FindIndex down the master list and then walked the visible one looking for
/// the same path; an arrival did both again before inserting; and each of them
/// finished by copying the entire visible list and recomputing every group
/// heading from the copy. One file is nothing. An extraction, a build or a
/// large download is thousands of files in a second, and thousands of full
/// passes over a 100k-row listing is a window that has stopped answering.
///
/// The events are queued and applied in one pass now, so these ask for the
/// listing a reload would have produced AND for it to have been arrived at
/// once.
/// </summary>
public sealed class WatcherBatchTests : OwnedViewModels
{
    /// <summary>Runs the dispatcher until the burst has been drained: the
    /// queued events, the pass below them, and the stats it awaits.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }

    private async Task<(PaneViewModel Pane, Folder Fs)> Pane(params string[] names)
    {
        var fs = new Folder(names);
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());
        await Settle();

        return (pane, fs);
    }

    private static List<string> Names(PaneViewModel pane)
        => pane.DetailsEntries.Select(e => e.Name).ToList();

    /// <summary>The path a watcher event about this name would carry.</summary>
    private static string Child(PaneViewModel pane, string name)
        => Path.Combine(pane.CurrentPath, name);

    // ---- one pass, not one per file -----------------------------------------

    /// <summary>
    /// Every rebuild of the headings raises GroupingChanged, which makes it the
    /// one honest count of how many times a burst walked the whole folder.
    /// Three files moving is three events and ONE pass.
    /// </summary>
    [AvaloniaFact]
    public async Task A_burst_of_news_is_one_pass_over_the_listing()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt", "d.txt");

        var passes = 0;
        pane.GroupingChanged += (_, _) => passes++;

        fs.Describe("c.txt");
        fs.Describe("e.txt");

        // Raised with nothing awaited in between, which is what a real burst
        // looks like: the watcher's thread posts them all before the UI thread
        // gets to any of them.
        fs.Raise(new FileSystemChange(ChangeKind.Removed, Child(pane, "b.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "c.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "e.txt")));

        await Settle();

        // A guard, not decoration: a burst that never arrived would also
        // "prove" one pass.
        Assert.Equal(["a.txt", "c.txt", "d.txt", "e.txt"], Names(pane));

        Assert.Equal(1, passes);
    }

    /// <summary>
    /// And the listing a burst leaves behind is the one a reload would have
    /// produced — in order, with the arrivals in their sorted places rather
    /// than at the end.
    /// </summary>
    [AvaloniaFact]
    public async Task A_burst_leaves_the_listing_a_reload_would_have_produced()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt", "d.txt");

        fs.Describe("c.txt");
        fs.Describe("e.txt");

        fs.Raise(new FileSystemChange(ChangeKind.Removed, Child(pane, "b.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "c.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "e.txt")));

        await Settle();

        Assert.Equal(["a.txt", "c.txt", "d.txt", "e.txt"], Names(pane));
    }

    /// <summary>
    /// The burst has to reach the master list, not just the visible one.
    ///
    /// **A row removed from Entries alone comes back on the next sort.** The
    /// two are rebuilt from `_all`, so a departure the master list never heard
    /// about is a row that reappears the moment a column heading is clicked —
    /// and a count that was wrong the whole time in between.
    /// </summary>
    [AvaloniaFact]
    public async Task A_burst_reaches_the_master_list_and_not_only_the_rows()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt", "d.txt");

        fs.Describe("c.txt");

        fs.Raise(new FileSystemChange(ChangeKind.Removed, Child(pane, "b.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "c.txt")));

        await Settle();

        Assert.Equal(["a.txt", "c.txt", "d.txt"], Names(pane));

        // Re-sorted from the master list, which is where a stale departure
        // would still be sitting.
        pane.SortByCommand.Execute("name");

        Assert.Equal(["d.txt", "c.txt", "a.txt"], Names(pane));
    }

    /// <summary>
    /// A file written to twice is one row, not two. The batch has to take the
    /// old row out before it puts the new one in — the same thing the old
    /// per-event path did with RemoveByPathSilently.
    /// </summary>
    [AvaloniaFact]
    public async Task An_update_replaces_the_row_rather_than_doubling_it()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt");

        fs.Describe("a.txt", size: 9);

        fs.Raise(new FileSystemChange(ChangeKind.Changed, Child(pane, "a.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Changed, Child(pane, "a.txt")));

        await Settle();

        Assert.Equal(["a.txt", "b.txt"], Names(pane));

        var row = Assert.Single(pane.DetailsEntries, e => e.Name == "a.txt");

        Assert.Equal(9, row.Length);
    }

    /// <summary>
    /// A row this listing excludes must be left alone rather than removed: the
    /// batch takes the old row out only for an arrival it is actually going to
    /// put back, which is why the hidden test drops out before that and not
    /// after it.
    ///
    /// The visible arrival in the same burst is what says the batch ran at all;
    /// without it this would pass on a pane that ignored the watcher entirely.
    /// </summary>
    [AvaloniaFact]
    public async Task A_concealed_arrival_leaves_the_row_it_already_had_alone()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt");

        Assert.False(pane.ShowHidden);

        fs.Describe("b.txt", flags: EntryFlags.Hidden);
        fs.Describe("c.txt");

        fs.Raise(new FileSystemChange(ChangeKind.Changed, Child(pane, "b.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "c.txt")));

        await Settle();

        Assert.Equal(["a.txt", "b.txt", "c.txt"], Names(pane));
    }

    /// <summary>
    /// A file that went and came back inside one burst is gone if the stat
    /// cannot describe it. The removal was reported and stands on its own; the
    /// arrival is only ever as good as the description of it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_removal_stands_when_the_arrival_after_it_cannot_be_described()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt");

        // b.txt is never registered with Describe, so it stats as nothing —
        // a file created and deleted again before we got to look at it.
        fs.Raise(new FileSystemChange(ChangeKind.Removed, Child(pane, "b.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "b.txt")));

        await Settle();

        Assert.Equal(["a.txt"], Names(pane));
    }

    /// <summary>
    /// The queue reopens once it has been emptied.
    ///
    /// **A burst that never released the slot would be the last one the pane
    /// ever saw.** Only the first event of a burst posts the pass, so the flag
    /// that says one is already posted has to be put back down when it runs —
    /// otherwise every later event joins a batch nobody will ever come for, and
    /// the listing silently stops following the folder while looking perfectly
    /// healthy.
    /// </summary>
    [AvaloniaFact]
    public async Task A_second_burst_is_applied_like_the_first()
    {
        var (pane, fs) = await Pane("a.txt");

        fs.Describe("b.txt");
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "b.txt")));

        await Settle();

        // A guard, not decoration: without it a pane that ignored the watcher
        // outright would fail on the second assertion for the wrong reason.
        Assert.Equal(["a.txt", "b.txt"], Names(pane));

        fs.Describe("c.txt");
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "c.txt")));

        await Settle();

        Assert.Equal(["a.txt", "b.txt", "c.txt"], Names(pane));
    }

    /// <summary>
    /// A file that appeared and went again inside one burst leaves no row.
    ///
    /// **The stat happens after the burst, so a temporary that still exists by
    /// then would be listed as though it had survived.** A build writes
    /// thousands of these. The later word wins: a path reported gone after it
    /// was reported there stops being an arrival, which is why the departure
    /// side of the queue clears the arrival side.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_that_goes_again_inside_the_burst_never_gets_a_row()
    {
        var (pane, fs) = await Pane("a.txt", "c.txt");

        // Describable throughout, which is the hard case: the stat cannot be
        // what saves us here.
        fs.Describe("b.txt");

        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "b.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Removed, Child(pane, "b.txt")));

        await Settle();

        Assert.Equal(["a.txt", "c.txt"], Names(pane));
    }

    /// <summary>
    /// News about the folder we have left does not get applied to the one we
    /// are in.
    ///
    /// **The queue is one queue, and it outlives a navigation.** A burst can be
    /// half-collected when the user moves on, and the pass that eventually runs
    /// judges the batch by the listing it was collected for — so a batch that
    /// has already been re-stamped with the new listing must not still be
    /// carrying the old one's paths, or a row from the previous folder is
    /// inserted into this one and survives every filter and re-sort.
    ///
    /// The two events are delivered without the dispatcher running in between,
    /// which is the only way one queue ever holds both.
    /// </summary>
    [AvaloniaFact]
    public async Task News_from_the_folder_we_left_is_not_applied_to_this_one()
    {
        var (pane, fs) = await Pane("a.txt");

        var left = pane.CurrentPath;

        // Describable throughout: the stat cannot be what saves us here.
        fs.Describe("m.txt");

        await pane.NavigateAsync(Path.Combine(left, "elsewhere"));
        await Settle();

        fs.Describe("z.txt");

        // The folder we left, on the watcher it was given: one row arriving
        // there and one leaving, so neither half of the queue may carry over.
        fs.RaiseOn(0, new FileSystemChange(ChangeKind.Added, Path.Combine(left, "m.txt")));
        fs.RaiseOn(0, new FileSystemChange(ChangeKind.Removed, Path.Combine(left, "a.txt")));

        // And the one we are in, before the dispatcher has run at all.
        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "z.txt")));

        await Settle();

        Assert.Equal(["a.txt", "z.txt"], Names(pane));
    }

    // ---- news that arrives while the folder is still loading ---------------

    /// <summary>
    /// **Changes made while a folder was loading were dropped.** The watch
    /// started only once the last row had arrived, and Drain emptied the queue
    /// and threw the batch away whenever IsLoading was set — so a file created
    /// behind the enumerator never appeared, and one deleted after it was read
    /// stayed on screen, both until the next refresh.
    ///
    /// Two halves, both needed: the watch has to be following the folder while
    /// the listing is held (the wait below times out otherwise), and what it
    /// says then has to be kept until the listing is done rather than dropped.
    /// b.txt is in the listing the enumeration hands back, so its removal
    /// only shows if the news outlived the load.
    /// </summary>
    [AvaloniaFact]
    public async Task News_that_arrives_while_the_folder_is_loading_is_applied_once_it_has()
    {
        var fs = new Folder(["a.txt", "b.txt"]);
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        fs.Hold();

        var loading = pane.NavigateAsync(Path.GetTempPath());

        await Until(() => fs.Watches > 0 && fs.Enumerations > 0, "nothing was following the folder while it loaded");

        fs.Describe("new.txt");

        fs.Raise(new FileSystemChange(ChangeKind.Added, Child(pane, "new.txt")));
        fs.Raise(new FileSystemChange(ChangeKind.Removed, Child(pane, "b.txt")));

        // The pass the first event posts runs here, mid-load, which is where
        // it used to take the batch and drop it.
        await Settle();

        Assert.True(pane.IsLoading, "the load finished before the news arrived, so this proves nothing");

        fs.Release();
        await loading;

        await Until(() => Names(pane).Contains("new.txt"), "a file created mid-load never appeared");

        Assert.Equal(["a.txt", "new.txt"], Names(pane));
    }

    /// <summary>
    /// **An overflow while the folder loaded was thrown away.** The watch runs
    /// during the load now, so its buffer can overrun there too — a big folder
    /// read while an archive is extracted into it — and the post that answers
    /// Lost by reading the folder again returned without a word while
    /// IsLoading was set. The load then replayed the news that had got through
    /// as though it were all of it, and nothing said the listing was short.
    ///
    /// A second read of the folder is the answer only a reload can give.
    /// </summary>
    [AvaloniaFact]
    public async Task Losing_track_while_the_folder_loads_reads_it_again()
    {
        var fs = new Folder(["a.txt"]);
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        fs.Hold();

        var loading = pane.NavigateAsync(Path.GetTempPath());

        await Until(() => fs.Watches > 0 && fs.Enumerations > 0, "the load never got as far as reading");

        fs.Raise(new FileSystemChange(ChangeKind.Lost, pane.CurrentPath));

        // The post the overflow makes runs here, mid-load, which is where it
        // used to drop it.
        await Settle();

        Assert.True(pane.IsLoading, "GUARD: the load finished before the overflow, so this proves nothing");

        fs.Release();
        await loading;

        await Until(() => fs.Enumerations > 1 && pane.IsLoaded,
            "the watch lost track mid-load and the folder was never read again");
    }

    /// <summary>
    /// **The watch opened beside the listing, and the listing went first.**
    /// A file created after the enumerator had passed its place, but before
    /// the watcher was running, was neither listed nor reported — and no
    /// watcher can report something from before it existed, so the only cure
    /// is for it to exist before the first read.
    /// </summary>
    [AvaloniaFact]
    public async Task The_watch_is_open_before_the_folder_is_read()
    {
        // Well inside the bound the load waits for a watch, and long enough
        // that a read which did not wait is certain to go first.
        var fs = new Folder(["a.txt"]) { WatchTakes = TimeSpan.FromMilliseconds(60) };
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        Assert.Equal(["watch", "read"], fs.Asked);
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(condition(), because);
    }

    // ---- the shape the cost depends on --------------------------------------

    /// <summary>
    /// The two things the pass may not go back to doing, in the one place a
    /// view model cannot be asked about them.
    ///
    /// **A scan per path and a copy per event is what made this O(n) a file.**
    /// Both are invisible from outside — the listing comes out identical either
    /// way — so they are pinned where they live.
    /// </summary>
    [AvaloniaFact]
    public void The_pass_scans_each_list_once_and_copies_neither()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("ViewModels", "PaneViewModel.Watching.cs"),
            "private void ApplyBatch(");

        // A departure is a hash lookup per row, not a FindIndex per path.
        Assert.Contains("_all.RemoveAll(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FindIndex", body, StringComparison.Ordinal);

        // And the headings are recomputed against the listing itself.
        Assert.Contains("RecomputeGroups(Entries);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToList()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Backs a listing whose rows are built from whatever root the pane
    /// enumerates, and answers a stat only for names a test has registered —
    /// anything else stats as nothing, the way a vanished file does.
    /// </summary>
    private sealed class Folder(string[] names) : IFileSystemProvider
    {
        private readonly Dictionary<string, FileEntry> _described = new(StringComparer.Ordinal);
        private readonly List<Action<FileSystemChange>> _watchers = [];
        private string _root = "";

        public void Describe(string name, long size = 1, EntryFlags flags = EntryFlags.None)
            => _described[name] = new(name, Path.Combine(_root, name), size,
                                      DateTimeOffset.UnixEpoch, flags);

        /// <summary>News from the folder currently on screen.</summary>
        public void Raise(FileSystemChange change) => _watchers[^1](change);

        /// <summary>News from a folder the pane has already left, delivered by
        /// the watcher that folder was given rather than the current one.</summary>
        public void RaiseOn(int watcher, FileSystemChange change) => _watchers[watcher](change);

        private TaskCompletionSource? _gate;
        private int _watches;

        /// <summary>Holds the next listing open until <see cref="Release"/>,
        /// after it has said which folder it is reading.</summary>
        public void Hold() => _gate = new TaskCompletionSource();

        public void Release() => _gate?.SetResult();

        /// <summary>How many watches have been opened. Read on the test's
        /// thread and counted on the pool, where the pane opens them.</summary>
        public int Watches => Volatile.Read(ref _watches);

        private int _enumerations;
        private readonly List<string> _order = [];

        /// <summary>How many listings have started reading.</summary>
        public int Enumerations => Volatile.Read(ref _enumerations);

        /// <summary>"watch" and "read" in the order the pane asked for them:
        /// a watch opened, and a listing's first read.</summary>
        public IReadOnlyList<string> Asked
        {
            get { lock (_order) return [.. _order]; }
        }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _root = path;

            lock (_order) _order.Add("read");
            Interlocked.Increment(ref _enumerations);

            if (_gate is { } gate) await gate.Task.ConfigureAwait(false);

            await Task.CompletedTask;

            yield return [.. names.Select(n => new FileEntry(
                n, Path.Combine(path, n), 1, DateTimeOffset.UnixEpoch, EntryFlags.None))];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(
                _described.TryGetValue(Path.GetFileName(path), out var entry)
                    ? entry
                    : (FileEntry?)null);

        /// <summary>How long opening a watch takes. A watcher on a busy pool
        /// or a slow mount is not instant, and the order below is only a
        /// question once it is not.</summary>
        public TimeSpan WatchTakes { get; set; }

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (WatchTakes > TimeSpan.Zero) Thread.Sleep(WatchTakes);

            lock (_watchers) _watchers.Add(onChange);
            lock (_order) _order.Add("watch");

            Interlocked.Increment(ref _watches);
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
