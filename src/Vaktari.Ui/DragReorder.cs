using System;
using System.Collections.Generic;

namespace Vaktari.Ui;

/// <summary>
/// Where a dragged thing belongs, along one axis.
///
/// **Tabs stayed in the order they were opened in, and there was no way to
/// change it.** Pressing a tab and dragging did nothing at all — no move, no
/// menu item, no key gesture — while Explorer, Dolphin and every browser
/// reorder by dragging. The order was already persisted and already restored;
/// nothing could ever alter it.
///
/// The rule is the NEIGHBOUR'S MIDDLE, not its near edge, and that is the whole
/// design. Comparing the pointer against the box it is currently inside
/// oscillates the moment a wide tab is dragged past a narrow one: the swap puts
/// the wide tab back under the pointer, the test reverses, and the strip
/// flickers every frame. Centre against centre cannot feed back, because the
/// dragged tab's centre is a function of the pointer alone.
///
/// Pure arithmetic, split from the window the way <see cref="TabStripScroll"/>
/// is, because the failure a reorder invites — the flip-flop above — takes two
/// calls to show and is not reachable from a headless test any other way.
///
/// One axis, so it serves the tab strip on X and the sidebar's pinned places on
/// Y. Nothing here knows which: a centre and a list of centres is the whole
/// input, and the oscillation it exists to prevent is the same one either way.
/// </summary>
internal static class DragReorder
{
    /// <summary>
    /// The slot a thing dragged to <paramref name="centre"/> should occupy.
    ///
    /// <paramref name="middles"/> holds the centre of each candidate along the
    /// axis being dragged; <paramref name="centre"/> is where the dragged
    /// item's own centre sits, glued to the pointer.
    ///
    /// Only one of the two loops can run: a centre past the neighbour on one
    /// side is not also short of the neighbour on the other. On a fast drag that jumps several
    /// slots at once they compare against the layout as it was before the move,
    /// which makes the answer slightly conservative — a lag of a frame, never an
    /// oscillation.
    /// </summary>
    internal static int SlotFor(double centre, IReadOnlyList<double> middles, int from)
    {
        if (from < 0 || from >= middles.Count) return from;

        var to = from;

        while (to + 1 < middles.Count && centre > middles[to + 1]) to++;
        while (to > 0 && centre < middles[to - 1]) to--;

        return to;
    }
}
