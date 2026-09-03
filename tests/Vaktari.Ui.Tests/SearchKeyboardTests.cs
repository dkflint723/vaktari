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
/// Reaching a search result without a mouse.
///
/// **Enter and Down did nothing at all.** The results were a column of Buttons,
/// which carry no selection — so there was nothing for an arrow key to move and
/// nothing for Enter to open. Type-then-Enter is the reflex in both Explorer
/// and Dolphin, and here it dead-ended: a result could only be reached by
/// clicking it, which makes the whole feature unusable without a pointer.
///
/// Results are the listing now, so arrow keys, type-ahead, Enter, selection and
/// every shortcut a pane has come with them and are tested where those live.
/// What is left here is the one gesture that has to cross from the box to the
/// listing: Enter.
/// </summary>
public sealed class SearchKeyboardTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private PaneViewModel Pane()
    {
        var pane = Own(new PaneViewModel(new Inert()));

        UseSearch(null);

        return pane;
    }

    /// <summary>Enter goes to the results, which is the gesture that used to
    /// dead-end.</summary>
    [AvaloniaFact]
    public async Task Enter_goes_to_the_results()
    {
        var pane = Pane();

        await pane.NavigateAsync(Path.GetTempPath());

        pane.BeginSearchCommand.Execute(null);
        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.Equal("report", pane.SearchQueryText);
    }

    /// <summary>
    /// With nothing typed, Enter does nothing rather than searching for the
    /// empty string — which on an index matches every file on the machine.
    /// Whitespace is the same nothing: it would build a path that looks like a
    /// real search and ask for exactly that.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Enter_with_nothing_typed_goes_nowhere(string draft)
    {
        var pane = Pane();

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SearchDraft = draft;
        pane.RunSearchCommand.Execute(null);

        Assert.False(pane.IsSearchListing);
    }

    /// <summary>
    /// **One character is a real query.** It used to be refused with "keep
    /// typing…" — but "b" for a folder of build outputs and "~" for the editor
    /// backups are both things people search for, and every other file manager
    /// runs them. Nothing is spent on a query until Enter now, so a short one
    /// costs exactly what a long one does.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("b")]
    [InlineData("~")]
    public async Task One_character_is_a_question_like_any_other(string draft)
    {
        var pane = Pane();

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SearchDraft = draft;
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.Equal(draft, pane.SearchQueryText);
    }

    /// <summary>
    /// Ctrl+F a second time puts the caret back in the field. The signal pulses
    /// rather than latching, because FocusBehavior acts on the false-to-true
    /// edge and the gesture has to work twice.
    /// </summary>
    [AvaloniaFact]
    public void Asking_for_the_field_twice_moves_the_caret_twice()
    {
        var pane = Pane();

        var edges = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.IsSearchFocused) && pane.IsSearchFocused)
                edges++;
        };

        pane.BeginSearchCommand.Execute(null);
        pane.BeginSearchCommand.Execute(null);

        Assert.Equal(2, edges);
        Assert.True(pane.IsSearchOpen);
    }

    /// <summary>
    /// Refining a question means editing it, not retyping it — so the field
    /// opens holding the search you are looking at.
    /// </summary>
    [AvaloniaFact]
    public async Task The_field_opens_holding_the_question_you_are_looking_at()
    {
        var pane = Pane();

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false));

        pane.BeginSearchCommand.Execute(null);

        Assert.Equal("report", pane.SearchDraft);
    }

    /// <summary>
    /// **Retyping over a search keeps where it was looking.** The origin must
    /// stay the folder the search started from rather than becoming the search
    /// path itself — which would carry "vaktari:search:…" forward as the place
    /// to look.
    ///
    /// Both directions, because either one alone is a rule that says "always".
    /// A scope narrowed on purpose must survive editing the question, or
    /// refining a search silently widens it; and one deliberately widened to
    /// everywhere must not snap back to the folder, which would answer a
    /// machine-wide question with one folder's worth of results and look like
    /// the search simply failing to find things.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retyping_over_a_search_keeps_its_folder_and_its_scope(bool scoped)
    {
        var pane = Pane();
        var folder = Path.GetTempPath();

        await pane.NavigateAsync(VirtualPaths.Search("first", folder, scoped));

        pane.BeginSearchCommand.Execute(null);
        pane.SearchDraft = "second";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.SearchQueryText == "second");

        Assert.Equal(folder, VirtualPaths.OriginOf(pane.CurrentPath));
        Assert.Equal(scoped, pane.SearchScopedHere);
        Assert.Equal(scoped ? folder : null, VirtualPaths.ScopeOf(pane.CurrentPath));
    }

    /// <summary>
    /// And a search started in a folder looks in that folder, the way
    /// Explorer's box does — it is the folder you are standing in that you
    /// mean.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_started_in_a_folder_looks_there()
    {
        var pane = Pane();

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.True(pane.SearchScopedHere);

        // Compared by the path rule, not by string: the origin is taken from
        // CurrentPath, which the load has already normalised — so a folder
        // named with a trailing separator is carried without one.
        Assert.True(PathRules.Same(
            Path.GetTempPath(), VirtualPaths.ScopeOf(pane.CurrentPath)!));
    }

    /// <summary>
    /// **But not from a place that is not a folder.** Scoping This PC to itself
    /// is the fault this whole scheme exists to prevent, and the field is one
    /// of the two roads to it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_started_where_there_is_no_folder_looks_everywhere()
    {
        var pane = Pane();

        await pane.NavigateAsync(VirtualPaths.Computer);

        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        await WaitUntil(() => pane.IsSearchListing);

        Assert.False(pane.SearchScopedHere);
        Assert.Null(VirtualPaths.ScopeOf(pane.CurrentPath));
    }

    /// <summary>
    /// The field puts itself away once the question is asked. The band above
    /// the results says what was searched for, so leaving the field open would
    /// show the same words twice while holding 230px of the path bar that the
    /// crumbs need.
    /// </summary>
    [AvaloniaFact]
    public async Task Asking_the_question_gives_the_path_bar_back()
    {
        var pane = Pane();

        await pane.NavigateAsync(Path.GetTempPath());

        pane.BeginSearchCommand.Execute(null);
        Assert.True(pane.IsSearchOpen);

        pane.SearchDraft = "report";
        pane.RunSearchCommand.Execute(null);

        Assert.False(pane.IsSearchOpen);
    }

    /// <summary>
    /// **Escape no longer takes the results with it.** The popup was keyed on
    /// the box's contents, so clearing the box was the only way to close it and
    /// the only way out of a running walk — one gesture doing three jobs.
    /// </summary>
    [AvaloniaFact]
    public async Task Escape_puts_the_field_away_and_leaves_the_results_standing()
    {
        var pane = Pane();

        await pane.NavigateAsync(VirtualPaths.Search("report", null, false));

        pane.BeginSearchCommand.Execute(null);
        pane.DismissSearchCommand.Execute(null);

        Assert.False(pane.IsSearchOpen);
        Assert.Equal("", pane.SearchDraft);

        // Still there, which is the whole point.
        Assert.True(pane.IsSearchListing);
        Assert.Equal("report", pane.SearchQueryText);
    }

    /// <summary>
    /// Clicking away collapses the field, but only when nothing is half-typed
    /// in it — losing an unfinished question to a stray click is worse than a
    /// field left open.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_away_from_a_half_typed_question_keeps_it()
    {
        var pane = Pane();

        pane.BeginSearchCommand.Execute(null);
        pane.SearchDraft = "repo";
        pane.CloseSearchIfEmptyCommand.Execute(null);

        Assert.True(pane.IsSearchOpen);

        pane.SearchDraft = "";
        pane.CloseSearchIfEmptyCommand.Execute(null);

        Assert.False(pane.IsSearchOpen);
    }

    /// <summary>
    /// **The gestures are properties of the markup, not of the view model.** A
    /// view-model-only test would pass with Enter bound to nothing at all,
    /// which is precisely the state this feature was in.
    /// </summary>
    [Fact]
    public void The_field_binds_enter_and_escape_to_the_pane()
    {
        var box = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "TextBox")
            .Single(t => (string?)t.Attribute("PlaceholderText") == "search files");

        var bound = box.Descendants(Avalonia + "KeyBinding")
            .ToDictionary(k => (string?)k.Attribute("Gesture") ?? "",
                          k => (string?)k.Attribute("Command") ?? "");

        Assert.Equal("{Binding ActiveTab.RunSearchCommand}", bound["Enter"]);
        Assert.Equal("{Binding ActiveTab.DismissSearchCommand}", bound["Escape"]);

        Assert.Equal("{Binding ActiveTab.SearchDraft}", (string?)box.Attribute("Text"));
    }

    /// <summary>
    /// And both shortcuts open it. Ctrl+E is Explorer's, Ctrl+F is everyone
    /// else's, and neither may be the one that quietly stopped working.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+F")]
    [InlineData("Ctrl+E")]
    public void Both_shortcuts_open_the_field(string gesture)
    {
        var binding = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "KeyBinding")
            .Single(k => (string?)k.Attribute("Gesture") == gesture);

        Assert.Equal("{Binding ActiveTab.BeginSearchCommand}", (string?)binding.Attribute("Command"));
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
}
