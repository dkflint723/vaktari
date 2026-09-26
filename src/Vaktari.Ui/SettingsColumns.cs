using Avalonia;
using Avalonia.Controls;

namespace Vaktari.Ui;

/// <summary>
/// Lays a short list of settings out in two columns when the page is wide
/// enough for two, and in one when it is not.
///
/// **Short check boxes stacked one per line made the settings window four
/// screens tall.** Confirmations, what the context menu shows, how the window
/// opens: each a handful of three- to six-word labels, each taking a full-width
/// line on a page that is about 480px wide at the dialog's default size. Two
/// per line halves them there.
///
/// **And at the dialog's floor, one.** At 520px the page beside the strip is
/// about 300px, and two 150px columns would cut "Closing a window with several
/// tabs open" in half — so the panel decides from the width it is GIVEN rather
/// than from a fixed column count, which a Grid or a UniformGrid would need.
/// A WrapPanel would decide per row instead and leave ragged columns. The
/// numbers above are at an interface text size of 100%; the column's
/// minimum grows with the text (see ScaledMinColumnWidth), so a larger text
/// size falls back to one column sooner rather than wrapping every label.
///
/// Row-major, so reading order, tab order and the order in the markup are all
/// the same: left, right, next line. Each row is as tall as its taller cell,
/// so a label that wraps in one column pushes the next row down rather than
/// under it.
/// </summary>
public sealed class SettingsColumns : Panel
{
    /// <summary>The narrowest a column may be before the panel falls back to
    /// one, at the text size <see cref="ReferenceFontSize"/> names. Chosen
    /// against the labels it holds: the longest wraps at most once at this
    /// width.</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<SettingsColumns, double>(nameof(MinColumnWidth), 200);

    /// <summary>
    /// The font size <see cref="MinColumnWidth"/> was chosen at, and at which
    /// it is taken as it stands. Not quite the settings window's small text at
    /// 100% — PaneScale sets that at 12.5, which makes the minimum about 208px
    /// there — but the size the column test measured and the 416px arithmetic
    /// in this class's summary were written at.
    /// </summary>
    public const double ReferenceFontSize = 12;

    /// <summary>Space between the two columns, and between rows.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SettingsColumns, double>(nameof(Spacing), 8);

    static SettingsColumns()
    {
        AffectsMeasure<SettingsColumns>(MinColumnWidthProperty, SpacingProperty);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How many columns the last measure chose — one or two. For
    /// tests, which measure the fall-back rather than trust it.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>
    /// The narrowest column, at the size the children's text is drawn.
    ///
    /// **A fixed 200px was only right at 100%.** Labels grow with the
    /// interface text size and a column does not, so at 150–200% the pair
    /// kept two columns and every label wrapped onto two or three lines.
    /// Scaled by the largest font among the children, which is what the
    /// labels are set in.
    /// </summary>
    private double ScaledMinColumnWidth
    {
        get
        {
            var size = Children.Count == 0
                ? ReferenceFontSize
                : Children.Max(c => c.GetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty));

            return MinColumnWidth * Math.Max(1, size / ReferenceFontSize);
        }
    }

    private int ColumnsFor(double width)
        => !double.IsInfinity(width) && width >= 2 * ScaledMinColumnWidth + Spacing ? 2 : 1;

    private double ColumnWidth(double width, int columns)
        => columns == 1 ? width : (width - Spacing) / 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        Columns = ColumnsFor(availableSize.Width);

        var cell = ColumnWidth(availableSize.Width, Columns);
        var visible = Children.Where(c => c.IsVisible).ToList();

        var height = 0.0;
        var widest = 0.0;

        for (var i = 0; i < visible.Count; i += Columns)
        {
            var row = 0.0;

            for (var j = i; j < Math.Min(i + Columns, visible.Count); j++)
            {
                visible[j].Measure(new Size(cell, double.PositiveInfinity));
                row = Math.Max(row, visible[j].DesiredSize.Height);
                widest = Math.Max(widest, visible[j].DesiredSize.Width);
            }

            height += row + (i > 0 ? Spacing : 0);
        }

        var width = double.IsInfinity(availableSize.Width)
            ? widest * Columns + (Columns - 1) * Spacing
            : availableSize.Width;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width);
        var cell = ColumnWidth(finalSize.Width, columns);
        var visible = Children.Where(c => c.IsVisible).ToList();

        var y = 0.0;

        for (var i = 0; i < visible.Count; i += columns)
        {
            var row = 0.0;

            for (var j = i; j < Math.Min(i + columns, visible.Count); j++)
                row = Math.Max(row, visible[j].DesiredSize.Height);

            for (var j = i; j < Math.Min(i + columns, visible.Count); j++)
            {
                var x = (j - i) * (cell + Spacing);
                // Its own height, top-aligned: stretched to the row, a check
                // box beside a taller neighbour would centre its tick.
                visible[j].Arrange(new Rect(x, y, cell, visible[j].DesiredSize.Height));
            }

            y += row + Spacing;
        }

        return finalSize;
    }
}
