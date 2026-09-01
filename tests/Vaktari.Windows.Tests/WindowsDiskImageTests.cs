using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which files Windows offers to mount.
///
/// **The gate is a promise.** A Mount entry says the file will open, so a type
/// this machine cannot attach must not carry one — and the type that CAN be
/// attached but only with administrator rights is the sharpest case of all,
/// because it fails at the very end, after the person has already clicked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskImageTests
{
    private static readonly WindowsDiskImages Images = new();

    [Theory]
    [InlineData("holiday.iso")]
    [InlineData("HOLIDAY.ISO")]
    [InlineData("raw.img")]
    [InlineData("disc.udf")]
    public void An_image_windows_can_attach_is_offered(string name)
        => Assert.True(Images.CanMount(Path.Combine(Path.GetTempPath(), name)));

    /// <summary>
    /// **VHD and VHDX are excluded on purpose, and not for lack of a provider.**
    /// AttachVirtualDisk returns ERROR_PRIVILEGE_NOT_HELD for them without
    /// elevation, measured, with and without a permanent lifetime — and Vaktari
    /// never holds administrator rights. An entry that always fails for a whole
    /// file type is worse than no entry.
    /// </summary>
    [Theory]
    [InlineData("machine.vhd")]
    [InlineData("machine.vhdx")]
    public void A_virtual_hard_disk_is_not_offered(string name)
        => Assert.False(Images.CanMount(Path.Combine(Path.GetTempPath(), name)));

    /// <summary>
    /// These are disk images by any reasonable reading, and Vaktari already
    /// draws them with a disc icon — but Windows ships no provider for either,
    /// so there is nothing to call.
    /// </summary>
    [Theory]
    [InlineData("apple.dmg")]
    [InlineData("machine.qcow2")]
    [InlineData("game.nrg")]
    [InlineData("disc.bin")]
    public void An_image_windows_has_no_provider_for_is_not_offered(string name)
        => Assert.False(Images.CanMount(Path.Combine(Path.GetTempPath(), name)));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("")]
    public void An_ordinary_file_is_never_offered(string name)
        => Assert.False(Images.CanMount(name));

    /// <summary>
    /// Only the LAST extension counts, which is the right answer here and the
    /// wrong one for archives: an ISO inside a gzip is not an ISO the loop
    /// driver can read, and mounting it would mean silently decompressing
    /// gigabytes nobody asked for.
    /// </summary>
    [Theory]
    [InlineData("ubuntu.iso.gz")]
    [InlineData("ubuntu.iso.torrent")]
    public void A_compressed_or_partial_image_is_not_offered(string name)
        => Assert.False(Images.CanMount(Path.Combine(Path.GetTempPath(), name)));

    /// <summary>
    /// **The gate is nominal, and that is deliberate.** A folder named
    /// holiday.iso answers true here — the directory is excluded one level up,
    /// where the listing already knows what each row is, rather than by a
    /// Directory.Exists in a predicate the menu asks per item. A stat on every
    /// right-click is a cost paid always, to catch a case that costs one clean
    /// error message when it happens at all.
    /// </summary>
    [Fact]
    public void The_gate_reads_the_name_and_never_touches_the_disk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vaktari-fake.iso");

        // True for a path that does not exist at all: nothing was looked up.
        Assert.True(Images.CanMount(folder));
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>Nothing is mounted until something mounts it — the verb starts
    /// as Mount, not Unmount.</summary>
    [Fact]
    public void An_image_nobody_mounted_reports_no_mount()
        => Assert.Null(Images.MountOf(Path.Combine(Path.GetTempPath(), "never.iso")));
}
