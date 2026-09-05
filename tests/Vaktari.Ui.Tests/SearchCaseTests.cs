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
/// Asking a search to mind the capitals.
///
/// **The flag had two readers and no writer.** `SearchQuery.CaseSensitive` is
/// branched on by `WindowsSearchProvider.Walk` — for the substring's
/// StringComparison and for the glob's ignoreCase, both pinned by
/// `SearchMatchingTests` — and by `LinuxSearchProvider.WalkOneAsync`. Nothing
/// anywhere set it: `SearchListing.EnumerateAsync` is the only place in the
/// application that builds a `SearchQuery`, and it assigned Text, ScopePath and
/// MaxResults. So every search anybody has ever run was case-insensitive, and
/// the two providers' careful branching could not be reached from the window at
/// all.
///
/// The fix is a writer, shaped exactly like the scope box that came before it:
/// the flag is a field of the search PATH, so asking the same words two ways is
/// being in two places — Back returns to the previous answer rather than
/// re-running it, and a tab restored from session.json comes back asking what it
/// was asking.
/// </summary>
public sealed class SearchCaseTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string Folder = @"C:\Users\me";

    // ---- the path carries it -------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_search_path_carries_how_it_is_being_asked(bool matchCase)
        => Assert.Equal(
            matchCase,
            VirtualPaths.MatchesCase(
                VirtualPaths.Search("report", Folder, scoped: true, matchCase: matchCase)));

    /// <summary>
    /// And it is a different place, which is what makes Back mean something
    /// and what stops one search's tab being reused for the other.
    /// </summary>
    [Fact]
    public void The_two_ways_of_asking_are_two_different_places()
    {
        var loose = VirtualPaths.Search("report", Folder, scoped: true);
        var exact = VirtualPaths.Search("report", Folder, scoped: true, matchCase: true);

        Assert.NotEqual(loose, exact);
        Assert.False(PathRules.Same(loose, exact));

        // The other three fields still say the same thing, so the difference
        // really is the one field rather than a differently built path.
        Assert.Equal(VirtualPaths.QueryOf(loose), VirtualPaths.QueryOf(exact));
        Assert.Equal(VirtualPaths.OriginOf(loose), VirtualPaths.OriginOf(exact));
        Assert.Equal(VirtualPaths.ScopeOf(loose), VirtualPaths.ScopeOf(exact));
    }

    /// <summary>
    /// **A search tab left open by an older build has three fields.**
    /// `PaneViewModel.ToTabState` writes `Path = CurrentPath` verbatim into
    /// session.json, so every such path comes back at the next start — and a
    /// parser that demanded the new field would reopen each one as an empty
    /// search, silently losing the question.
    ///
    /// Written out rather than built, because `Search` cannot produce a
    /// three-field path any more and a test that asked it to would be checking
    /// nothing.
    /// </summary>
    [Fact]
    public void A_search_path_written_before_the_case_field_still_opens()
    {
        var old = "vaktari:search:report:" + Uri.EscapeDataString(Folder) + ":here";

        Assert.Equal("report", VirtualPaths.QueryOf(old));
        Assert.Equal(Folder, VirtualPaths.OriginOf(old));
        Assert.True(VirtualPaths.IsScoped(old));
        Assert.Equal(Folder, VirtualPaths.ScopeOf(old));

        // Absent means insensitive: what those paths were written under.
        Assert.False(VirtualPaths.MatchesCase(old));
    }

    // ---- and the backend is told ---------------------------------------------

    /// <summary>
    /// The point of the whole change: the flag reaches the query the provider
    /// is handed. Without this line the two walks' case branches are
    /// unreachable code.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task What_was_asked_for_is_what_the_backend_is_asked(bool matchCase)
    {
        var backend = new Recording();

        await ReadAsync(backend, VirtualPaths.Search("report", null, false, matchCase));

        Assert.Equal(matchCase, backend.Asked!.CaseSensitive);
    }

    private static async Task ReadAsync(ISearchProvider backend, string path)
    {
        await foreach (var _ in SearchListing.EnumerateAsync(
            backend, path, new ListingOptions { IncludeHidden = false }, CancellationToken.None))
        {
            // Drained rather than read: the assertion is on what the backend was
            // handed, and the fake yields nothing.
        }
    }

    // ---- the box ------------------------------------------------------------

    private async Task<PaneViewModel> Searching(
        string? origin, bool scoped, bool matchCase, ISearchProvider? backend = null)
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(backend ?? new Recording());

        await pane.NavigateAsync(VirtualPaths.Search("report", origin, scoped, matchCase));

        return pane;
    }

    /// <summary>
    /// **Ticking it goes somewhere**, the same move the scope box makes, so the
    /// looser answer is still behind you rather than gone.
    /// </summary>
    [AvaloniaFact]
    public async Task Ticking_the_box_is_a_navigation_that_back_undoes()
    {
        var pane = await Searching(Folder, scoped: true, matchCase: false);

        Assert.False(pane.SearchMatchesCase);

        pane.SearchMatchesCase = true;

        // Started rather than awaited by the setter, so the property is read
        // back once the navigation has landed.
        await WaitUntil(() => pane.SearchMatchesCase);

        Assert.True(VirtualPaths.MatchesCase(pane.CurrentPath));
        Assert.True(pane.CanGoBack);

        await pane.GoBackAsync();
        await WaitUntil(() => !pane.SearchMatchesCase);

        Assert.Equal("report", pane.SearchQueryText);
    }

    /// <summary>
    /// **A checkbox writes back what it was just told**, and the navigation
    /// that lands the new path is what tells it. Re-entered with the value it
    /// already has, mid-load, it would start a second navigation — which
    /// cancels the walk in progress and begins it again from nothing.
    ///
    /// **Settled before the count is read, and that is the whole test.** The
    /// re-entrant navigation is started fire-and-forget by the setter and the
    /// walk it begins is several awaits further on, so awaiting only the FIRST
    /// navigation and counting immediately was a race: measured with the
    /// `value == SearchMatchesCase` half of the guard removed, this test passed
    /// anyway, because the second walk had not started yet when the count was
    /// read. It reddens with the wait in place.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_writing_back_what_it_was_told_does_not_restart_the_walk()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording { PauseMs = 200, Results = [Entry("a.txt"), Entry("b.txt")] };

        UseSearch(backend);

        var going = pane.NavigateAsync(VirtualPaths.Search("t", null, scoped: false));

        await WaitUntil(() => pane.IsLoading && backend.Walks > 0);

        pane.SearchMatchesCase = pane.SearchMatchesCase;

        await going;
        await Settle();

        Assert.Equal(1, backend.Walks);
    }

    /// <summary>
    /// **The two boxes must not undo each other.** Each setter rebuilds the
    /// whole path, so one that filled the other's field from a default would
    /// silently widen a search the moment you touched the box beside it.
    /// </summary>
    [AvaloniaFact]
    public async Task Narrowing_the_scope_keeps_the_capitals()
    {
        var pane = await Searching(Folder, scoped: false, matchCase: true);

        pane.SearchScopedHere = true;

        await WaitUntil(() => pane.SearchScopedHere);

        Assert.True(pane.SearchMatchesCase, "ticking the scope box dropped the case flag");
    }

    [AvaloniaFact]
    public async Task And_minding_the_capitals_keeps_the_scope()
    {
        var pane = await Searching(Folder, scoped: true, matchCase: false);

        pane.SearchMatchesCase = true;

        await WaitUntil(() => pane.SearchMatchesCase);

        Assert.True(pane.SearchScopedHere, "ticking the case box widened the scope");
        Assert.Equal(Folder, VirtualPaths.ScopeOf(pane.CurrentPath));
    }

    /// <summary>
    /// Refining the question in the search field keeps it too — retyping is
    /// editing the search you are looking at, and the scope already survives it.
    ///
    /// **This is also the case the pane refused to move to.** With the box
    /// ticked, "report" and "Report" are two questions with two answers, and
    /// `NavigateAsync`'s already-here guard used `PathRules.Same` — which is
    /// OrdinalIgnoreCase on Windows, so the two paths read as one and Enter did
    /// nothing whatsoever. Only the query's capitals differ here, deliberately:
    /// change anything else and the test passes without the guard being right.
    /// </summary>
    [AvaloniaFact]
    public async Task Retyping_only_the_capitals_is_a_new_question()
    {
        var pane = await Searching(Folder, scoped: true, matchCase: true);

        pane.SearchDraft = "Report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.SearchQueryText == "Report");

        Assert.True(pane.SearchMatchesCase);

        // And it was a move, so the looser spelling is still behind you.
        Assert.True(pane.CanGoBack);
    }

    /// <summary>
    /// And what the pane drops when you go somewhere else is dropped here too.
    ///
    /// **The filter followed you into the next answer.** A capital-only retype
    /// left `FilterText` standing because `LoadListingAsync` asks the same
    /// "same place?" question the navigation guard does, so a rule applied to
    /// one and not the other would run the new search through a word typed
    /// against the old one — reading as an empty result set. The raised cap and
    /// the open subfolders are cleared in the same block.
    ///
    /// The SELECTION carry a few lines above that block is under the same
    /// question and is deliberately NOT asserted on here, because nothing can
    /// separate its two branches from outside: `ResortInPlace` runs before
    /// `Reselect(carry)` and captures the selection for itself with
    /// `SelectedPaths()`, so the rows are already back by the time the carry is
    /// read. Measured in this worktree — two rows selected in a search, then a
    /// capital-only retype, and both came back with the carry EMPTY. So the one
    /// line that reads it is the only element of this change with no killing
    /// mutation, and it is a consistency change rather than a behaviour one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_new_question_leaves_the_old_answers_filter_behind()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording { Results = [Entry("Report.txt")] });

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, true, matchCase: true));
        await WaitUntil(() => pane.Entries.Count == 1);

        pane.FilterText = "txt";

        pane.SearchDraft = "Report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.SearchQueryText == "Report"
                              && !pane.IsLoading && pane.Entries.Count == 1);

        Assert.Equal("", pane.FilterText);
    }

    /// <summary>
    /// The other half of the same rule, which is why the search clause is a
    /// clause rather than a replacement: a FOLDER spelled with the other case
    /// is one folder where the platform says so, and reloading it flashed the
    /// listing and pushed a Back that went nowhere.
    ///
    /// Asked of <c>PathRules.Comparison</c> rather than of the OS, because that
    /// is the rule under test: it is OrdinalIgnoreCase on Windows and Ordinal
    /// on Linux, where the two spellings really are two directories.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_spelled_the_other_way_follows_the_platform_rule()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording());

        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        await pane.NavigateAsync(folder);
        await pane.NavigateAsync(folder.ToUpperInvariant());

        var oneFolder = PathRules.Comparison == StringComparison.OrdinalIgnoreCase;

        Assert.Equal(
            !oneFolder,
            pane.CanGoBack);
    }

    /// <summary>And a search begun from a folder starts loose, because that is
    /// what every search did before there was a box.</summary>
    [AvaloniaFact]
    public async Task A_fresh_search_from_a_folder_asks_the_old_way()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording());

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.False(pane.SearchMatchesCase);
    }

    /// <summary>
    /// **The setter's first clause is the one that keeps a folder a folder.**
    /// Its guard has two halves and they refuse different things: the second
    /// stops the write-back the landing navigation causes, and this one stops a
    /// pane that is not looking at a search from being sent into one.
    ///
    /// Measured with the clause removed: a pane sitting on a real folder
    /// answered `SearchMatchesCase = true` by navigating to
    /// <c>vaktari:search:::everywhere:case</c> — <c>SearchQueryText</c> and
    /// <c>OriginOf</c> both answer "" off a folder path, so the pane jumped
    /// into an empty search asking nothing of everywhere.
    ///
    /// The history assertion is the exact one, and deliberately made before any
    /// waiting: <c>NavigateAsync</c> pushes the entry it is leaving before its
    /// first await, so a navigation that started at all is already visible on
    /// the line after the write.
    /// </summary>
    [AvaloniaFact]
    public async Task Ticking_it_on_a_folder_does_not_send_the_pane_into_a_search()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording();

        UseSearch(backend);

        await pane.NavigateAsync(Path.GetTempPath());

        var before = pane.CurrentPath;

        Assert.False(pane.IsSearchListing);
        Assert.False(pane.CanGoBack);

        pane.SearchMatchesCase = true;

        Assert.False(pane.CanGoBack);

        // And nothing lands late either: a walk started here would be a second
        // way for the same write to have gone somewhere.
        await Settle();

        Assert.Equal(before, pane.CurrentPath);
        Assert.False(pane.IsSearchListing);
        Assert.Equal(0, backend.Walks);
    }

    // ---- and it is only offered where it means something ---------------------

    /// <summary>
    /// **An index answers its own way.** `LinuxSearchProvider` hands the query
    /// to baloosearch and filters the answers by scope alone, so a tick over
    /// that backend would change nothing — the same silence this finding is
    /// about, moved from a field to a checkbox. The provider says whether it
    /// honours the flag, and the box is drawn from that.
    /// </summary>
    [AvaloniaFact]
    public async Task A_backend_that_ignores_the_flag_is_not_offered_the_box()
        => Assert.False(
            (await Searching(Folder, true, false, new Recording { Honours = false }))
                .CanMatchCase);

    [AvaloniaFact]
    public async Task A_backend_that_honours_it_is()
        => Assert.True(
            (await Searching(Folder, true, false, new Recording { Honours = true }))
                .CanMatchCase);

    /// <summary>
    /// With no backend at all there is nothing to honour it, and the interface
    /// default is what answers for a provider that has not thought about it.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_backend_there_is_no_box()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(null);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, true));

        Assert.False(pane.CanMatchCase);

        // Through the interface, because that is the only way a default member
        // is reachable — and the default is the whole point: a provider that
        // has not thought about the flag must not be offered the box.
        ISearchProvider silent = new Deaf();

        Assert.False(silent.SupportsCaseSensitivity);
    }

    /// <summary>
    /// And the band really draws it. Everything above is about the view model;
    /// a flag nothing binds to is the same silence with more code behind it.
    /// </summary>
    [Fact]
    public void The_band_carries_the_box_and_wires_it_to_the_pane()
    {
        var band = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "DockPanel")
            .Single(d => (string?)d.Attribute(X + "Name") == "SearchBand");

        var box = band.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "SearchCase");

        Assert.Equal("{Binding SearchMatchesCase}", (string?)box.Attribute("IsChecked"));
        Assert.Equal("{Binding CanMatchCase}", (string?)box.Attribute("IsVisible"));

        // Labelled, and labelled the word BatchRename already uses for this.
        Assert.Equal("Match case", (string?)box.Attribute("Content"));
    }

    // ---- machinery ----------------------------------------------------------

    private static FileEntry Entry(string name)
        => new(name, "/tmp/" + name, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    /// <summary>
    /// The other half of <see cref="WaitUntil"/>, for a test whose claim is
    /// that nothing happens: it gives a navigation that should not have started
    /// as long as one that should, so "it did not" cannot just mean "not yet".
    /// </summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), "the pane never got there");
    }

    /// <summary>
    /// A backend that remembers what it was asked, and can be told whether it
    /// claims to honour the case flag.
    /// </summary>
    private sealed class Recording : ISearchProvider
    {
        public bool IsAvailable => true;
        public string BackendName => "recording";
        public bool SupportsContentSearch => false;

        public bool Honours { get; init; } = true;
        public bool SupportsCaseSensitivity => Honours;

        public FileEntry[] Results { get; init; } = [];
        public int PauseMs { get; init; }

        /// <summary>The query as the provider received it.</summary>
        public SearchQuery? Asked { get; private set; }

        /// <summary>How many walks were started, which a redundant navigation moves.</summary>
        public int Walks { get; private set; }

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            Asked = query;
            Walks++;

            foreach (var entry in Results)
            {
                if (PauseMs > 0) await Task.Delay(PauseMs, ct).ConfigureAwait(false);

                yield return entry;
            }
        }
    }

    /// <summary>
    /// A provider that implements nothing beyond the interface's requirements,
    /// which is how the default reaches a test at all.
    /// </summary>
    private sealed class Deaf : ISearchProvider
    {
        public bool IsAvailable => false;
        public string BackendName => "deaf";
        public bool SupportsContentSearch => false;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoDisk : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
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
