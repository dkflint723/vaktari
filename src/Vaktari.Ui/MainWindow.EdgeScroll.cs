using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Vaktari.Ui;

/// <summary>
/// Scrolling a listing while the pointer rests near its top or bottom edge.
///
/// **Two callers and neither of them owns it.** The rubber band drives it so a
/// sweep can reach past one screenful, and the file drag drives it so a folder
/// below the fold can be dropped into; UpdateBand and DragScroll start it,
/// EndBand and StopDragScroll stop it. It sat in the window for a while on the
/// argument that a shared engine filed under one of its two callers is how the
/// two drift apart — which was true, and was an argument against putting it
/// under one of them rather than an argument for leaving it where nothing
/// called it at all. It has no caller in MainWindow.axaml.cs.
///
/// Only one thing crosses back, and it is guarded at the far end: the tick asks
/// the band to re-apply itself, and MainWindow.Band.cs refuses unless a band is
/// actually in progress. Unguarded, that call rebuilt the hovered listing's
/// selection from a stale rectangle during a file drag.
///
/// It keeps the <c>_band</c> prefix on its two fields because they are the
/// band's by history and renaming them is a separate change from moving them.
/// The names are the one misleading thing left here.
/// </summary>
public partial class MainWindow
{
    /// <summary>How close to an edge starts scrolling, and how fast.</summary>
    private const double EdgeZone = 28;

    private DispatcherTimer? _bandScroll;
    private double _bandScrollBy;

    /// <summary>
    /// Scrolls the listing while the pointer sits near its top or bottom edge.
    ///
    /// **A timer, not a nudge per pointer-move.** Move events only arrive while
    /// the pointer is moving, so scrolling on them alone means the band stops the
    /// instant you hold still at the edge — which is exactly when you want it to
    /// keep going.
    /// </summary>
    private void AutoScroll(ListBox list, Point pointer)
    {
        if (Scroller(list) is not { } scroller)
        {
            StopBandScroll();
            return;
        }

        // The list's own box, in the overlay's coordinates — the band and the
        // pointer are already measured there.
        if (list.TranslatePoint(default, BandLayer) is not { } origin)
        {
            StopBandScroll();
            return;
        }

        var top = origin.Y;
        var bottom = origin.Y + list.Bounds.Height;

        // Proportional to how far into the zone the pointer is, so easing toward
        // the edge eases the speed rather than switching it on.
        _bandScrollBy =
            pointer.Y < top + EdgeZone ? -(EdgeZone - (pointer.Y - top)) / EdgeZone * 24
            : pointer.Y > bottom - EdgeZone ? (EdgeZone - (bottom - pointer.Y)) / EdgeZone * 24
            : 0;

        if (Math.Abs(_bandScrollBy) < 0.5) { StopBandScroll(); return; }

        if (_bandScroll is not null) return;

        _bandScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _bandScroll.Tick += (_, _) =>
        {
            if (_bandList is not { } live || Scroller(live) is not { } view)
            {
                StopBandScroll();
                return;
            }

            var next = Math.Clamp(view.Offset.Y + _bandScrollBy, 0,
                                  Math.Max(0, view.Extent.Height - view.Viewport.Height));

            if (Math.Abs(next - view.Offset.Y) < 0.01) return;

            view.Offset = view.Offset.WithY(next);

            // The rows under the band have moved, so the selection has to be
            // recomputed against the rectangle as it now stands.
            ReapplyBandAfterScroll(live);
        };

        _bandScroll.Start();
    }

    private void StopBandScroll()
    {
        _bandScroll?.Stop();
        _bandScroll = null;
    }
}
