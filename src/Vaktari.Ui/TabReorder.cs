using System;
using System.Collections.Generic;

namespace Vaktari.Ui;

/// <summary>
/// Where a dragged tab belongs.
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
/// </summary>
internal static class TabReorder
{
    /// <summary>
    /// The slot a tab dragged to <paramref name="centre"/> should occupy.
    ///
    /// <paramref name="middles"/> holds the horizontal centre of each tab in the
    /// strip's frame; <paramref name="centre"/> is where the dragged tab's own
    /// centre sits, glued to the pointer.
    ///
    /// Only one of the two loops can run: a centre past the tab on one side is
    /// not also short of the tab on the other. On a fast drag that jumps several
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
