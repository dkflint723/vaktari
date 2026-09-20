using Avalonia;
using Avalonia.Controls;

namespace Vaktari.Ui;

/// <summary>
/// The width this window lends a details panel, and how it gets it back.
///
/// A loan, and the whole of the bookkeeping for one. Three private fields hold
/// what was borrowed — the width before, the position before, and what the
/// window was left at — and four members are the only things in the
/// application that touch them. GrowToFit opens the loan, ReleaseGrownWidth
/// closes it, ResizedSinceGrow is the one question both closers ask, and
/// WithoutTheLoan answers what this window would be if the panel gave the
/// width back right now. A grep of the three field names across src and tests
/// finds nothing outside this file.
///
/// **Two ways in, both of which stay in the constructor.** The shell raises
/// GrowRequested and ReleaseRequested, and MainWindow.axaml.cs:328 and :329
/// turn those into the only two calls either method has. The shortfall itself
/// is worked out elsewhere: PaneGroupViewModel.AskForRoomIfNeeded
/// (ViewModels/PaneGroupViewModel.cs:336-354) decides a panel does not fit and
/// asks for the difference, and ShellViewModel divides it by the side's share
/// before forwarding (ViewModels/ShellViewModel.cs:708-709) because only it
/// knows the window is split.
///
/// **None of this runs on most machines.** AskForRoomIfNeeded returns at
/// ViewModels/PaneGroupViewModel.cs:340-341 unless NarrowDetailsPanel is set
/// to GrowWindow. Under every other setting a panel that does not fit is
/// simply not shown, and this file is dead code.
///
/// **The session reads it, and that is the edge worth being exact about.**
/// WithoutTheLoan has no caller in this file at all. Its only caller anywhere
/// is CaptureGeometry, at MainWindow.Geometry.cs:143, which asks what to write
/// down instead of restating the rule — before it did restate it, and a window
/// closed while a panel was borrowing saved the borrowed width as its own and
/// came back wider every launch.
///
/// That one line is not a rare visitor. CaptureGeometry is the shell's
/// GeometryProvider, assigned at MainWindow.axaml.cs:278 and invoked by
/// ToWindowSession (ViewModels/ShellViewModel.Session.cs:131-133), which every
/// session write goes through — and MainWindow.axaml.cs:568 and :569 subscribe
/// Resized and PositionChanged to the shell's NotifyWindowChanged, which marks
/// the session dirty. The width and position this file writes ARE resizes and
/// moves. So the traffic is a circuit rather than a one-way seam, and a header
/// claiming this file is entered from two places and no more would be wrong.
///
/// **Nothing here calls any other member of MainWindow.** What leaves this
/// file is Avalonia's Width, Position and WindowState, and PanelDebug, which
/// has callers of its own in the view models
/// (ViewModels/PaneGroupViewModel.cs:244, ViewModels/ShellViewModel.cs:718).
///
/// **The measurement is deliberately not here.** OnGroupSizeChanged stays in
/// MainWindow.axaml.cs, wired from markup, and it is the application's only
/// writer of PaneGroupViewModel.GroupWidth — which makes it tempting, because
/// GroupWidth is what AskForRoomIfNeeded compares against. But GroupWidth also
/// drives CanShowInfo (ViewModels/PaneGroupViewModel.cs:161, :165), the
/// ceiling ResizeInfoBy clamps to (:122-123) and the InfoWidth re-clamp at
/// :315-317, and all three keep working when GrowWindow is switched off and
/// nothing here runs at all. A member that is load-bearing when this file is
/// dead does not belong to this file. The loop closes through the view model,
/// not through any call written here.
///
/// **Not MainWindow.Geometry.cs either, and the pull there is real.** Those
/// four members and these are the only writers of this window's own Width and
/// Position in any of the partials. Against it: they share no call in either
/// direction and no field, so what they have in common is a vocabulary rather
/// than a mechanism; merging would not remove a cross-file edge but trade one
/// for two, since the constructor wiring would then cross instead; and that
/// file's own summary counts its members and names its subjects, so the move
/// would falsify its header while claiming to be a pure move.
///
/// **What nothing checks.** WithoutTheLoan's only caller is in another file,
/// and if that call were deleted no tool here would say so: the C# compiler
/// has no unused-private-property diagnostic, and this build runs no
/// code-style analyzer — Directory.Build.props turns warnings into errors but
/// sets no analysis level, and .editorconfig sets no diagnostic severities.
/// The naming of MainWindow.Geometry.cs:143 above is therefore load-bearing
/// rather than decoration; it is the only thing holding that edge.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Widens the window so a details panel has room.
    ///
    /// **The window manager has the final say and that is deliberate.** Asking
    /// Avalonia which screen this is on and clamping to its working area would
    /// mean an API this project has not verified, and getting it wrong is worse
    /// than letting the WM do what it already does correctly. If the screen
    /// cannot accommodate the request the window simply stops growing, and the
    /// panel stays hidden — `IsInfoVisible` is still set, so it appears the
    /// moment there is room.
    /// </summary>
    private void GrowToFit(double by)
    {
        if (by <= 0) return;

        // Maximised or full-screen windows cannot usefully be widened, and
        // trying would either do nothing or un-maximise them, which is a
        // surprising thing for a panel toggle to do.
        if (WindowState != WindowState.Normal) return;

        // Only the FIRST grow records the original. A second panel opening in a
        // split must not overwrite it, or closing both would restore to the
        // already-grown width instead of the one the user chose.
        _widthBeforeGrow ??= Width;

        // **The POSITION has to be remembered as well as the width.** Growing
        // pushes the right edge outward; when that runs past the screen the window
        // manager shoves the whole window LEFT to keep it visible. Shrinking the
        // width afterwards pulls the right edge back in but leaves the window
        // where the WM put it — so it lands well left of where it started, which
        // is exactly what "shooting to the left" was.
        _positionBeforeGrow ??= Position;

        Width += Math.Ceiling(by);

        // What we left it at, so a later release can tell whether the user has
        // resized in the meantime.
        //
        // **Written AFTER the width, which reads like a gap and is not one.**
        // Until this line runs, _grownTo still holds its previous value, so
        // ResizedSinceGrow answers true and WithoutTheLoan would hand the
        // session the GROWN width — the very thing the loan exists to keep out
        // of it. That matters only if something can read it in between, and
        // nothing can: assigning Width is a styled-property set whose whole
        // effect is to invalidate measure, and Avalonia queues the layout pass
        // rather than running it, so Resized cannot fire before this statement.
        // Checked by reading Avalonia 12.1.2's own IL rather than by argument.
        // It is the queueing this depends on; a toolkit that ever resized
        // synchronously would put the gap back.
        _grownTo = Width;

        ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: grew by {Math.Ceiling(by)} to {Width:F0} "
            + $"(original {_widthBeforeGrow:F0} at {_positionBeforeGrow?.X},"
            + $"{_positionBeforeGrow?.Y}; now at {Position.X},{Position.Y})");
    }

    /// <summary>The window's width before any panel grew it, if one did.</summary>
    private double? _widthBeforeGrow;

    /// <summary>The width this class last set, to detect a manual resize since.</summary>
    private double _grownTo;

    /// <summary>Where the window sat before any panel grew it.</summary>
    private PixelPoint? _positionBeforeGrow;

    /// <summary>
    /// Whether the edge has been dragged since this class last set the width.
    ///
    /// Asked by both readers of the loan, so "the user has taken this width
    /// over" is decided once. A window that has never grown answers true here —
    /// `_grownTo` is still zero — which is why both callers establish that there
    /// IS a loan before they ask.
    /// </summary>
    private bool ResizedSinceGrow => Math.Abs(Width - _grownTo) > 1;

    /// <summary>
    /// The size and place this window would be in if the panel handed its loan
    /// back right now.
    ///
    /// **A loan does not survive a close, so it must not be written down as
    /// though it were the window's own size.** CaptureGeometry stored the LIVE
    /// width, so a window closed with a details panel still borrowing saved the
    /// grown one — and the next launch had `_widthBeforeGrow` null, nothing to
    /// repay, and no way to learn there had ever been a debt. The window came
    /// back wider every time and stayed that way.
    ///
    /// **The pair travels together or not at all.** Growing can make the window
    /// manager shove the whole window left to keep it on screen, so the
    /// pre-loan width belongs with the pre-loan position — saving one with the
    /// other lands the window somewhere it has never been, and a session that
    /// took the width back while keeping the shoved position would walk the
    /// window leftwards across launches instead of widening it.
    ///
    /// Live values once the edge has been dragged, for the reason
    /// <see cref="ReleaseGrownWidth"/> gives for refusing there: a width the
    /// user chose since is theirs, and writing the remembered one would undo
    /// their drag at the next launch rather than at the next panel close.
    /// </summary>
    private (double Width, PixelPoint Position) WithoutTheLoan
        => _widthBeforeGrow is { } width
           && _positionBeforeGrow is { } place
           && !ResizedSinceGrow
            ? (width, place)
            : (Width, Position);

    /// <summary>
    /// Hands back the width taken for a details panel.
    ///
    /// **Refuses if the window is no longer the size we made it.** Someone who
    /// has dragged the edge since has expressed a preference, and snapping back to
    /// a width they last saw several actions ago would feel like the application
    /// fighting them. The recorded width is dropped in that case rather than kept,
    /// because it no longer describes anything the user would recognise.
    /// </summary>
    private void ReleaseGrownWidth()
    {
        // Every branch says WHY, because this feature has now taken three rounds
        // and "nothing happened" has four different causes that look identical.
        if (_widthBeforeGrow is not { } original)
        {
            ViewModels.PaneGroupViewModel.PanelDebug("[vaktari] panel: nothing to give back — the window was never grown");
            return;
        }

        var origin = _positionBeforeGrow;

        _widthBeforeGrow = null;
        _positionBeforeGrow = null;

        if (WindowState != WindowState.Normal)
        {
            ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: not restoring — window is {WindowState}");
            return;
        }

        // A pixel of tolerance: the grow rounded up, and layout can settle a
        // fraction either way. The same question the session asks through
        // WithoutTheLoan, so the two cannot come to different answers about
        // whose width this is.
        if (ResizedSinceGrow)
        {
            ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: not restoring — width is {Width:F0} but we left "
                + $"it at {_grownTo:F0}, so it was resized by hand");
            return;
        }

        ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: restoring {Width:F0} -> {original:F0}"
            + (origin is { } p && p != Position ? $", moving back to {p.X},{p.Y}" : ""));

        // WIDTH FIRST, then position. Narrowing makes the window fit again, so the
        // move that follows cannot trip the same off-screen correction that caused
        // the problem — doing it the other way round can bounce it straight back.
        Width = original;

        if (origin is { } home && home != Position) Position = home;
    }
}
