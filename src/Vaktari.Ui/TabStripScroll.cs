using System;

namespace Vaktari.Ui;

/// <summary>
/// The arithmetic behind the tab strip's overflow chevrons.
///
/// **The wheel was the only way to reach an overflowed tab, and nothing said
/// so.** The strip's scrollbar was pared back to a thin position line — the
/// theme's full bar painted arrows across the tab labels — but that trade
/// removed every affordance that LOOKED clickable: a wheel is invisible until
/// discovered, and a three-pixel line is a target nobody should have to hit.
/// The chevrons are the browsers' answer, and the reason they work is that they
/// only exist while there is somewhere to go: hidden when the tabs fit,
/// disabled at their own end of travel, so their mere presence says "there are
/// more tabs than you can see".
///
/// Pure arithmetic, split from the window so a test can hold the clamping —
/// the failure a chevron invites is scrolling past either end, and neither end
/// is reachable in a headless test any other way.
/// </summary>
internal static class TabStripScroll
{
    /// <summary>
    /// Where one press lands, clamped to the strip.
    ///
    /// Just over half a viewport per press: one click visibly moves, and the
    /// repeat while held sweeps without teleporting past the tab being looked
    /// for. The floor keeps a very narrow pane from degenerating into
    /// ten-pixel nudges.
    /// </summary>
    internal static double Toward(double offset, double viewport, double extent, int direction)
    {
        var step = Math.Max(60, viewport * 0.5);

        return Math.Clamp(offset + (direction * step), 0, Math.Max(0, extent - viewport));
    }

    /// <summary>
    /// Whether the strip holds more than it shows — the gate on the chevrons
    /// existing at all. Half a pixel of slack, because extent and viewport are
    /// layout doubles and equality between them is luck.
    /// </summary>
    internal static bool Overflows(double extent, double viewport)
        => extent - viewport > 0.5;

    internal static bool CanGoLeft(double offset) => offset > 0.5;

    internal static bool CanGoRight(double offset, double viewport, double extent)
        => offset < extent - viewport - 0.5;
}
