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
    }

    public static void SetNameWidth(Grid grid, double value) => grid.SetValue(NameWidthProperty, value);
    public static double GetNameWidth(Grid grid) => grid.GetValue(NameWidthProperty);

    public static void SetIndent(Grid grid, double value) => grid.SetValue(IndentProperty, value);
    public static double GetIndent(Grid grid) => grid.GetValue(IndentProperty);

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

        grid.ColumnDefinitions[NameColumn].Width = NameColumnWidth(width, GetIndent(grid));
        grid.ColumnDefinitions[TrailingColumn].Width = width > 0
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
    }
}
