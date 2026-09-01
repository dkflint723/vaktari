using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Choosing which columns the details view shows.
///
/// **There was no way to turn a column off, and no type column to turn on.**
/// The only thing that ever hid a column was the pane getting too narrow for
/// it, which is not a choice — and sorting by type was implemented from the
/// start with nothing to click, because there was no type heading.
///
/// The two tests that matter most here are the ones this could quietly get
/// wrong: that an upgrade does not move anybody's columns, and that choosing a
/// column does not override the width rule keeping the name readable.
/// </summary>
public sealed class ColumnChooserTests : IDisposable
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private readonly SettingsState _before = AppSettings.Current;
    private readonly Action<SettingsState>? _persist = AppSettings.Persist;

    // Never write a test preference into the real settings file.
    public ColumnChooserTests() => AppSettings.Persist = null;

    public void Dispose()
    {
        AppSettings.Apply(_before);
        AppSettings.Persist = _persist;
    }

    private static void Choose(DetailsViewSettings details)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { Details = details },
        });

    private static PaneViewModel Pane(double width = 1400)
        => new(new Inert(), null, null) { ViewportWidth = width };

    // ---- the upgrade, which is the half that would go unnoticed -------------

    /// <summary>
    /// **A settings file written before this feature must read as it always
    /// did.** Deserialization here does not run property initializers — an
    /// absent key arrives as default(T) — which AppSettings.Apply documents and
    /// still instruments, having shipped a startup crash that way once. So all
    /// three of these are phrased to make <c>false</c> mean "what it did
    /// before", and this reads a settings file with no columns in it at all, to
    /// prove the phrasing survives the round trip rather than only that the
    /// record has the right defaults.
    /// </summary>
    [Fact]
    public void A_settings_file_that_never_heard_of_columns_shows_the_old_ones()
    {
        var old = JsonSerializer.Deserialize(
            "{\"version\":1,\"views\":{\"details\":{\"dateStyle\":\"Relative\"}}}",
            SettingsJsonContext.Default.SettingsState);

        Assert.NotNull(old);

        var details = old!.Views.Details;

        Assert.False(details.HideSize, "size vanished for everyone who upgraded");
        Assert.False(details.HideModified, "modified vanished for everyone who upgraded");
        Assert.False(details.ShowType, "a new column appeared uninvited");
    }

    [AvaloniaFact]
    public void Out_of_the_box_that_means_size_and_modified_and_no_type()
    {
        Choose(new DetailsViewSettings());

        var pane = Pane();

        Assert.True(pane.ShowSize);
        Assert.True(pane.ShowModified);
        Assert.False(pane.ShowType);
    }

    // ---- the choice ---------------------------------------------------------

    [AvaloniaFact]
    public void Turning_a_column_off_turns_it_off()
    {
        Choose(new DetailsViewSettings { HideSize = true, HideModified = true });

        var pane = Pane();

        Assert.False(pane.ShowSize);
        Assert.False(pane.ShowModified);
    }

    [AvaloniaFact]
    public void Turning_type_on_turns_it_on()
    {
        Choose(new DetailsViewSettings { ShowType = true });

        Assert.True(Pane().ShowType);
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
        Choose(new DetailsViewSettings { ShowType = true });

        var narrow = Pane(width: 300);

        Assert.False(narrow.ShowSize, "the width rule stopped applying");
        Assert.False(narrow.ShowModified);
        Assert.False(narrow.ShowType, "a chosen column ignored the width rule");
    }

    /// <summary>And room is not a request: the type column stays off in a pane
    /// wide enough for it until somebody asks.</summary>
    [AvaloniaFact]
    public void Room_for_a_column_is_not_a_request_for_it()
    {
        Choose(new DetailsViewSettings());

        Assert.False(Pane(width: 2400).ShowType);
    }

    // ---- the plumbing that makes the screen follow the setting --------------

    /// <summary>
    /// The visibility is computed from a static, so nothing raises it on its
    /// own. Miss the fan-out and the setting changes while the screen does not.
    /// </summary>
    [AvaloniaFact]
    public void Changing_the_choice_tells_the_pane()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Choose(new DetailsViewSettings { ShowType = true });
        pane.RefreshColumns();

        Assert.Contains(nameof(PaneViewModel.ShowType), raised);
        Assert.Contains(nameof(PaneViewModel.ShowSize), raised);
        Assert.Contains(nameof(PaneViewModel.ShowModified), raised);
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

    private static XDocument Markup()
        => XDocument.Load(Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
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
