using System.Runtime.Versioning;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Windows' own text-size setting, on its way to the window.
///
/// **Vaktari read the desktop's colours, accent, font face and click
/// behaviour, and not the one setting that says how big text should be.**
/// Settings &gt; Accessibility &gt; Text size writes a percentage to
/// <c>HKCU\Software\Microsoft\Accessibility\TextScaleFactor</c> — the same
/// number WinRT's <c>UISettings.TextScaleFactor</c> reports — and nothing in
/// this repository looked at it, so somebody who had already told Windows they
/// need text at 150% got a file manager at 100% and no control to change it.
///
/// The registry read itself is a machine fact and is not asserted here; the
/// conversion is the part with a decision in it, and the call site is checked
/// against the source, since a test cannot move somebody's accessibility
/// slider to find out whether it is being read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TextSizeFactorTests
{
    /// <summary>The slider's documented range, straight through.</summary>
    [Theory]
    [InlineData(100u, 1.0)]
    [InlineData(125u, 1.25)]
    [InlineData(225u, 2.25)]
    public void The_slider_percentage_becomes_a_multiplier(uint percent, double expected)
        => Assert.Equal(expected, WindowsThemeProvider.ScaleFromPercent(percent));

    /// <summary>
    /// **Nothing read is not an error, and it is not the ordinary case
    /// either.** Measured on the Windows 11 machine this was written on, with
    /// the slider untouched: <c>TextScaleFactor</c> is present under
    /// <c>HKCU\Software\Microsoft\Accessibility</c> and reads 0x64, so a
    /// default machine states 100 rather than staying silent. Null is what a
    /// Windows without the value gives, and what a failed read gives, since
    /// <c>Native.ReadDword</c> hands both over as null.
    ///
    /// It means the same as a number outside the range the setting is
    /// documented to write: the desktop did not state a text size, and the
    /// application's own default applies. Clamping an out-of-range value
    /// instead would take a number written by something other than the slider
    /// and act on it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(99u)]
    [InlineData(226u)]
    public void Anything_else_means_the_desktop_did_not_say(uint? percent)
        => Assert.Null(WindowsThemeProvider.ScaleFromPercent(percent));

    /// <summary>
    /// The value reaches the palette, from the key it is really written to, and
    /// that key is watched like the other two.
    ///
    /// **A source assertion because the alternative is a machine fact**: what
    /// <c>Read()</c> returns here depends on the slider on the machine running
    /// the suite, so asserting on it would pass or fail for reasons that have
    /// nothing to do with the code. Nor can a test move somebody's accessibility
    /// slider to see whether the watch fires. What can be checked is that the
    /// provider asks for the right value under the right key, puts the answer on
    /// the field the UI layer reads, and arms a watch on that key — without
    /// which a slider moved while Vaktari is open changes nothing until the next
    /// scheme change or settings save.
    /// </summary>
    [Fact]
    public void And_it_is_read_from_the_accessibility_key_onto_the_palette()
    {
        var source = RepoSource.Read("src", "Vaktari.Windows", "WindowsThemeProvider.cs");

        Assert.Contains(@"AccessibilityKey = @""Software\Microsoft\Accessibility""", source,
            StringComparison.Ordinal);

        Assert.Contains(
            @"TextScale = ScaleFromPercent(Native.ReadDword(AccessibilityKey, ""TextScaleFactor""))",
            source, StringComparison.Ordinal);

        Assert.Contains("Watch(AccessibilityKey);", source, StringComparison.Ordinal);
    }
}
