using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The band above a search listing: what was asked, where it looked, and the
/// way out of a walk that is taking too long.
///
/// **All three were in a popup that could only exist while the popup did.** The
/// question was in a text box in the toolbar, so navigating anywhere lost it;
/// the scope box had no control bound to it at all; and Stop was reachable only
/// while the floating list was open over the pane. Attached to the listing
/// instead, all three survive everything a pane survives — including Back.
/// </summary>
public sealed class SearchBandTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private async Task<PaneViewModel> Searching(
        string query, string? origin, bool scoped, params FileEntry[] found)
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Fake(found));

        await pane.NavigateAsync(VirtualPaths.Search(query, origin, scoped));

        return pane;
    }

    private static FileEntry Entry(string name)
        => new(name, "/tmp/" + name, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    [AvaloniaFact]
    public async Task The_band_is_there_for_a_search_and_nowhere_else()
    {
        var pane = await Searching("report", null, false);

        Assert.True(pane.IsSearchListing);
        Assert.Equal("report", pane.SearchQueryText);

        await pane.NavigateAsync(Path.GetTempPath());

        Assert.False(pane.IsSearchListing);
    }

    /// <summary>
    /// **Ticking the box goes somewhere**, which is the difference from the
    /// popup's version of it. The scope is part of where you are, so narrowing
    /// a search is a navigation and Back returns to the wider answer instead of
    /// re-running it.
    /// </summary>
    [AvaloniaFact]
    public async Task Narrowing_the_scope_is_a_navigation_that_back_undoes()
    {
        var pane = await Searching("report", @"C:\Users\me", scoped: false);

        Assert.False(pane.SearchScopedHere);

        pane.SearchScopedHere = true;

        // The navigation is started rather than awaited by the setter, so the
        // property is read back once it has landed.
        await WaitUntil(() => pane.SearchScopedHere);

        Assert.Equal(@"C:\Users\me", VirtualPaths.ScopeOf(pane.CurrentPath));
        Assert.True(pane.CanGoBack);

        await pane.GoBackAsync();
        await WaitUntil(() => !pane.SearchScopedHere);

        Assert.Equal("report", pane.SearchQueryText);
    }

    /// <summary>
    /// **A checkbox writes back what it was just told**, and the navigation
    /// that lands the new scope is what tells it — so the setter is re-entered
    /// with the value it already has, WHILE the search it started is still
    /// running. A navigation to the path you are already on is a no-op only
    /// once that load has finished; mid-load it starts a second one, which
    /// cancels the first walk and begins it again from nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_writing_back_what_it_was_told_does_not_restart_the_walk()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Fake([Entry("one.txt"), Entry("two.txt")]) { PauseMs = 200 };

        UseSearch(backend);

        var going = pane.NavigateAsync(VirtualPaths.Search("t", null, scoped: false));

        await WaitUntil(() => pane.IsLoading && backend.Asked > 0);

        // What the checkbox does the moment the property is raised at it.
        pane.SearchScopedHere = pane.SearchScopedHere;

        await going;

        Assert.Equal(1, backend.Asked);
    }

    /// <summary>
    /// **"This folder only" over This PC searched for a folder called
    /// "vaktari:computer".** A search with no folder behind it cannot be
    /// scoped, and the box says so rather than sitting ticked and ignored.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_with_no_folder_behind_it_says_so_on_the_box()
    {
        var nowhere = await Searching("report", null, false);

        Assert.False(nowhere.CanScopeSearch);
        Assert.Equal("searching everywhere", nowhere.SearchScopeLabel);

        var virtualPlace = await Searching("report", VirtualPaths.Computer, scoped: true);

        Assert.Contains("is not a folder", virtualPlace.SearchScopeLabel);
    }

    /// <summary>And over a real folder it names the folder.</summary>
    [AvaloniaFact]
    public async Task Over_a_folder_the_box_names_it()
    {
        var pane = await Searching("report", Path.Combine("C:", "Users", "me"), scoped: true);

        Assert.True(pane.CanScopeSearch);
        Assert.Equal("Only in me", pane.SearchScopeLabel);
    }

    /// <summary>
    /// **A search that found nothing must not report that a folder is empty.**
    /// The empty text's final arm is a catch-all, so without its own arm a
    /// fruitless search says "this folder is empty" about a folder nobody
    /// named.
    /// </summary>
    [AvaloniaFact]
    public async Task Finding_nothing_says_so_about_the_question()
    {
        var pane = await Searching("nothing-matches-this", null, false);

        Assert.True(pane.IsEmpty);
        Assert.Equal("nothing found for \u201cnothing-matches-this\u201d", pane.EmptyText);
    }

    /// <summary>
    /// Stop keeps what was found. An unindexed walk is unbounded, and the only
    /// other way out of one is to leave — which takes the results with it.
    /// </summary>
    [AvaloniaFact]
    public async Task Stopping_keeps_the_results_it_already_had()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var slow = new Fake([Entry("one.txt"), Entry("two.txt")]) { PauseMs = 400 };

        UseSearch(slow);

        var going = pane.NavigateAsync(VirtualPaths.Search("t", null, false));

        await WaitUntil(() => pane.Entries.Count > 0);

        var found = pane.Entries.Count;

        pane.StopSearchCommand.Execute(null);

        Assert.False(pane.IsLoading);
        Assert.True(pane.IsLoaded);
        Assert.NotEmpty(pane.Entries);
        Assert.Contains("stopped", pane.Status);

        await going;

        // **And the walk really ended.** Keeping the rows is only half of it:
        // an unindexed search reads every fixed drive, so a Stop that tidied
        // the flags without calling the walk off would leave the disk churning
        // for a search that says it has stopped — and the rows would go on
        // arriving underneath the line claiming they had not.
        Assert.True(slow.Disposed, "the backend was left walking");
        Assert.Equal(found, pane.Entries.Count);
    }

    /// <summary>
    /// **The load's own cancellation deliberately clears nothing**, because it
    /// assumes a newer navigation is following and owns the state. Nothing
    /// follows a Stop, so a pane that leant on that path would sit with its
    /// progress bar running for ever over a search that had ended.
    /// </summary>
    [AvaloniaFact]
    public async Task And_the_listing_is_finished_rather_than_left_running()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Fake([Entry("one.txt")]) { PauseMs = 400 });

        var going = pane.NavigateAsync(VirtualPaths.Search("nothing", null, false));

        await WaitUntil(() => pane.IsLoading);

        pane.StopSearchCommand.Execute(null);

        Assert.False(pane.IsLoading);

        await going;

        Assert.False(pane.IsLoading);
    }

    /// <summary>
    /// And on a walk that never ends by itself, which is the shape of the case
    /// Stop exists for: an unindexed search from This PC reads every fixed
    /// drive, and "e" matches most of it. The finite fake above would finish on
    /// its own eventually; this one only stops because it was stopped.
    /// </summary>
    [AvaloniaFact]
    public async Task Stopping_ends_a_walk_that_would_never_end_on_its_own()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var endless = new Endless();

        UseSearch(endless);

        var going = pane.NavigateAsync(VirtualPaths.Search("hit", null, false));

        await WaitUntil(() => pane.Entries.Count > 0);

        pane.StopSearchCommand.Execute(null);

        await going;
        await WaitUntil(() => endless.Ended);

        Assert.False(pane.IsLoading);
        Assert.NotEmpty(pane.Entries);
    }

    /// <summary>
    /// **Stop belongs to the search, not to loading.** A folder can be slow for
    /// its own reasons — a dead network share is the whole point of the pane's
    /// cancellation — and a Stop that fired on any load at all would offer to
    /// abandon one from a band that is not even on screen, then report a
    /// half-read folder as though the reading had finished.
    /// </summary>
    [AvaloniaFact]
    public async Task Stop_leaves_a_slow_folder_alone()
    {
        var pane = Own(new PaneViewModel(new NoDisk { PauseMs = 300 }));

        var going = pane.NavigateAsync(Path.GetTempPath());

        await WaitUntil(() => pane.IsLoading);

        pane.StopSearchCommand.Execute(null);

        Assert.True(pane.IsLoading, "a folder's own load was called off by the search's Stop");
        Assert.Equal("", pane.Status);

        await going;
    }

    /// <summary>
    /// **A search moves between searches**, and the band is bound to five
    /// properties that all derive from the path. Retyping the query or ticking
    /// the scope changes the path from one search to another without ever
    /// leaving the listing kind — so a band that was only told when it appeared
    /// and disappeared would go on displaying the previous question over the
    /// new one's results.
    /// </summary>
    [AvaloniaFact]
    public async Task Moving_from_one_search_to_another_tells_the_band()
    {
        var pane = await Searching("first", @"C:\Users\me", scoped: false);

        var announced = new List<string>();

        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        await pane.NavigateAsync(VirtualPaths.Search("second", @"C:\Users\me", scoped: true));
        await WaitUntil(() => announced.Contains(nameof(PaneViewModel.SearchScopeLabel)));

        Assert.Contains(nameof(PaneViewModel.IsSearchListing), announced);
        Assert.Contains(nameof(PaneViewModel.SearchQueryText), announced);
        Assert.Contains(nameof(PaneViewModel.CanScopeSearch), announced);
        Assert.Contains(nameof(PaneViewModel.SearchScopedHere), announced);
    }

    /// <summary>
    /// The band sits ABOVE the column header, docked the way the trash band is.
    /// Below it, it would be inside the part bound to IsDetailsView and would
    /// vanish in grid and compact — where a search is just as likely to be run.
    /// </summary>
    [Fact]
    public void The_band_is_drawn_where_every_view_shows_it()
    {
        var band = Band();

        var border = band.Parent!;

        Assert.Equal("Top", (string?)border.Attribute("DockPanel.Dock"));
        Assert.Equal("{Binding IsSearchListing}", (string?)border.Attribute("IsVisible"));

        // The header it must precede, found by the binding that would have
        // hidden it.
        var header = border.Parent!.Elements(Avalonia + "Border")
            .First(b => (string?)b.Attribute("IsVisible") == "{Binding IsDetailsView}");

        Assert.True(
            border.ElementsAfterSelf().Contains(header),
            "the search band is below the column header, so grid and compact hide it");
    }

    /// <summary>
    /// Both controls are bound: the box that had none in the popup, and a Stop
    /// that is present only while there is something to stop.
    /// </summary>
    [Fact]
    public void The_box_and_the_stop_are_wired_to_the_pane()
    {
        var band = Band();

        var box = Named(band, "SearchScope");

        Assert.Equal("{Binding SearchScopedHere}", (string?)box.Attribute("IsChecked"));
        Assert.Equal("{Binding CanScopeSearch}", (string?)box.Attribute("IsEnabled"));
        Assert.Equal("{Binding SearchScopeLabel}", (string?)box.Attribute("Content"));

        var stop = Named(band, "SearchStop");

        Assert.Equal("{Binding StopSearchCommand}", (string?)stop.Attribute("Command"));
        Assert.Equal("{Binding IsLoading}", (string?)stop.Attribute("IsVisible"));

        Assert.Equal("{Binding IsLoading}",
                     (string?)Named(band, "SearchWalking").Attribute("IsVisible"));
    }

    /// <summary>
    /// The bar spans the band rather than the space left over beside the
    /// controls, which is what docking it first buys.
    /// </summary>
    [Fact]
    public void The_progress_bar_spans_the_whole_band()
        => Assert.Equal(
            "SearchWalking",
            (string?)Band().Elements().First().Attribute(X + "Name"));

    /// <summary>
    /// **A walk stopped by its cap looked exactly like one that finished.** The
    /// bar goes, the Stop goes, the listing settles — and until now the only
    /// difference between an answer and a limit was invisible.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_cut_off_by_the_cap_says_so()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Fake([Entry("one.txt"), Entry("two.txt"), Entry("three.txt")]));

        await pane.NavigateAsync(VirtualPaths.Search("t", null, false));

        Assert.False(pane.SearchHitLimit);
        Assert.Equal(3, pane.Entries.Count);

        // Keep looking in reverse: the cap belongs to the pane, so a small one
        // can be tried without building ten thousand files.
        pane.SearchLimit = 2;
        await pane.RefreshAsync();

        Assert.True(pane.SearchHitLimit);
        Assert.Equal(2, pane.Entries.Count);
        Assert.Equal("stopped after the first 2 matches — there are more", pane.SearchLimitLine);
    }

    /// <summary>
    /// Keep looking keeps the question and raises the budget, rather than
    /// leaving the message as a better-worded dead end.
    /// </summary>
    [AvaloniaFact]
    public async Task Keep_looking_asks_the_same_question_with_a_bigger_budget()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Fake([.. Enumerable.Range(0, 4).Select(i => Entry($"f{i}.txt"))]));

        await pane.NavigateAsync(VirtualPaths.Search("f", null, false));

        pane.SearchLimit = 2;
        await pane.RefreshAsync();

        Assert.True(pane.SearchHitLimit);

        await pane.SearchMoreCommand.ExecuteAsync(null);

        Assert.Equal(2 + SearchListing.Limit, pane.SearchLimit);
        Assert.False(pane.SearchHitLimit);
        Assert.Equal(4, pane.Entries.Count);
        Assert.Equal("f", pane.SearchQueryText);

        // And the button is honest about what it costs: there is no cursor, so
        // this is the same walk again rather than a continuation.
        Assert.Contains("starts from the beginning", pane.SearchMoreHint);
    }

    /// <summary>
    /// And it does nothing to an answer that was never cut off: a command is
    /// reachable without its button, and re-reading every fixed drive to change
    /// nothing is an expensive way to do nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task Keep_looking_does_not_re_walk_a_complete_answer()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Fake([Entry("one.txt")]);

        UseSearch(backend);

        await pane.NavigateAsync(VirtualPaths.Search("t", null, false));

        Assert.False(pane.SearchHitLimit);

        await pane.SearchMoreCommand.ExecuteAsync(null);

        Assert.Equal(SearchListing.Limit, pane.SearchLimit);
        Assert.Equal(1, backend.Asked);
    }

    /// <summary>
    /// **A raised cap would have followed you into the next question.** Keep
    /// looking belongs to the search that was cut off; carried into an
    /// unrelated one it spends a bigger budget on a walk nobody asked to widen,
    /// with nothing on screen saying why.
    /// </summary>
    [AvaloniaFact]
    public async Task A_raised_cap_does_not_follow_you_to_the_next_search()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Fake([Entry("one.txt"), Entry("two.txt"), Entry("three.txt")]));

        await pane.NavigateAsync(VirtualPaths.Search("t", null, false));

        pane.SearchLimit = 2;
        await pane.RefreshAsync();

        await pane.SearchMoreCommand.ExecuteAsync(null);

        Assert.Equal(2 + SearchListing.Limit, pane.SearchLimit);

        await pane.NavigateAsync(VirtualPaths.Search("o", null, false));

        Assert.Equal(SearchListing.Limit, pane.SearchLimit);
    }

    /// <summary>
    /// **The last answer's "there are more" stood over the next one.** Stop
    /// ends a listing without going through the completion that decides this,
    /// so a hand-stopped search following one that was cut off went on claiming
    /// a truncation belonging to a question that was no longer on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task A_stopped_search_does_not_inherit_the_last_truncation()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Fake([Entry("one.txt"), Entry("two.txt"), Entry("three.txt")]));

        await pane.NavigateAsync(VirtualPaths.Search("t", null, false));

        pane.SearchLimit = 2;
        await pane.RefreshAsync();

        Assert.True(pane.SearchHitLimit);

        UseSearch(new Fake([Entry("only.txt")]) { PauseMs = 400 });

        var going = pane.NavigateAsync(VirtualPaths.Search("only", null, false));

        await WaitUntil(() => pane.IsLoading);

        pane.StopSearchCommand.Execute(null);

        await going;

        Assert.False(
            pane.SearchHitLimit, "the previous search's truncation was still on the band");
    }

    /// <summary>
    /// And the band really draws it. Everything above is about the view model;
    /// a sentence nothing binds to is the same silence with more code behind
    /// it.
    /// </summary>
    [Fact]
    public void The_cap_is_said_in_the_band_and_offers_a_way_on()
    {
        var band = Band();

        var capped = Named(band, "SearchCapped");

        Assert.Equal("{Binding SearchHitLimit}", (string?)capped.Attribute("IsVisible"));
        Assert.Equal("Bottom", (string?)capped.Attribute("DockPanel.Dock"));

        var line = capped.Descendants(Avalonia + "TextBlock").Single();

        Assert.Equal("{Binding SearchLimitLine}", (string?)line.Attribute("Text"));

        var more = Named(band, "SearchMore");

        Assert.Equal("{Binding SearchMoreCommand}", (string?)more.Attribute("Command"));
        Assert.Equal("{Binding SearchMoreHint}", (string?)more.Attribute("ToolTip.Tip"));

        // The way on has to be readable and reachable. An unlabelled button is
        // the dead end this whole band exists to avoid, and a tooltip is not an
        // accessible name.
        Assert.Equal("Keep looking", (string?)more.Attribute("Content"));

        Assert.Equal("{Binding SearchMoreHint}",
                     (string?)more.Attribute("AutomationProperties.Name"));
    }

    private static XElement Band()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "DockPanel")
            .Single(d => (string?)d.Attribute(X + "Name") == "SearchBand");

    private static XElement Named(XElement within, string name)
        => within.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), "the pane never got there");
    }

    private sealed class Fake(FileEntry[] results) : ISearchProvider
    {
        public bool IsAvailable => true;
        public string BackendName => "fake";
        public bool SupportsContentSearch => false;
        public int PauseMs { get; init; }

        /// <summary>
        /// How many walks were started, which is the count a redundant
        /// navigation moves.
        /// </summary>
        public int Asked { get; private set; }

        /// <summary>Where a real backend closes its process or its socket.</summary>
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            Asked++;

            try
            {
                foreach (var entry in results)
                {
                    if (PauseMs > 0) await Task.Delay(PauseMs, ct).ConfigureAwait(false);

                    yield return entry;
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }

    /// <summary>
    /// Yields hits and then goes on for ever, which is what a walk of a large
    /// tree looks like from here. Records that it was really ended rather than
    /// merely abandoned.
    /// </summary>
    private sealed class Endless : ISearchProvider
    {
        public bool Ended { get; private set; }

        public bool IsAvailable => true;
        public string BackendName => "endless";
        public bool SupportsContentSearch => false;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return Entry("hit" + i);

                    await Task.Delay(5, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                Ended = true;
            }
        }
    }

    private sealed class NoDisk : IFileSystemProvider
    {
        /// <summary>A folder slow for its own reasons, the way a dead share is.</summary>
        public int PauseMs { get; init; }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            if (PauseMs > 0) await Task.Delay(PauseMs, ct).ConfigureAwait(false);

            yield return [];
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
}
