using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Asking a search to look inside files as well as at their names.
///
/// **SearchQuery.MatchContent had no writer and, on Windows, no reader.**
/// Nothing in the application set it, the Windows walk could match names and
/// nothing else, and Baloo — the one backend that could look inside files —
/// did so whatever it was asked. The box that writes it is shaped exactly like
/// Match case before it: a field of the search PATH, so the answer with
/// contents and the answer without are two places, Back goes between them,
/// and a restored tab or a saved search asks what it was asking.
///
/// The walks' own reading is pinned beside each provider; these are the
/// wiring from the box to the query, and what the band says back.
/// </summary>
public sealed class SearchContentsTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string Folder = @"C:\Users\me";

    // ---- the path carries it -------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_search_path_carries_whether_contents_count(bool contents)
        => Assert.Equal(
            contents,
            VirtualPaths.MatchesContent(
                VirtualPaths.Search("report", Folder, scoped: true, matchContent: contents)));

    /// <summary>A different place, which is what makes Back mean something.</summary>
    [Fact]
    public void The_two_ways_of_asking_are_two_different_places()
    {
        var names = VirtualPaths.Search("report", Folder, scoped: true, matchCase: true);
        var both = VirtualPaths.Search("report", Folder, scoped: true, matchCase: true, matchContent: true);

        Assert.NotEqual(names, both);
        Assert.False(VirtualPaths.SamePlace(names, both));

        // Every other field says the same thing, so the difference really is
        // the one field — and the case field in particular survives beside it.
        Assert.Equal(VirtualPaths.QueryOf(names), VirtualPaths.QueryOf(both));
        Assert.Equal(VirtualPaths.ScopeOf(names), VirtualPaths.ScopeOf(both));
        Assert.True(VirtualPaths.MatchesCase(both));
    }

    /// <summary>
    /// **A search kept by an older build has four fields, or three.** Session
    /// tabs and saved searches both hold the path verbatim, and a parser that
    /// demanded the new field would reopen every one as an empty search.
    /// Written out rather than built, because Search cannot make them any more.
    /// </summary>
    [Theory]
    [InlineData(":here:case", true)]
    [InlineData(":here:any", false)]
    [InlineData(":here", false)]
    public void A_search_path_written_before_the_contents_field_still_opens(string tail, bool cased)
    {
        var old = "vaktari:search:report:" + Uri.EscapeDataString(Folder) + tail;

        Assert.Equal("report", VirtualPaths.QueryOf(old));
        Assert.Equal(Folder, VirtualPaths.ScopeOf(old));
        Assert.Equal(cased, VirtualPaths.MatchesCase(old));

        // Absent means names only: all a search could match when it was written.
        Assert.False(VirtualPaths.MatchesContent(old));
    }

    /// <summary>
    /// **An old spelling and a new one are one question.** A history entry or
    /// a saved search written with four fields must be the same question as
    /// the five-field path the pane writes for it now, or asking it again
    /// leaves two identical rows. Scoped as well as unscoped: a scoped search
    /// used to be its own identity, verbatim.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_old_spelling_is_the_same_question_as_the_new_one(bool scoped)
    {
        var old = "vaktari:search:report:" + Uri.EscapeDataString(Folder)
                  + (scoped ? ":here" : ":everywhere") + ":any";

        Assert.Equal(
            VirtualPaths.SearchIdentity(VirtualPaths.Search("report", Folder, scoped)),
            VirtualPaths.SearchIdentity(old));
    }

    [Fact]
    public void Asking_about_contents_is_a_different_question()
        => Assert.NotEqual(
            VirtualPaths.SearchIdentity(VirtualPaths.Search("report", null, false)),
            VirtualPaths.SearchIdentity(VirtualPaths.Search("report", null, false, matchContent: true)));

    // ---- and the backend is told ---------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task What_was_asked_for_is_what_the_backend_is_asked(bool contents)
    {
        var backend = new Recording();

        await foreach (var _ in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("report", null, false, matchContent: contents),
            new ListingOptions { IncludeHidden = false }, CancellationToken.None))
        {
        }

        Assert.Equal(contents, backend.Asked!.MatchContent);
    }

    /// <summary>
    /// **The hidden-files setting reaches the backend**, so a content search
    /// does not open files whose rows would be dropped. Both ways round, so a
    /// constant cannot pass.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_hidden_files_setting_decides_what_is_opened(bool includeHidden)
    {
        var backend = new Recording();

        await foreach (var _ in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("report", null, false, matchContent: true),
            new ListingOptions { IncludeHidden = includeHidden }, CancellationToken.None))
        {
        }

        Assert.Equal(includeHidden, backend.Asked!.ReadsConcealed);
    }

    /// <summary>
    /// The tally the band reads is the one the backend is handed. A listing
    /// that built its own query would count into an object nobody reads.
    /// </summary>
    [Fact]
    public async Task The_backend_counts_into_the_tally_it_was_handed()
    {
        var backend = new Recording();
        var skipped = new ContentSkips();

        await foreach (var _ in SearchListing.EnumerateAsync(
            backend, VirtualPaths.Search("report", null, false, matchContent: true),
            new ListingOptions { IncludeHidden = false }, CancellationToken.None,
            skipped: skipped))
        {
        }

        Assert.Same(skipped, backend.Asked!.Skipped);
    }

    // ---- the box ------------------------------------------------------------

    private async Task<PaneViewModel> Searching(
        string? origin, bool scoped, bool matchCase = false, bool contents = false,
        ISearchProvider? backend = null)
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(backend ?? new Recording());

        await pane.NavigateAsync(VirtualPaths.Search("report", origin, scoped, matchCase, contents));

        return pane;
    }

    /// <summary>Ticking it goes somewhere, and Back returns to the names-only answer.</summary>
    [AvaloniaFact]
    public async Task Ticking_the_box_is_a_navigation_that_back_undoes()
    {
        var pane = await Searching(Folder, scoped: true);

        Assert.False(pane.SearchesContents);

        pane.SearchesContents = true;

        await WaitUntil(() => pane.SearchesContents);

        Assert.True(VirtualPaths.MatchesContent(pane.CurrentPath));
        Assert.True(pane.CanGoBack);

        await pane.GoBackAsync();
        await WaitUntil(() => !pane.SearchesContents);

        Assert.Equal("report", pane.SearchQueryText);
    }

    /// <summary>
    /// **A checkbox writes back what it was just told.** Re-entered with the
    /// value it already has, mid-load, it would start a second navigation and
    /// the walk would begin again from nothing. Settled before the count is
    /// read, for the reason SearchCaseTests gives for its twin.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_writing_back_what_it_was_told_does_not_restart_the_walk()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording { PauseMs = 200, Results = [Entry("a.txt"), Entry("b.txt")] };

        UseSearch(backend);

        var going = pane.NavigateAsync(VirtualPaths.Search("t", null, scoped: false));

        await WaitUntil(() => pane.IsLoading && backend.Walks > 0);

        pane.SearchesContents = pane.SearchesContents;

        await going;
        await Settle();

        Assert.Equal(1, backend.Walks);
    }

    /// <summary>
    /// **No box may undo another.** Each setter rebuilds the whole path, so one
    /// that filled the contents field from a default would silently drop it
    /// the moment the box beside it was touched — and this box arrived after
    /// the other two setters were written.
    /// </summary>
    [AvaloniaFact]
    public async Task Narrowing_the_scope_keeps_the_contents()
    {
        var pane = await Searching(Folder, scoped: false, contents: true);

        pane.SearchScopedHere = true;

        await WaitUntil(() => pane.SearchScopedHere);

        Assert.True(pane.SearchesContents, "ticking the scope box dropped the contents flag");
    }

    [AvaloniaFact]
    public async Task Minding_the_capitals_keeps_the_contents()
    {
        var pane = await Searching(Folder, scoped: true, contents: true);

        pane.SearchMatchesCase = true;

        await WaitUntil(() => pane.SearchMatchesCase);

        Assert.True(pane.SearchesContents, "ticking the case box dropped the contents flag");
    }

    [AvaloniaFact]
    public async Task And_asking_about_contents_keeps_the_scope_and_the_capitals()
    {
        var pane = await Searching(Folder, scoped: true, matchCase: true);

        pane.SearchesContents = true;

        await WaitUntil(() => pane.SearchesContents);

        Assert.True(pane.SearchScopedHere, "ticking the contents box widened the scope");
        Assert.True(pane.SearchMatchesCase, "ticking the contents box dropped the case flag");
    }

    /// <summary>Refining the words in the field keeps what the box was set to.</summary>
    [AvaloniaFact]
    public async Task Retyping_the_question_keeps_the_contents()
    {
        var pane = await Searching(Folder, scoped: true, contents: true);

        pane.SearchDraft = "invoice";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.SearchQueryText == "invoice");

        Assert.True(pane.SearchesContents);
    }

    /// <summary>
    /// And a search begun from a folder starts with names only, because a walk
    /// reads every text file to answer the other way and the person asking is
    /// the one who should choose to pay for that.
    /// </summary>
    [AvaloniaFact]
    public async Task A_fresh_search_from_a_folder_asks_about_names_only()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording());

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.False(pane.SearchesContents);
    }

    /// <summary>
    /// The setter's first clause keeps a folder a folder: a pane that is not
    /// looking at a search is not sent into one by a write to the box.
    /// </summary>
    [AvaloniaFact]
    public async Task Ticking_it_on_a_folder_does_not_send_the_pane_into_a_search()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording();

        UseSearch(backend);

        await pane.NavigateAsync(Path.GetTempPath());

        var before = pane.CurrentPath;

        pane.SearchesContents = true;

        Assert.False(pane.CanGoBack);

        await Settle();

        Assert.Equal(before, pane.CurrentPath);
        Assert.False(pane.IsSearchListing);
        Assert.Equal(0, backend.Walks);
    }

    // ---- where it is offered, and what it says it costs ----------------------

    [AvaloniaFact]
    public async Task A_backend_that_can_look_inside_files_is_offered_the_box()
        => Assert.True((await Searching(Folder, true, backend: new Recording { Contents = true })).CanSearchContents);

    [AvaloniaFact]
    public async Task One_that_cannot_is_not()
        => Assert.False((await Searching(Folder, true, backend: new Recording { Contents = false })).CanSearchContents);

    /// <summary>
    /// **Not for a pattern.** "*.pdf" is a question about names whatever the
    /// box says, so a box beside it would walk again to the same answer. The
    /// same backend, the same pane, asked two ways — and asserted in the
    /// hidden direction, which is the one a dead binding cannot pass.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pattern_is_not_offered_the_box()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording { Contents = true });

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, true));
        Assert.True(pane.CanSearchContents);

        var announced = new List<string>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        await pane.NavigateAsync(VirtualPaths.Search("*.pdf", Folder, true));

        Assert.False(pane.CanSearchContents);

        // Raised as the path moved, or a bound box would keep what it had.
        Assert.Contains(nameof(PaneViewModel.CanSearchContents), announced);
    }

    [AvaloniaFact]
    public async Task With_no_backend_there_is_no_box()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(null);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, true));

        Assert.False(pane.CanSearchContents);
    }

    /// <summary>
    /// **The tooltip says what ticking it costs, and that depends on who
    /// answers.** An index has read the files already; a walk has to read
    /// every one, and reads plain text only — so the same words over both
    /// would be a promise one of them does not keep.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_says_what_ticking_it_costs()
    {
        var walked = await Searching(Folder, true, backend: new Recording { Indexes = _ => false });

        Assert.Contains("Every file is opened", walked.SearchContentsHint, StringComparison.Ordinal);
        Assert.Contains("plain-text", walked.SearchContentsHint, StringComparison.Ordinal);

        var indexed = await Searching(Folder, true, backend: new Recording { Indexes = _ => true });

        Assert.Contains("as the index has read them", indexed.SearchContentsHint, StringComparison.Ordinal);
        Assert.DoesNotContain("Every file is opened", indexed.SearchContentsHint, StringComparison.Ordinal);
    }

    /// <summary>
    /// **An index that had nothing is said, while the walk behind it runs.**
    /// On a KDE desktop the band decides before Baloo has answered, and Baloo
    /// installed but switched off answers nothing — so the walk that followed,
    /// which with the box ticked opens every file in reach, ran under a band
    /// that said nothing at all. The sentence names the index as having had
    /// nothing rather than claiming there is none, because there is one.
    /// </summary>
    [AvaloniaFact]
    public async Task An_index_that_had_nothing_is_said_while_the_walk_runs()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording { Indexes = _ => true, WalksInstead = true };

        UseSearch(backend);

        var announced = new List<string>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false, matchContent: true));
        await WaitUntil(() => pane.SearchUnindexed);

        Assert.Equal("every folder and file is read in turn — the index had nothing for this",
                     pane.SearchBackendLine);
        Assert.Contains("Every file is opened", pane.SearchContentsHint, StringComparison.Ordinal);
        Assert.Contains(nameof(PaneViewModel.SearchUnindexed), announced);

        // And it belongs to that load: the next question, which the index
        // does answer, is not said to have been walked.
        backend.WalksInstead = false;

        await pane.NavigateAsync(VirtualPaths.Search("invoice", null, false, matchContent: true));
        await WaitUntil(() => !pane.IsLoading && pane.SearchQueryText == "invoice");

        Assert.False(pane.SearchUnindexed);
    }

    /// <summary>
    /// **The line under an unindexed search says what is read.** Reading every
    /// text file is the slow part, and the part worth being told about.
    /// </summary>
    [AvaloniaFact]
    public async Task The_band_says_the_files_are_read_when_they_are()
    {
        var names = await Searching(null, scoped: false);

        Assert.Equal("every folder is read in turn — there is no index on this machine",
                     names.SearchBackendLine);

        var contents = await Searching(null, scoped: false, contents: true);

        Assert.Equal("every folder and file is read in turn — there is no index on this machine",
                     contents.SearchBackendLine);
    }

    /// <summary>And not when they are not: a pattern reads names whatever the box says.</summary>
    [AvaloniaFact]
    public async Task A_pattern_is_not_claimed_to_read_files()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording());

        await pane.NavigateAsync(VirtualPaths.Search("*.txt", null, false, matchContent: true));

        Assert.StartsWith("every folder is read in turn", pane.SearchBackendLine, StringComparison.Ordinal);
    }

    /// <summary>Moving between the two answers tells the band about both lines that change.</summary>
    [AvaloniaFact]
    public async Task Moving_to_the_other_answer_tells_the_band()
    {
        var pane = await Searching(null, scoped: false);

        var announced = new List<string>();

        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false, matchContent: true));

        Assert.Contains(nameof(PaneViewModel.SearchesContents), announced);
        Assert.Contains(nameof(PaneViewModel.SearchContentsHint), announced);
        Assert.Contains(nameof(PaneViewModel.SearchBackendLine), announced);
    }

    // ---- what was not read ----------------------------------------------------

    [Fact]
    public void Nothing_skipped_says_nothing()
    {
        Assert.Equal("", PaneViewModel.SkippedLine(null));
        Assert.Equal("", PaneViewModel.SkippedLine(new ContentSkips()));
    }

    [Fact]
    public void One_and_many_are_said_properly()
    {
        var one = new ContentSkips();
        one.CountTooLarge();

        Assert.Equal("1 file over 64 MiB was not read", PaneViewModel.SkippedLine(one));

        var many = new ContentSkips();
        for (var i = 0; i < 1200; i++) many.CountTooLarge();
        many.CountOnline();
        many.CountOnline();

        Assert.Equal(
            "1,200 files over 64 MiB were not read; 2 files kept online were not downloaded to be read",
            PaneViewModel.SkippedLine(many));
    }

    /// <summary>
    /// **The count reaches the band.** Counted on the pool by the backend and
    /// said on the dispatcher when the search settles — and a count that
    /// stayed in the tally would be the silence this line exists to break.
    /// </summary>
    [AvaloniaFact]
    public async Task What_the_backend_skipped_is_said_when_the_search_settles()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording { TooLarge = 3 });

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false, matchContent: true));
        await WaitUntil(() => !pane.IsLoading);

        Assert.True(pane.HasSearchSkipped);
        Assert.Equal("3 files over 64 MiB were not read", pane.SearchSkippedLine);
    }

    /// <summary>
    /// **The line is only drawn if its visibility is announced with it.** The
    /// band binds IsVisible to HasSearchSkipped, which is computed from the
    /// line — so a change to the line that did not also announce the flag
    /// would leave the row hidden with the words already in it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_line_s_visibility_is_announced_with_it()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(new Recording { TooLarge = 3 });

        var announced = new List<string>();

        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false, matchContent: true));
        await WaitUntil(() => !pane.IsLoading && pane.HasSearchSkipped);

        Assert.Contains(nameof(PaneViewModel.HasSearchSkipped), announced);
    }

    /// <summary>
    /// And it goes with the answer it belonged to. A fresh tally per load, and
    /// the line cleared as the next begins, so one question's count can never
    /// stand over another's rows.
    /// </summary>
    [AvaloniaFact]
    public async Task The_next_question_starts_from_nothing()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording { TooLarge = 3 };

        UseSearch(backend);

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false, matchContent: true));
        await WaitUntil(() => !pane.IsLoading && pane.HasSearchSkipped);

        backend.TooLarge = 0;

        await pane.NavigateAsync(VirtualPaths.Search("invoice", null, false, matchContent: true));
        await WaitUntil(() => !pane.IsLoading && pane.SearchQueryText == "invoice");

        Assert.False(pane.HasSearchSkipped);
        Assert.Equal("", pane.SearchSkippedLine);
    }

    /// <summary>
    /// **A stopped search never reaches the block that says it**, and what it
    /// skipped before the Stop is still missing from the rows being kept.
    /// </summary>
    [AvaloniaFact]
    public async Task Stopping_says_what_was_skipped_so_far()
    {
        var pane = Own(new PaneViewModel(new NoDisk()));
        var backend = new Recording { TooLarge = 1, PauseMs = 5_000, Results = [Entry("a.txt")] };

        UseSearch(backend);

        _ = pane.NavigateAsync(VirtualPaths.Search("report", null, false, matchContent: true));

        await WaitUntil(() => pane.IsLoading && backend.Counted);

        pane.StopSearchCommand.Execute(null);

        Assert.False(pane.IsLoading);
        Assert.Equal("1 file over 64 MiB was not read", pane.SearchSkippedLine);
    }

    // ---- the history row and the saved search --------------------------------

    /// <summary>
    /// **The two answers must not read alike.** Ticking the box navigates, so
    /// one question asked both ways leaves two history rows — and a saved
    /// search is named from the same words, so two saved side by side would
    /// share a name. The phrase follows the box's own words, as "matching
    /// case" follows "Match case".
    /// </summary>
    [Theory]
    [InlineData(false, false, false, "report")]
    [InlineData(false, false, true, "report  searching contents")]
    [InlineData(true, true, true, "report  in @, matching case, searching contents")]
    [InlineData(true, false, false, "report  in @")]
    [InlineData(false, true, false, "report  matching case")]
    public void A_history_row_says_how_the_question_was_asked(
        bool scoped, bool matchCase, bool contents, string expected)
        // The leaf is asked of PathRules rather than written in, because this
        // folder is spelled for Windows and the same test runs on Fedora.
        => Assert.Equal(
            expected.Replace("@", PathRules.LeafName(Folder)),
            PaneViewModel.SearchStepName(VirtualPaths.Search("report", Folder, scoped, matchCase, contents)));

    /// <summary>
    /// Asking the same question again moves its history entry rather than
    /// adding one — including when the entry was written by a build that knew
    /// only four fields.
    /// </summary>
    [Fact]
    public void An_old_history_entry_is_moved_rather_than_repeated()
    {
        var dir = Directory.CreateTempSubdirectory("vaktari-content-history").FullName;

        try
        {
            var store = new JsonSearchHistory(dir);

            store.Record("vaktari:search:report:::any");
            store.Record(VirtualPaths.Search("report", null, false));

            Assert.Single(store.Recent(10));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- the band draws it -----------------------------------------------------

    private static XElement Band()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "DockPanel")
            .Single(d => (string?)d.Attribute(X + "Name") == "SearchBand");

    private static XElement Named(XElement band, string name)
        => band.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void The_band_carries_the_box_and_wires_it_to_the_pane()
    {
        var box = Named(Band(), "SearchContents");

        Assert.Equal("{Binding SearchesContents}", (string?)box.Attribute("IsChecked"));
        Assert.Equal("{Binding CanSearchContents}", (string?)box.Attribute("IsVisible"));
        Assert.Equal("{Binding SearchContentsHint}", (string?)box.Attribute("ToolTip.Tip"));
        Assert.Equal("Search contents", (string?)box.Attribute("Content"));
    }

    [Fact]
    public void The_band_says_what_was_not_read()
    {
        var line = Named(Band(), "SearchSkipped");

        Assert.Equal("{Binding SearchSkippedLine}", (string?)line.Attribute("Text"));
        Assert.Equal("{Binding HasSearchSkipped}", (string?)line.Attribute("IsVisible"));
    }

    /// <summary>
    /// **In a narrow pane every control keeps its size, and the boxes wrap.**
    /// On one docked row, three boxes, a Stop and Save search did not fit a
    /// narrow pane, and what gave way was the boxes — measured here with the
    /// old markup, a 321-pixel band gave Match case and Search contents no
    /// width at all, so they vanished while still "visible", and squeezed
    /// "Only in Temp" into 61 pixels over three lines. A real window at that
    /// width with every control up: a backend that honours capitals, and a
    /// search still running so Stop shows. Every control must have width, the
    /// three boxes one line each — the same height — and nothing may cover
    /// anything else or run past the band.
    /// </summary>
    [AvaloniaFact]
    public async Task In_a_narrow_pane_every_control_keeps_its_size()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 560, Height = 700 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var backend = new Recording { PauseMs = 10_000, Results = [Entry("a.txt")], Capitals = true };
        UseSearch(backend);

        var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

        try
        {
            _ = pane.NavigateAsync(VirtualPaths.Search("report", Path.GetTempPath(), scoped: true));
            await WaitUntil(() => pane.IsLoading && backend.Walks > 0);

            window.Measure(new Size(560, 700));
            window.Arrange(new Rect(0, 0, 560, 700));
            Dispatcher.UIThread.RunJobs();

            var band = window.GetVisualDescendants().OfType<DockPanel>()
                .First(d => d.Name == "SearchBand" && d.IsEffectivelyVisible);

            var boxes = new[] { "SearchScope", "SearchCase", "SearchContents", "SearchStop", "SearchSave" }
                .Select(name => band.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name))
                .Select(c => (c.Name, Box: new Rect(c.TranslatePoint(default, band)!.Value, c.Bounds.Size),
                              c.IsEffectivelyVisible))
                .ToList();

            Assert.All(boxes, b => Assert.True(b.IsEffectivelyVisible, $"{b.Name} is not showing"));

            foreach (var a in boxes)
            {
                Assert.True(a.Box.Width > 0, $"{a.Name} was given no width, so it is not there");
                Assert.True(a.Box.Right <= band.Bounds.Width + 0.5, $"{a.Name} runs past the band");

                foreach (var b in boxes.Where(b => b.Name != a.Name))
                    Assert.False(a.Box.Intersects(b.Box), $"{a.Name} is drawn over {b.Name}");
            }

            // One line each: a box squeezed narrower than its label wraps it,
            // and comes out taller than the others.
            var heights = boxes.Take(3).Select(b => b.Box.Height).Distinct().ToList();

            Assert.True(heights.Count == 1, "a box was squeezed onto more than one line: "
                                            + string.Join(", ", boxes.Take(3).Select(b => $"{b.Name} {b.Box.Height}")));
        }
        finally
        {
            pane.StopSearchCommand.Execute(null);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---- machinery ----------------------------------------------------------

    private static FileEntry Entry(string name)
        => new(name, "/tmp/" + name, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

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
    /// A backend that remembers what it was asked, says whether it can look
    /// inside files, and counts refusals into the tally it was handed.
    /// </summary>
    private sealed class Recording : ISearchProvider
    {
        public string BackendName => "recording";

        public bool Contents { get; init; } = true;
        public bool SupportsContentSearch => Contents;

        public Func<SearchQuery, bool> Indexes { get; init; } = static _ => false;
        public bool AnswersFromIndex(SearchQuery query) => Indexes(query);

        /// <summary>Whether the Match case box is offered, for the test that needs every control up.</summary>
        public bool Capitals { get; init; }
        public bool SupportsCaseSensitivity => Capitals;

        public FileEntry[] Results { get; init; } = [];
        public int PauseMs { get; init; }

        /// <summary>How many files over the limit to report, before any result.</summary>
        public int TooLarge { get; set; }

        /// <summary>Whether to say, as it starts, that it is walking rather than asking its index.</summary>
        public bool WalksInstead { get; set; }

        public SearchQuery? Asked { get; private set; }
        public int Walks { get; private set; }

        /// <summary>Whether the refusals have been counted yet, for the Stop test.</summary>
        public volatile bool Counted;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            Asked = query;
            Walks++;

            for (var i = 0; i < TooLarge; i++) query.Skipped?.CountTooLarge();

            if (WalksInstead) query.WalkingInstead?.Invoke();

            Counted = true;

            foreach (var entry in Results)
            {
                if (PauseMs > 0) await Task.Delay(PauseMs, ct).ConfigureAwait(false);

                yield return entry;
            }
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
