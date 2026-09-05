using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What an unscoped search covers on Windows, and what the box says it covers.
///
/// **It walked the fixed drives and called that "everywhere".** So a search
/// with the scope box unticked skipped the stick, the SD card and the external
/// disk — the drives whose layout somebody is least likely to remember, and so
/// the ones they are most likely to be searching — while the label beside the
/// box claimed the whole machine.
///
/// The rule is asked as a predicate rather than through DriveInfo, because
/// nobody can plug a CD-ROM into a test.
/// </summary>
[SupportedOSPlatform("windows")]
public class SearchReachTests
{
    /// <summary>The whole finding: removable drives are searched.</summary>
    [WindowsTheory]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Removable)]
    public void A_drive_you_can_open_is_searched(DriveType type)
        => Assert.True(WindowsSearchProvider.Searchable(type, ready: true));

    /// <summary>
    /// **A network drive is not**, and that is a decision rather than an
    /// omission: a mapped drive whose server has gone away blocks for the whole
    /// SMB timeout, and an unscoped walk would pay that with nothing on screen
    /// to say why. It is also what makes the label true — a mapped drive is on
    /// a server, not "on this machine".
    /// </summary>
    [WindowsFact]
    public void A_network_drive_is_not()
        => Assert.False(WindowsSearchProvider.Searchable(DriveType.Network, ready: true));

    /// <summary>And neither is an empty optical drive or a RAM disk.</summary>
    [WindowsTheory]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void And_neither_is_anything_else(DriveType type)
        => Assert.False(WindowsSearchProvider.Searchable(type, ready: true));

    /// <summary>
    /// A drive that is not ready is skipped whatever it is. An empty card
    /// reader is Removable and answers nothing but an exception.
    /// </summary>
    [WindowsTheory]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Removable)]
    public void A_drive_that_is_not_ready_is_skipped(DriveType type)
        => Assert.False(WindowsSearchProvider.Searchable(type, ready: false));

    /// <summary>
    /// And the box says what that adds up to, rather than "everywhere". The
    /// wording carries the network exclusion: a mapped drive is on a server.
    /// </summary>
    [WindowsFact]
    public void The_box_says_what_is_actually_covered()
    {
        var said = new WindowsSearchProvider().Everywhere;

        Assert.Equal("every drive on this machine", said);
        Assert.NotEqual("everywhere", said);
    }
}
