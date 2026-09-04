using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Per-pane type and icon scale.
///
/// Works by exploiting the same lookup rule that once broke scaling entirely:
/// a DynamicResource resolves from the nearest dictionary outward. Writing the
/// metrics into a pane's own root control means everything inside that pane
/// resolves there, while the sidebar, status bar and the other side of a split
/// keep resolving at application level.
///
/// The formulas live in <see cref="Compute"/> and are shared with the
/// application-level defaults, so the two can never drift apart.
/// </summary>
public static class PaneScale
{
    private static readonly (string Key, double Value)[] FontMetrics =
    [
        ("FontSizeTiny", 11),
        ("FontSizeSmall", 12.5),
        ("FontSizeBase", 14),
        ("FontSizeLarge", 15.5),
    ];

    // The design reference's sizes at 100%: a 16px sidebar or row icon and a
    // 72px folder on a grid tile. They stay the BASE of the scale rather than
    // becoming literals in the markup — the pane's zoom still multiplies them,
    // which is what Ctrl+scroll and the per-layout zoom both ride on.
    // The icon each layout draws at 100%, named once so nothing else has to
    // keep a copy.
    //
    // **The flyout's typed size box kept one, and it went stale.** It
    // multiplied 26 — what ThumbSize was before the design-reference pass took
    // the details row icon to 18 — so the box read 26 beside an 18px icon, and
    // read 26 in Grid and Compact too, where the icons are 72 and 36. A number
    // no layout had drawn since.
    private const double DetailsIcon = 18;
    private const double CompactIcon = 36;
    private const double GridIcon = 72;

    /// <summary>
    /// The 16px icon the sidebar and the row decorations draw at 100%, and the
    /// base of every metric that has to keep its ratio to it — including the
    /// step a folder opened in place indents its children by.
    /// </summary>
    public const double RowIcon = 16;

    /// <summary>
    /// The icon a given layout draws at 100%: the base the flyout's typed size
    /// box multiplies and divides by. Read from here rather than restated in
    /// the view model, because restating it is how it drifted.
    /// </summary>
    public static double BaseIcon(Vaktari.Core.Session.ViewMode mode) => mode switch
    {
        Vaktari.Core.Session.ViewMode.Grid => GridIcon,
        Vaktari.Core.Session.ViewMode.Compact => CompactIcon,
        _ => DetailsIcon,
    };

    private static readonly (string Key, double Value)[] IconMetrics =
    [
        // **The details row icon sets the row height, not the label.**
        // rowHeight is max(body * 2.1, thumb + pad), and at 100% that was
        // max(29.4, 38) — so 26 was deciding it outright and the text never got
        // a vote. An icon at 1.86x the type size is a launcher's proportion, not
        // a listing's; Explorer and Dolphin sit nearer 1.3x.
        //
        // 18 puts the icon at 1.29x the 14px body and hands the decision back to
        // the text: max(29.4, 30). Rows go from 38 to 30 at 100%, which is about
        // eight more files on screen in a full-height window, and the icon still
        // reads at a glance because it is an icon rather than a thumbnail.
        ("ThumbSize", DetailsIcon),
        ("IconSize", RowIcon),
        ("TileSize", GridIcon),

        // The expand triangle, inside a slot one IconSize wide. Derived here
        // rather than written as 11 in the markup for the reason IconStroke
        // below states: a glyph that kept its pixel size while the slot around
        // it doubled would sit adrift in the middle of it.
        ("TwistSize", 11),

        // **Stroke width is not a stretch-invariant in Avalonia, and it is in
        // SVG.** The design draws its glyphs on a 24-unit viewBox at 16px with
        // stroke-width 1.6, and SVG scales the stroke with the box: it lands at
        // 1.6 * 16/24 = 1.067 device pixels. Avalonia's StrokeThickness is in
        // device pixels already and Stretch does not touch it, so the same 1.6
        // drew half again too heavy and every icon read bolder than the mock.
        //
        // Derived here rather than written as 1.067 in the markup so it keeps
        // its ratio to IconSize through the pane zoom — the two are scaled by
        // the same factor, which is the whole point of it being a metric.
        ("IconStroke", 16.0 / 24.0 * 1.6),
    ];

