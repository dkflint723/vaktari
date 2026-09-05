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
    /// **A search moves between searches**, and the band is bound to eight
    /// properties that all derive from the path. Retyping the query or ticking
    /// either box changes the path from one search to another without ever
    /// leaving the listing kind — so a band that was only told when it appeared
    /// and disappeared would go on displaying the previous question over the
    /// new one's results.
    ///
    /// The warning and its sentence are on that list because both read the path
    /// now: the sentence names the folder the scope box names, and the warning
    /// asks the backend about this particular question — which on a KDE box is
    /// the difference between a word Baloo answers and a glob it cannot.
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
        Assert.Contains(nameof(PaneViewModel.SearchMatchesCase), announced);
        Assert.Contains(nameof(PaneViewModel.SearchUnindexed), announced);
        Assert.Contains(nameof(PaneViewModel.SearchBackendLine), announced);
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

    // ---- what the box claims it is covering ----------------------------------

    private async Task<string> LabelFor(ISearchProvider? backend, string? origin)
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(backend);

        await pane.NavigateAsync(VirtualPaths.Search("report", origin, scoped: true));

        return pane.SearchScopeLabel;
    }

    /// <summary>
    /// **The box said "everywhere" and neither platform meant it.** Windows
    /// walked the fixed drives, so the stick just plugged in was skipped;
    /// Linux walked the home folder alone, so every other disk on the machine
    /// was. The backend is what decides the roots, so the backend is what says
    /// how to describe them — a phrase kept beside the checkbox would be a
    /// second copy of a rule that lives there, and the two would part company
    /// the first time either moved.
    /// </summary>
    [AvaloniaFact]
    public async Task The_box_says_what_the_backend_actually_reaches()
        => Assert.Equal(
            "searching every drive on this machine",
            await LabelFor(new Fake([]) { Everywhere = "every drive on this machine" }, null));

    /// <summary>
    /// And so does the sentence that explains why the box is disabled, which is
    /// the other place the claim is made and the one a person reads while
    /// wondering what is being searched instead.
    /// </summary>
    [AvaloniaFact]
    public async Task And_so_does_the_sentence_that_says_why_it_is_disabled()
        => Assert.EndsWith(
            "— searching every drive on this machine",
            await LabelFor(
                new Fake([]) { Everywhere = "every drive on this machine" }, VirtualPaths.Trash));

    /// <summary>
    /// **With no backend at all it still says "everywhere."** That is the one
    /// case where nothing is being covered, so no narrower claim would be
    /// truer, and it is what the interface default is for.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_backend_it_says_what_it_always_said()
        => Assert.Equal("searching everywhere", await LabelFor(null, null));

    // ---- and whether anything is indexing ------------------------------------

    private async Task<PaneViewModel> Asking(
        ISearchProvider? backend, string query = "report", string? origin = null,
        bool scoped = false)
    {
        var pane = Own(new PaneViewModel(new NoDisk()));

        UseSearch(backend);

        await pane.NavigateAsync(VirtualPaths.Search(query, origin, scoped));

        return pane;
    }

    /// <summary>
    /// **The one sentence explaining a slow search was written and never
    /// shown.** It sat on the pane and no .axaml named the property, so it
    /// appeared nowhere — and nothing could have reached it in any case, because
    /// the getter chose between it and the backend's name on IsAvailable, which
    /// both shipped providers hardcoded true. A walk of every drive therefore
    /// looked exactly like an index answering, with the wait unaccounted for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_with_no_index_behind_it_says_why_it_is_slow()
    {
        var pane = await Asking(new Fake([]));

        Assert.True(pane.SearchUnindexed);

        Assert.Equal(
            "every folder is read in turn — there is no index on this machine",
            pane.SearchBackendLine);
    }

    /// <summary>
    /// **The two halves of the band contradicted each other on a scoped
    /// search.** The box read "Only in Documents" and the sentence directly
    /// under it said every folder was being read — which on Windows, where
    /// nothing is indexed, was every scoped search there has ever been. The
    /// scope comes off the path, so it is the same string the backend is
    /// handed.
    ///
    /// Both spellings of the folder, because the box and the sentence share one
    /// naming rule and a trailing separator is what a GetFileName without a
    /// TrimEnd answers the empty string to.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Documents\")]
    public async Task A_search_narrowed_to_a_folder_says_that_folder(string origin)
    {
        var pane = await Asking(new Fake([]), origin: origin, scoped: true);

        Assert.Equal("Only in Documents", pane.SearchScopeLabel);

        Assert.Equal(
            "every folder in Documents is read in turn — there is no index on this machine",
            pane.SearchBackendLine);
    }

    /// <summary>
    /// **The sentence outlives the walk, so it must not claim one is running.**
    /// It was written "searching by reading every folder", while the bar and
    /// the Stop beside it are gated on IsLoading and go the moment the search
    /// settles — so on Windows, where every search is a walk, the band went on
    /// announcing a search in progress for as long as the results were on
    /// screen. It stays deliberately: having no index is exactly what somebody
    /// wants to read when they look up at a finished search and wonder what
    /// took so long.
    /// </summary>
    [AvaloniaFact]
    public async Task The_sentence_stays_after_the_walk_without_claiming_one()
    {
        var pane = await Asking(new Fake([Entry("one.txt")]));

        // NavigateAsync is awaited and the backend pauses for nothing, so the
        // search is over by here — no wait, and no proxy for one.
        Assert.False(pane.IsLoading);
        Assert.Single(pane.Entries);

        Assert.True(pane.SearchUnindexed);
        Assert.DoesNotContain("searching", pane.SearchBackendLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// **And an index answering neither explains itself nor names itself.** The
    /// branch that could actually be reached read "searching with directory
    /// walk" on Windows and "searching with baloo" on Fedora: the names of
    /// implementation details, put in front of somebody looking for a file.
    /// Nothing needs explaining when the answer is fast, so nothing is said.
    /// </summary>
    [AvaloniaFact]
    public async Task An_index_answering_says_nothing_and_names_nothing()
    {
        var pane = await Asking(new Fake([]) { Indexes = _ => true });

        Assert.False(pane.SearchUnindexed);
        Assert.DoesNotContain("fake", pane.SearchBackendLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A backend with an index can still walk, and the band has to follow the
    /// QUESTION rather than the backend.** LinuxSearchProvider sends anything
    /// holding * or ? straight past Baloo to the recursive walk, because the
    /// index stores words and not filename patterns — so an answer read off the
    /// provider alone hid the warning for "*.pdf" on every KDE box while home
    /// and every mounted drive were being read, which is the commonest shape of
    /// slow search there is.
    /// </summary>
    [AvaloniaFact]
    public async Task An_index_that_this_question_misses_still_warns()
    {
        var indexed = new Fake([]) { Indexes = q => !q.Text.Contains('*') };

        Assert.False((await Asking(indexed, query: "report")).SearchUnindexed);
        Assert.True((await Asking(indexed, query: "*.pdf")).SearchUnindexed);
    }

    /// <summary>
    /// With no backend at all the warning stands. Nothing is indexing then
    /// either, which is what the sentence says — and it is the arm the
    /// interface's own default was written for, since a null cannot be asked.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_backend_at_all_the_warning_stands()
        => Assert.True((await Asking(null)).SearchUnindexed);

    /// <summary>
    /// And the band really draws it, which is the half the sentence was missing:
    /// a phrase nothing binds to is the same silence with more code behind it.
    ///
    /// The dim and the small are asserted because they are the whole of its
    /// manner: this explains a wait, it does not announce anything, and drawn
    /// at body weight beside the question it would compete with it. The
    /// trimming is asserted because the sentence is the longest thing in the
    /// band and the band is as narrow as a split pane.
    /// </summary>
    [Fact]
    public void The_missing_index_is_said_in_the_band()
    {
        var line = Named(Band(), "SearchNoIndex");

        Assert.Equal("{Binding SearchBackendLine}", (string?)line.Attribute("Text"));
        Assert.Equal("{Binding SearchUnindexed}", (string?)line.Attribute("IsVisible"));

        Assert.Equal("{DynamicResource ViewDimText}", (string?)line.Attribute("Foreground"));
        Assert.Equal("{DynamicResource FontSizeSmall}", (string?)line.Attribute("FontSize"));
        Assert.Equal("CharacterEllipsis", (string?)line.Attribute("TextTrimming"));

        // The same gap the cap row above it keeps off the row above THAT.
        Assert.Equal("0,6,0,0", (string?)line.Attribute("Margin"));
    }

    /// <summary>
    /// Docked under the question rather than beside it — the row above is
    /// already carrying two boxes and a Stop — and THIRD of the three bottom
    /// rows, so downwards from the question the band reads why this is slow,
    /// then how much was cut off, then the bar.
    ///
    /// **Not gated on IsLoading, unlike the two below it.** Having no index is
    /// still true when the walk stops, and that is the moment somebody looks up
    /// to ask what took so long.
    /// </summary>
    [Fact]
    public void The_three_rows_under_the_question_are_in_that_order()
    {
        Assert.Equal(
            ["SearchWalking", "SearchCapped", "SearchNoIndex"],
            Band().Elements()
                .Where(e => (string?)e.Attribute("DockPanel.Dock") == "Bottom")
                .Select(e => (string?)e.Attribute(X + "Name")));
    }

    /// <summary>
    /// **The backend's own name is out of the pane for good.** BackendName is a
    /// diagnostic — "directory walk", "baloo", "walk" — and the moment it is
    /// interpolated into a property the band binds, it is UI copy again. The
    /// whole file is scanned rather than the one property, because the next
    /// time this happens it will be somewhere else.
    /// </summary>
    [Fact]
    public void The_pane_never_puts_the_backend_s_name_in_front_of_anybody()
        => Assert.DoesNotContain(
            "BackendName",
            RepoSource.Ui("ViewModels", "PaneViewModel.cs"),
            StringComparison.Ordinal);

    private sealed class Fake(FileEntry[] results) : ISearchProvider
    {
        public string BackendName => "fake";
        public bool SupportsContentSearch => false;
        public int PauseMs { get; init; }

        /// <summary>
        /// Whether an index answers a given question. A function rather than a
        /// flag, because the real answer is one: LinuxSearchProvider has an
        /// index for a word and none for a glob, and a fake that could not say
        /// that could not describe the case the warning exists for.
        ///
        /// Defaulted to never, the same way round as the interface's own
        /// default, so every test above describes the walk — which is what
        /// every real provider on both platforms currently is.
        /// </summary>
        public Func<SearchQuery, bool> Indexes { get; init; } = static _ => false;

        public bool AnswersFromIndex(SearchQuery query) => Indexes(query);

        /// <summary>
        /// What this backend claims an unscoped search covers. Defaulted to the
        /// interface's own word so every existing test is unchanged.
        /// </summary>
        public string Everywhere { get; init; } = "everywhere";

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
