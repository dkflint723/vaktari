using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The parts of safe removal that carry the bugs: which volumes go with which
/// device, what the system's refusal is turned into, and whether the control
/// codes are the ones Windows documents.
///
/// The three ioctl wrappers around these are a handful of lines each and need
/// real hardware; everything decidable is decided here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEjectorTests
{
    /// <summary>
    /// **Derived, not transcribed — and this proves the derivation.** Writing
    /// 0x002D4808 by hand is one typo away from sending a different command to
    /// a storage device, a mistake that shows up only on hardware and only as a
    /// meaningless error code.
    /// </summary>
    [Fact]
    public void The_control_codes_are_the_ones_windows_documents()
    {
        Assert.Equal(0x00090018u, Native.FSCTL_LOCK_VOLUME);
        Assert.Equal(0x00090020u, Native.FSCTL_DISMOUNT_VOLUME);
        Assert.Equal(0x002D1080u, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER);
        Assert.Equal(0x002D4804u, Native.IOCTL_STORAGE_MEDIA_REMOVAL);
        Assert.Equal(0x002D4808u, Native.IOCTL_STORAGE_EJECT_MEDIA);
    }

    private static IReadOnlyList<(string, uint, DriveType)> Probed =>
    [
        ("C:", 0u, DriveType.Fixed),
        ("E:", 3u, DriveType.Removable),
        ("F:", 3u, DriveType.Removable),
        ("Z:", 9u, DriveType.Removable),
    ];

    /// <summary>
    /// **Every volume on the device, or the eject cannot work.** A sibling
    /// partition left mounted vetoes the removal, and half a device cannot be
    /// unplugged — so ejecting E: must quiesce F: as well.
    /// </summary>
    [Fact]
    public void Every_volume_on_the_same_device_goes_together()
    {
        var letters = WindowsEjector.SiblingsOf("E:", Probed);

        Assert.Equal(["E:", "F:"], letters);
    }

    [Fact]
    public void A_volume_alone_on_its_device_takes_nothing_with_it()
        => Assert.Equal(["Z:"], WindowsEjector.SiblingsOf("Z:", Probed));

    /// <summary>A device Windows would not identify still gets its own volume
    /// attempted, rather than nothing at all.</summary>
    [Fact]
    public void An_unidentified_drive_falls_back_to_itself()
        => Assert.Equal(["Q:"], WindowsEjector.SiblingsOf("Q:", Probed));

    /// <summary>
    /// **A device instance path must never reach the status bar.**
    /// PNP_VetoOutstandingOpen is the most common refusal by far, and its
    /// "name" is a string like USBSTOR\Disk&amp;Ven_SanDisk\4C530001… — showing
    /// that to someone asking why their stick will not eject is worse than
    /// showing nothing at all.
    /// </summary>
    [Fact]
    public void An_outstanding_handle_is_explained_without_the_device_path()
    {
        var said = WindowsEjector.ExplainVeto(
            Native.PnpVetoType.OutstandingOpen,
            @"USBSTOR\Disk&Ven_SanDisk&Prod_Cruzer\4C530001120523103232",
            "E:");

        Assert.DoesNotContain("USBSTOR", said);
        Assert.Contains("E:", said);
        Assert.Contains("close it", said);
    }

    /// <summary>The two veto types that genuinely name an application do use
    /// the name — that is the one case where it helps.</summary>
    [Fact]
    public void An_application_that_can_be_named_is_named()
    {
        Assert.Contains(
            "Photos",
            WindowsEjector.ExplainVeto(Native.PnpVetoType.WindowsApp, "Photos", "E:"));

        Assert.Contains(
            "Windows Search",
            WindowsEjector.ExplainVeto(Native.PnpVetoType.WindowsService, "Windows Search", "E:"));
    }

    /// <summary>A veto type that names an app but carries no name still has to
    /// say something useful.</summary>
    [Fact]
    public void A_nameless_refusal_still_reads_as_a_sentence()
    {
        var said = WindowsEjector.ExplainVeto(Native.PnpVetoType.WindowsApp, null, "E:");

        Assert.Contains("E:", said);
        Assert.DoesNotContain("  ", said);
    }

    /// <summary>
    /// Every veto type produces a sentence, including ones this code has never
    /// seen — the enum comes from the system, not from us, and a value we do
    /// not recognise must still be explainable.
    ///
    /// Taken as an int because the veto enum is internal to the platform
    /// assembly and a public xunit theory cannot name it in its signature.
    /// </summary>
    [Theory]
    [InlineData(0)]  // TypeUnknown
    [InlineData(1)]  // LegacyDevice
    [InlineData(6)]  // Device
    [InlineData(7)]  // Driver
    [InlineData(10)] // NonDisableable
    [InlineData(12)] // InsufficientRights
    [InlineData(99)] // something this build has never heard of
    public void Every_refusal_says_something(int veto)
    {
        var said = WindowsEjector.ExplainVeto((Native.PnpVetoType)veto, null, "E:");

        Assert.False(string.IsNullOrWhiteSpace(said));
        Assert.Contains("E:", said);
    }

    [Fact]
    public void A_device_list_splits_on_its_nulls()
    {
        var paths = WindowsEjector.MultiString("\\\\?\\one\0\\\\?\\two\0\0").ToList();

        Assert.Equal(["\\\\?\\one", "\\\\?\\two"], paths);
    }
}