    /// <summary>
    /// Every metric for a given pair of scales. Structural sizes are derived
    /// rather than free: a row has to fit the taller of its label and its icon,
    /// so it cannot be set independently of either.
    /// </summary>
    public static IEnumerable<(string Key, double Value)> Compute(
        double fontScale, double iconScale)
    {
        foreach (var (key, value) in FontMetrics)
            yield return (key, Math.Round(value * fontScale, 1));

        foreach (var (key, value) in IconMetrics)
            yield return (key, Math.Round(value * iconScale, 1));

        var body = 14 * fontScale;
        var thumb = 18 * iconScale;
        var tile = 84 * iconScale;

        // ---- one breathing-room constant, used by all three modes ----------
        //
        // [stated] "the spacing between icons ... feels too tight." It was 8,
        // unscaled — so at 150% zoom the icons grew and the gap between them did
        // not, which is why it tightened the further you zoomed in rather than
        // staying proportional.
        //
        // Scaled with the ICON axis, because that is the axis it separates.
        // The per-mode settings in the dialog are still ADDED on top of this;
        // this is the floor, not a replacement for them.
        var pad = Math.Round(12 * iconScale, 1);

        var rowHeight = Math.Round(Math.Max(body * 2.1, thumb + pad), 1);

        // ---- compact has its OWN proportions, and that is the point ---------
        //
        // Reusing the details row's metrics was a mistake: the two modes want
        // different shapes. Measured against Dolphin, a compact icon is roughly
        // DOUBLE the text line height and the row hugs it; a details row is a
        // line of text with a smaller icon beside it. Sharing `RowHeight` and
        // `ThumbSize` produced something that was neither — a details-shaped row
        // with an undersized icon floating in it.
        //
        // The row still cannot be set independently: it has to fit the taller of
        // the icon and the label, which is why this is a max rather than a
        // constant.
        var compactIcon = Math.Round(CompactIcon * iconScale, 1);
        var compactRow = Math.Round(Math.Max(body * 1.9, compactIcon + pad), 1);

        // Icons.MaximumLines and the two text-width settings are NOT wired, and
        // the reason is structural rather than laziness. Every metric here is a
        // double, written into control Resources and read back by
        // DynamicResource — which assigns directly, without converting. MaxLines
        // is an int, so it cannot come down this path, and a second typed
        // pipeline is more machinery than the setting is worth today.
        //
        // Tile height would also have to follow: label lines are not free, and a
        // tile that does not grow to fit them just clips the label.
        yield return ("RowHeight", rowHeight);
        yield return ("TileWidth", Math.Round(tile + 24 + pad, 1));
        yield return ("TileHeight", Math.Round(tile + body * 2.9 + pad, 1));

        // ---- user-adjustable spacing ----------------------------------------
        //
        // Read from AppSettings rather than passed in, matching the static
        // provider convention used by IconLoader and RowMetadata. It makes this
        // method impure, which is worth naming: it now depends on when it runs,
        // so a settings save has to re-apply the metrics.
        //
        // **The six-pixel floor is not padding, it is correctness.** The grid
        // item template carries Margin="3", so a cell exactly TileWidth clips
        // every tile by that margin — uniformly, which reads as a styling fault
        // rather than a layout bug. The user's number is EXTRA on top, so zero
        // means "as it was" instead of "broken".
        var icons = Settings.AppSettings.Current.Views.Icons.Spacing;
        var compact = Settings.AppSettings.Current.Views.Compact.Spacing;

        // The 6 is not spacing — it is the grid template's Margin="3" on each
        // side, and a cell exactly TileWidth clips every tile without it. The
        // breathing room lives in TileWidth/TileHeight rather than here, so the
        // cell and the tile inside it are computed from the same numbers.
        yield return ("TileSpacing", Math.Round(6 + Math.Max(0, icons) * iconScale, 1));

        // Compact gets a CELL larger than its row, and the row is drawn inside
        // it — rather than growing the row itself, which would stretch the
        // selection highlight across the gap.
        yield return ("CompactCellWidth",
            Math.Round(210 * fontScale + Math.Max(0, compact) * iconScale, 1));
        yield return ("CompactCellHeight",
            Math.Round(compactRow + Math.Max(0, compact) * iconScale, 1));
        yield return ("RailWidth", Math.Round(44 * fontScale, 1));

        // Compact columns are sized by the text they hold, not by the icons —
        // the mode exists to fit names on screen.
        yield return ("CompactWidth", Math.Round(210 * fontScale, 1));
        // ---- the metadata columns -------------------------------------------
        //
        // These were literal pixels in BOTH the details header and the row
        // template — matching each other, so they aligned, but pinned. Scale the
        // text up and the date grew while its 150 px column did not, so the
        // columns most affected by a larger font were the only ones that could
        // not grow. Every other size in this file already derives from the
        // scale; these were the exception, and the accessibility case is exactly
        // the one that breaks.
        //
        // fontScale, not iconScale: they hold text.
        yield return ("ColPathNarrow", Math.Round(120 * fontScale, 1));
        yield return ("ColPathWide", Math.Round(200 * fontScale, 1));
        yield return ("ColPermissions", Math.Round(100 * fontScale, 1));
        // Wide enough for "DOCX file" and no wider: it is the newest column and
        // the one that has to justify every pixel, because all of them come out
        // of the name beside it.
        yield return ("ColType", Math.Round(110 * fontScale, 1));
        yield return ("ColSize", Math.Round(100 * fontScale, 1));
        yield return ("ColModified", Math.Round(150 * fontScale, 1));

        yield return ("CompactIconSize", compactIcon);
        yield return ("CompactRowHeight", compactRow);

        // Three rows of chain, preserved at any combination of the two scales.
        yield return ("ColumnStripHeight", Math.Round(rowHeight * 3 + 6, 1));
    }

