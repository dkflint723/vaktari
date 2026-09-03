using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Giving a mapped network drive back.
///
/// **There was no way to.** A mapped drive's row offered Open, Open in a new
/// tab, Pin and Properties; Eject is for media you take out, and the remote
/// list deliberately holds only the letterless connections Vaktari made itself
/// — so the only way to take Z: off the sidebar was `net use /delete` in a
/// console. Explorer has Disconnect on exactly this row.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MappedDriveDisconnectTests
{
    /// <summary>
    /// **A drive letter is spelled "Z:", not "Z:\".** A place's Path is the
    /// root the rest of the application navigates to, which carries the
    /// trailing separator; the connection table is keyed on the device name
    /// without it, and the call answers ERROR_NOT_CONNECTED for the other
    /// spelling — a disconnect that reports failure while doing nothing.
    /// </summary>
    [Theory]
    [InlineData(@"Z:\", "Z:")]
    [InlineData("Z:", "Z:")]
    [InlineData("Z:/", "Z:")]
    public void A_drive_root_is_named_the_way_the_connection_table_names_it(
        string path, string expected)
        => Assert.Equal(expected, WindowsRemoteMounts.CancelTarget(path).Name);

    /// <summary>
    /// **And it is taken out of the sign-in profile too.** "Reconnect at
    /// sign-in" is the default in Explorer's own Map Network Drive dialog, so a
    /// mapping made the ordinary way is persistent — tearing down only the
    /// session makes the drive vanish from the sidebar and come back tomorrow,
    /// which is not what anybody means by disconnect.
    /// </summary>
    [Fact]
    public void A_mapped_drive_is_taken_out_of_the_sign_in_profile()
        => Assert.Equal(
            ("Z:", Native.CONNECT_UPDATE_PROFILE),
            WindowsRemoteMounts.CancelTarget(@"Z:\"));

    /// <summary>
    /// A letterless connection is one Vaktari made and never persisted, so
    /// there is nothing in the profile to take out.
    /// </summary>
    [Fact]
    public void A_connection_without_a_letter_has_no_profile_entry_to_clear()
    {
        var (name, flags) = WindowsRemoteMounts.CancelTarget(@"\\nas\media");

        Assert.Equal(@"\\nas\media", name);
        Assert.Equal(0u, flags);
    }

    /// <summary>
    /// The verb reaches the place. A row can only offer Disconnect if the
    /// provider says so, and nothing an object can be asked says whether it
    /// does — the line is inside the method that builds a drive.
    /// </summary>
    [WindowsFact]
    public void A_network_drive_is_built_with_the_verb_on_it()
    {
        var body = RepoSource.Body(
            RepoSource.Read("src", "Vaktari.Windows", "WindowsPlacesProvider.cs"),
            "private static Place BuildDrive");

        Assert.Contains("CanDisconnect = drive.DriveType is DriveType.Network", body);
    }
}
