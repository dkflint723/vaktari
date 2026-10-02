using Avalonia;
using Avalonia.Controls;

namespace Vaktari.Ui;

/// <summary>
/// How wide the name column of a details grid is: a width of its own once
/// somebody has dragged its edge, or whatever the other columns leave until
/// then.
///
/// **An attached property because a ColumnDefinition cannot take a resource.**
/// It is not a styled element, so neither a DynamicResource nor a binding
/// reaches its Width; the Grid it belongs to can take both, and this hands the
/// value on. The heading's grid and every row's grid carry it, from the same
/// per-tab resource, so they cannot disagree.
///
/// **The indent is the row's half of the arithmetic.** A row nested under a
/// folder opened in place docks its indent into the first column, so that
/// column is wider on that row than on the heading by exactly the indent — and
/// with the name filling, the name column absorbed the difference and its
/// right edge stayed put. Once the name has a width of its own nothing absorbs
/// it, so the row's name column is drawn the indent narrower: the edge the
/// heading's grip sits on is then the same edge on every row. The heading has
/// no indent and leaves this at zero.
/// </summary>
public static class DetailsColumns
{
    /// <summary>The name column's width in pixels as drawn, or zero (or
    /// less) for "fills what the other columns leave".</summary>
    public static readonly AttachedProperty<double> NameWidthProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameWidth", typeof(DetailsColumns));

    /// <summary>How far this row is indented inside the first column.</summary>
    public static readonly AttachedProperty<double> IndentProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("Indent", typeof(DetailsColumns));

    /// <summary>The column the name sits in, in both grids.</summary>
    private const int NameColumn = 1;

    /// <summary>
    /// The empty column after Created, in both grids: Auto, and so nothing,
    /// while the name fills; the rest of the row once it does not.
    ///
    /// **Not decoration — without it every column grew by a pixel.** A grid
    /// with no star column whose columns do not add up to its own width hands
    /// the difference out a pixel per column when it rounds to the screen, so
    /// the moment the name took a width of its own the icon slot, the hidden
    /// path column and every heading after the name drew one pixel wider than
    /// asked, and a 30-pixel drag moved the edge 29. Measured in a headless
    /// window at 100%. A star column that takes the slack leaves nothing to
    /// hand out.
    /// </summary>
    private const int TrailingColumn = 7;

    static DetailsColumns()
    {
        NameWidthProperty.Changed.AddClassHandler<Grid>((grid, _) => Update(grid));
        IndentProperty.Changed.AddClassHandler<Grid>((grid, _) => Update(grid));
        NameSpanProperty.Changed.AddClassHandler<Grid>((grid, _) => Update(grid));
        NameMinProperty.Changed.AddClassHandler<Grid>((grid, _) => Update(grid));

        // The row's own width is half of how wide the name is drawn once it
        // gives way, so a grid that changes width works it out again. Only
        // the grids that carry a name width of their own: every other Grid
        // in the window passes through here and is left alone.
        Visual.BoundsProperty.Changed.AddClassHandler<Grid>((grid, e) =>
        {
            if (GetNameWidth(grid) > 0
                && e.GetOldValue<Rect>().Width != e.GetNewValue<Rect>().Width)
                Update(grid);
        });
    }

    public static void SetNameWidth(Grid grid, double value) => grid.SetValue(NameWidthProperty, value);
    public static double GetNameWidth(Grid grid) => grid.GetValue(NameWidthProperty);

    public static void SetIndent(Grid grid, double value) => grid.SetValue(IndentProperty, value);
    public static double GetIndent(Grid grid) => grid.GetValue(IndentProperty);

    /// <summary>
    /// The width of the row the name's width was chosen in, in pixels as
    /// drawn, or zero when nobody has said. See <see cref="NameAsDrawn"/>.
    /// </summary>
    public static readonly AttachedProperty<double> NameSpanProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameSpan", typeof(DetailsColumns));

    /// <summary>The narrowest the name is drawn when it gives way, in pixels
    /// as drawn.</summary>
    public static readonly AttachedProperty<double> NameMinProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameMin", typeof(DetailsColumns));

    public static void SetNameSpan(Grid grid, double value) => grid.SetValue(NameSpanProperty, value);
    public static double GetNameSpan(Grid grid) => grid.GetValue(NameSpanProperty);

    public static void SetNameMin(Grid grid, double value) => grid.SetValue(NameMinProperty, value);
    public static double GetNameMin(Grid grid) => grid.GetValue(NameMinProperty);

    /// <summary>
    /// How wide the name is drawn in a row <paramref name="width"/> wide, as
    /// the heading would draw it (before any indent comes off).
    ///
    /// **A name given its width in a wide pane pushed every later column, and
    /// every grip, off the edge of a narrower one** — F3 on a pane whose name
    /// had been dragged, a narrower window, a zoom in — and only Reset
    /// brought them back. So the name gives way: it is drawn as much narrower
    /// than its width as the row is narrower than the one the width was
    /// chosen in (<paramref name="span"/>), down to <paramref name="min"/>,
    /// and comes back to its width when the row does. Its width itself is
    /// left alone, being what somebody chose.
    ///
    /// **Measured against the row it was chosen in, not against the room the
    /// other columns leave**, because a column widened by a drag would
    /// otherwise take its room from the name the moment the button came up —
    /// the "resizing Size resizes Name" report again, a step later. Room a
    /// drag used up past the edge stays the drag's; room the PANE lost comes
    /// out of the name. Where the span is not known (zero) the name is drawn
    /// at its width: only a drag sets one, and every drag does.
    ///
    /// **And not against the room the others leave for a second reason: that
    /// goes stale.** It was tried as the fallback and measured: the room is
    /// read off the columns as last laid out, a column changing width does
    /// not change the row's, and a heading and its rows worked it out at
    /// different moments and disagreed by 39 pixels at 125%. The span and the
    /// row's own width are the same for the heading and for every row.
    /// </summary>
    public static double NameAsDrawn(double pinned, double span, double min, double width)
    {
        // Half a pixel either way is rounding in the span, not a narrower row.
        var drawn = span > 0
            ? (span - width > 0.5 ? pinned - (span - width) : pinned)
            : pinned;

        return Math.Min(pinned, Math.Max(min, drawn));
    }

    /// <summary>
    /// The width the name column of a grid with this indent is drawn at.
    /// Star while the name fills; never below zero, so a row nested deeper
    /// than the name is wide loses its name rather than pushing its other
    /// cells right of their headings.
    /// </summary>
    public static GridLength NameColumnWidth(double nameWidth, double indent)
        => nameWidth > 0
            ? new GridLength(Math.Max(0, nameWidth - indent), GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

    private static void Update(Grid grid)
    {
        if (grid.ColumnDefinitions.Count <= TrailingColumn) return;

        var width = GetNameWidth(grid);
        var indent = GetIndent(grid);

        if (width > 0 && grid.Bounds.Width > 0)
            width = NameAsDrawn(width, GetNameSpan(grid), GetNameMin(grid), grid.Bounds.Width);

        grid.ColumnDefinitions[NameColumn].Width = NameColumnWidth(width, indent);
        grid.ColumnDefinitions[TrailingColumn].Width = width > 0
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
    }
}
