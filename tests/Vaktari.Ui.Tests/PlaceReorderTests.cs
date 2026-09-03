using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Places;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging a pinned place into a different position.
///
/// **Both providers have implemented ReorderAsync since they were written and
/// nothing ever called it.** Pins came out in the order they were added, and
/// the only way to change that was to edit places.json by hand — which starts
/// to matter at exactly the point a sidebar has enough pins to be worth
/// tidying. Explorer and Dolphin both reorder by dragging.
/// </summary>
public sealed class PlaceReorderTests
{
    private static Place Fixed(string label) => new()
    {
        Id = label.ToLowerInvariant(), Label = label, Path = "/" + label,
        Kind = PlaceKind.UserFolder, Icon = "folder",
    };

    private static Place Pin(string label) => new()
    {
        Id = "pin:/pins/" + label, Label = label, Path = "/pins/" + label,
        Kind = PlaceKind.Bookmark, Icon = "bookmark", IsUserPinned = true,
    };

    /// <summary>Home, Documents, then three pins — the shape both providers build.</summary>
    private static PlaceGroupViewModel Group()
        => new(new PlaceGroup("places",
            [Fixed("Home"), Fixed("Documents"), Pin("alpha"), Pin("beta"), Pin("gamma")]));

    private static List<string> Labels(PlaceGroupViewModel group)
        => [.. group.Places.Select(p => p.Label)];

    [Fact]
    public void Only_the_rows_the_person_pinned_can_move()
        => Assert.Equal([2, 3, 4], Group().PinnedRows());

    [Fact]
    public void A_pin_dragged_down_lands_where_it_was_dropped()
    {
        var group = Group();

        group.MovePin(group.Places[2], slot: 2);

        Assert.Equal(["Home", "Documents", "beta", "gamma", "alpha"], Labels(group));
    }

    [Fact]
    public void And_one_dragged_up_does_too()
    {
        var group = Group();

        group.MovePin(group.Places[4], slot: 0);

        Assert.Equal(["Home", "Documents", "gamma", "alpha", "beta"], Labels(group));
    }

    /// <summary>
    /// **The slot is a position among the PINS, not in the group.** Home and
    /// Documents are the desktop's own rows, rebuilt from it every time — an
    /// order imposed on them would not survive a rebuild, and the provider's
    /// reorder reads none of them. Dragging a pin to the very top puts it at
    /// the top of the pins.
    /// </summary>
    [Fact]
    public void A_pin_dragged_off_the_top_stops_at_the_first_pin()
    {
        var group = Group();

        group.MovePin(group.Places[3], slot: -5);

        Assert.Equal(["Home", "Documents", "beta", "alpha", "gamma"], Labels(group));
    }

    [Fact]
    public void And_off_the_bottom_stops_at_the_last()
    {
        var group = Group();

        group.MovePin(group.Places[2], slot: 900);

        Assert.Equal(["Home", "Documents", "beta", "gamma", "alpha"], Labels(group));
    }

    /// <summary>A row that is not a pin is not moved by asking.</summary>
    [Fact]
    public void Home_cannot_be_dragged_anywhere()
    {
        var group = Group();

        group.MovePin(group.Places[0], slot: 2);

        Assert.Equal(["Home", "Documents", "alpha", "beta", "gamma"], Labels(group));
    }

    /// <summary>And a drop back where it started changes nothing.</summary>
    [Fact]
    public void Dropping_a_pin_where_it_already_was_moves_nothing()
    {
        var group = Group();

        group.MovePin(group.Places[3], slot: 1);

        Assert.Equal(["Home", "Documents", "alpha", "beta", "gamma"], Labels(group));
    }

    /// <summary>
    /// The order written down is the order on screen, and it names only the
    /// pins — the provider matches on the "pin:" prefix and would sort an
    /// unrecognised id to the end.
    /// </summary>
    [AvaloniaFact]
    public async Task Saving_writes_the_order_the_rows_are_in()
    {
        var provider = new Recording();
        var sidebar = new SidebarViewModel(provider);

        await sidebar.ReloadAsync();

        var group = sidebar.Groups.Single();

        group.MovePin(group.Places[2], slot: 2);

        await sidebar.SavePinOrderAsync();

        Assert.Equal(["pin:/pins/beta", "pin:/pins/gamma", "pin:/pins/alpha"], provider.Ordered);
    }

    /// <summary>
    /// **The rows are not rebuilt afterwards.** They are already in the order
    /// being saved — the drag put them there — so a reload would flash the
    /// whole sidebar to land on what is on screen already.
    /// </summary>
    [AvaloniaFact]
    public async Task Saving_does_not_rebuild_the_sidebar()
    {
        var provider = new Recording();
        var sidebar = new SidebarViewModel(provider);

        await sidebar.ReloadAsync();

        var built = provider.Builds;
        var group = sidebar.Groups.Single();

        group.MovePin(group.Places[2], slot: 1);

        await sidebar.SavePinOrderAsync();

        Assert.Equal(built, provider.Builds);
        Assert.Equal(["Home", "Documents", "beta", "alpha", "gamma"], Labels(group));
    }

