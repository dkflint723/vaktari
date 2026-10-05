using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Which folders are reached over a network, for the per-listing work that
/// costs a round trip per call there (the git walk).
///
/// **Not the thumbnail rule, which calls anything starting with \\ remote**
/// (review H1): that would have taken the marks off a LOCAL repository opened
/// through \\?\ — the 0.11.2 fix GitExtendedPathTests guards — and its bare
/// StartsWith made "/mnt/nasty" remote because "/mnt/nas" was.
///
/// The drive type is faked, so the rules are tested on a machine with no
/// network drive and on Linux alike.
/// </summary>
public sealed class OverTheWireTests : IDisposable
{
    public OverTheWireTests()
        => OverTheWire.DriveTypeOverride = letter => letter == 'Z' ? DriveType.Network : DriveType.Fixed;

    public void Dispose() => OverTheWire.DriveTypeOverride = null;

    [Theory]
    [InlineData(@"\\server\share\repo")]
    [InlineData(@"\\?\UNC\server\share\repo")]
    [InlineData(@"\\.\UNC\server\share\repo")]
    [InlineData(@"\\wsl$\Fedora\home\me\repo")]
    [InlineData(@"\\wsl.localhost\Fedora\home\me\repo")]
    [InlineData(@"Z:\repo")]
    [InlineData(@"z:\repo")]
    [InlineData(@"\\?\Z:\repo")]
    public void These_are_over_the_wire(string path)
        => Assert.True(OverTheWire.IsRemote(path, []));

    [Theory]
    [InlineData(@"C:\repo")]
    [InlineData(@"\\?\C:\repo")]
    [InlineData(@"\\.\C:\repo")]
    [InlineData(@"\\?\Volume{0a1b2c3d-0000-0000-0000-100000000000}\repo")]
    [InlineData("")]
    public void These_are_local(string path)
        => Assert.False(OverTheWire.IsRemote(path, []));

    /// <summary>
    /// A discovered mount counts at a separator and not as a prefix of a
    /// name: "/mnt/nas" holds "/mnt/nas/x" and not "/mnt/nasty".
    /// </summary>
    [PosixFact]
    public void A_mount_root_holds_what_is_under_it_and_not_its_neighbour()
    {
        OverTheWire.DriveTypeOverride = null;

        Assert.True(OverTheWire.IsRemote("/mnt/nas", ["/mnt/nas"]));
        Assert.True(OverTheWire.IsRemote("/mnt/nas/photos", ["/mnt/nas"]));
        Assert.False(OverTheWire.IsRemote("/mnt/nasty/photos", ["/mnt/nas"]));
        Assert.False(OverTheWire.IsRemote("/home/me", ["/mnt/nas"]));
    }

    [WindowsFact]
    public void A_mapped_root_the_sidebar_found_holds_what_is_under_it()
    {
        OverTheWire.DriveTypeOverride = _ => DriveType.Fixed;

        Assert.True(OverTheWire.IsRemote(@"Y:\photos", [@"Y:\"]));
        Assert.False(OverTheWire.IsRemote(@"C:\photos", [@"Y:\"]));
    }
}
