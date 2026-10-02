using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
    /// The drag in progress: which tab and column, where the pointer was
    /// pressed in the window's coordinates and where it was pressed in the
    /// grip's, the scale the columns were drawn at, the width the column had
    /// then at 100%, and what the name and its span are to be from the first
    /// move on. <see cref="NameFrom"/> and <see cref="NameTo"/> bound a drag
    /// of the name's own edge.
    /// </summary>
    private sealed record ColumnDrag(
        PaneViewModel Pane, DetailsColumn Column, Visual Frame,
        double PressedAt, Point PressedOnGrip, double Scale, double StartWidth,
        double Name, double Span, double NameFrom, double NameTo);

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

        // All read off the heading's own grid, which every grip is in, as it
        // is drawn now: the name, the empty room after the last column, and
        // the row.
        var drawn = heading.ColumnDefinitions[1].ActualWidth / scale;
        var room = heading.ColumnDefinitions[7].ActualWidth / scale;
        var row = heading.Bounds.Width / scale;

        // **The name keeps exactly the width it is drawn at.** A name that
        // was filling takes it as its own; a name that is giving way to a
        // narrower row keeps its own width and gets a span that gives way by
        // exactly as much as it does now — so nothing moves when the drag
        // begins, the width somebody chose is not lost to a drag of some
        // other column, and from here on only the dragged column changes.
        var name = pane.NameFills ? drawn : pane.ColumnWidths.Name;
        var span = row + (name - drawn);

        var start = column == DetailsColumn.Name ? name : PaneScale.ColumnWidth(pane.ColumnWidths, column);

        // **The name's edge stops at the pane's edge.** Past it the columns
        // after the name would go off the side, which is what a name given
        // its width in a wider pane used to do to them. Narrower, it stops
        // where the name would be drawn at its floor.
        var from = name - Math.Max(0, drawn - PaneScale.NameMin);
        var to = name + room;

        _columnDrag = new ColumnDrag(
            pane, column, frame, pressed.X, new Point(e.Vector.X, e.Vector.Y), scale, start,
            name, span, from, to);
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

        var width = drag.StartWidth + moved / drag.Scale;

        if (drag.Column == DetailsColumn.Name)
            width = Math.Clamp(width, drag.NameFrom, Math.Max(drag.NameFrom, drag.NameTo));

        pane.SetColumnWidth(drag.Column, width);
    }

    private void OnColumnGripDragCompleted(object? sender, VectorEventArgs e) => _columnDrag = null;
}
