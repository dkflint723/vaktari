using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The /proc/mounts parse, tested at last.
///
/// These run on any machine — including the Windows desktop this project is
/// developed on — because the rules take their input as an argument instead of
/// reading a literal path. That was the point of splitting them out: roughly a
/// hundred lines of escape handling, snap filtering and optical detection had
/// never been executed by a test on any platform.
/// </summary>
public sealed class MountTableTests
{
    /// <summary>
    /// **Left to right, once.** Unescaping the backslash first would turn the
    /// remaining escapes into literal backslashes followed by digits, so a
    /// folder genuinely named with a backslash and one containing a space would
    /// resolve to the same path — and eject matches on that path.
    /// </summary>
    [Theory]
    [InlineData("/mnt/my\\040drive", "/mnt/my drive")]
    [InlineData("/mnt/tab\\011here", "/mnt/tab\there")]
    [InlineData("/mnt/plain", "/mnt/plain")]
    [InlineData("/mnt/back\\134slash", "/mnt/back\\slash")]
    public void Escapes_are_unwound_in_one_pass(string raw, string expected)
        => Assert.Equal(expected, MountTable.Unescape(raw));

    /// <summary>
    /// The case a chained Replace gets wrong: an escaped backslash immediately
    /// followed by what would read as another escape. Unescaping \134 first
    /// yields "\040" and a second pass turns it into a space — a different
    /// directory than the one actually mounted.
    /// </summary>
    [Fact]
    public void An_escaped_backslash_does_not_swallow_what_follows()
        => Assert.Equal("/mnt/\\040", MountTable.Unescape("/mnt/\\134040"));

    private const string Realistic = """
        sysfs /sys sysfs rw,nosuid 0 0
        proc /proc proc rw,nosuid 0 0
        /dev/nvme0n1p2 / ext4 rw,relatime 0 0
        /dev/nvme0n1p1 /boot/efi vfat rw,relatime 0 0
        /dev/loop0 /snap/core22/1122 squashfs ro,nodev 0 0
        /dev/loop1 /var/lib/snapd/snap/firefox/3836 squashfs ro,nodev 0 0
        tmpfs /run/user/1000 tmpfs rw,nosuid 0 0
        /dev/sdb1 /run/media/flint/STICK vfat rw,nosuid 0 0
        //server/share /mnt/work cifs rw,relatime 0 0
        gvfsd-fuse /run/user/1000/gvfs fuse.gvfsd-fuse rw,nosuid 0 0
        """;

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void The_signature_keeps_real_volumes_and_shares()
    {
        var signature = MountTable.Signature(Lines(Realistic));

        Assert.Contains("/dev/sdb1|/run/media/flint/STICK|vfat", signature);
        Assert.Contains("/dev/nvme0n1p2|/|ext4", signature);
        Assert.Contains("//server/share|/mnt/work|cifs", signature);
    }

    /// <summary>
    /// **Filtering happens before signing, and that is a correctness rule.**
    /// snapd and flatpak mount and unmount squashfs loops continuously; an
    /// unfiltered signature would change several times a minute and rebuild the
    /// sidebar each time, for rows the listing then discards. The watcher would
    /// be a busy loop that never showed anything.
    /// </summary>
    [Fact]
    public void Snap_churn_never_reaches_the_signature()
    {
        var before = MountTable.Signature(Lines(Realistic));

        var after = MountTable.Signature(Lines(Realistic + """

            /dev/loop9 /snap/core22/1150 squashfs ro,nodev 0 0
            """));

        Assert.Equal(before, after);
    }

    [Fact]
    public void Pseudo_filesystems_and_gvfs_control_mounts_are_left_out()
    {
        var signature = MountTable.Signature(Lines(Realistic));

        Assert.DoesNotContain("sysfs", signature);
        Assert.DoesNotContain("tmpfs", signature);
        Assert.DoesNotContain("gvfs", signature);
        Assert.DoesNotContain("/boot/efi", signature);
    }

    /// <summary>
    /// **A data CD used to be invisible on Linux.** iso9660 was excluded
    /// wholesale by a rule aimed at snap images — which are squashfs, and were
    /// already caught twice over by the loop-device and fstype checks. The
    /// giveaway was a branch a few lines later that detects iso9660 as optical
    /// media and could never run.
    /// </summary>
    [Fact]
    public void A_data_disc_is_a_real_volume()
    {
        Assert.True(MountTable.IsRealVolume("/dev/sr0", "/run/media/flint/DISC", "iso9660"));

        var signature = MountTable.Signature([
            "/dev/sr0 /run/media/flint/AUDIO_CD iso9660 ro,nosuid 0 0",
        ]);

        Assert.Contains("/dev/sr0", signature);
    }

    [Fact]
    public void A_stick_arriving_changes_the_signature()
    {
        var before = MountTable.Signature([
            "/dev/nvme0n1p2 / ext4 rw,relatime 0 0",
        ]);

        var after = MountTable.Signature([
            "/dev/nvme0n1p2 / ext4 rw,relatime 0 0",
            "/dev/sdb1 /run/media/flint/STICK vfat rw,nosuid 0 0",
        ]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void The_order_lines_appear_in_does_not_matter()
    {
        var one = MountTable.Signature([
            "/dev/nvme0n1p2 / ext4 rw 0 0",
            "/dev/sdb1 /run/media/flint/STICK vfat rw 0 0",
        ]);

        var other = MountTable.Signature([
            "/dev/sdb1 /run/media/flint/STICK vfat rw 0 0",
            "/dev/nvme0n1p2 / ext4 rw 0 0",
        ]);

        Assert.Equal(one, other);
    }

    /// <summary>
    /// The device behind a mount point is what every eject verb takes, and it
    /// is read fresh rather than remembered — a list built minutes ago can name
    /// a device since renumbered onto other hardware.
    /// </summary>
    [Fact]
    public void The_device_behind_a_mount_point_is_found_by_its_unescaped_name()
    {
        var lines = Lines(Realistic).Append("/dev/sdc1 /run/media/flint/MY\\040DISK vfat rw 0 0");

        Assert.Equal("/dev/sdb1", MountTable.DeviceFor(lines, "/run/media/flint/STICK"));
        Assert.Equal("/dev/sdc1", MountTable.DeviceFor(lines, "/run/media/flint/MY DISK"));
        Assert.Null(MountTable.DeviceFor(lines, "/run/media/flint/GONE"));
    }

    [Fact]
    public void A_short_or_ragged_line_is_ignored_rather_than_throwing()
    {
        var signature = MountTable.Signature(["", "garbage", "/dev/sdb1 /mnt"]);

        Assert.Equal("", signature);
    }
}
