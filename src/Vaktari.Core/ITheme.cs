namespace Vaktari.Core;

/// <summary>
/// Named colour roles the UI paints with. Roles rather than literal colours so
/// the same markup works under any desktop theme — and so a colour scheme
/// change is one lookup table, not a sweep through the markup.
/// </summary>
public static class ThemeRole
{
    public const string WindowBackground = "window.bg";
    public const string WindowText = "window.fg";
    public const string ViewBackground = "view.bg";
    public const string ViewAlternate = "view.alt";
    public const string ViewText = "view.fg";
    public const string ViewDimText = "view.fg.dim";
    public const string SelectionBackground = "selection.bg";
    public const string SelectionText = "selection.fg";
    public const string Accent = "accent";
    public const string Border = "border";
}

/// <summary>
/// Colours as hex strings and the desktop's UI font. Strings keep Core free of
/// any toolkit type — the UI layer parses them.
/// </summary>
public sealed record ThemePalette
{
    public required IReadOnlyDictionary<string, string> Colours { get; init; }

    public string? FontFamily { get; init; }
    public double? FontSize { get; init; }

    /// <summary>
    /// The desktop's text-size setting as a MULTIPLIER of its own default:
    /// 1.25 for Windows' "Make text bigger" at 125%.
    ///
    /// **A second field rather than a second reading of <see cref="FontSize"/>,
    /// because the two desktops answer in units neither provider can convert
    /// into the other's.** Plasma states an absolute point size in kdeglobals
    /// and has no percentage anywhere; Windows states a percentage under
    /// <c>Software\Microsoft\Accessibility</c> and its font SIZE is a LOGFONT
    /// height in device units, which cannot be turned into points without the
    /// DPI of the display the window lands on — the reason
    /// <c>WindowsThemeProvider</c> has always left FontSize null.
    ///
    /// So a provider fills whichever of the two its desktop actually states,
    /// null means the desktop did not say, and the UI layer reads both.
    /// </summary>
    public double? TextScale { get; init; }

    /// <summary>Drives which of our own derived shades read correctly.</summary>
    public bool IsDark { get; init; } = true;

    /// <summary>The desktop's icon theme name, for when icons are wired up.</summary>
    public string? IconTheme { get; init; }

    /// <summary>
    /// Whether the desktop opens items on a single click. Null when the desktop
    /// does not say, which is not the same as "double" — it means fall back to
    /// this application's own default rather than assert something about a
    /// desktop that never expressed a preference.
    ///
    /// Here rather than in a separate provider because it comes out of the same
    /// file, in the same read, as everything else on this record.
    /// </summary>
    public bool? SingleClick { get; init; }
}

public interface IThemeProvider
{
    /// <summary>Null when the desktop exposes no scheme we can read.</summary>
    ThemePalette? Read();

    /// <summary>Raised when the desktop's scheme changes, so the UI can repaint.</summary>
    event EventHandler? Changed;
}
