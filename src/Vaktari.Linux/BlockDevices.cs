namespace Vaktari.Linux;

/// <summary>What the kernel says about the disk behind a mount.</summary>
internal readonly record struct DeviceTraits(bool Removable, bool OnUsbBus);

/// <summary>
/// Whether a mounted volume is one you can unplug.
///
/// **Removability was decided from the mount POINT alone.** Anything under
/// /media or /run/media was removable and everything else was fixed. That is
/// where the automounters put things, so it is right most of the time and wrong
/// in exactly the case that matters: a USB disk given a stable /mnt/backup line
/// in fstab, or mounted by hand, came out a fixed disk with no eject button —
/// and the eject command refuses before an ejector is even reached, so the only
/// safe way to unplug it was a terminal.
///
/// The kernel already knows. /sys/class/block/&lt;disk&gt;/removable reads 1 for a
/// stick or a card reader, and a disk reached over the USB bus is unpluggable
/// whatever that flag says — many USB enclosures report 0, because the DRIVE
/// inside them is not removable, the enclosure is.
///
/// **The two sources are ORed and never traded.** sysfs adds drives the path
/// rule misses; it never takes away one the path rule found. An automounter
/// having put a volume under /run/media is itself evidence, and demoting on a
/// sysfs read that came back wrong would remove an eject button people already
/// have and rely on.
///
/// Reading /sys is reading text. Unlike <c>new DriveInfo(mountPoint)</c> it
/// never touches the mounted filesystem, so it cannot block on a stick that was
/// physically yanked — the same hazard MountTable's header names.
/// </summary>
internal static class BlockDevices
{
    /// <summary>
    /// The decision, apart from the reading, so it can be checked without a
    /// /sys to read.
    /// </summary>
    internal static bool IsRemovable(string mountPoint, DeviceTraits? traits)
    {
        // On a live USB or an SD-card root the flag reads 1, and offering to
        // eject the running root is never what anybody meant. The Linux twin of
        // the system-drive refusal the Windows ejector already makes.
        if (mountPoint == "/") return false;

        return MountedByAutomounter(mountPoint)
               || (traits is { } t && (t.Removable || t.OnUsbBus));
    }

    /// <summary>The surviving path rule: where the automounters put things.
    /// Still evidence, just no longer the whole answer.</summary>
    internal static bool MountedByAutomounter(string mountPoint)
        => mountPoint.StartsWith("/run/media", StringComparison.Ordinal)
           || mountPoint.StartsWith("/media", StringComparison.Ordinal);

    /// <summary>
    /// Whether a sysfs device path runs through a USB controller.
    ///
    /// The resolved link for a USB disk contains a "usb" path segment —
    /// .../pci0000:00/0000:00:14.0/usb2/2-1/... — and no internal SATA or NVMe
    /// device does. Separated from the reading so the string rule is testable.
    /// </summary>
    internal static bool OnUsbBus(string? resolved)
        => resolved is not null
           && resolved.Split('/').Any(part => part.StartsWith("usb", StringComparison.Ordinal));

    /// <summary>
    /// The whole-disk name for a partition device: "/dev/sdb1" is on "sdb",
    /// "/dev/nvme0n1p2" on "nvme0n1", "/dev/mmcblk0p1" on "mmcblk0".
    ///
    /// The flag lives on the DISK, never the partition — /sys/class/block/sdb1
    /// has no "removable" at all — so asking about the partition answers
    /// nothing, which is how a correct sysfs read could still say "fixed".
    /// </summary>
    internal static string? DiskFor(string source)
    {
        if (!source.StartsWith("/dev/", StringComparison.Ordinal)) return null;

        var name = source[5..];

        if (name.Length == 0) return null;

        // nvme and mmcblk number their partitions with a 'p'; sd and hd just
        // append digits.
        if (name.StartsWith("nvme", StringComparison.Ordinal)
            || name.StartsWith("mmcblk", StringComparison.Ordinal))
        {
            var p = name.LastIndexOf('p');

            return p > 0 && name[(p + 1)..].All(char.IsAsciiDigit) ? name[..p] : name;
        }

        return name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') is { Length: > 0 } disk
            ? disk
            : name;
    }

    /// <summary>
    /// What /sys says about the disk behind a device, or null when there is
    /// nothing to read — a network mount, a tmpfs, or a kernel that does not
    /// expose it.
    /// </summary>
    internal static DeviceTraits? TraitsFor(string source)
    {
        if (DiskFor(source) is not { } disk) return null;

        var root = $"/sys/class/block/{disk}";

        if (!Directory.Exists(root)) return null;

        try
        {
            var flag = File.Exists($"{root}/removable")
                       && File.ReadAllText($"{root}/removable").Trim() == "1";

            // The symlink's target names the bus the device hangs off.
            var resolved = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName;

            return new DeviceTraits(flag, OnUsbBus(resolved));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable sysfs is no answer, not a "no" — the path rule still
            // applies, and it is the one that was there before.
            return null;
        }
    }
}
