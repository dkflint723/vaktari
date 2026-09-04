using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Three colours nobody could see.
///
/// **The two oldest age shades were the same colour.** Six stops were spread
/// over a range of 1.25 and then clamped back to 1.0, so the fifth and sixth
/// both landed exactly on the dim colour — a year old and a decade old drawn
/// identically, and the ramp was five shades wearing six names.
///
/// **Row banding was judged in raw channel units**, which mean different things
/// at different lightnesses. Six values apart is legible at mid-grey and
/// invisible at near-black, so both dark schemes sailed past a sum-of-channels
/// threshold and banded at about 1.05:1 — the flat sheet the guard was written
/// to prevent, passing the guard.
///
/// **And the hints were dimmed with Opacity**, which threw away the one thing
/// ViewDimText is derived to guarantee.
///
/// These apply the real theme and measure what the application resolves, the
/// way AccentContrastTests does, rather than reading a table — the ramp has
/// three sources and a table would cover one.
/// </summary>
public sealed class ShadeAndBandTests
{
    private static double Channel(byte v)
    {
        var s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c)
        => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    /// <summary>Computed here rather than borrowed from the code under test: a
    /// fault in the application's own formula would be invisible to a test that
    /// asked the formula.</summary>
    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        var (hi, lo) = x > y ? (x, y) : (y, x);

