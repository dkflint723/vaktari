using System.Runtime.Versioning;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The one thing Windows knows about a network that a share should repeat:
/// whether it is marked public.
///
/// **Nothing was said.** A share started at a café served the folder to the
/// café. The password stands between the room and the files now, but "this
/// folder" and "this folder, to this whole room" are different decisions,
/// and the second deserves a line. The active firewall profile is where
/// Windows keeps the answer, and netsh prints it — in the language of the
/// installation, so on a Windows in another language nothing is recognised
/// and nothing is said, which is the quiet side to fail on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CopypartyNetworkWarningTests : IDisposable
{
    public void Dispose() => WindowsCopyparty.ProfileOverride = null;

    [Theory]
    [InlineData("\r\nPublic Profile Settings:\r\n------------------------\r\nState                                 ON\r\n")]
    [InlineData("Domain Profile Settings:\r\nState ON\r\n\r\nPublic Profile Settings:\r\nState ON\r\n")]
    public void A_public_network_is_said(string netsh)
    {
        WindowsCopyparty.ProfileOverride = () => netsh;

        var said = new WindowsCopyparty().StartWarning();

        Assert.NotNull(said);
        Assert.Contains("public", said);
    }

    [Theory]
    [InlineData("\r\nPrivate Profile Settings:\r\nState                                 ON\r\n")]
    [InlineData("\r\nDomain Profile Settings:\r\nState                                 ON\r\n")]
    public void A_private_or_domain_network_is_not(string netsh)
    {
        WindowsCopyparty.ProfileOverride = () => netsh;

        Assert.Null(new WindowsCopyparty().StartWarning());
    }

    /// <summary>netsh missing, failing or silent says nothing — neither crying
    /// wolf nor stopping the share over a diagnostic.</summary>
    [Fact]
    public void When_windows_cannot_say_nothing_is_said()
    {
        WindowsCopyparty.ProfileOverride = () => null;

        Assert.Null(new WindowsCopyparty().StartWarning());
    }
}
