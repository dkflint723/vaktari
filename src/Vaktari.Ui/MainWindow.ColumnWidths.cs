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
    /// grip's, the width the column had then at 100%, the scale it was drawn
    /// at, and — while the name still fills — the width the name was drawn at,
    /// to give it once the pointer moves.
    /// </summary>
    private sealed record ColumnDrag(
        PaneViewModel Pane, DetailsColumn Column, Visual Frame,
        double PressedAt, Point PressedOnGrip, double StartWidth, double Scale,
        double? NameAsDrawn);

    private ColumnDrag? _columnDrag;

    private void OnColumnGripDragStarted(object? sender, VectorEventArgs e)
    {
        _columnDrag = null;

        if (sender is not Thumb { DataContext: PaneViewModel pane, Tag: string tag } grip
            || TopLevel.GetTopLevel(grip) is not { } frame
            || grip.TranslatePoint(new Point(e.Vector.X, e.Vector.Y), frame) is not { } pressed)
            return;

        var column = Enum.Parse<DetailsColumn>(tag);

        // The product PaneScale multiplies a width by on the way out.
        var scale = pane.TextScale > 0 ? pane.TextScale : 1.0;

        // Read off the heading's own grid, which every grip is in, while the
        // name is still filling it.
        double? name = pane.NameFills && grip.Parent is Grid { ColumnDefinitions.Count: > 1 } heading
            ? heading.ColumnDefinitions[1].ActualWidth / scale
            : null;

        var start = column == DetailsColumn.Name && name is { } drawn
            ? drawn
            : PaneScale.ColumnWidth(pane.ColumnWidths, column);

        _columnDrag = new ColumnDrag(
            pane, column, frame, pressed.X, new Point(e.Vector.X, e.Vector.Y), start, scale, name);
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
            || grip.TranslatePoint(drag.PressedOnGrip + e.Vector, drag.Frame) is not { } now
            || now.X == drag.PressedAt)
            return;

        // **The name stops filling the moment an edge is MOVED**, at exactly
        // the width it is drawn at, so it does not jump — and from then on it
        // is the dragged column alone that changes. On the first move rather
        // than the press: a click on an edge that goes nowhere changes nothing,
        // including whether the name follows the window when it is resized.
        if (drag.NameAsDrawn is { } name && pane.NameFills)
            pane.SetColumnWidth(DetailsColumn.Name, name);

        pane.SetColumnWidth(drag.Column, drag.StartWidth + (now.X - drag.PressedAt) / drag.Scale);
    }

    private void OnColumnGripDragCompleted(object? sender, VectorEventArgs e) => _columnDrag = null;
}
