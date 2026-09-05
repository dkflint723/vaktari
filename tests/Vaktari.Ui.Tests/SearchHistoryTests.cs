using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What has been searched for.
///
/// **Nothing recorded it.** A question was typed, answered, and gone the moment
/// the tab moved on — the only copy of it was the pane's own path. Asking it
/// again meant retyping it, and one that had been refined (narrowed to a
/// folder, then told to mind its capitals) had to be rebuilt a control at a
/// time from memory.
///
/// A history that cannot be stopped or emptied is a log rather than a tool,
/// which is the whole finding the recent lists produced next door — so the
/// switch and the one-gesture clear arrive with the recording rather than a
/// release later.
/// </summary>
public sealed class SearchHistoryTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// NORMALISED, because LoadListingAsync normalises what it is handed and
    /// RunSearch takes its origin from <c>CurrentPath</c> afterwards. Left with
    /// the trailing separator <c>GetTempPath</c> hands out, the path a search
    /// is recorded under and the one a test builds to compare with it are two
    /// different strings.
    /// </summary>
    private static readonly string Folder = PathRules.Normalise(Path.GetTempPath());

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-searches-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly SettingsState _settingsBefore = AppSettings.Current;

    public SearchHistoryTests() => Directory.CreateDirectory(_root);

    public override void Dispose()
    {
        // A process-wide static, like the store OwnedViewModels gives back for
        // us below — a test that left either changed would decide what every
        // later pane records and where it records it.
        AppSettings.Apply(_settingsBefore);

        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private JsonSearchHistory Store() => new(_root);

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
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

    /// <summary>
    /// A history that says what it was asked to keep.
    ///
    /// It announces and it folds, because the shipped store does both and a
    /// double that behaved otherwise would let a pane test pass over
    /// behaviour the application does not have.
    /// </summary>
    private sealed class Recording : ISearchHistory
    {
        public List<string> Recorded { get; } = [];

        public event EventHandler? Changed;

        public void Record(string path)
        {
            Recorded.RemoveAll(held =>
                VirtualPaths.SearchIdentity(held) == VirtualPaths.SearchIdentity(path));

            Recorded.Insert(0, path);

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<string> Recent(int count) => Recorded.Take(count).ToList();

        public int Count => Recorded.Count;

        public int ForgetAll()
        {
            var had = Recorded.Count;

            if (had == 0) return 0;

            Recorded.Clear();
            Changed?.Invoke(this, EventArgs.Empty);

            return had;
        }
    }

    /// <summary>
    /// A backend that answers nothing but says where it would have looked.
    ///
    /// **Its <c>Everywhere</c> is the point of it.** A test that leaves the
    /// provider null reads the hard-coded fallback in
    /// <c>SearchStepWhere</c> and never touches <c>Search?.Everywhere</c> at
    /// all — so the whole reason the tooltip asks the provider could be
    /// deleted with the suite green. The real phrases are "every drive on this
    /// machine" and "your home folder and any mounted drives"; this is neither,
    /// so nothing can pass by accident.
    /// </summary>
    private sealed class Reaching : ISearchProvider
    {
        public bool IsAvailable => true;
        public string BackendName => "probe";
        public bool SupportsContentSearch => false;
        public string Everywhere => "the two drives this probe reaches";

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private PaneViewModel Pane(ISearchHistory? history, ISearchProvider? backend = null)
    {
        UseSearch(backend);
        UseSearchHistory(history);

        return Own(new PaneViewModel(new Inert()));
    }

    // ---- the store ----------------------------------------------------------

    /// <summary>Nothing searched for, nothing to say.</summary>
    [Fact]
    public void A_new_store_remembers_nothing()
    {
        Assert.Equal(0, Store().Count);
        Assert.Empty(Store().Recent(10));
    }

    /// <summary>**The whole finding**: a search that was run is kept, and the
    /// most recent question comes back first.</summary>
    [Fact]
    public void What_was_searched_for_is_kept_newest_first()
    {
        var store = Store();

        store.Record("vaktari:search:one:::any");
        store.Record("vaktari:search:two:::any");

        Assert.Equal(
            ["vaktari:search:two:::any", "vaktari:search:one:::any"],
            store.Recent(10));
    }

    /// <summary>
    /// Asking the same question again moves it up rather than filing it twice
    /// — a menu whose top three rows are one search is a menu of one search.
    /// </summary>
    [Fact]
    public void Asking_the_same_question_again_moves_it_to_the_top()
    {
        var store = Store();

        store.Record("vaktari:search:one:::any");
        store.Record("vaktari:search:two:::any");
        store.Record("vaktari:search:one:::any");

        Assert.Equal(
            ["vaktari:search:one:::any", "vaktari:search:two:::any"],
            store.Recent(10));

        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// **Two "everywhere" searches for the same words, started from two
    /// different folders, were two entries drawing two identical rows.** The
    /// origin travels in the path whether or not it is the scope, and nothing
    /// reads it when it is not — SearchListing hands the backend QueryOf,
    /// ScopeOf and MatchesCase, and ScopeOf is null for both — so the menu
    /// offered one question twice, spelled the same, tooltipped the same and
    /// answering the same. A person who habitually unticks "This folder only"
    /// filled all twelve rows with it.
    /// </summary>
    [Fact]
    public void One_question_asked_from_two_folders_is_one_entry()
    {
        var store = Store();

        store.Record(VirtualPaths.Search("report", @"C:\Alpha", scoped: false));
        store.Record(VirtualPaths.Search("report", @"C:\Beta", scoped: false));

        Assert.Equal(1, store.Count);
    }

    /// <summary>
    /// And the entry that survives is the LAST asking, origin and all.
    ///
    /// **The origin is the wrong thing to compare on, not the wrong thing to
    /// store**: it is what "This folder only" narrows to when the row is
    /// opened again, and CanScopeSearch is false without one — so an entry
    /// stripped of it would replay as a search that can no longer be narrowed.
    /// Keeping the newest asking makes that folder the one the question was
    /// last asked from.
    /// </summary>
    [Fact]
    public void The_surviving_entry_is_the_last_asking_with_its_origin()
    {
        var store = Store();

        store.Record(VirtualPaths.Search("report", @"C:\Alpha", scoped: false));
        store.Record(VirtualPaths.Search("report", @"C:\Beta", scoped: false));

        var kept = Assert.Single(store.Recent(10));

        Assert.Equal(@"C:\Beta", VirtualPaths.OriginOf(kept));
    }

    /// <summary>
    /// And a scope IS part of the question, so narrowing one does not swallow
    /// the wide one it was narrowed from. The control for the two above: fold
    /// on the whole origin rather than on the origin-when-unscoped and this
    /// goes to 1.
    /// </summary>
    [Fact]
    public void A_search_narrowed_to_a_folder_is_a_different_question()
    {
        var store = Store();

        store.Record(VirtualPaths.Search("report", Folder, scoped: false));
        store.Record(VirtualPaths.Search("report", Folder, scoped: true));

        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// **Two spellings of one word are two questions here, and folding them
    /// would lose one.** VirtualPaths.SamePlace already compares a search path
    /// ordinally for exactly this reason: with the case box ticked, "readme"
    /// and "README" have two different answers, so the pane treats them as two
    /// places and this store has to agree.
    /// </summary>
    [Fact]
    public void Two_questions_that_differ_only_in_case_are_two_entries()
    {
        var store = Store();

        store.Record("vaktari:search:readme:::case");
        store.Record("vaktari:search:README:::case");

        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// **The search just made is newest even when the clock disagrees.**
    ///
    /// Recency is carried by a timestamp, and the clock is not a reliable
    /// source of order: two hundred consecutive reads of DateTimeOffset.Now on
    /// this machine gave 199 distinct values, so two searches in one tick tie —
    /// and a tie is settled by dictionary order, which puts the newer one
    /// second and makes the trim drop it first.
    ///
    /// Measured deterministically here through the same fault over a longer
    /// interval: a stamp already in the file that is AHEAD of the clock, which
    /// is what an NTP correction or a settings folder carried from another
    /// machine leaves behind. Without the store forcing each new stamp past the
    /// newest it knows about, the search just made sorts below a file entry
    /// from "tomorrow".
    /// </summary>
    [Fact]
    public void A_stamp_from_the_future_does_not_outrank_a_search_just_made()
    {
        var tomorrow = DateTimeOffset.Now.AddDays(1).ToString("o");

        File.WriteAllText(
            Path.Combine(_root, "searches.json"),
            $"{{\"version\":1,\"searches\":{{\"vaktari:search:old:::any\":\"{tomorrow}\"}}}}");

        var store = Store();

        Assert.Equal(["vaktari:search:old:::any"], store.Recent(10));

        store.Record("vaktari:search:new:::any");

        Assert.Equal(
            ["vaktari:search:new:::any", "vaktari:search:old:::any"],
            store.Recent(10));
    }

    /// <summary>
    /// And minding the capitals is part of the question, not part of its
    /// spelling: the same words asked both ways are two entries with two
    /// different answers.
    ///
    /// The control for the folding above from the other side — the case flag is
    /// the one field besides the query and the scope that the backend is handed.
    /// </summary>
    [Fact]
    public void The_same_words_asked_both_ways_about_capitals_are_two_entries()
    {
        var store = Store();

        store.Record(VirtualPaths.Search("readme", Folder, scoped: false));
        store.Record(VirtualPaths.Search("readme", Folder, scoped: false, matchCase: true));

        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// A file that cannot be read is an empty history, not a launch that fails.
    /// This store is built in the first window's constructor, so anything it
    /// throws happens before there is a window to show it in.
    /// </summary>
    [Fact]
    public void A_file_that_makes_no_sense_is_an_empty_history()
    {
        File.WriteAllText(Path.Combine(_root, "searches.json"), "{ this is not json");

        Assert.Equal(0, Store().Count);
    }

    /// <summary>Bounded, or this becomes a record of somebody's year.</summary>
    [Fact]
    public void It_keeps_a_bounded_number_of_searches()
    {
        var store = Store();

        for (var i = 0; i < 200; i++) store.Record($"vaktari:search:q{i}:::any");

        // The NUMBER, not merely "bounded at all": a bound of 10 is as green
        // as a bound of 50 against an inequality, and the settings page reports
        // this count to the user as what is being kept.
        Assert.Equal(50, store.Count);

        // And the bound drops the OLDEST, not the newest: a history that
        // forgets what you just asked is the one thing it cannot do.
        Assert.Contains("vaktari:search:q199:::any", store.Recent(50));
    }

    /// <summary>**The whole other half of the finding**: it empties in one
    /// action, and says how many that was.</summary>
    [Fact]
    public void Forgetting_them_all_empties_it()
    {
        var store = Store();

        store.Record("vaktari:search:one:::any");
        store.Record("vaktari:search:two:::any");

        Assert.Equal(2, store.ForgetAll());
        Assert.Equal(0, store.Count);
        Assert.Empty(store.Recent(10));
        Assert.Equal(0, store.ForgetAll());
    }

    /// <summary>
    /// The list reaches the FILE, and a clear stays cleared. A history that
    /// came back on the next launch would be the same bug wearing a button.
    /// </summary>
    [Fact]
    public void What_it_holds_survives_a_reload_and_what_it_forgets_does_not()
    {
        var first = Store();

        first.Record("vaktari:search:report:::any");
        first.Flush();

        Assert.Equal(["vaktari:search:report:::any"], Store().Recent(10));

        first.ForgetAll();
        first.Flush();

        Assert.Equal(0, Store().Count);
    }

    /// <summary>
    /// An empty store is not marked dirty by being cleared. Flush runs on the
    /// way out of every session, and a store that reported work to do after an
    /// empty clear would rewrite the same empty file for the life of the
    /// process.
    /// </summary>
    [Fact]
    public void Forgetting_nothing_leaves_nothing_to_write()
    {
        var store = Store();

        Assert.Equal(0, store.ForgetAll());

        store.Flush();

        Assert.False(File.Exists(Path.Combine(_root, "searches.json")),
                     "an empty clear wrote the file anyway");
    }

    // ---- what the pane records ----------------------------------------------

    /// <summary>
    /// **The whole finding, through the road a person takes**: type a question,
    /// press Enter, and the question is remembered.
    ///
    /// Recorded synchronously, before NavigateAsync's first suspension, so
    /// there is nothing here to wait on — and nothing that could raise a
    /// property change from a pool thread.
    /// </summary>
    [AvaloniaFact]
    public async Task Running_a_search_records_it()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(Folder);

        pane.BeginSearchCommand.Execute(null);
        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        Assert.Equal([VirtualPaths.Search("report", Folder, scoped: true)], history.Recorded);
    }

    /// <summary>
    /// And the four fields go in, not just the words: a search narrowed to a
    /// folder comes back narrowed to it. That is the whole reason an entry is
    /// a search PATH rather than a query.
    /// </summary>
    [AvaloniaFact]
    public async Task Narrowing_a_search_records_the_narrower_question_too()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: false));
        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        Assert.Equal(
            [
                VirtualPaths.Search("report", Folder, scoped: true),
                VirtualPaths.Search("report", Folder, scoped: false),
            ],
            history.Recorded);
    }

    /// <summary>
    /// **A search that found nothing is still a search that was made**, and is
    /// the one most worth asking again once the files it looked for exist. The
    /// recent-folder line records only what loaded; this one does not, which is
    /// why it sits above the load rather than below it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_that_finds_nothing_is_still_recorded()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(VirtualPaths.Search("nothing-matches-this", Folder, scoped: true));

        Assert.Empty(pane.Entries);
        Assert.Single(history.Recorded);
    }

    /// <summary>Going to a folder is not asking a question.</summary>
    [AvaloniaFact]
    public async Task Opening_a_folder_records_no_search()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(Folder);

        Assert.Empty(history.Recorded);
    }

    /// <summary>
    /// **The switch, which is the half a history cannot ship without.** Read
    /// where the store is reached rather than at the call site, so a second
    /// recording road cannot be added that forgets to ask.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_is_recorded_when_the_setting_is_off()
    {
        var history = new Recording();
        var pane = Pane(history);

        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { ForgetSearches = true },
        });

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        Assert.Empty(history.Recorded);

        // The control: the same pane and the same fake DO record with the
        // setting back on, so "empty" above is the switch and not an inert
        // double.
        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { ForgetSearches = false },
        });

        await pane.NavigateAsync(VirtualPaths.Search("report again", Folder, scoped: true));

        Assert.Single(history.Recorded);
    }

    // ---- what the menu says -------------------------------------------------

    /// <summary>
    /// A row says the question and the folder it was confined to — two rows
    /// reading "report" that can only be told apart by hovering them is a menu
    /// that makes you work for what it already knows.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_names_the_question_and_where_it_looked()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        var step = Assert.Single(pane.SearchSteps);

        Assert.Equal($"report  in {PathRules.LeafName(Folder)}", step.Name);
        Assert.Equal(Folder, step.Where);
    }

    /// <summary>
    /// And an unscoped one says what "everywhere" actually covers, because
    /// **"everywhere" was not true on either platform** — the provider is what
    /// decides the roots, so the provider is what says.
    /// </summary>
    [AvaloniaFact]
    public async Task An_unscoped_row_says_what_everywhere_covers()
    {
        var history = new Recording();

        // **With no provider this reads the hard-coded fallback and the
        // Search?.Everywhere read can be deleted with the suite green** —
        // measured, by replacing that expression with the bare literal and
        // watching 36 tests pass. The phrase below can only come through the
        // provider.
        var pane = Pane(history, new Reaching());

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: false));

        var step = Assert.Single(pane.SearchSteps);

        Assert.Equal("report", step.Name);
        Assert.Equal("Searching the two drives this probe reaches", step.Where);
    }

    /// <summary>
    /// And with no provider at all it still says something, because that is
    /// the state a pane is in before WindowServices has assigned one.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_backend_the_row_still_says_everywhere()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: false));

        Assert.Equal("Searching everywhere", Assert.Single(pane.SearchSteps).Where);
    }

    /// <summary>
    /// **The case was in the key and in neither the row nor its tooltip, so
    /// refining a search through the box left two rows that were
    /// indistinguishable in every visible respect and did different things.**
    /// That is the exact failure the scope is in the row to avoid, reached by
    /// one click: the box's setter navigates to a new search path, and a
    /// navigation to a search path is recorded.
    /// </summary>
    [AvaloniaFact]
    public async Task Minding_the_capitals_is_part_of_what_a_row_says()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(VirtualPaths.Search("readme", Folder, scoped: true));
        await pane.NavigateAsync(VirtualPaths.Search("readme", Folder, scoped: true, matchCase: true));

        var names = pane.SearchSteps.Select(step => step.Name).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains($"readme  in {PathRules.LeafName(Folder)}, matching case", names);
    }

    /// <summary>And on an unscoped one it is the only aside there is.</summary>
    [AvaloniaFact]
    public async Task An_unscoped_row_says_it_minds_the_capitals_too()
    {
        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(
            VirtualPaths.Search("readme", Folder, scoped: false, matchCase: true));

        Assert.Equal("readme  matching case", Assert.Single(pane.SearchSteps).Name);
    }

    /// <summary>Choosing a row asks the question again, exactly as it was
    /// asked — which is what an entry being a path rather than a query buys.
    /// </summary>
    [AvaloniaFact]
    public async Task Choosing_a_row_asks_that_question_again()
    {
        var history = new Recording();
        var pane = Pane(history);

        var asked = VirtualPaths.Search("report", Folder, scoped: true);

        await pane.NavigateAsync(asked);
        await pane.NavigateAsync(Folder);

        Assert.False(pane.IsSearchListing);

        // By Path, which is what the row carries the question in: a row picked
        // by position would pass with the menu in any order at all.
        pane.SearchSteps.Single(step => step.Path == asked).Open.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.Equal(asked, pane.CurrentPath);
    }

    /// <summary>
    /// The button only advertises the gesture once it leads somewhere: a hint
    /// pointing at an empty menu is worse than no hint.
    /// </summary>
    [AvaloniaFact]
    public async Task The_button_advertises_the_menu_only_once_there_is_one()
    {
        var history = new Recording();
        var pane = Pane(history);

        Assert.False(pane.HasSearchSteps);
        Assert.Equal("Search files  (ctrl+f)", pane.SearchTip);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        Assert.True(pane.HasSearchSteps);
        Assert.Contains("right-click", pane.SearchTip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The menu shows twelve, whatever the store is holding.
    ///
    /// **The store keeps fifty and the menu is the only route to them**, which
    /// is deliberate and is the shape the recent places next door already ship:
    /// JsonRecentStore keeps 200 against a menu of 12, and its settings page
    /// reports the full count too, because a privacy page is asked how much is
    /// being kept rather than how much is on offer. The two numbers being
    /// different is the point; the depth being a number rather than "some" is
    /// what this pins, because nothing else here builds more than two rows.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_shows_twelve_however_many_are_held()
    {
        var store = Store();
        var pane = Pane(store);

        for (var i = 0; i < 20; i++)
            await pane.NavigateAsync(VirtualPaths.Search($"q{i}", Folder, scoped: true));

        Assert.Equal(20, store.Count);
        Assert.Equal(12, pane.SearchSteps.Count);
    }

    /// <summary>
    /// **A GUARD, and it says so.** With no store at all the menu is empty
    /// rather than a crash — the rule every provider static on this type
    /// follows. There is no line to delete that reddens it; what it protects is
    /// the absence of a null dereference.
    /// </summary>
    [AvaloniaFact]
    public void With_no_store_the_menu_is_empty_rather_than_a_crash()
    {
        var pane = Pane(null);

        Assert.Empty(pane.SearchSteps);
        Assert.False(pane.HasSearchSteps);
    }

    // ---- the settings page --------------------------------------------------

    private SettingsViewModel Dialog(ISearchHistory? searches = null)
        => new(new SettingsState(),
               settingsFile: Path.Combine(_root, "settings.json"),
               searches: searches);

    /// <summary>
    /// On by default, for the reason the recent lists give: a privacy switch
    /// that ships off is a feature nobody finds.
    /// </summary>
    [AvaloniaFact]
    public void Remembering_what_was_searched_for_is_on_by_default()
        => Assert.True(Dialog().RememberSearches);

    /// <summary>A settings.json written before the key existed, which is the
    /// case the property's NAME exists for.</summary>
    private static SettingsState Older()
        => System.Text.Json.JsonSerializer.Deserialize(
               "{\"version\":1,\"general\":{\"showTooltips\":true}}",
               SettingsJsonContext.Default.SettingsState)!;

    /// <summary>
    /// **A GUARD, and it says so.** What it protects is the premise the
    /// property's NAME rests on: that deserialization here does not run
    /// property initializers, so whatever <c>default(T)</c> is, is what every
    /// upgrading install gets. It is re-measured rather than quoted — the
    /// neighbouring <c>RememberRecent</c> IS declared <c>= true</c> and comes
    /// back false from a file that has never heard of it.
    ///
    /// No compiling mutation reddens the second assertion, and that is the
    /// point: the zero value of a <c>bool</c> named <c>ForgetSearches</c> is
    /// "remember", and the only way to break that is to RENAME the property,
    /// which is a compile error in its callers rather than a mutation. Adding
    /// a <c>= true</c> initializer to it was tried and changed nothing, which
    /// is the first assertion restated. The observable half is pinned by the
    /// test below.
    /// </summary>
    [Fact]
    public void A_settings_file_that_has_never_heard_of_the_key_still_remembers()
    {
        Assert.False(Older().General.RememberRecent,
                     "initializers now survive deserialization — ForgetSearches can be renamed");

        Assert.False(Older().General.ForgetSearches);
    }

    /// <summary>
    /// And what that buys: a machine upgrading into this build starts keeping
    /// a history rather than shipping the feature switched off behind a
    /// checkbox that says it is on.
    /// </summary>
    [AvaloniaFact]
    public async Task A_settings_file_that_has_never_heard_of_the_key_still_records()
    {
        AppSettings.Apply(Older());

        var history = new Recording();
        var pane = Pane(history);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        Assert.Single(history.Recorded);
    }

    /// <summary>And the switch reaches the file, the inverted way round.</summary>
    [AvaloniaFact]
    public void The_switch_is_saved()
    {
        var vm = Dialog();

        vm.RememberSearches = false;
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result.General.ForgetSearches);
    }

    /// <summary>Clearing is offered when there is something to clear.</summary>
    [AvaloniaFact]
    public void Forgetting_what_was_searched_for_is_offered_when_there_is_some()
    {
        var vm = Dialog(new Recording
        {
            Recorded = { "vaktari:search:one:::any", "vaktari:search:two:::any" },
        });

        Assert.True(vm.HasSearchHistory);
        Assert.True(vm.ForgetSearchHistoryCommand.CanExecute(null));
        Assert.Equal("2 searches are remembered", vm.SearchCountLabel);

        vm.ForgetSearchHistoryCommand.Execute(null);

        Assert.Equal("2 remembered searches will be forgotten when you press Save.",
                     vm.SettingsFileStatus);
    }

    /// <summary>And one search is one search, the way the folder count beside
    /// it already says "One folder is remembered".</summary>
    [AvaloniaFact]
    public void One_remembered_search_is_counted_in_the_singular()
        => Assert.Equal(
            "One search is remembered",
            Dialog(new Recording { Recorded = { "vaktari:search:one:::any" } }).SearchCountLabel);

    /// <summary>And not when there is nothing: an offer to clear an empty list
    /// is a button that does nothing.</summary>
    [AvaloniaFact]
    public void And_not_when_nothing_was_searched_for()
    {
        var vm = Dialog(new Recording());

        Assert.False(vm.HasSearchHistory);
        Assert.False(vm.ForgetSearchHistoryCommand.CanExecute(null));
    }

    /// <summary>
    /// **Armed, not done**, and separate from the switch: turning recording off
    /// must not silently delete what is already there, because that is not what
    /// the checkbox says.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_forget_does_not_clear_anything_yet()
    {
        var history = new Recording { Recorded = { "vaktari:search:one:::any" } };

        var vm = Dialog(history);

        vm.ForgetSearchHistoryCommand.Execute(null);

        Assert.True(vm.ForgetSearchHistoryOnSave);
        Assert.Single(history.Recorded);

        // And it SAYS it is armed, in the strip the two buttons beside it use:
        // a button that reports nothing looks like a button that did nothing,
        // and the clearing does not happen until Save.
        Assert.Equal("One remembered search will be forgotten when you press Save.",
                     vm.SettingsFileStatus);
    }

    /// <summary>
    /// And turning the switch off does not arm the clearing either.
    ///
    /// **A GUARD, and it says so.** What it protects is an ABSENCE — that the
    /// switch's setter does not reach the clearing — so there is no line to
    /// remove and no compiling mutation that reddens it. It would catch
    /// somebody later wiring the two together, which is the tempting shortcut
    /// here and the one that would silently delete a list from a checkbox that
    /// says only "stop recording".
    /// </summary>
    [AvaloniaFact]
    public void Turning_the_switch_off_does_not_forget_anything()
    {
        var vm = Dialog(new Recording { Recorded = { "vaktari:search:one:::any" } });

        vm.RememberSearches = false;

        Assert.False(vm.ForgetSearchHistoryOnSave);
    }

    /// <summary>
    /// **The clear emptied the store and left the menu full.** The rows are
    /// built from the store on every read, so nothing was stale except that
    /// nobody had told the binding to read again.
    /// </summary>
    [AvaloniaFact]
    public async Task Clearing_the_history_empties_the_menu_without_a_restart()
    {
        var history = new Recording();

        UseSearch(null);
        UseSearchHistory(history);

        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Folder);

        var pane = shell.Left.Tabs[0];

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        var raised = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.SearchSteps)) raised++;
        };

        history.ForgetAll();

        Assert.True(raised > 0, "nothing told the menu to read the store again");
        Assert.Empty(pane.SearchSteps);
    }

    /// <summary>
    /// **And it reaches the OTHER window, which is the whole of why the notice
    /// is the store's rather than the shell's.**
    ///
    /// The store is one per application — <c>WindowServices</c> builds it once
    /// and every later window is handed the same one — while the menu is one
    /// per pane. Refreshed from the shell that saved, a second window's
    /// magnifier went on listing every forgotten search; and those rows are not
    /// merely stale, because opening one is a navigation to a search path and a
    /// navigation to a search path RECORDS it, so the entry the user asked to
    /// delete was written straight back into the store.
    ///
    /// Measured through the shipped store rather than the fake, because it is
    /// the shipped store that has to announce.
    /// </summary>
    [AvaloniaFact]
    public async Task Forgetting_them_reaches_a_window_that_did_not_press_save()
    {
        var store = Store();

        UseSearch(null);
        UseSearchHistory(store);

        var first = Own(new ShellViewModel(new Inert()));
        first.Start(null, Folder);

        var second = Own(new ShellViewModel(new Inert()));
        second.Start(null, Folder);

        await first.Left.Tabs[0].NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        var elsewhere = second.Left.Tabs[0];

        // The control: the second window HAS the search the first one ran, so
        // the emptiness below is the clear arriving and not a menu that was
        // never filled.
        Assert.Single(elsewhere.SearchSteps);
        Assert.True(elsewhere.HasSearchSteps);

        // **The NOTICE is the subject, not the property.** SearchSteps is
        // rebuilt from the store on every read, so reading it in a test answers
        // correctly whether or not anything told the binding to read — which is
        // exactly the state the menu was in: right in the view model and stale
        // on screen.
        var told = 0;

        elsewhere.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.SearchSteps)) told++;
        };

        store.ForgetAll();

        Assert.True(told > 0, "the other window's menu was never told to read the store again");

        Assert.Empty(elsewhere.SearchSteps);
        Assert.False(elsewhere.HasSearchSteps);
    }

    /// <summary>
    /// And a search run in one window fills the other window's menu by the same
    /// road — which is what puts the stale row there to be clicked.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_in_one_window_reaches_the_other_windows_menu()
    {
        var store = Store();

        UseSearch(null);
        UseSearchHistory(store);

        var first = Own(new ShellViewModel(new Inert()));
        first.Start(null, Folder);

        var second = Own(new ShellViewModel(new Inert()));
        second.Start(null, Folder);

        var elsewhere = second.Left.Tabs[0];

        Assert.Empty(elsewhere.SearchSteps);

        // The notice, for the reason the test above gives: the property answers
        // correctly whether or not the binding was ever told.
        var told = 0;

        elsewhere.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.SearchSteps)) told++;
        };

        await first.Left.Tabs[0].NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        Assert.True(told > 0, "the other window's menu was never told to read the store again");

        Assert.Single(elsewhere.SearchSteps);
    }

    /// <summary>
    /// A closed tab stops listening.
    ///
    /// **The handler goes on a store that outlives every pane in the
    /// application**, so a pane that did not come off it is held alive by that
    /// store for the life of the process, and every search anybody runs
    /// afterwards raises a property change into the bindings of a tab that is
    /// gone. The recents store next door carries the same pairing for the same
    /// reason.
    /// </summary>
    [AvaloniaFact]
    public async Task A_closed_tab_stops_listening_to_the_store()
    {
        var store = Store();
        var pane = Pane(store);

        await pane.NavigateAsync(VirtualPaths.Search("report", Folder, scoped: true));

        pane.Dispose();

        var raised = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.SearchSteps)) raised++;
        };

        store.Record(VirtualPaths.Search("another", Folder, scoped: true));

        Assert.Equal(0, raised);
    }

    // ---- and the wiring that no view model can be asked about ---------------

    /// <summary>
    /// The magnifier carries the menu, bound to the pane's own rows.
    ///
    /// **Read from the markup, because that is where it lives.** A view-model
    /// test would pass with the flyout bound to nothing at all, which is
    /// exactly the state a search history with no route to it would be in.
    ///
    /// The last assertion is the one line here that no mutation can redden,
    /// and that was MEASURED rather than assumed. <c>Directory.Build.props</c>
    /// sets <c>AvaloniaUseCompiledBindingsByDefault</c>, and the only record
    /// in this assembly carrying all three of Name, Open and Where is
    /// <c>SearchStep</c>: pointing the x:DataType at <c>HistoryStep</c>, the
    /// nearest thing to it, failed the BUILD with
    /// <c>AVLN2000: Unable to resolve property or method of name 'Where'</c>
    /// rather than failing this test. It is kept because it says which record
    /// the rows above are read off; the four assertions before it are what
    /// fails when the wiring is wrong.
    /// </summary>
    [Fact]
    public void The_search_button_carries_the_history_menu()
    {
        var button = SearchButton();

        var flyout = button.Descendants(Avalonia + "MenuFlyout").Single();

        Assert.Equal("{Binding ActiveTab.SearchSteps}", (string?)flyout.Attribute("ItemsSource"));

        var setters = flyout.Descendants(Avalonia + "Setter")
            .ToDictionary(s => (string?)s.Attribute("Property") ?? "",
                          s => (string?)s.Attribute("Value") ?? "");

        Assert.Equal("{Binding}", setters["Header"]);
        Assert.Equal("{Binding Open}", setters["Command"]);
        Assert.Equal("{Binding Where}", setters["ToolTip.Tip"]);

        Assert.Equal("vm:SearchStep",
            (string?)flyout.Descendants(Avalonia + "ControlTheme").Single().Attribute(Xaml + "DataType"));
    }

    /// <summary>
    /// **The row's words go through a TextBlock rather than a Header string,
    /// or an underscore in the question is eaten.** The mechanism is measured
    /// by the test below; this is the markup that avoids it. A view-model test
    /// cannot see this at all: <c>SearchStep.Name</c> is the same string
    /// either way, and it is the CONTROL that renders it that decides whether
    /// "*_test.cs" comes out as "*test.cs" with a 't' underlined.
    /// </summary>
    [Fact]
    public void The_row_renders_the_question_through_a_text_block()
    {
        var theme = SearchButton().Descendants(Avalonia + "ControlTheme").Single();

        var template = theme.Descendants(Avalonia + "DataTemplate").Single();

        Assert.Equal("vm:SearchStep", (string?)template.Attribute(Xaml + "DataType"));

        var block = template.Descendants(Avalonia + "TextBlock").Single();

        Assert.Equal("{Binding Name}", (string?)block.Attribute("Text"));
    }

    /// <summary>
    /// **A GUARD on somebody else's behaviour, and it says so.** It measures
    /// the wound the markup above exists to avoid rather than any line of
    /// Vaktari: a MenuItem renders a STRING header through an AccessText with
    /// RecognizesAccessKey, which eats the underscore and underlines the
    /// letter after it. No mutation of this repository reddens it.
    ///
    /// It is here because the breadcrumb already carries the same measurement
    /// in a comment and nothing tested it, so an Avalonia release that stopped
    /// parsing mnemonics — or started parsing them somewhere else — would
    /// leave two workarounds in place with nothing to say why. A question
    /// somebody typed is arbitrary text; "*_test.cs" and "report_final" are
    /// ordinary things to search for.
    /// </summary>
    [AvaloniaFact]
    public void A_string_header_would_eat_an_underscore()
    {
        var item = new Avalonia.Controls.MenuItem { Header = "report_final" };
        var window = new Avalonia.Controls.Window
        {
            Content = new Avalonia.Controls.Menu { ItemsSource = new[] { item } },
        };

        window.Show();
        window.UpdateLayout();

        var text = global::Avalonia.VisualTree.VisualExtensions
            .GetVisualDescendants(item)
            .OfType<Avalonia.Controls.Primitives.AccessText>()
            .Single();

        Assert.Equal("report_final", text.Text);
        Assert.Equal("f", text.AccessKey);
        Assert.Equal("reportfinal", text.TextLayout.TextLines[0].TextRuns[0].Text.ToString());

        window.Close();
    }

    /// <summary>And its tooltip is the one that grows a second half.</summary>
    [Fact]
    public void The_search_button_says_the_menu_is_there()
        => Assert.Equal("{Binding ActiveTab.SearchTip}",
                        (string?)SearchButton().Attribute("ToolTip.Tip"));

    /// <summary>
    /// **The menu is on the COLLAPSED button, and that is a constraint rather
    /// than a taste.** The open field's TextBox runs CloseSearchIfEmpty on
    /// LostFocus, so a control inside the box that took focus when clicked
    /// would collapse the box out from under itself — the same wound
    /// FocusBehavior.OnLostFocus already special-cases for the address bar's
    /// context menu.
    /// </summary>
    [Fact]
    public void The_menu_hangs_off_the_button_that_is_not_focus_sensitive()
        => Assert.Equal("{Binding !ActiveTab.IsSearchOpen}",
                        (string?)SearchButton().Attribute("IsVisible"));

    private static XElement SearchButton()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "Button")
            .Single(b => (string?)b.Attribute("AutomationProperties.Name")
                         == "Search files  (ctrl+f)");

    /// <summary>
    /// The settings page carries both halves.
    ///
    /// **A view-model test passes with neither control on the page**, which is
    /// the state a switch nobody can reach would be in — and an unstoppable,
    /// unemptiable history is the whole fault this change exists to avoid.
    /// </summary>
    [Fact]
    public void The_settings_page_carries_the_switch_and_the_clear()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));
        var ns = markup.Root!.GetDefaultNamespace();

        Assert.Contains(
            markup.Descendants(ns + "CheckBox"),
            box => (string?)box.Attribute("IsChecked") == "{Binding RememberSearches}");

        var button = markup.Descendants(ns + "Button")
            .Single(b => (string?)b.Attribute("Command") == "{Binding ForgetSearchHistoryCommand}");

        Assert.Equal("Forget what was searched for", (string?)button.Attribute("Content"));

        // Shown only when there is something to forget: an offer to clear an
        // empty list is a button that does nothing.
        Assert.Equal("{Binding HasSearchHistory}", (string?)button.Parent?.Attribute("IsVisible"));

        // And the number beside it, which is what says how much a press would
        // throw away.
        Assert.Contains(
            button.Parent!.Elements(ns + "TextBlock"),
            block => (string?)block.Attribute("Text") == "{Binding SearchCountLabel}");
    }

    /// <summary>
    /// And the dialog is handed the real store, or the switch is on a page that
    /// reports nothing to clear and hides the button that clears it.
    ///
    /// Read from the call site for the reason the one below gives: driving a
    /// real window to prove one argument is an end-to-end test wearing a unit
    /// test's clothes.
    /// </summary>
    [Fact]
    public void The_dialog_is_told_how_many_searches_are_held()
        => Assert.Contains(
            "_services.Searches);",
            RepoSource.Ui("MainWindow.axaml.cs"),
            StringComparison.Ordinal);

    /// <summary>
    /// Emptying it on Save reaches the real store, through the one handler that
    /// already applies a save — so Cancel throws it away like every other
    /// change made on those pages.
    ///
    /// Read from the call site: building a real window and driving its settings
    /// dialog to prove one statement is an end-to-end test wearing a unit
    /// test's clothes, and the statement is the whole of the risk.
    /// </summary>
    [Fact]
    public void The_save_handler_clears_the_store_when_the_dialog_armed_it()
        => Assert.Contains(
            "if (model.ForgetSearchHistoryOnSave) _services.Searches.ForgetAll();",
            RepoSource.Ui("MainWindow.axaml.cs"),
            StringComparison.Ordinal);

    /// <summary>
    /// And the store is built once per application and flushed by the last
    /// window out, beside the two it stands with. Two of these on one directory
    /// would have the first window's Flush write its stale snapshot over the
    /// second's — the fault WindowServices exists to prevent.
    /// </summary>
    [Fact]
    public void The_store_is_built_once_and_flushed_on_the_way_out()
    {
        var source = RepoSource.Ui("WindowServices.cs");

        Assert.Contains("ViewModels.PaneViewModel.Searches = searches;", source,
                        StringComparison.Ordinal);
        Assert.Contains("Searches.Flush();", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper that lends the store gives it back.
    ///
    /// **The one static a test is most likely to leave behind**, and a leak
    /// here is invisible where it happens: the class that left its fake in the
    /// static goes green, and every later class in the assembly records into it
    /// instead. Driven directly rather than waited for, because the restoring
    /// runs at CLASS teardown and no test can see its own.
    /// </summary>
    [Fact]
    public void The_helper_gives_the_search_store_back()
    {
        var before = PaneViewModel.Searches;

        var borrower = new Borrower();

        borrower.Borrow(new Recording());

        Assert.NotSame(before, PaneViewModel.Searches);

        borrower.Dispose();

        Assert.Same(before, PaneViewModel.Searches);
    }

    /// <summary>Reaches the protected helper, which is the thing under test.
    /// </summary>
    private sealed class Borrower : OwnedViewModels
    {
        internal void Borrow(ISearchHistory? history) => UseSearchHistory(history);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        // Wall-clock ceiling on the assertion's own subject, never a fixed
        // number of dispatcher turns.
        var until = DateTime.UtcNow.AddSeconds(5);

        while (!done() && DateTime.UtcNow < until) await Task.Delay(10);
    }
}
