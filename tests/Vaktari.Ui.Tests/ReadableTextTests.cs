using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Text that has to be read, measured against the surface it is read on.
///
/// **Dim text failed AA on the window chrome** — 4.44:1, five hundredths under,
/// on the surface carrying the column headers, the status bar, the inactive tab
/// titles and the breadcrumb ancestors. An earlier pass raised it for the
/// listing and the panel and did not check the chrome.
///
/// **And two windows asked for a background nothing defined**, so the theme
/// cards in settings and the conflict dialog's panel drew with whatever
/// happened to be behind them.
///
/// The ratios are computed, so these say whether the colour is readable rather
/// than whether somebody changed it.
/// </summary>
public sealed class ReadableTextTests
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

    private static Color Resolved(string key)
    {
        Assert.True(Avalonia.Application.Current!.Resources.TryGetResource(key, null, out var value),
                    $"{key} is not in the applied theme");

        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Vaktari.Core.ThemePalette Palette(bool dark)
        => new() { IsDark = dark, Colours = new Dictionary<string, string>() };

    /// <summary>Every surface dim text is drawn on.</summary>
    public static TheoryData<bool, string> Surfaces
    {
        get
        {
            var data = new TheoryData<bool, string>();

            foreach (var dark in new[] { true, false })
                foreach (var surface in new[]
                         { "AppBackground", "ChromeBrush", "PanelBackground", "ViewBackground" })
                    data.Add(dark, surface);

            return data;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Surfaces))]
    public void Dim_text_is_readable_on_every_surface(bool dark, string surface)
    {
        var window = new Window();

        try
        {
            ThemeApplier.Apply(window, Palette(dark));

            var ratio = Contrast(Resolved("ViewDimText"), Resolved(surface));

            Assert.True(ratio >= AA,
                $"ViewDimText on {surface} is {ratio:0.00}:1 in the "
                + $"{(dark ? "dark" : "light")} theme, under AA's {AA}:1");
        }
        finally
        {
            // A Window left open is torn down on whatever thread comes next,
            // which surfaces as a thread error in an unrelated test's cleanup.
            window.Close();
        }
    }

    /// <summary>
    /// **Two windows bound to a resource that did not exist**, so both drew
    /// with no background at all. A missing DynamicResource is silent — the
    /// binding simply produces nothing — which is why it survived.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_background_the_markup_asks_for_exists(bool dark)
    {
        var window = new Window();

        try
        {
            ThemeApplier.Apply(window, Palette(dark));

            foreach (var key in BackgroundKeysUsedInMarkup())
                Assert.True(
                    Avalonia.Application.Current!.Resources.TryGetResource(key, null, out _),
                    $"{key} is bound in the markup and defined nowhere, so it draws as nothing");
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<string> BackgroundKeysUsedInMarkup()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(Repo(), "src", "Vaktari.Ui"), "*.axaml", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file),
                         @"Background=""\{DynamicResource (\w+)\}"""))
                keys.Add(m.Groups[1].Value);

        return keys;
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }
}