        return (hi + 0.05) / (lo + 0.05);
    }

    private static Vaktari.Core.ThemePalette Palette(bool dark)
        => new() { IsDark = dark, Colours = new Dictionary<string, string>() };

    /// <summary>
    /// Applies a theme to a throwaway window and closes it. A Window builds a
    /// Compositor, and one left open is torn down later on whatever thread
    /// xunit happens to be on — which surfaces in the cleanup of some unrelated
    /// test that merely ran afterwards.
    /// </summary>
    private static T Under<T>(Vaktari.Core.ThemePalette palette, Func<Window, T> read)
    {
        var window = new Window();

        try
        {
            ThemeApplier.Apply(window, palette);

            return read(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Turns on "follow the desktop's colours" for the length of a test, and
    /// puts the setting back.
    ///
    /// **Without this a desktop palette is not applied at all** — the Mapping
    /// loop sits behind that setting, which is off by default, so a test that
    /// hands over a pathological desktop colour and then measures the result is
    /// measuring the built-in scheme and passing for the wrong reason.
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

    private static Color Resolved(string key)
    {
        Assert.True(
            Avalonia.Application.Current!.Resources.TryGetResource(key, null, out var value),
            $"{key} is not in the applied theme");

        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    // ---- the age ramp ------------------------------------------------------

    /// <summary>One date inside each of the six buckets the converter
    /// splits on: an hour, a day, a week, a month, a year, and older.</summary>
    private static IEnumerable<DateTimeOffset> OneOfEachAge()
    {
        var now = DateTimeOffset.Now;

        yield return now - TimeSpan.FromMinutes(2);
        yield return now - TimeSpan.FromHours(6);
        yield return now - TimeSpan.FromDays(3);
        yield return now - TimeSpan.FromDays(14);
        yield return now - TimeSpan.FromDays(100);
        yield return now - TimeSpan.FromDays(2000);
    }

    private static Color Shade(DateTimeOffset when)
        => Assert.IsAssignableFrom<ISolidColorBrush>(
            AgeConverters.Brush.Convert(
                when, typeof(IBrush), null, CultureInfo.InvariantCulture)).Color;

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_six_age_shades_are_six_different_colours(bool dark)
    {
        var shades = Under(Palette(dark), _ => OneOfEachAge().Select(Shade).ToList());

        Assert.Equal(6, shades.Distinct().Count());
    }

    /// <summary>
    /// The point of the last stop: "ancient" should recede FURTHER than
    /// ordinary secondary text, which is what the clamped arithmetic could
    /// never do — it stopped dead on the dim colour and stayed there.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void And_the_oldest_recedes_past_the_dim_colour(bool dark)
    {
        var (shades, ground, dim) = Under(Palette(dark), _ =>
            (OneOfEachAge().Select(Shade).ToList(),
             Resolved("ViewBackground"),
             Resolved("ViewDimText")));

        Assert.True(
            Contrast(shades[5], ground) < Contrast(dim, ground),
            $"the oldest shade is {Contrast(shades[5], ground):0.00}:1 against the listing, "
            + $"no further back than dim text at {Contrast(dim, ground):0.00}:1");
    }

    /// <summary>The freshest end is unchanged: it is ordinary text, and the
    /// ramp only ever moved away from it.</summary>
    [AvaloniaFact]
    public void The_freshest_shade_is_still_the_text_colour()
    {
        var (fresh, text) = Under(Palette(true),
            _ => (Shade(DateTimeOffset.Now - TimeSpan.FromMinutes(2)), Resolved("ViewText")));

        Assert.Equal(text, fresh);
    }

    // ---- row banding -------------------------------------------------------

    /// <summary>
    /// Low on purpose — banding is a reading aid rather than text, and stripes
    /// you notice are worse than no stripes. It only has to be a difference the
    /// eye can find following a row across a wide window, which 1.05:1 is not.
    /// </summary>
    private const double Band = 1.2;

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_banded_row_can_be_told_from_the_one_above_it(bool dark)
    {
        var (alt, view) = Under(Palette(dark),
            _ => (Resolved("ViewAlternate"), Resolved("ViewBackground")));

        var ratio = Contrast(alt, view);

        Assert.True(ratio >= Band,
            $"banding is {ratio:0.000}:1 in the {(dark ? "dark" : "light")} theme, under {Band}:1");
    }

    /// <summary>
    /// The case the channel-distance rule was actually wrong about: a desktop
    /// whose own alternate row is a couple of values from its view colour, at
    /// the dark end where a couple of values is nothing.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("#0f0f0f", "#151515")]
    [InlineData("#23232b", "#26262d")]
    [InlineData("#000000", "#000000")]
    public void Even_when_the_desktop_hands_over_one_nobody_could_see(string view, string alt)
    {
        using var following = new FollowingTheDesktop();

        var (band, ground) = Under(
            new Vaktari.Core.ThemePalette
            {
                IsDark = true,
                Colours = new Dictionary<string, string>
                {
                    [Vaktari.Core.ThemeRole.ViewBackground] = view,
                    [Vaktari.Core.ThemeRole.ViewAlternate] = alt,
                },
            },
            _ => (Resolved("ViewAlternate"), Resolved("ViewBackground")));

        Assert.True(Contrast(band, ground) >= Band,
            $"a desktop banding of {alt} on {view} stayed at {Contrast(band, ground):0.000}:1");
    }

    // ---- the hints ---------------------------------------------------------

    /// <summary>
    /// **Dimmed with Opacity, which took it under AA.** These are the
    /// instructions for the box beside them, read by whoever is least sure what
    /// to do next. ViewDimText is derived to clear 4.5:1 against the ground it
    /// sits on; multiplying it by an opacity throws exactly that away.
    /// </summary>
    [Fact]
    public void The_hints_are_dim_text_rather_than_dimmed_text()
    {
        var markup = System.Xml.Linq.XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        System.Xml.Linq.XNamespace avalonia = "https://github.com/avaloniaui";
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var hints = markup.Descendants(avalonia + "TextBlock")
            .Where(t => (string?)t.Attribute(x + "Name") == "PromptHint"
                        || (string?)t.Attribute("Text") == "Ctrl+F")
            .ToList();

        Assert.Equal(2, hints.Count);

        foreach (var hint in hints)
        {
            Assert.Null(hint.Attribute("Opacity"));
            Assert.Contains("ViewDimText", (string?)hint.Attribute("Foreground") ?? "");
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void And_that_colour_is_readable_where_the_hints_sit(bool dark)
    {
        var (dim, chrome, view) = Under(Palette(dark),
            _ => (Resolved("ViewDimText"), Resolved("AppBackground"), Resolved("ViewBackground")));

        Assert.True(Contrast(dim, chrome) >= 4.5,
                    $"hint text is {Contrast(dim, chrome):0.00}:1 on the prompt bar");

        Assert.True(Contrast(dim, view) >= 4.5,
                    $"hint text is {Contrast(dim, view):0.00}:1 on the listing");
    }
}
