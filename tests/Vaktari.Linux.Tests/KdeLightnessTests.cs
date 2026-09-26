using System.Reflection;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **Light or dark, as Plasma states it.** With "the desktop's own colours"
/// chosen, ThemeApplier takes light or dark from the palette alone and no
/// longer reads the stored lightness (SettingsPagesTests), so on Linux that
/// decision is KdeThemeProvider's: the luminance of the view background in
/// kdeglobals. Nothing tested it.
///
/// The provider reads a fixed path from XDG_CONFIG_HOME in its constructor;
/// the path is pointed at a temporary file afterwards rather than the
/// variable being changed, because other classes in this assembly read
/// XDG_CONFIG_HOME and run in parallel.
/// </summary>
public sealed class KdeLightnessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-kde").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private Vaktari.Core.ThemePalette? Read(string? file)
    {
        using var provider = new KdeThemeProvider();

        typeof(KdeThemeProvider)
            .GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(provider, file ?? Path.Combine(_root, "no-kdeglobals-here"));

        return provider.Read();
    }

    private string Scheme(string viewBackground)
    {
        var file = Path.Combine(_root, "kdeglobals");

        File.WriteAllLines(file,
        [
            "[Colors:View]",
            "BackgroundNormal=" + viewBackground,
            "ForegroundNormal=35,38,41",
            "[Colors:Window]",
            "BackgroundNormal=" + viewBackground,
        ]);

        return file;
    }

    /// <summary>A white view is light and a near-black one dark — the two a
    /// light and a dark Plasma scheme write.</summary>
    [Theory]
    [InlineData("255,255,255", false)]
    [InlineData("239,240,241", false)]
    [InlineData("20,22,24", true)]
    [InlineData("41,44,48", true)]
    public void The_view_background_decides(string background, bool dark)
        => Assert.Equal(dark, Read(Scheme(background))!.IsDark);

    /// <summary>
    /// **No kdeglobals is no palette**, not a dark one — which is what lets a
    /// stored lightness stand on a desktop that is not Plasma.
    /// </summary>
    [Fact]
    public void No_kdeglobals_is_no_palette()
        => Assert.Null(Read(file: null));
}
