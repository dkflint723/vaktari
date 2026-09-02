using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Choosing which columns a pane shows.
///
/// **There was no way to turn a column off, and no type column to turn on.**
/// The only thing that ever hid a column was the pane getting too narrow for
/// it, which is not a choice — and sorting by type was implemented from the
/// start with nothing to click, because there was no type heading.
///
/// **The choice is per pane**, the way sort and grouping are. A reference
/// listing beside a working one wants different columns, and ticking one on
/// the left must not move the right. It travels with the tab in the session.
///
/// The tests that matter most are the ones this could quietly get wrong: that
/// a session written before the choice existed restores the old columns, that
/// one pane's choice stays out of the other, and that choosing a column does
/// not override the width rule keeping the name readable.
/// </summary>
public sealed class ColumnChooserTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private PaneViewModel Pane(double width = 1400)
        => new(new Inert(), null, null) { ViewportWidth = width };

    // ---- per pane, which is the point ---------------------------------------

    [AvaloniaFact]
    public void Choosing_on_one_pane_leaves_the_other_alone()
    {
        var left = Pane();
        var right = Pane();

        left.ToggleTypeColumnCommand.Execute(null);
        left.ToggleSizeColumnCommand.Execute(null);

        Assert.True(left.ShowType);
        Assert.False(left.ShowSize);

        Assert.False(right.ShowType, "the right pane grew a column it never asked for");
        Assert.True(right.ShowSize, "the right pane lost a column it never touched");
    }

    // ---- the upgrade, which is the half that would go unnoticed -------------

    /// <summary>
    /// **A session written before this existed must restore the old columns.**
    /// Deserialization here does not run property initializers — an absent
    /// key arrives as default(T), which TabState documents for its own scales —
    /// so all three are phrased to make <c>false</c> mean "what it showed
    /// before". This reads a session with no column keys at all, through the
    /// same source-generated context the real store uses, and restores a pane
    /// from it.
    /// </summary>
    [AvaloniaFact]
    public void A_session_that_never_heard_of_columns_restores_the_old_ones()
    {
        var json = "{\"version\":13,\"windows\":[{\"panes\":[{\"tabs\":[{\"path\":\"" +
                   Path.GetTempPath().Replace("\\", "\\\\") + "\"}]}]}]}";

        var session = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState);

        Assert.NotNull(session);

        var tab = session!.Windows[0].Panes[0].Tabs[0];
        var pane = Pane();

        pane.RestoreFrom(tab);

        Assert.True(pane.ShowSize, "size vanished for everyone who upgraded");
        Assert.True(pane.ShowModified, "modified vanished for everyone who upgraded");
        Assert.False(pane.ShowType, "a new column appeared uninvited");
    }

    [AvaloniaFact]
    public void Out_of_the_box_that_means_size_and_modified_and_no_type()
    {
        var pane = Pane();

        Assert.True(pane.ShowSize);
        Assert.True(pane.ShowModified);
        Assert.False(pane.ShowType);
    }

    // ---- it travels with the tab ---------------------------------------------

    [AvaloniaFact]
    public void The_choice_round_trips_through_the_session()
    {
        var before = Pane();

        before.ToggleTypeColumnCommand.Execute(null);
        before.ToggleModifiedColumnCommand.Execute(null);

        var after = Pane();

        after.RestoreFrom(before.ToTabState());

        Assert.True(after.ShowType);
        Assert.False(after.ShowModified);
        Assert.True(after.ShowSize);
    }

    /// <summary>
    /// **A property ToTabState writes but the shell never marks dirty only
    /// persists when something else changes first.** Grouping had exactly that
    /// gap. So this goes through the shell rather than the pane, and asks the
    /// store whether it heard.
    /// </summary>
    [AvaloniaFact]
    public void Changing_the_choice_is_worth_saving()
    {
        var store = new Listening();
        var shell = Own(new ShellViewModel(new Inert(), store: store));

        shell.Start(null, Path.GetTempPath());

        var heard = store.Heard;

        shell.ActiveTab!.ToggleTypeColumnCommand.Execute(null);

        Assert.True(store.Heard > heard, "the session store was not told");
        Assert.True(store.Last!.Windows[0].Panes[0].Tabs[0].ShowType, "it was told, but not the new value");
    }

    // ---- the width rule, which the choice must not override -----------------

    /// <summary>
    /// **Both questions have to say yes.** The width rule was here first and
    /// keeps the last word: a column crushing the name into an ellipsis is
    /// worse than one that stepped aside, and somebody who ticked Size in a
    /// wide window did not thereby ask for an unreadable narrow one.
    /// </summary>
    [AvaloniaFact]
    public void A_chosen_column_still_gives_way_in_a_narrow_pane()
    {
        var narrow = Pane(width: 300);

        narrow.ToggleTypeColumnCommand.Execute(null);

        Assert.False(narrow.ShowSize, "the width rule stopped applying");
        Assert.False(narrow.ShowModified);
        Assert.False(narrow.ShowType, "a chosen column ignored the width rule");

        // And the tick still says what was chosen, so the menu explains the
        // gap rather than hiding it.
        Assert.True(narrow.IsTypeColumnShown);
    }

    /// <summary>And room is not a request: the type column stays off in a pane
    /// wide enough for it until somebody asks.</summary>
    [AvaloniaFact]
    public void Room_for_a_column_is_not_a_request_for_it()
        => Assert.False(Pane(width: 2400).ShowType);

    // ---- the plumbing that makes the screen follow the tick -----------------

    /// <summary>
    /// The visibility is computed, so nothing raises it on its own. Miss the
    /// fan-out and the tick moves while the column does not.
    /// </summary>
    [AvaloniaFact]
    public void Toggling_tells_the_view()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.ToggleTypeColumnCommand.Execute(null);

        Assert.Contains(nameof(PaneViewModel.ShowType), raised);
        Assert.Contains(nameof(PaneViewModel.IsTypeColumnShown), raised);
    }

    /// <summary>Sorting by type was implemented from the start and had nothing
    /// to click. The heading needs an arrow like the other three, and an arrow
    /// that never moves is worse than none.</summary>
    [AvaloniaFact]
    public void The_type_heading_gets_a_sort_arrow()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.SortByCommand.Execute("kind");

        Assert.NotEqual("", pane.KindSortGlyph);
        Assert.Contains(nameof(PaneViewModel.KindSortGlyph), raised);
    }

    // ---- the two grids nobody was checking ----------------------------------

    /// <summary>
    /// **The header and the rows are two separate grids kept in step by hand.**
    /// Nothing couples them: the headings sit over their columns only because
    /// both declare the same six columns, the same widths and the same margin.
    /// Adding a column is precisely the edit that breaks that, and it breaks it
    /// silently — headings sliding one column left is the kind of thing that
    /// ships.
    /// </summary>
    [Fact]
    public void The_heading_grid_and_the_row_grid_still_agree()
    {
        var grids = Markup()
            .Descendants(Avalonia + "Grid")
            .Where(g => (string?)g.Attribute("Margin") == "12,0,18,0")
            .Select(g => g.Element(Avalonia + "Grid.ColumnDefinitions")
                          ?.Elements(Avalonia + "ColumnDefinition")
                          .Select(c => (string?)c.Attribute("Width"))
                          .ToList())
            .Where(widths => widths is not null)
            .ToList();

        Assert.Equal(2, grids.Count);
        Assert.Equal(grids[0], grids[1]);
    }

    /// <summary>
    /// The type column really did land in the slot that was standing empty, in
    /// both grids. Renumbering the columns after it is the edit the row
    /// template warns "goes wrong quietly".
    /// </summary>
    [Fact]
    public void The_type_column_took_the_empty_slot_in_both_grids()
    {
        var inColumnThree = Markup()
            .Descendants()
            .Count(e => (string?)e.Attribute("Grid.Column") == "3");

        Assert.Equal(2, inColumnThree);
    }

    /// <summary>
    /// The chooser binds to the pane under it, not out through the window. A
    /// binding that reaches the shell would make the choice global again by
    /// accident, and nothing else would notice until both panes moved at once.
    /// </summary>
    [Fact]
    public void The_chooser_binds_to_its_own_pane()
    {
        var rows = Markup()
            .Descendants(Avalonia + "MenuItem")
            .Where(m => ((string?)m.Attribute("Command") ?? "").Contains("ColumnCommand", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, rows.Count);

        foreach (var row in rows)
        {
            Assert.DoesNotContain("$parent[Window]", (string?)row.Attribute("Command"));
            Assert.DoesNotContain("$parent[Window]", (string?)row.Attribute("IsChecked"));
        }
    }

    private static XDocument Markup()
        => XDocument.Load(Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }

    private sealed class Listening : ISessionStore
    {
        public int Heard { get; private set; }
        public SessionState? Last { get; private set; }

        public SessionState? Load() => null;

        public void NotifyChanged(SessionState state)
        {
            Heard++;
            Last = state;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
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
