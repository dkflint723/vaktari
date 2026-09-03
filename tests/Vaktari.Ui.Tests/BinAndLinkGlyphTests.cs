using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Two glyphs that were never drawn.
///
/// **The bin drew the same one whether it held a thousand items or nothing**,
/// so the one question you ask a bin was the one thing it would not answer.
///
/// **And a link was drawn exactly like the thing it points at** — a link to a
/// folder was a folder, in all three listings, while the flag saying otherwise
/// sat on every entry, correct and unread.
/// </summary>
public sealed class BinAndLinkGlyphTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    /// <summary>Answers what it was told to, and counts how often it is asked
    /// the expensive question.</summary>
    private sealed class Bin(bool holding) : ITrashMaintenance
    {
        public int Listed { get; private set; }
        public int Probed { get; private set; }
        public bool Throws { get; init; }

        public bool HasAny()
        {
            Probed++;

            return Throws ? throw new IOException("the bin will not answer") : holding;
        }

        public IReadOnlyList<TrashedItem> List()
        {
            Listed++;

            return holding
                ? [new TrashedItem("t", "/x", "/p", DateTimeOffset.UnixEpoch, 1, false)]
                : [];
        }

        public ValueTask<TrashSweepResult> SweepAsync(
            Vaktari.Core.Settings.TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(new TrashSweepResult());

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(new TrashSweepResult());

        public string Restore(string trashName) => trashName;
    }

    private static Place BinPlace()
        => new()
        {
            Id = "trash", Label = "Bin", Path = VirtualPaths.Trash,
            Icon = "trash", IsAvailable = true, Kind = PlaceKind.Virtual,
        };

    private static PlaceItemViewModel Row() => new(BinPlace());

    // ---- the glyph the row chooses ----------------------------------------

    [AvaloniaFact]
    public void An_empty_bin_draws_the_bin()
    {
        var row = Row();

        Assert.True(row.IsBin);
        Assert.Equal("trash", row.IconToken);
    }

    [AvaloniaFact]
    public void And_one_holding_something_draws_the_full_one()
    {
        var row = Row();

        row.BinHasItems = true;

        Assert.Equal("trash-full", row.IconToken);
    }

    /// <summary>
    /// Every other row is its provider's token unchanged — the fill state is
    /// meaningless there and must not leak into it.
    /// </summary>
    [AvaloniaFact]
    public void No_other_row_changes_glyph()
    {
        var row = new PlaceItemViewModel(new Place
        {
            Id = "home", Label = "Home", Path = Path.GetTempPath(),
            Icon = "home", IsAvailable = true, Kind = PlaceKind.UserFolder,
        });

        row.BinHasItems = true;

        Assert.False(row.IsBin);
        Assert.Equal("home", row.IconToken);
    }

    /// <summary>Both glyphs have to exist, or the row asks for a token that
    /// draws nothing at all.</summary>
    [AvaloniaTheory]
    [InlineData("trash")]
    [InlineData("trash-full")]
    public void Both_glyphs_are_drawn(string token)
    {
        var shape = new global::Avalonia.Controls.Shapes.Path();

        SidebarIcon.SetToken(shape, token);

        Assert.NotNull(shape.Data);
    }

    /// <summary>
    /// The full one differs from the empty one, and differs in the right place:
    /// a contents line across the body, meeting both walls so nothing floats.
    ///
    /// Compared as path data rather than as geometry. A parsed Geometry does
    /// not stringify to its own path data and does not answer StrokeContains
    /// headlessly, so two different drawings are indistinguishable through the
    /// public surface — which is how the first version of this test passed
    /// while asserting nothing.
    /// </summary>
    [Fact]
    public void And_the_full_one_is_the_bin_plus_a_line_across_the_body()
    {
        var empty = SidebarIcon.DataFor("trash");
        var full = SidebarIcon.DataFor("trash-full");

        Assert.NotNull(empty);
        Assert.NotNull(full);
        Assert.NotEqual(empty, full);

        // Everything the empty bin draws, unchanged.
        Assert.StartsWith(empty!, full!, StringComparison.Ordinal);

        // The body's walls are at x 6.5 and 17.5, so a line between exactly
        // those meets both and floats at neither end.
        Assert.Contains("M6.5 11 H17.5", full!, StringComparison.Ordinal);
    }

    // ---- and the sidebar asks, cheaply ------------------------------------

    private static SidebarViewModel Sidebar(Bin bin)
        => new(places: null, search: null, currentPath: null, trash: () => bin);

    [AvaloniaFact]
    public void The_sidebar_asks_the_bin_what_it_holds()
    {
        var bin = new Bin(holding: true);
        var sidebar = Sidebar(bin);

        sidebar.RefreshBinState();

        Assert.Equal(1, bin.Probed);
    }

    /// <summary>
    /// With HasAny, not List. Listing walks every volume's bin and reads a
    /// sidecar per item to recover where each came from, and the sidebar asks
    /// this on every rebuild — none of that work answers the question.
    /// </summary>
    [AvaloniaFact]
    public void Without_listing_the_whole_bin_to_do_it()
    {
        var bin = new Bin(holding: true);

        Sidebar(bin).RefreshBinState();

        Assert.Equal(0, bin.Listed);
    }

    /// <summary>Puts a real bin row in the sidebar, so what the refresh does to
    /// it can be read.</summary>
    private static PlaceItemViewModel BinRowIn(SidebarViewModel sidebar)
    {
        sidebar.Groups.Add(new PlaceGroupViewModel(new PlaceGroup("Places", [BinPlace()])));

        return sidebar.Groups[0].Places[0];
    }

    [AvaloniaFact]
    public void A_full_bin_marks_its_row()
    {
        var sidebar = Sidebar(new Bin(holding: true));
        var row = BinRowIn(sidebar);

        sidebar.RefreshBinState();

        Assert.True(row.BinHasItems);
        Assert.Equal("trash-full", row.IconToken);
    }

    /// <summary>
    /// A bin that will not answer is drawn EMPTY. The glyph is a hint, and the
    /// wrong hint is worse than the plain one — a bin reported full because a
    /// volume refused to answer sends you looking for something to restore.
    /// </summary>
    [AvaloniaFact]
    public void And_one_that_will_not_answer_is_drawn_empty()
    {
        var sidebar = Sidebar(new Bin(holding: true) { Throws = true });
        var row = BinRowIn(sidebar);

        sidebar.RefreshBinState();

        Assert.False(row.BinHasItems);
        Assert.Equal("trash", row.IconToken);
    }

    /// <summary>
    /// The source is read when asked, not captured when built: the trash is
    /// installed well after the shell exists, so a captured value is null
    /// forever and the bin never fills.
    /// </summary>
    [AvaloniaFact]
    public void The_trash_is_read_when_asked_rather_than_captured()
    {
        ITrashMaintenance? installed = null;
        var sidebar = new SidebarViewModel(
            places: null, search: null, currentPath: null, trash: () => installed);

        var row = BinRowIn(sidebar);

        // Nothing installed yet: the row must not claim anything.
        sidebar.RefreshBinState();
        Assert.False(row.BinHasItems);

        var bin = new Bin(holding: true);
        installed = bin;

        sidebar.RefreshBinState();

        Assert.Equal(1, bin.Probed);
        Assert.True(row.BinHasItems);
    }

    // ---- the link emblem ---------------------------------------------------

    [Fact]
    public void The_emblem_is_two_shapes_and_one_of_them_is_filled()
    {
        Assert.NotNull(LinkEmblem.Ground);
        Assert.NotNull(LinkEmblem.Arrow);

        // Not the same drawing, asked by geometry rather than by text — a
        // Geometry does not stringify to its path data, so comparing ToString()
        // compares two identical type names and passes for anything.
        Assert.NotEqual(LinkEmblem.Ground.Bounds, LinkEmblem.Arrow.Bounds);

        // And the arrow sits ON the ground rather than beside it, which is the
        // whole reason the ground is there.
        Assert.True(LinkEmblem.Ground.Bounds.Contains(LinkEmblem.Arrow.Bounds),
                    "the arrow is not inside the ground drawn to carry it");
    }

    /// <summary>
    /// It sits in the bottom-left of the 24×24 grid the rest of the set uses —
    /// the corner Explorer marks, and the one a name never grows into.
    /// </summary>
    [Fact]
    public void And_it_sits_in_the_corner_it_claims_to()
    {
        var bounds = LinkEmblem.Ground.Bounds;

        Assert.True(bounds.Left < 4, $"the emblem starts at x={bounds.Left:0.0}, not at the left edge");
        Assert.True(bounds.Bottom > 20, $"the emblem ends at y={bounds.Bottom:0.0}, not at the bottom");
        Assert.True(bounds.Right < 14, "the emblem reaches more than half way across the icon");
        Assert.True(bounds.Top > 10, "the emblem reaches more than half way up the icon");
    }

    /// <summary>
    /// All three listings, or switching view silently drops the one mark that
    /// says a folder is not the folder it looks like. Details and compact had
    /// exactly that happen to the look-alike chip.
    /// </summary>
    [Fact]
    public void Every_listing_draws_it()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var emblems = markup.Descendants(Avalonia + "Viewbox")
            .Where(v => v.Descendants(Avalonia + "Path").Any(
                p => ((string?)p.Attribute("Data"))?.Contains("LinkEmblem", StringComparison.Ordinal) == true))
            .ToList();

        Assert.Equal(3, emblems.Count);

        foreach (var emblem in emblems)
        {
            Assert.Equal("{Binding IsSymlink}", (string?)emblem.Attribute("IsVisible"));

            // Never eats a click meant for the row underneath it.
            Assert.Equal("False", (string?)emblem.Attribute("IsHitTestVisible"));

            // The ground is filled, which is the whole reason it reads over a
            // shell bitmap it does not control.
            Assert.Contains(emblem.Descendants(Avalonia + "Path"),
                            p => (string?)p.Attribute("Fill") == "{DynamicResource ViewBackground}");
        }
    }
}
