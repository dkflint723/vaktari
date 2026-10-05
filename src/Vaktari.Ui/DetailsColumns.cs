using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;

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
///
/// **How far the name gives way is worked out once, on the heading, and every
/// row takes the same number** (<see cref="NameGiveProperty"/>, from
/// <see cref="PaneViewModel.NameGive"/>). Worked out per grid it went stale:
/// a row and the heading read the other columns at different moments and
/// disagreed by 39 pixels at 125%.
/// </summary>
public static class DetailsColumns
{
    /// <summary>The name column's chosen width in pixels as drawn, or zero
    /// (or less) for "fills what the other columns leave".</summary>
    public static readonly AttachedProperty<double> NameWidthProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameWidth", typeof(DetailsColumns));

    /// <summary>How far this row is indented inside the first column.</summary>
    public static readonly AttachedProperty<double> IndentProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("Indent", typeof(DetailsColumns));

    /// <summary>How many pixels narrower than its chosen width the name is
    /// drawn, the same for the heading and every row of one tab.</summary>
    public static readonly AttachedProperty<double> NameGiveProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameGive", typeof(DetailsColumns));

    /// <summary>
    /// The width of the row the name's width was chosen in, in pixels as
    /// drawn, or zero when nobody has said. Read on the heading only.
    /// </summary>
    public static readonly AttachedProperty<double> NameSpanProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameSpan", typeof(DetailsColumns));

    /// <summary>The narrowest the name is drawn when it gives way, in pixels
    /// as drawn. Read on the heading only.</summary>
    public static readonly AttachedProperty<double> NameMinProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("NameMin", typeof(DetailsColumns));

    /// <summary>True on the heading's grid: the one that works out
    /// <see cref="PaneViewModel.NameGive"/> for its tab.</summary>
    public static readonly AttachedProperty<bool> MeasuresProperty =
        AvaloniaProperty.RegisterAttached<Grid, bool>("Measures", typeof(DetailsColumns));

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
        NameGiveProperty.Changed.AddClassHandler<Grid>((grid, _) => Update(grid));

