using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Vaktari.Ui;

/// <summary>
/// What the wheel means, before anything else gets to say.
///
/// Three questions in order: is Ctrl held, in which case this is a zoom; is the
/// thing under the pointer a strip that scrolls sideways and not down, in which
/// case the vertical wheel drives it; otherwise leave it alone.
///
/// **It is registered on the TUNNEL, and that is what makes it work.** The
/// registration stays in the constructor with its own note — claiming the
/// gesture before the listing's ScrollViewer sees it is the only thing
/// stopping a Ctrl+wheel from zooming and scrolling at the same time. A
/// bubbling handler here would be a different feature wearing the same name.
///
/// **ScrolledSideways is here rather than with the tab strip**, which is where
/// its own comment reads like it belongs, because it is a routing rule for a
/// wheel and not part of that strip's machinery: it hardcodes its own step and
/// touches none of the TabStripScroll arithmetic every real member of that
/// concern shares. The tab strip is the bug it was written for, not its owner.
///
/// <c>_zoomTravel</c> is written from one place outside this file — the
/// Ctrl+middle-click reset in the pointer handler, which the two comments at
/// either end name as the other half of the same gesture: wheel to scale,
/// click to undo. Left as a direct write, the way _dragOrigin and _bandList
/// are shared by the gestures that read them, rather than wrapped for a single
/// caller.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Accumulated wheel travel. A mouse notch is a whole 1.0, but a trackpad
    /// sends a stream of fractions — stepping on each one would race from
    /// smallest to largest in a single swipe.
    /// </summary>
    private double _zoomTravel;

    /// <summary>
    /// A strip that scrolls sideways and not down takes the wheel.
    ///
    /// **The toolkit does not do this, and the tab strip proved it.** A vertical
    /// wheel over the tabs did nothing whatever: the ScrollViewer there has its
    /// vertical axis disabled, so there was no vertical scrolling to perform and
    /// the horizontal axis was never offered the gesture. What made that
    /// survivable was the theme's stepper arrows — which are exactly the bulky
    /// machinery the strip has just stopped drawing. Removing them without this
    /// would have left dragging a three-pixel line as the only way to reach a
    /// tab that had scrolled out.
    ///
    /// Wheeling away from you goes right, which is the direction the same
    /// gesture moves a horizontal strip everywhere else.
    /// </summary>
    private static bool ScrolledSideways(PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return false;

        foreach (var visual in (e.Source as Visual)?.GetVisualAncestors()
                 ?? Array.Empty<Visual>())
        {
            if (visual is not ScrollViewer viewer) continue;
            if (viewer.VerticalScrollBarVisibility
                != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled) continue;

            // Nothing hidden means nothing to reach, and claiming the gesture
            // then would stop it doing whatever it would otherwise have done.
            if (viewer.Extent.Width <= viewer.Viewport.Width) return false;

            // A notch is 1.0 and moves about the width of a short tab. A
            // trackpad's fractions scale down from the same figure rather than
            // needing an accumulator, because a partial scroll is meaningful
            // where a partial zoom step is not.
            var moved = Math.Clamp(
                viewer.Offset.X - (e.Delta.Y * 64), 0, viewer.Extent.Width - viewer.Viewport.Width);

            viewer.Offset = viewer.Offset.WithX(moved);

            return true;
        }

        return false;
    }

    private void OnWheelAnywhere(object? sender, PointerWheelEventArgs e)
    {
        // Before the zoom test, because a strip that scrolls sideways wants the
        // plain wheel and zoom wants it only with Control held.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && ScrolledSideways(e))
        {
            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;

        // Claimed even when the accumulator has not tripped yet: releasing it
        // would scroll the list mid-zoom.
        e.Handled = true;

        // Direction reversal starts over, so a small overshoot does not need to
        // be unwound before the other direction responds.
        if (Math.Sign(e.Delta.Y) != Math.Sign(_zoomTravel)) _zoomTravel = 0;

        _zoomTravel += e.Delta.Y;

        while (Math.Abs(_zoomTravel) >= 1.0)
        {
            var up = _zoomTravel > 0;
            _zoomTravel -= up ? 1.0 : -1.0;

            // The pane under the pointer, not the active one: reaching over to
            // scale the other side without clicking into it first is the whole
            // reason the wheel gesture is nicer than the buttons.
            var pane = PaneAt(e.Source) ?? _shell.ActiveTab;

            // Shift narrows it to the icons, which is the axis people mean when
            // they say "zoom" — the labels usually want to stay put.
            //
            // ZoomPane rather than ScalePane: at the end of a layout's icon
            // range the next notch steps to the neighbouring LAYOUT, which is
            // what makes this one gesture reach from a dense list of names to
            // 256px tiles. ScalePane alone stopped at whatever the layout on
            // screen could stretch to.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _shell.ZoomPane(pane, 0, up ? 0.15 : -0.15);
            else
                _shell.ZoomPane(pane, up ? 0.1 : -0.1, up ? 0.15 : -0.15);
        }
    }
}
