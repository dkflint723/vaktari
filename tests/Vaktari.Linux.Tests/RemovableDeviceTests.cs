using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Which mounted volumes you can unplug.
///
/// **Removability was decided from the mount POINT alone.** Anything under
/// /media or /run/media was removable and everything else was fixed — which is
/// where the automounters put things, so it is right most of the time and wrong
/// in exactly the case that matters. A USB disk given a stable /mnt/backup line
/// in fstab, or mounted by hand, came out a fixed disk with no eject button,
/// and the eject command refuses before an ejector is even reached. The only
/// safe way to unplug it was a terminal.
///
/// The decision is separated from the reading here, so the rule can be checked
/// without a /sys to read — and so it can be checked on Windows, where these
/// tests otherwise could not run at all.
/// </summary>
public sealed class RemovableDeviceTests
{
    private static DeviceTraits Fixed => new(Removable: false, OnUsbBus: false);
    private static DeviceTraits Stick => new(Removable: true, OnUsbBus: true);
    private static DeviceTraits Enclosure => new(Removable: false, OnUsbBus: true);

    /// <summary>The case the finding is about.</summary>
    [Fact]
    public void A_usb_disk_mounted_by_hand_can_still_be_ejected()
        => Assert.True(BlockDevices.IsRemovable("/mnt/backup", Stick));

    /// <summary>
    /// **Many USB enclosures report removable=0**, because the DRIVE inside is
    /// not removable — the enclosure is. The bus is the answer there.
    /// </summary>
    [Fact]
    public void A_disk_in_a_usb_enclosure_counts_as_removable()
        => Assert.True(BlockDevices.IsRemovable("/mnt/backup", Enclosure));

    [Fact]
    public void An_internal_disk_is_still_fixed()
        => Assert.False(BlockDevices.IsRemovable("/mnt/data", Fixed));

    /// <summary>
    /// The two sources are ORed and never traded. An automounter having put a
    /// volume under /run/media is itself evidence, and demoting on a sysfs read
    /// that came back wrong would take away an eject button people already have.
    /// </summary>
    [Theory]
    [InlineData("/run/media/flint/STICK")]
    [InlineData("/media/usb0")]
    public void The_path_rule_still_stands_on_its_own(string mountPoint)
    {
        Assert.True(BlockDevices.IsRemovable(mountPoint, Fixed));
        Assert.True(BlockDevices.IsRemovable(mountPoint, traits: null));
    }

    /// <summary>
    /// On a live USB or an SD-card root the flag reads 1, and offering to eject
    /// the running root is never what anybody meant.
    /// </summary>
    [Fact]
    public void The_running_root_is_never_offered()
        => Assert.False(BlockDevices.IsRemovable("/", Stick));

    /// <summary>Nothing to read is no answer, not a "no" — the path rule is
    /// what was there before and still applies.</summary>
    [Fact]
    public void An_unreadable_device_falls_back_to_the_path()
    {
        Assert.False(BlockDevices.IsRemovable("/mnt/backup", traits: null));
        Assert.True(BlockDevices.IsRemovable("/media/backup", traits: null));
    }

    // ---- finding the disk behind a partition ---------------------------------

    /// <summary>
    /// **The flag lives on the DISK, never the partition.**
    /// /sys/class/block/sdb1 has no "removable" at all, so asking about the
    /// partition answers nothing — which is how a correct read could still say
    /// "fixed".
    /// </summary>
    [Theory]
    [InlineData("/dev/sdb1", "sdb")]
    [InlineData("/dev/sda", "sda")]
    [InlineData("/dev/nvme0n1p2", "nvme0n1")]
    [InlineData("/dev/nvme0n1", "nvme0n1")]
    [InlineData("/dev/mmcblk0p1", "mmcblk0")]
    [InlineData("/dev/mmcblk0", "mmcblk0")]
    public void The_disk_behind_a_partition_is_found(string source, string disk)
        => Assert.Equal(disk, BlockDevices.DiskFor(source));

    /// <summary>A network mount or a tmpfs has no block device to ask about.</summary>
    [Theory]
    [InlineData("server:/export")]
    [InlineData("tmpfs")]
    [InlineData("")]
    public void Something_that_is_not_a_device_has_no_disk(string source)
        => Assert.Null(BlockDevices.DiskFor(source));

    // ---- reading the bus off the sysfs link ----------------------------------

    /// <summary>
    /// A USB disk's resolved sysfs path runs through a USB controller, and no
    /// internal SATA or NVMe device does.
    /// </summary>
    [Fact]
    public void A_usb_path_is_recognised()
        => Assert.True(BlockDevices.OnUsbBus(
            "/sys/devices/pci0000:00/0000:00:14.0/usb2/2-1/2-1:1.0/host6/target6:0:0/6:0:0:0/block/sdb"));

    [Theory]
    [InlineData("/sys/devices/pci0000:00/0000:00:17.0/ata1/host0/target0:0:0/0:0:0:0/block/sda")]
    [InlineData("/sys/devices/pci0000:00/0000:00:1d.0/0000:03:00.0/nvme/nvme0/nvme0n1")]
    [InlineData(null)]
    public void An_internal_path_is_not(string? resolved)
        => Assert.False(BlockDevices.OnUsbBus(resolved));

    /// <summary>
    /// Segment-wise, not a substring search: a disk mounted at a path that
    /// merely contains "usb" is not on the USB bus, and calling it removable
    /// would offer to eject an internal drive.
    /// </summary>
    [Fact]
    public void A_path_that_merely_contains_the_word_is_not_a_bus()
        => Assert.False(BlockDevices.OnUsbBus("/sys/devices/pci0000:00/ata1/notusbatall/block/sda"));
}
