using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Which press begins a reorder of the sidebar's places, and which does not.
///
/// Split from the window for the reason <see cref="DragReorder"/> is: the rule
/// this encodes — that only the rows a person pinned may be dragged — is one
/// whose failure is silent. A drag that wrongly armed on Home would move a row
/// that is rebuilt from the desktop on the next refresh, so the reorder would
/// appear to work and then undo itself, which is worse than not offering it.
/// </summary>
internal static class PlaceDrag
{
    /// <summary>
    /// The place a press at <paramref name="source"/> should start dragging,
    /// or null for a press that must not.
    ///
    /// **Unlike the tab strip's equivalent this does not stop at a Button**,
    /// because a place row IS one — that is how clicking a place navigates. The
    /// pinned test below is what keeps the gesture off the rows it must not
    /// move, so it does the whole job the Button check does there.
    /// </summary>
    internal static PlaceItemViewModel? ArmedBy(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control { DataContext: PlaceItemViewModel place }) continue;

            // Home, Documents, the drives, the shares and the bin are the
            // desktop's own rows. They are assembled fresh from it on every
            // rebuild and the provider's reorder reads none of them, so an
            // order imposed on them would survive until the next refresh and
            // no longer.
            return place.IsUserPinned ? place : null;
        }

        return null;
    }

    /// <summary>
    /// The list a row belongs to, whose containers give the geometry a reorder
    /// is measured against.
    /// </summary>
    internal static ItemsControl? ListFor(Visual? row)
        => row?.FindAncestorOfType<ItemsControl>();
}
