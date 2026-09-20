using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Vaktari.Ui;

/// <summary>
/// The marquee: the rectangle dragged across a listing, and what it selects.
///
/// **Not the edge scrolling, which only looks like it belongs.** EdgeZone,
/// AutoScroll and StopBandScroll stay where they are because the file drag
/// drives that timer too — DragScroll says so in its own comment — and a
/// shared engine filed under one of its two drivers is how the two drift
/// apart.
///
/// The traffic runs BOTH ways, and it is worth being exact about which. This
/// file drives the timer: UpdateBand starts it and EndBand stops it, exactly
/// as DragScroll and StopDragScroll do for a drag. What comes back is one
/// call, <see cref="ReapplyBandAfterScroll"/>, and the guard is on that return
/// leg alone — the timer asks the band to re-apply itself, and the band
/// refuses unless one is actually in progress. Without that refusal a file
/// drag emptied the selection it was dragged over.
///
/// **Not _bandList either.** Three places write it — the press, ArmNothing and
/// the drag — and three read it as a gesture guard: the press itself, the
/// pointer-move handler, and the timer's own tick. Writers and readers are not
/// the same set, which is the whole reason it is shared state rather than this
/// file's. It stays with the press that arms it, as _dragOrigin does.
///
/// What stays behind with them is the band's arming and its dispatch: the
/// press handler fills these fields, and the pointer-move handler is what
/// calls UpdateBand. That is a real open edge, and it is the same one every
/// gesture in this window has — the window owns the events, and the concern
/// owns what to do with them.
/// </summary>
public partial class MainWindow
{
    /// <summary>Where the drag began, in the overlay's coordinates.</summary>
    private Point _bandOrigin;

    /// <summary>The band as last drawn, so the scroll timer can re-test against
    /// it without a pointer event.</summary>
    private Rect _bandRect;

    /// <summary>Ctrl or Shift was held, so the band ADDS to the selection.</summary>
    private bool _bandAdditive;

    /// <summary>
    /// The selection as it stood when an additive band began.
    ///
    /// Snapshotted rather than read live, because the band rewrites the
    /// selection on every move — without a fixed baseline, shrinking the
    /// rectangle could not give back what it had already taken.
    /// Null until the band actually starts, so a click that never moves leaves
    /// the selection alone.
    /// </summary>
    private List<object>? _bandKept;

    /// <summary>
    /// The scroll offset when the band started.
    ///
    /// **The origin was anchored to the window**, so auto-scrolling slid the
    /// content out from under a rectangle that stayed put: the band never
    /// covered more than one screenful, and everything swept past scrolled out
    /// of it. Holding the offset lets the rectangle be re-expressed in content
    /// terms every pass, so it keeps covering everything the drag has crossed.
    /// </summary>
    private Vector _bandScrollAt;

    /// <summary>
    /// What the band has selected so far.
    ///
    /// A row that has scrolled out of the viewport has no container and no
    /// bounds, so it cannot be re-tested — and rebuilding the selection from
    /// what is visible therefore DESELECTED it. Marquee-selecting two hundred
    /// files kept only the last screenful. Rows that have left stay selected;
    /// rows still on screen are re-tested as before, so dragging back up still
    /// takes them off.
    /// </summary>
    private readonly List<object> _bandTaken = [];

    private void UpdateBand(PointerEventArgs e)
    {
        if (_bandList is not ListBox list) return;

        // The button can be released outside the window, where no release event
        // arrives — so the live button state is what ends the band, not just the
        // event that ought to have come.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndBand();
            return;
        }

        var here = e.GetPosition(BandLayer);

        // **The origin travels with the content.** It was fixed in window
        // coordinates, so once the list auto-scrolled the rectangle stopped
        // covering what the drag had crossed — the band could never select more
        // than one screenful. Subtracting how far the view has moved since the
        // press puts the anchor back over the row it started on.
        var scrolled = Scroller(list) is { } view ? view.Offset - _bandScrollAt : default;
        var anchor = new Point(_bandOrigin.X - scrolled.X, _bandOrigin.Y - scrolled.Y);

        var rect = new Rect(
            Math.Min(anchor.X, here.X), Math.Min(anchor.Y, here.Y),
            Math.Abs(here.X - anchor.X), Math.Abs(here.Y - anchor.Y));

        // The same six-pixel threshold the file drag uses. Below it this is a
        // click that happened to wobble, and rewriting the selection would make
        // clicking empty space feel unreliable.
        if (rect.Width < 6 && rect.Height < 6) return;

        _bandKept ??= _bandAdditive
            ? list.SelectedItems?.Cast<object>().ToList() ?? []
            : [];

        AutoScroll(list, here);

