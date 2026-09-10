using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Vaktari.Core;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which font actually reaches the screen.
///
/// **This is the test that did not exist when the font setting shipped
/// broken.** Choosing a font in Settings did nothing for a release: Apply
/// resolved the configured family and then ApplyDesignScheme overwrote it three
/// lines later, so the list offered every installed family, accepted a choice,
/// saved it, and the window carried on in the reference typeface.
///
/// It went unnoticed because the diagnostic built to catch exactly this ran
/// BEFORE the overwrite and reported the value it had just written. Broken and
/// fixed builds printed identical logs; only a screenshot could separate them.
/// A test reads the dictionary, which is the thing the markup binds to, so it
/// cannot be fooled the same way.
/// </summary>
public class ThemeApplierTests : IDisposable
{
    private readonly SettingsState _original = AppSettings.Current;

    public void Dispose()
    {
        AppSettings.Apply(_original);
        GC.SuppressFinalize(this);
    }

    private static void Configure(string? font)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { CustomFontFamily = font },
        });

    /// <summary>A desktop that asks for something distinctive, so "followed the
    /// desktop" and "used the reference" cannot be confused.</summary>
    private static ThemePalette Desktop(string? font) => new()
    {
        Colours = new Dictionary<string, string>(),
        FontFamily = font,
        IsDark = true,
    };

    private static FontFamily? Applied(Window window)
        => (Avalonia.Application.Current?.Resources ?? window.Resources)["AppFontFamily"] as FontFamily;

    private static Color Resource(Window window, string key)
        => ((ISolidColorBrush)(Avalonia.Application.Current?.Resources
                               ?? window.Resources)[key]!).Color;

    /// <summary>
    /// **The palette and Fluent must agree about light and dark, and for a long
    /// time nothing made them.**
    ///
    /// Two things decide colours here. This file writes the resources the markup
    /// binds to; FluentTheme supplies everything the markup does NOT name — and
    /// a filename in the listing is one of those, because its TextBlock sets no
    /// Foreground and inherits ListBoxItem's. Fluent picks its values from the
    /// requested theme variant, which App.axaml leaves following the OS.
    ///
    /// So on a machine set to LIGHT, the shipped build painted the design
    /// scheme's dark surfaces and let Fluent write near-black filenames onto
    /// them: 1.02:1, which is not "low contrast", it is invisible. It survived
    /// because the machine it was developed on is set to dark, where the two
    /// happen to agree — every screenshot looked right.
    ///
    /// Asserting on the variant is the point. A test that only checked the
    /// resource values would have passed throughout the entire life of the bug.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_palette_and_the_control_theme_agree_about_lightness(bool dark)
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            IsDark = dark,
        });

        Assert.Equal(
            dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            Avalonia.Application.Current?.RequestedThemeVariant);

        // And the surfaces went the same way, so the two cannot be passing for
        // opposite reasons. A dark scheme's listing is darker than its chrome;
        // a light scheme's listing is the lightest thing on screen.
        var listing = Resource(window, "ViewBackground");
        var chrome = Resource(window, "AppBackground");

        if (dark) Assert.True(listing.R < chrome.R, "dark: the listing should recede");
        else Assert.True(listing.R > chrome.R, "light: the listing should be the paper");
    }

    /// <summary>
    /// The text has to be readable on the surface it lands on, in both schemes.
    /// Contrast is the property that actually matters and the one a hex value
    /// cannot be eyeballed for — 4.45:1 and 4.75:1 look identical and only one
    /// of them passes.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Body_text_clears_AA_on_every_surface(bool dark)
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            IsDark = dark,
        });

        foreach (var surface in new[] { "ViewBackground", "ViewAlternate", "PanelBackground" })
        foreach (var text in new[] { "ViewText", "ViewDimText" })
        {
            var ratio = Contrast(Resource(window, text), Resource(window, surface));

            Assert.True(ratio >= 4.5,
                $"{text} on {surface} is {ratio:F2}:1 in the {(dark ? "dark" : "light")} scheme");
        }
    }

    /// <summary>
    /// **The setting has to beat the desktop, or it is decoration.**
    ///
    /// Each case forces the scheme OPPOSITE to what the desktop asks for, so a
    /// pass cannot come from the two happening to agree — which is exactly how
    /// the lightness bug hid for months. Asserting the variant as well as the
    /// surfaces, because both have to move together: a setting that repainted
    /// the backgrounds and left Fluent on the desktop's answer would reproduce
    /// the original defect through a new door.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ThemeMode.Light, true)]
    [InlineData(ThemeMode.Dark, false)]
    public void A_chosen_scheme_overrules_the_desktop(ThemeMode mode, bool desktopIsDark)
    {
        Configure(null);
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { ThemeMode = mode },
        });

        var window = new Window();
        ThemeApplier.Apply(window, new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            IsDark = desktopIsDark,
        });

        var wantDark = mode == ThemeMode.Dark;

        Assert.Equal(
            wantDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            Avalonia.Application.Current?.RequestedThemeVariant);

        var listing = Resource(window, "ViewBackground");
        var chrome = Resource(window, "AppBackground");

        if (wantDark) Assert.True(listing.R < chrome.R, "forced dark: the listing should recede");
        else Assert.True(listing.R > chrome.R, "forced light: the listing should be the paper");
    }

    /// <summary>
    /// And with nothing chosen it still follows the desktop, which is the
    /// default and the case that actually protects people.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Following_the_desktop_stays_the_default(bool desktopIsDark)
    {
        Configure(null);
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { ThemeMode = ThemeMode.FollowDesktop },
        });

        var window = new Window();
        ThemeApplier.Apply(window, new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            IsDark = desktopIsDark,
        });

        Assert.Equal(
            desktopIsDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            Avalonia.Application.Current?.RequestedThemeVariant);
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    [AvaloniaFact]
    public void A_configured_font_survives_the_design_scheme()
    {
        Configure("Georgia");

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        Assert.Equal("Georgia", Applied(window)?.Name);
    }

    /// <summary>
    /// **The interface face is proportional; the mono face still exists for the
    /// columns that need it.** This assertion used to demand JetBrains Mono here
    /// and was right to, until the split: monospace everywhere cost roughly
    /// 15-20% of the room every label had, and three clipping faults traced back
    /// to it. Chrome is read, so it is proportional; sizes and dates are compared
    /// down a column, so they keep the mono face.
    /// </summary>
    [AvaloniaFact]
    public void With_nothing_configured_the_interface_face_is_proportional()
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        var applied = Applied(window)?.Name ?? "";

        Assert.DoesNotContain("JetBrains", applied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mono", applied, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the face the numeric columns bind to, which is deliberately NOT
    /// touched by the font setting: choosing a typeface for the interface is a
    /// preference, whereas a column of figures that stops lining up is a defect.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("Georgia")]
    public void The_column_face_is_monospaced_whatever_the_setting_says(string? configured)
    {
        Configure(configured);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        var mono = ((Avalonia.Application.Current?.Resources ?? window.Resources)["AppMonoFamily"]
            as FontFamily)?.Name ?? "";

        Assert.Contains("JetBrains", mono, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blank is not a choice. The settings list uses an empty string
    /// for "follow the desktop", and treating that as a family name would ask
    /// Avalonia to resolve a font called "".</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_choice_is_not_a_font(string? configured)
    {
        Configure(configured);

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        // Blank means "no choice", which lands on the bundled interface face —
        // not on the desktop's, and not on a family literally named "".
        var applied = Applied(window)?.Name ?? "";

        Assert.NotEqual("Segoe UI", applied);
        Assert.Contains("Segoe UI Variable", applied, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **The desktop is ignored until it is asked for.** The bundled scheme is
    /// the base, so a machine with a bright Plasma theme still opens looking
    /// like Vaktari. This is the half that keeps the inversion from being a
    /// visible change for anyone who never opens Settings.
    /// </summary>
    [AvaloniaFact]
    public void The_desktop_palette_is_ignored_by_default()
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, Loud());

        Assert.Equal("#ff23232b", Colour(window, "ViewBackground"));
    }

    /// <summary>
    /// And the half that makes it worth doing: with the flag on, the desktop's
    /// colours reach the screen. Every one of these was computed and thrown
    /// away before — the reader ran, the derivations ran, and the reference
    /// scheme overwrote all of it.
    /// </summary>
    [AvaloniaFact]
    public void With_the_flag_on_the_desktop_palette_wins()
    {
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { FollowDesktopColours = true },
        });

        var window = new Window();
        ThemeApplier.Apply(window, Loud());

        Assert.Equal("#ff102030", Colour(window, "ViewBackground"));
        Assert.Equal("#ffddeeff", Colour(window, "ViewText"));
    }

    /// <summary>A palette nothing could be mistaken for.</summary>
    private static ThemePalette Loud() => new()
    {
        Colours = new Dictionary<string, string>
        {
            [ThemeRole.ViewBackground] = "#102030",
            [ThemeRole.ViewText] = "#DDEEFF",
            [ThemeRole.WindowBackground] = "#203040",
            [ThemeRole.WindowText] = "#DDEEFF",
        },
        FontFamily = "Segoe UI",
        IsDark = true,
    };

    private static string? Colour(Window window, string key)
        => ((Avalonia.Application.Current?.Resources ?? window.Resources)[key] as ISolidColorBrush)
            ?.Color.ToString()?.ToLowerInvariant();

    /// <summary>
    /// Apply runs on every palette read — startup, a desktop theme change and a
    /// settings save all reach it — so it has to be idempotent. A previous
    /// ordering bug in this method was only visible on the second call.
    /// </summary>
    [AvaloniaFact]
    public void Applying_twice_gives_the_same_answer()
    {
        Configure("Georgia");

        var window = new Window();
        ThemeApplier.Apply(window, Desktop("Segoe UI"));
        var first = Applied(window)?.Name;

        ThemeApplier.Apply(window, Desktop("Segoe UI"));

        Assert.Equal(first, Applied(window)?.Name);
        Assert.Equal("Georgia", Applied(window)?.Name);
    }

    // ---- the band under the pointer ----------------------------------------

    /// <summary>
    /// Ordering only, so it does not need the linearised curve: what is under
    /// test is which way a stripe points and how far, not a contrast ratio.
    /// </summary>
    private static double Lightness(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>
    /// What an UNBANDED row draws as while hovered, which is the reference the
    /// banded one has to differ from.
    ///
    /// The hover brush is translucent on most paths, so its own RGB is the wash
    /// and not a colour any row ever shows — comparing against it directly is
    /// what made the first version of this test report the stripe inverting.
    /// </summary>
    private static Color Over(Color src, Color dst)
    {
        var a = src.A / 255.0;

        return Color.FromRgb(
            (byte)(src.R * a + dst.R * (1 - a)),
            (byte)(src.G * a + dst.G * (1 - a)),
            (byte)(src.B * a + dst.B * (1 - a)));
    }

    /// <summary>
    /// **A hovered banded row lost its band completely.** The hover wash is
    /// translucent on purpose, but it is set on the same ContentPresenter
    /// Background the band is set on — so there was nothing beneath it to wash,
    /// and every other row dropped to the plain ground the moment the pointer
    /// crossed it. Reported as the darker line fading away entirely.
    ///
    /// Three things, and each fails a different way of getting it wrong:
    /// dropping the band (the bug), keeping it whole (defensible, but the
    /// pointer reads as having done nothing), and softening it past the plain
    /// ground so the stripe changes sides under the pointer.
    ///
    /// Both variants, because the band goes UP from the ground on a dark scheme
    /// and DOWN on a light one, and a fix that assumed one direction would draw
    /// the other backwards.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hovered_band_is_softened_rather_than_dropped(bool dark)
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            IsDark = dark,
        });

        var view = Resource(window, "ViewBackground");
        var band = Resource(window, "ViewAlternate");
        var hover = Resource(window, "HoverBackground");
        var hoveredBand = Resource(window, "HoverBackgroundBanded");

        var atRest = Lightness(band) - Lightness(view);
        var hovered = Lightness(hoveredBand) - Lightness(Over(hover, view));

        Assert.True(Math.Abs(atRest) > 0.001, "there is no band to hover over");

        // Not dropped: the hovered banded row is still not the hovered plain row.
        Assert.NotEqual(hover, hoveredBand);

        // And still on the same side of the plain row, so the stripe does not
        // invert under the pointer.
        Assert.True(Math.Sign(hovered) == Math.Sign(atRest),
            $"the band points {atRest:+0.000;-0.000} at rest and {hovered:+0.000;-0.000} hovered");

        // Softened: nearer the plain row than it was, but not all the way.
        Assert.True(Math.Abs(hovered) < Math.Abs(atRest),
            $"the band was not softened — {Math.Abs(hovered):0.0000} against {Math.Abs(atRest):0.0000}");

        Assert.True(Math.Abs(hovered) > Math.Abs(atRest) * 0.25,
            $"almost none of the band survived — {Math.Abs(hovered):0.0000} against {Math.Abs(atRest):0.0000}");
    }

    /// <summary>
    /// The hover wash is translucent, and the composite has to be opaque or the
    /// row draws whatever is behind the listing through itself.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_hovered_band_is_opaque(bool dark)
    {
        Configure(null);

        var window = new Window();
        ThemeApplier.Apply(window, new ThemePalette
        {
            Colours = new Dictionary<string, string>(),
            IsDark = dark,
        });

        Assert.Equal(255, Resource(window, "HoverBackgroundBanded").A);
    }
}
