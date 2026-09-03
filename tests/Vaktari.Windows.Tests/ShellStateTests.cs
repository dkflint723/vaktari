using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Whether the desktop opens things on one click.
///
/// **Windows was never asked.** The palette hardcoded null on the strength of a
/// comment claiming the setting lived in an undocumented blob under
/// Explorer\Advanced — the wrong key, and a structure that is in fact
/// shlobj_core.h's SHELLSTATE. So the "Whatever the desktop is set to" option
/// collapsed to double on Windows however Folder Options was set, while the
/// same option worked on KDE.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellStateTests
{
    /// <summary>
    /// The value off a real Windows 11 profile, byte for byte. Nine of its bits
    /// have DWORD mirrors under Explorer\Advanced and every one agrees, which is
    /// what makes this a fixture rather than a guess: bit 0 against Hidden=1,
    /// bit 1 against HideFileExt=0 (inverted — the flag is fShowExtensions),
    /// bit 11 against ShowInfoTip=1, bit 15 against ShowSuperHidden=0. cbSize is
    /// 36 and matches the length; the version field at offset 24 is 19.
    ///
    /// This profile is set to double click, so bit 5 is set.
    /// </summary>
    private static byte[] Real() =>
    [
        0x24, 0x00, 0x00, 0x00, 0x37, 0x38, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0x01, 0, 0, 0, 0x13, 0, 0, 0, 0, 0, 0, 0, 0x62, 0, 0, 0,
    ];

    [Fact]
    public void A_machine_set_to_double_click_reads_as_double()
        => Assert.False(ShellState.OpensOnSingleClick(Real()));

    /// <summary>
    /// One bit apart from the fixture above, and the opposite answer — which is
    /// what a decoder reading the wrong offset, the wrong bit, or the right bit
    /// backwards cannot give.
    /// </summary>
    [Fact]
    public void Clearing_one_bit_is_the_whole_difference()
    {
        var blob = Real();
        blob[4] &= 0xDF;

        Assert.True(ShellState.OpensOnSingleClick(blob));
    }

    /// <summary>
    /// The null contract, which the old comment was right about even though its
    /// facts were wrong: a decoder that helpfully answers false on garbage
    /// asserts a preference nobody expressed.
    /// </summary>
    [Fact]
    public void An_absent_value_says_nothing()
        => Assert.Null(ShellState.OpensOnSingleClick(null));

    [Fact]
    public void A_truncated_blob_says_nothing()
        => Assert.Null(ShellState.OpensOnSingleClick([0x24, 0x00, 0x00, 0x00]));

    /// <summary>A blob that disagrees with its own cbSize is not this layout,
    /// which is how a future change to it degrades to today's behaviour rather
    /// than to a confident wrong answer.</summary>
    [Fact]
    public void A_blob_that_disagrees_about_its_own_size_says_nothing()
    {
        var blob = Real();
        blob[0] = 0x20;

        Assert.Null(ShellState.OpensOnSingleClick(blob));
    }

    /// <summary>The one that pins the wiring: the palette has to report what the
    /// shell state holds, rather than the null it used to.</summary>
    [Fact]
    public void The_palette_reports_what_the_shell_state_holds()
    {
        var live = ShellState.OpensOnSingleClick();

        if (live is null) return;

        using var provider = new WindowsThemeProvider();

        Assert.Equal(live, provider.Read()!.SingleClick);
    }
}
