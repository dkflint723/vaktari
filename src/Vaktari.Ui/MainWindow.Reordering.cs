using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Dragging a tab along the strip, and a pinned place up or down the sidebar.
///
/// **These are not drag and drop, however much they look like it.** Nothing is
/// carried: no payload is built, no toolkit drag is started, nothing leaves the
/// window and no other application can receive one. They are bookkeeping on a
/// list that is already on screen — which is why they can be done with plain
/// pointer events, and why a drop's rules about intent, effect and what the
/// cursor should say have nothing to do with them.
///
/// The one thing they share with a real drag is <c>_dragOrigin</c>, and that
/// belongs to neither: the pointer press sets it, and all three gestures read
/// it to decide whether the pointer has moved far enough to mean anything. It
/// stays with the press.
/// </summary>
public partial class MainWindow
{
    private PaneViewModel? _tabDrag;
    private Avalonia.Controls.Primitives.TabStrip? _tabStrip;
    private double _tabGrab;
    private bool _tabDragging;

    private ViewModels.PlaceItemViewModel? _placeDrag;
    private ItemsControl? _placeList;
    private double _placeGrab;
    private bool _placeDragging;

    /// <summary>
    /// Notes that a press landed on a tab, so a move past the threshold can
    /// reorder the strip.
    ///
    /// **Tabs could not be reordered at all** — the press was recorded and then
    /// dropped, because neither of the things a press arms is a tab: EntryAt
    /// finds no row, and ListForEmptySpace bails on the tab template. This must
    /// leave both of those alone; a tab that armed <c>_dragSource</c> would drag
    /// real files again, which is the fault the ROW rule above was written to
    /// fix.
    ///
    /// Not the DragDrop system, deliberately: the strip already declares
    /// AllowDrop so a tab is a target for FILE drops, and starting a real drag
    /// from a tab would put a drop target and a reorder on one gesture.
    ///
    /// The close button lives inside the item, so a press on it reaches here
    /// too — a wobble while pressing ✕ should close the tab, not shuffle the
    /// strip first.
    /// </summary>
    private void ArmTabDrag(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        EndTabDrag();

        if (!properties.IsLeftButtonPressed) return;

        for (var visual = e.Source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Button) return;

            if (visual is not Avalonia.Controls.Primitives.TabStripItem
                { DataContext: PaneViewModel tab } item) continue;

            if (item.FindAncestorOfType<Avalonia.Controls.Primitives.TabStrip>() is not { } strip)
                return;

            _tabDrag = tab;
            _tabStrip = strip;

            // Where inside the tab it was grabbed, so the tab keeps its grip on
            // the pointer however the strip scrolls underneath.
            _tabGrab = e.GetPosition(item).X;

            return;
        }
    }

    /// <summary>
    /// Moves the pressed tab under the pointer.
    ///
    /// Container bounds rather than accumulated widths: margins and the strip's
    /// own padding are not this function's business, and TranslatePoint is exact
    /// whatever the panel does with them. A container that is not laid out yet
    /// gives no geometry to reason about, so the frame is skipped rather than
    /// computed from zeroes — the strip would shuffle at random.
    /// </summary>
    private void DragTab(PointerEventArgs e)
    {
        if (_tabDrag is not { } tab
            || _tabStrip is not { DataContext: PaneGroupViewModel group })
        {
            EndTabDrag();
            return;
        }

        // The button can be released outside the window, where no release
        // arrives — the live state ends the drag, not just the event that ought
        // to have come. The band already works this way.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndTabDrag();
            return;
        }

        var here = e.GetPosition(this);

        // X only: the strip is one horizontal row, and a vertical wobble while
        // pressing a tab is not a reorder.
        if (!_tabDragging && Math.Abs(here.X - _dragOrigin.X) < 6) return;

        _tabDragging = true;

        var from = group.Tabs.IndexOf(tab);
        if (from < 0)
        {
            EndTabDrag();
            return;
        }

        var middles = new List<double>(group.Tabs.Count);
        double width = 0;

        for (var i = 0; i < group.Tabs.Count; i++)
        {
            if (_tabStrip.ContainerFromIndex(i) is not Control box
                || box.TranslatePoint(default, this) is not { } at) return;

            middles.Add(at.X + box.Bounds.Width / 2);

            if (i == from) width = box.Bounds.Width;
        }

        group.MoveTab(tab, DragReorder.SlotFor(here.X - _tabGrab + width / 2, middles, from));
    }

    private void EndTabDrag()
    {
        _tabDrag = null;
        _tabStrip = null;
        _tabDragging = false;
    }

    /// <summary>
    /// Arms a reorder of the sidebar's pinned places.
    ///
    /// **Both providers have implemented ReorderAsync since they were written
    /// and nothing ever called it.** Pins stayed in the order they were added,
    /// and the only way to change that was to edit places.json by hand — which
    /// starts to matter at exactly the point a sidebar has enough pins to be
    /// worth tidying. Explorer and Dolphin both reorder by dragging.
    ///
    /// Unlike the tab strip's version this does NOT stop at a Button, because
    /// the place row IS one. The pinned test is what keeps the gesture off the
    /// rows it must not move.
    /// </summary>
    private void ArmPlaceDrag(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        EndPlaceDrag(save: false);

        if (!properties.IsLeftButtonPressed) return;

        if (e.Source is not Visual source) return;
        if (PlaceDrag.ArmedBy(source) is not { } place) return;
        if (PlaceDrag.ListFor(source) is not { } list) return;

        _placeDrag = place;
        _placeList = list;

        // Where inside the row it was grabbed, so the row keeps its grip on the
        // pointer whatever the list does underneath.
        _placeGrab = e.GetPosition(list.ContainerFromItem(place) as Visual ?? source).Y;
    }

    /// <summary>
    /// Moves the pressed place under the pointer.
    ///
    /// The Y twin of <see cref="DragTab"/>, over the same arithmetic and for
    /// the same reason — a neighbour's MIDDLE rather than its near edge, which
    /// is what stops a tall row dragged past a short one from oscillating every
    /// frame.
    ///
    /// Only the pinned rows are candidates, so their centres are what the
    /// pointer is compared against: a pin dragged to the top of the list lands
    /// at the top of the PINS rather than above Home.
    /// </summary>
    private void DragPlace(PointerEventArgs e)
    {
        if (_placeDrag is not { } place
            || _placeList is not { DataContext: ViewModels.PlaceGroupViewModel group })
        {
            EndPlaceDrag(save: false);
            return;
        }

        // The button can be released outside the window, where no release
        // arrives — the live state ends the drag, not just the event that ought
        // to have come.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndPlaceDrag(save: true);
            return;
        }

        var here = e.GetPosition(this);

        // Y only: the sidebar is one vertical column, and a horizontal wobble
        // while pressing a place is not a reorder.
        if (!_placeDragging && Math.Abs(here.Y - _dragOrigin.Y) < 6) return;

        _placeDragging = true;

        var rows = group.PinnedRows();
        var from = rows.IndexOf(group.Places.IndexOf(place));

        if (from < 0)
        {
            EndPlaceDrag(save: false);
            return;
        }

        var middles = new List<double>(rows.Count);
        double height = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            if (_placeList.ContainerFromIndex(rows[i]) is not Control box
                || box.TranslatePoint(default, this) is not { } at) return;

            middles.Add(at.Y + box.Bounds.Height / 2);

            if (i == from) height = box.Bounds.Height;
        }

        group.MovePin(place, DragReorder.SlotFor(here.Y - _placeGrab + height / 2, middles, from));
    }

    /// <summary>
    /// Ends the reorder, writing the new order down only if one really
    /// happened — a plain click on a pinned place arms this and moves nothing,
    /// and must not send the provider a write on every click.
    /// </summary>
    /// <returns>Whether a reorder really happened, which the release handler
    /// uses to keep the drop from also being a click.</returns>
    private bool EndPlaceDrag(bool save)
    {
        var moved = save && _placeDragging;

        _placeDrag = null;
        _placeList = null;
        _placeDragging = false;

        if (moved) _ = _shell.Sidebar.SavePinOrderAsync();

        return moved;
    }
}