    // ---- attached property ------------------------------------------------

    public static readonly AttachedProperty<PaneViewModel?> PaneProperty =
        AvaloniaProperty.RegisterAttached<Control, PaneViewModel?>("Pane", typeof(PaneScale));

    public static void SetPane(Control control, PaneViewModel? value)
        => control.SetValue(PaneProperty, value);

    public static PaneViewModel? GetPane(Control control) => control.GetValue(PaneProperty);

    // Subscriptions are held per control, not per pane: containers are reused
    // as tabs switch, so the previous pane's handler must come off or a pane
    // that is no longer shown keeps rewriting this control's resources.
    private static readonly ConditionalWeakTable<Control, Subscription> Live = new();

    private sealed class Subscription
    {
        public PropertyChangedEventHandler? Handler;
    }

    static PaneScale()
    {
        PaneProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            var subscription = Live.GetOrCreateValue(control);

            // Class-handler args carry object?, not Optional<T>, so this is a
            // plain type pattern rather than GetValueOrDefault.
            if (args.OldValue is PaneViewModel previous
                && subscription.Handler is not null)
                previous.PropertyChanged -= subscription.Handler;

            if (args.NewValue is not PaneViewModel pane)
            {
                subscription.Handler = null;
                return;
            }

            subscription.Handler = (_, e) =>
            {
                if (e.PropertyName is nameof(PaneViewModel.FontScale)
                    or nameof(PaneViewModel.IconScale))
                    Apply(control, pane);
            };

            pane.PropertyChanged += subscription.Handler;
            Apply(control, pane);
        });
    }

    /// <summary>
    /// **Nothing is global any more, and that reversal was the bug.**
    ///
    /// Spacing began as a purely global preference, so writing it once at
    /// application level was right. Then breathing room that scales with
    /// `iconScale` moved into the same resources — and a per-pane value
    /// published only at application scale meant a pane at 188% drew its
    /// compact rows 90px tall inside 48px cells, which overlapped.
    ///
    /// The settings-change problem that motivated the filter is now solved at
    /// the other end: `PaneViewModel.RefreshScale()` re-raises the scale
    /// notification, and the shell calls it for every pane on save.
    /// </summary>
    private static void Apply(Control control, PaneViewModel pane)
    {
        foreach (var (key, value) in Compute(pane.FontScale, pane.IconScale))
            control.Resources[key] = value;
    }
}
