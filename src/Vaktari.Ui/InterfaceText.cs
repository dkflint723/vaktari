using Vaktari.Core;

namespace Vaktari.Ui;

/// <summary>
/// How large the interface's text is drawn, before any per-pane zoom.
///
/// **Vaktari had no answer to "the text is too small" at all.** The one global
/// type scale, <c>ShellViewModel.FontScale</c>, was written from the restored
/// window geometry and read back into it and nothing else ever moved it: no
/// control in Settings, no menu item, no key. The per-pane zoom reached the
/// listings and stopped there, so the sidebar, the tab strip, the toolbar and
/// the status bar stayed at the size chosen at build time whatever the pane was
/// doing. And nothing anywhere read the desktop's own text-size setting —
/// Windows' "Make text bigger", Plasma's general font — so the one place a
/// person has already said how big they need text was the one place Vaktari
/// never looked.
///
/// This is that missing global factor. It multiplies the FONT axis of
/// <see cref="PaneScale.Compute"/>, which is the single funnel both the
/// application-level metrics and each pane's own dictionary are computed
/// through — so the chrome and the listings move together, and the per-pane
/// zoom stays a multiplier on top of it rather than a replacement for it.
/// </summary>
public static class InterfaceText
{
    /// <summary>
    /// The range any scale may take, from either source. The same 0.7–2.5 the
    /// per-pane zoom clamps to (<c>PaneViewModel</c>), because they multiply
    /// each other and a shared ceiling is the only one that can be reasoned
    /// about — and because a hand-edited settings.json can say 40 as easily as
    /// 1.4, and Windows' own slider reaches 225%.
    /// </summary>
    public const double Minimum = 0.7;
    public const double Maximum = 2.5;

    /// <summary>
    /// Plasma's default general font size, in points, and the reference a
    /// kdeglobals size is read against.
    ///
    /// **A desktop point size only means something as a ratio to the desktop's
    /// own default.** 12pt is not 12/14ths of Vaktari's 14px body — it is
    /// Plasma's 10 raised by a fifth, and a fifth is the fact worth carrying
    /// across. Reading it the other way would shrink the window by 5% on a
    /// stock Plasma, which nobody asked for and which is exactly the kind of
    /// silent change a default must never make: at 10 this returns 1.0.
    /// </summary>
    private const double DesktopBaseFontSize = 10;

    /// <summary>
    /// The desktop's own setting, or null when it did not say.
    ///
    /// Published by <see cref="ThemeApplier"/> rather than read from here,
    /// following <c>MainWindow.SystemSingleClick</c> exactly: Apply is the one
    /// place every palette read funnels through — startup, a desktop scheme
    /// change and a settings save all reach it — so a value published there
    /// cannot fall out of step with the palette it came from.
    ///
    /// Settable, so a test can state the desktop's answer instead of the
    /// machine's. **It is a static and it is read by the metric pipeline**, so
    /// a test that changes it must put it back, like every other static in this
    /// codebase's test rules.
    /// </summary>
    public static double? SystemScale { get; set; }

    /// <summary>
    /// What everything is drawn at: the configured size if there is one, else
    /// the desktop's, else 1.0.
    ///
    /// The setting wins because it is the more specific of the two — somebody
    /// who has opened Settings and chosen 150% has said something about
    /// Vaktari, not about their desktop.
    /// </summary>
    public static double Scale
    {
        get
        {
            var chosen = Settings.AppSettings.Current.Views.InterfaceTextScale;

            if (chosen > 0) return Math.Clamp(chosen, Minimum, Maximum);

            return SystemScale is { } system
                ? Math.Clamp(system, Minimum, Maximum)
                : 1.0;
        }
    }

    /// <summary>
    /// A desktop point size as a multiplier of that desktop's default. Null for
    /// a desktop that states no size, which is every Windows machine — see
    /// <see cref="ThemePalette.TextScale"/> for why the two cannot share one
    /// field.
    /// </summary>
    public static double? FromDesktopFontSize(double? points)
        => points is > 0 ? points.Value / DesktopBaseFontSize : null;

    /// <summary>
    /// Whichever of the two facts the desktop stated. A percentage is preferred
    /// where both exist: it is the setting a person moved on purpose, whereas a
    /// UI font size is part of a theme they may only have chosen the look of.
    /// </summary>
    public static double? FromPalette(ThemePalette? palette)
        => palette?.TextScale ?? FromDesktopFontSize(palette?.FontSize);

    /// <summary>
    /// The sizes the Settings combo offers, in its order. The first row stores
    /// zero — the setting's own zero value, which follows the desktop — so
    /// choosing that row and never opening the dialog leave the same value in
    /// the file rather than two that merely behave alike.
    ///
    /// Here rather than in the view model because the markup lists the labels
    /// and the view model decodes the index, and a table split across two files
    /// is how the two would come to disagree about which row means 150%.
    /// </summary>
    public static readonly double[] Steps = [0, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    /// <summary>The row a stored size selects, falling back to the first —
    /// a hand-written 1.37 is not a row, and must not silently become one.</summary>
    public static int RowFor(double scale)
    {
        var at = Array.IndexOf(Steps, scale);

        return at < 0 ? 0 : at;
    }

    /// <summary>The size a row stores. Out of range means the first row, for
    /// the same reason: an index nobody offered cannot become a text size.</summary>
    public static double ScaleFor(int row)
        => row >= 0 && row < Steps.Length ? Steps[row] : 0;
}