    /// <summary>
    /// A provider that cannot write the order down leaves the rows where the
    /// drag put them rather than throwing at the pointer.
    /// </summary>
    [AvaloniaFact]
    public async Task A_provider_that_refuses_does_not_take_the_drag_down_with_it()
    {
        var provider = new Recording { Refuses = true };
        var sidebar = new SidebarViewModel(provider);

        await sidebar.ReloadAsync();

        var group = sidebar.Groups.Single();

        group.MovePin(group.Places[2], slot: 2);

        await sidebar.SavePinOrderAsync();

        Assert.Equal(["Home", "Documents", "beta", "gamma", "alpha"], Labels(group));
    }

    /// <summary>With nothing pinned there is nothing to write down.</summary>
    [AvaloniaFact]
    public async Task With_no_pins_at_all_nothing_is_written()
    {
        var provider = new Recording { Pins = false };
        var sidebar = new SidebarViewModel(provider);

        await sidebar.ReloadAsync();
        await sidebar.SavePinOrderAsync();

        Assert.Null(provider.Ordered);
    }

    // ---- which press begins a drag ------------------------------------------

    /// <summary>
    /// The rows are laid out for real, so the walk under test crosses the same
    /// visual parents it crosses in the window — a DataContext set on a
    /// detached control would answer without ever proving the walk works.
    /// </summary>
    private static (ItemsControl List, PlaceGroupViewModel Group) Laid()
    {
        var group = Group();

        var list = new ItemsControl
        {
            DataContext = group,
            ItemsSource = group.Places,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PlaceItemViewModel>(
                (_, _) => new Button { Content = new TextBlock() }, supportsRecycling: true),
        };

        var window = new Window { Content = list, Width = 200, Height = 400 };

        window.Show();
        window.UpdateLayout();

        return (list, group);
    }

    /// <summary>
    /// **The row IS a Button**, which is how clicking a place navigates — so
    /// unlike the tab strip's version of this walk, stopping at a Button would
    /// stop at every row. Pressing a pin arms the drag from inside its button's
    /// own content.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_a_pin_arms_a_reorder_from_inside_its_button()
    {
        var (list, group) = Laid();

        var inside = list.ContainerFromIndex(2)!.GetVisualDescendants()
            .OfType<TextBlock>().First();

        Assert.Same(group.Places[2], PlaceDrag.ArmedBy(inside));
        Assert.Same(list, PlaceDrag.ListFor(inside));
    }

    /// <summary>
    /// And pressing the row's own padding arms it too, which is the case a
    /// Button check would break.
    ///
    /// **This is the load-bearing half.** Pressing the LABEL cannot show it:
    /// a DataContext inherits down the tree, so a TextBlock inside the row
    /// answers the walk on its own and the button above it is never reached.
    /// Press the button itself — the margin either side of the label, which is
    /// most of the row — and it is.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_the_rows_own_padding_arms_it_as_well()
    {
        var (list, group) = Laid();

        var button = list.ContainerFromIndex(2)!.GetVisualDescendants()
            .OfType<Button>().First();

        Assert.Same(group.Places[2], PlaceDrag.ArmedBy(button));
    }

    /// <summary>
    /// **And pressing Home arms nothing.** It is the desktop's own row,
    /// rebuilt from it on every refresh and read by no reorder — so a drag that
    /// moved it would appear to work and undo itself at the next rebuild, which
    /// is worse than not offering the gesture.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_a_row_the_desktop_owns_arms_nothing()
    {
        var (list, _) = Laid();

        var inside = list.ContainerFromIndex(0)!.GetVisualDescendants()
            .OfType<TextBlock>().First();

        Assert.Null(PlaceDrag.ArmedBy(inside));
    }

    /// <summary>And a press on nothing at all is not a drag either.</summary>
    [AvaloniaFact]
    public void Pressing_outside_any_row_arms_nothing()
    {
        Laid();

        Assert.Null(PlaceDrag.ArmedBy(null));
        Assert.Null(PlaceDrag.ArmedBy(new Border()));
    }

    private sealed class Recording : IPlacesProvider
    {
        public IReadOnlyList<string>? Ordered { get; private set; }
        public int Builds { get; private set; }
        public bool Refuses { get; init; }
        public bool Pins { get; init; } = true;

        // Never raised: a rebuild is asked for, not announced.
        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
        {
            Builds++;

            List<Place> places = Pins
                ? [Fixed("Home"), Fixed("Documents"), Pin("alpha"), Pin("beta"), Pin("gamma")]
                : [Fixed("Home"), Fixed("Documents")];

            return ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
                [new PlaceGroup("places", places)]);
        }

        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
        {
            if (Refuses) throw new IOException("places.json is read-only");

            Ordered = orderedIds;
            return ValueTask.CompletedTask;
        }

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject here"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct)
            => ValueTask.FromResult(0);
    }
}