        Canvas.SetLeft(SelectionBand, rect.X);
        Canvas.SetTop(SelectionBand, rect.Y);
        SelectionBand.Width = rect.Width;
        SelectionBand.Height = rect.Height;
        SelectionBand.IsVisible = true;

        _bandRect = rect;

        ApplyBand(list, rect);
    }

    /// <summary>
    /// Selects every realized row the rectangle touches.
    ///
    /// **Realized rows only, and that is not a limitation to apologise for:** the
    /// listings virtualize, so a row outside the viewport has no container and no
    /// bounds. A band can only be drawn across what is on screen anyway.
    /// </summary>
    private void ApplyBand(ListBox list, Rect rect)
    {
        if (list.SelectedItems is not { } selected) return;

        var wanted = new List<object>(_bandKept ?? []);

        // **What the band already took, and can no longer see.** A row outside
        // the viewport has no container, so it cannot be re-tested — and
        // rebuilding from what is visible therefore dropped it. Sweeping two
        // hundred files kept only the last screenful.
        var realized = new List<object>();

        foreach (var container in Rows(list))
        {
            if (container.DataContext is not { } item) continue;

            realized.Add(item);

            // TranslatePoint rather than a stored offset: rows move as the list
            // scrolls, and a cached position would select the wrong ones the
            // moment it did.
            if (container.TranslatePoint(default, BandLayer) is not { } origin) continue;

            var bounds = new Rect(origin, container.Bounds.Size);

            if (bounds.Intersects(rect) && !wanted.Contains(item)) wanted.Add(item);
        }

        // Off-screen rows the band has already claimed stay claimed. On-screen
        // ones were just re-tested, so dragging back up still takes them off.
        foreach (var taken in _bandTaken)
            if (!realized.Contains(taken) && !wanted.Contains(taken))
                wanted.Add(taken);

        _bandTaken.Clear();

        foreach (var item in wanted)
            if (_bandKept is null || !_bandKept.Contains(item))
                _bandTaken.Add(item);

        // Diffed rather than cleared and refilled. Every change to this
        // collection refreshes the details panel and the status line, and a
        // clear-then-add would do that twice per pointer move.
        for (var i = selected.Count - 1; i >= 0; i--)
            if (selected[i] is { } existing && !wanted.Contains(existing))
                selected.RemoveAt(i);

        foreach (var item in wanted)
            if (!selected.Contains(item))
                selected.Add(item);
    }

    /// <summary>
    /// Re-applies the band after the edge scroll has moved the rows under it.
    ///
    /// **Only when a band is what started the scrolling.** The shared
    /// edge-scroll timer has two drivers and only one of them owns a band: UpdateBand draws one, and
    /// DragScroll borrows the same timer for a FILE drag, which is the whole
    /// point of the note in MainWindow.DragDrop.cs. On that second path there
    /// is no band at all — <c>_bandKept</c> is null and <c>_bandRect</c> still
    /// holds whatever rectangle the last band left, or nothing whatever on a
    /// window where none has been drawn.
    ///
    /// Re-applying it there rebuilt the hovered listing's selection from a
    /// rectangle that had nothing to do with the drag. An empty rect intersects
    /// no row, so ApplyBand wanted nothing and removed everything: drag files
    /// over a listing, rest near its top or bottom edge until it scrolls, and
    /// that listing's selection emptied under the pointer. Dragging within the
    /// window it is the source's own rows that vanish; OnDragOver reaches
    /// DragScroll with no internal-drag gate, so a drag from Explorer does it
    /// too.
    ///
    /// <c>_bandKept</c> is the predicate because it is exactly "a band is in
    /// progress": null from the press, assigned by UpdateBand before it asks
    /// for any scrolling, and null again from EndBand.
    ///
    /// **The one state it does not cover**, named rather than guarded: a band
    /// left live with the button already released would still re-apply. That
    /// needs the release to have escaped both the tunnelled handler and
    /// UpdateBand's own button test, and such a window is broken in louder
    /// ways first — the marquee is still drawn on the glass and _bandList is
    /// still armed. Closing it would mean a second sentinel to keep in step
    /// with this one, which is how the two halves of a rule drift apart.
    /// </summary>
    internal void ReapplyBandAfterScroll(ListBox live)
    {
        if (_bandKept is null) return;

        ApplyBand(live, _bandRect);
    }

    private void EndBand()
    {
        StopBandScroll();

        _bandList = null;
        _bandKept = null;

        // Released here as well as armed on the press: the list it names may be
        // torn down before the next band starts, and holding rows from a
        // listing that has gone would keep them alive for nothing.
        _bandTaken.Clear();

        SelectionBand.IsVisible = false;
    }
}
