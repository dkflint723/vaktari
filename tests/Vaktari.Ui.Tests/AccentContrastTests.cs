using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The accent has two jobs, and only one of them has to be readable.
///
/// **As text it failed AA in the dark theme, which is the default.** One value
/// served both filling a shape — the selected row's edge bar, the settings tab
/// marker, where contrast is a matter of taste — and colouring text, where
/// 4.5:1 is a requirement at the 11 to 12.5px these are actually set in.
/// #6d6df0 measures 3.41:1 on the chrome and 3.78:1 on the listing: the
/// settings status lines, the three link-style buttons, the "look-alike" chip,
/// the tile badge and "offline" were all under it.
///
/// AccentText is derived from whatever AccentColour turns out to be, because
/// the accent has three sources — the design scheme, the fallback scheme, and
/// whatever the desktop hands over — and a second literal beside the first
/// would have covered exactly one. So these apply the real theme and measure
/// what the application actually resolves, rather than reading a table.
/// </summary>
public sealed class AccentContrastTests
{
    private const double AA = 4.5;

    private static double Channel(byte v)
    {
        var s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c)
        => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        var (hi, lo) = x > y ? (x, y) : (y, x);

        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Applies a theme to a throwaway window and hands back the resolved
    /// colours, then closes it.
    ///
    /// **Closing matters more than it looks.** A Window builds a Compositor,
    /// and one left open is torn down later on whatever thread xunit happens to
    /// be on — which surfaces as "the calling thread cannot access this object"
    /// in the CLEANUP of some unrelated test that merely ran afterwards. These
    /// tests leaked six of them, and that is what CI caught.
    /// </summary>
    private static Dictionary<string, Color> Under(Vaktari.Core.ThemePalette palette,
                                                   params string[] keys)
    {
        var window = new Window();

        try
        {
            ThemeApplier.Apply(window, palette);

            return keys.ToDictionary(key => key, key => Resolved(window, key));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Turns on "follow the desktop's colours" for the length of a test.
    ///
    /// **Without this the desktop palette below was never applied**, because
    /// the Mapping loop sits behind that setting and the setting is off by
    /// default — so the test handed over a pathological accent, measured the
    /// built-in scheme instead, and passed for the wrong reason.
    /// </summary>
    private sealed class FollowingTheDesktop : IDisposable
    {
        private readonly Vaktari.Core.Settings.SettingsState _before
            = Vaktari.Ui.Settings.AppSettings.Current;

        public FollowingTheDesktop()
            => Vaktari.Ui.Settings.AppSettings.Apply(_before with
            {
                Views = _before.Views with { FollowDesktopColours = true },
            });

        public void Dispose() => Vaktari.Ui.Settings.AppSettings.Apply(_before);
    }

    private static Color Resolved(Window window, string key)
    {
        Assert.True(Avalonia.Application.Current!.Resources.TryGetResource(key, null, out var value),
                    $"{key} is not in the applied theme");

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);

        return brush.Color;
    }

    /// <summary>The surfaces accent text can land on.</summary>
    private static readonly string[] Grounds =
        ["ChromeBrush", "ViewBackground", "AppBackground", "PanelBackground"];

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Accent_text_is_readable_on_every_surface(bool dark)
    {
        var colours = Under(Palette(dark), [.. Grounds, "AccentText"]);

        foreach (var key in Grounds)
        {
            var ratio = Contrast(colours["AccentText"], colours[key]);

            Assert.True(ratio >= AA,
                $"AccentText on {key} is {ratio:0.00}:1 in the {(dark ? "dark" : "light")} theme, "
                + $"under AA's {AA}:1");
        }
    }

    /// <summary>
    /// **A desktop accent is somebody else's colour and can be anything.** This
    /// is the case a hand-written second value could never have covered: a
    /// Plasma accent that is far too dark to read on a dark listing still has
    /// to come out legible.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#3b3bd0")]   // dark violet — the shape of the original fault
    [InlineData("#7a4a00")]   // dark brown
    [InlineData("#000000")]   // the pathological case
    public void A_desktop_accent_is_made_readable_too(string hex)
    {
        using var following = new FollowingTheDesktop();

        var colours = Under(
            new Vaktari.Core.ThemePalette
            {
                IsDark = true,
                Colours = new Dictionary<string, string> { [Vaktari.Core.ThemeRole.Accent] = hex },
            },
            [.. Grounds, "AccentText", "AccentColour"]);

        // **That the desktop's value actually landed**, first. Without this the
        // test cannot tell "made readable" from "never applied": with the
        // mapping switched off it went on measuring the built-in scheme's own
        // accent, which was readable all along, and passed.
        Assert.Equal(Color.Parse(hex), colours["AccentColour"]);

        foreach (var key in Grounds)
            Assert.True(Contrast(colours["AccentText"], colours[key]) >= AA,
                        $"a desktop accent of {hex} stayed unreadable on {key}");
    }

    /// <summary>
    /// The fill accent is deliberately NOT held to this: it colours shapes, not
    /// words. Stated so the split reads as a decision rather than an oversight.
    /// </summary>
    [AvaloniaFact]
    public void The_fill_accent_is_left_as_the_designer_chose_it()
    {
        var colours = Under(Palette(dark: true), "AccentColour", "AccentText");

        Assert.Equal(Color.Parse("#6d6df0"), colours["AccentColour"]);
        Assert.NotEqual(colours["AccentColour"], colours["AccentText"]);
    }

    /// <summary>
    /// And nothing colours text with the fill accent any more. The fault was
    /// which resource each label named, so measuring colours alone would pass
    /// with every one of them still pointing at the wrong one.
    /// </summary>
    [Fact]
    public void No_markup_colours_text_with_the_fill_accent()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(Repo(), "src", "Vaktari.Ui"), "*.axaml",
                            SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, i) => (File: Path.GetFileName(file), Line: i + 1, Text: line)))
            .Where(x => x.Text.Contains("Foreground=\"{DynamicResource AccentColour}\"",
                                        StringComparison.Ordinal))
            .Select(x => $"{x.File}:{x.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "these colour text with the fill accent, which fails AA in the dark theme: "
            + string.Join(", ", offenders));
    }

    private static Vaktari.Core.ThemePalette Palette(bool dark)
        => new()
        {
            IsDark = dark,
            Colours = new Dictionary<string, string>(),
        };

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
