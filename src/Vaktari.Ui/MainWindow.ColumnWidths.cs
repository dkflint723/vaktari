using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Dragging a details column wider or narrower.
///
/// **Each grip sits on its column's RIGHT edge, and a drag moves that edge and
/// nothing else** — the column gets wider or narrower and every column after it
/// moves along with the edge, the way Explorer's do. Two things stood in the
/// way, both measured with real pointer input on 0.11.1:
///
/// - **The name took up every pixel.** It filled whatever the other columns
///   left, and they were laid from the right, so widening Size by 40 narrowed
///   the name by 40 and Size grew LEFTWARDS: its right edge, the one being
///   held, never moved, and what visibly changed was the name. The name had no
///   grip of its own either. So the first drag in a tab gives the name the
///   width it is drawn at (<see cref="ColumnWidths.Name"/>); from then on it
///   is a column like the others, and the space after the last one is left
///   empty.
/// - **The Thumb's delta is measured from where the press landed, in the
///   grip's own coordinates**, so it is only a step while the grip follows the
///   pointer. On an edge that stood still it was the whole distance so far,
///   added again on every move: a 40-pixel drag in four moves added
///   10 + 20 + 30 + 40 = 100. The width is worked out here from where the
///   pointer is NOW against where it was pressed, both in the window's
///   coordinates, so it is right however many moves arrive before a layout
///   pass catches the grip up.
///
/// **The widths are the tab's own** (<see cref="PaneViewModel.ColumnWidths"/>)
/// and they go into the session with it, so a drag in one half of a split
/// leaves the other half where it was, and there is nothing to write when the
/// drag ends: every step marks the session, and its store writes a second
/// after the last one.
///
/// The pane's own zoom goes with it: the grip reports pixels on screen and the
/// width is kept at 100%, and it is THIS pane's scale the column under the
/// pointer was drawn at. From the DataContext rather than a name, because in a
/// split there are two of these and a name would find one.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The drag in progress: which tab and column, the heading it is in, where
    /// the pointer was pressed in the window's coordinates and where it was
    /// pressed in the grip's, the scale the columns were drawn at, the width
    /// the column had then at 100% and the most and least it may be given,
    /// and what the name and its span are to be from the first move on.
    /// </summary>
    private sealed record ColumnDrag(
        PaneViewModel Pane, DetailsColumn Column, Grid Heading, Visual Frame,
        double PressedAt, Point PressedOnGrip, double Scale, double StartWidth,
        double Least, double Most, double Name, double Span);

    private ColumnDrag? _columnDrag;

    /// <summary>Whether the pointer has left the press point since the
    /// press: until it has, nothing is changed.</summary>
    private bool _columnDragMoved;

    private void OnColumnGripDragStarted(object? sender, VectorEventArgs e)
    {
        _columnDrag = null;
        _columnDragMoved = false;

        if (sender is not Thumb { DataContext: PaneViewModel pane, Tag: string tag } grip
            || grip.Parent is not Grid { ColumnDefinitions.Count: > 7 } heading
            || TopLevel.GetTopLevel(grip) is not { } frame
            || grip.TranslatePoint(new Point(e.Vector.X, e.Vector.Y), frame) is not { } pressed)
            return;

        var column = Enum.Parse<DetailsColumn>(tag);

        // The product PaneScale multiplies a width by on the way out.
        var scale = pane.TextScale > 0 ? pane.TextScale : 1.0;

        // Read off the heading's own grid, which every grip is in, as it is
        // drawn now: the name, and the row — **as much of it as is on screen**,
        // the heading scroller's viewport less the grid's margins. Once the
        // columns scroll the grid is as wide as they are, and a span taken
        // from it would be wider than the row every time: the name would give
        // way the moment the drag ended, which is "resizing Size resizes Name"
        // again.
        var drawn = heading.ColumnDefinitions[1].ActualWidth / scale;
        var row = (heading.FindAncestorOfType<ScrollViewer>() is { Viewport.Width: > 0 } scroller
            ? scroller.Viewport.Width - heading.Margin.Left - heading.Margin.Right
            : heading.Bounds.Width) / scale;

        // **The name keeps exactly the width it is drawn at when the drag
        // begins**, so nothing moves. A name that was filling, or whose own
        // edge is taken, is given that width as its own, chosen in this row;
        // a name giving way to a narrower pane while some OTHER column is
        // dragged keeps the width somebody chose, and the give is held where
        // it is (IsResizingColumns) until the drag ends.
        var rebase = pane.NameFills || column == DetailsColumn.Name;
        var name = rebase ? drawn : pane.ColumnWidths.Name;
        var span = rebase ? row : pane.ColumnWidths.Span;

        var start = column == DetailsColumn.Name ? name : PaneScale.ColumnWidth(pane.ColumnWidths, column);

        // **Every edge follows the pointer past the pane's, the name's too.**
        // In 0.11.2 no edge could go past it, because a column pushed off the
        // side took its grip with it and nothing on screen could drag it back.
        // The columns scroll now, so the grip is a scroll away, and a name too
        // long for the pane is the commonest reason to widen a column at all.
        // The top of each column's own range is the only limit left.
        var (least, most) = column == DetailsColumn.Name
            ? (Math.Min(name, PaneScale.NameMin), PaneScale.NameMax)
            : (PaneScale.ColumnMin, PaneScale.ColumnMax);

        pane.IsResizingColumns = true;

        _columnDrag = new ColumnDrag(
            pane, column, heading, frame, pressed.X, new Point(e.Vector.X, e.Vector.Y), scale, start,
            least, Math.Max(least, most), name, span);
    }

    /// <summary>
    /// Sets the column to its width at the press plus how far the pointer has
    /// gone since, both in pixels at 100%. The Thumb's vector is where the
    /// pointer is relative to the press point, in the grip's coordinates as
    /// they are NOW — so the grip's own position, read at the same moment,
    /// turns it into a point in the window that does not depend on whether the
    /// grip has been laid out again since the last move.
    /// </summary>
    private void OnColumnGripDragDelta(object? sender, VectorEventArgs e)
    {
        if (_columnDrag is not { } drag
            || sender is not Thumb { DataContext: PaneViewModel pane } grip
            || !ReferenceEquals(pane, drag.Pane)
            || grip.TranslatePoint(drag.PressedOnGrip + e.Vector, drag.Frame) is not { } now)
            return;

        var moved = now.X - drag.PressedAt;

        // **Nothing until the pointer first leaves the press point**, so a
        // click on an edge that goes nowhere changes nothing — the name's
        // filling included. After that every move counts, a move back to
        // exactly the press point too: skipping that one left the column at
        // the width of the move before.
        if (!_columnDragMoved)
        {
            if (moved == 0) return;

            _columnDragMoved = true;
            pane.ColumnWidths = pane.ColumnWidths with { Name = drag.Name, Span = drag.Span };
        }

        pane.SetColumnWidth(drag.Column,
            Math.Clamp(drag.StartWidth + moved / drag.Scale, drag.Least, drag.Most));
    }

    /// <summary>
    /// The drag is over: the name may give way again, and a layout is asked
    /// for so that it is worked out at once — a column narrowed while the
    /// name was giving way gives its room back to the name here.
    /// </summary>
    private void OnColumnGripDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_columnDrag is { } drag)
        {
            drag.Pane.IsResizingColumns = false;
            drag.Heading.InvalidateMeasure();
        }

        _columnDrag = null;
    }
}