        MeasuresProperty.Changed.AddClassHandler<Grid>((grid, e) =>
        {
            grid.LayoutUpdated -= OnHeadingLaidOut;

            if (e.GetNewValue<bool>()) grid.LayoutUpdated += OnHeadingLaidOut;
        });
    }

    public static void SetNameWidth(Grid grid, double value) => grid.SetValue(NameWidthProperty, value);
    public static double GetNameWidth(Grid grid) => grid.GetValue(NameWidthProperty);

    public static void SetIndent(Grid grid, double value) => grid.SetValue(IndentProperty, value);
    public static double GetIndent(Grid grid) => grid.GetValue(IndentProperty);

    public static void SetNameGive(Grid grid, double value) => grid.SetValue(NameGiveProperty, value);
    public static double GetNameGive(Grid grid) => grid.GetValue(NameGiveProperty);

    public static void SetNameSpan(Grid grid, double value) => grid.SetValue(NameSpanProperty, value);
    public static double GetNameSpan(Grid grid) => grid.GetValue(NameSpanProperty);

    public static void SetNameMin(Grid grid, double value) => grid.SetValue(NameMinProperty, value);
    public static double GetNameMin(Grid grid) => grid.GetValue(NameMinProperty);

    public static void SetMeasures(Grid grid, bool value) => grid.SetValue(MeasuresProperty, value);
    public static bool GetMeasures(Grid grid) => grid.GetValue(MeasuresProperty);

    /// <summary>
    /// How many pixels narrower than its chosen width the name is drawn, in
    /// a row <paramref name="width"/> wide whose other columns take
    /// <paramref name="others"/>.
    ///
    /// **A name given its width in a wide pane pushed every later column, and
    /// every grip, off the edge of a narrower one** — F3 on a pane whose name
    /// had been dragged, a narrower window, a zoom in — and only Reset
    /// brought them back. So the name gives way, but only as far as both of
    /// these say it must:
    ///
    /// - **the room the pane has lost** since the widths were last dragged
    ///   (<paramref name="span"/> less <paramref name="width"/>). Never more,
    ///   so a column widened by a drag never takes its room from the name —
    ///   right after a drag the span IS the row, and this is nothing.
    /// - **the room the columns would run past the edge** with the name at
    ///   its chosen width. Never more, so a narrower pane first uses up the
    ///   empty room after the last column, and a name that gives way leaves
    ///   no gap after it: in QA's split the name went to its floor with 170
    ///   pixels empty after Size.
    ///
    /// And never past <paramref name="min"/>: in a pane too narrow even for
    /// that, the columns scroll sideways rather than the name getting any
    /// narrower (<see cref="PaneViewModel.ColumnsOverflow"/>).
    ///
    /// <paramref name="width"/> is the row as much of it as is on SCREEN, not
    /// the heading grid's own width, which is the columns' once they scroll.
    /// </summary>
    public static double Give(double pinned, double span, double min, double width, double others)
    {
        if (pinned <= 0 || span <= 0 || width <= 0) return 0;

        // Half a pixel either way is rounding, not a narrower row.
        var lost = span - width;
        var overflow = pinned + others - width;

        if (lost <= 0.5 || overflow <= 0.5) return 0;

        return Math.Clamp(Math.Min(lost, overflow), 0, Math.Max(0, pinned - min));
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

        if (width > 0) width -= GetNameGive(grid);

        grid.ColumnDefinitions[NameColumn].Width = NameColumnWidth(width, GetIndent(grid));
        grid.ColumnDefinitions[TrailingColumn].Width = width > 0
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
    }

    /// <summary>
    /// The heading, laid out: works out how far its tab's name gives way, and
    /// whether the columns still overflow after that, from its own columns and
    /// the row as much of it as is on screen — and hands both to the tab, from
    /// where the heading and every row take them.
    ///
    /// **The visible row is the heading scroller's viewport, not the grid's
    /// width.** Once the columns scroll, the grid is as wide as the columns,
    /// and a give worked out from that would read a scrolled pane as a wider
    /// one and never give anything.
    ///
    /// **Not while a column is being dragged** — neither the give, nor the
    /// row's width shrinking. The edge under the pointer has to follow it: a
    /// name giving more or less would move it, and so would a narrower row,
    /// because at the far right the scroller pulls a shrinking row back by as
    /// much as it shrank. The row may still GROW while a column is dragged
    /// wider. The drag ends by asking for a layout, which lands here again
    /// with the drag over.
    ///
    /// **No guard for a hidden heading.** One was written for the review's
    /// worry — a hidden tab's heading laid out from stale numbers, or a viewport
    /// of nothing read as columns that overflow it entirely — and QA proved it
    /// dead: a tab opened in the grid, and a tab whose widths and window
    /// changed while its heading was hidden, came back right in their first
    /// pass with it removed. Removed rather than kept as a gate nothing can
    /// show is needed.
    /// </summary>
    private static void OnHeadingLaidOut(object? sender, EventArgs e)
    {
        if (sender is not Grid { DataContext: PaneViewModel pane } grid
            || grid.ColumnDefinitions.Count <= TrailingColumn
            || grid.FindAncestorOfType<ScrollViewer>() is not { } scroller)
            return;

        var margins = grid.Margin.Left + grid.Margin.Right;
        var visible = scroller.Viewport.Width - margins;

        var others = 0.0;

        for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
            if (i != NameColumn && i != TrailingColumn)
                others += grid.ColumnDefinitions[i].ActualWidth;

        var pinned = GetNameWidth(grid);

        var give = pane.IsResizingColumns
            ? pane.NameGive
            : Give(pinned, GetNameSpan(grid), GetNameMin(grid), visible, others);

        if (!pane.IsResizingColumns && Math.Abs(give - pane.NameGive) > 0.25) pane.NameGive = give;

        // What the columns take with the name as narrow as it will be drawn:
        // its width less the give, or its floor while it fills — a filling
        // name in a pane too narrow even for that scrolls rather than cutting
        // the last columns off.
        var content = others + (pinned > 0 ? pinned - give : GetNameMin(grid));

        // Half a pixel is rounding, as in Give. Without it a sub-pixel
        // difference at 115% showed a scroll bar with nothing to scroll.
        var overflow = content > visible + 0.5;
        var row = overflow ? content + margins : double.NaN;

        // Held during a drag: it may grow, and it may start, but nothing smaller.
        if (pane.IsResizingColumns
            && (!overflow || (pane.ColumnsOverflow && row <= pane.DetailsRowWidth)))
            return;

        pane.ColumnsOverflow = overflow;

        // **NaN on either side is a change**: the 0.25 rule alone never leaves
        // NaN and never goes back to it, because every comparison with NaN is
        // false.
        if (double.IsNaN(row) != double.IsNaN(pane.DetailsRowWidth)
            || Math.Abs(row - pane.DetailsRowWidth) > 0.25)
            pane.DetailsRowWidth = row;
    }
}
