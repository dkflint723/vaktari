using Vaktari.Core.Places;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Volumes that are there but not mounted.
///
/// **/proc/mounts was the only source**, so a partition nobody had mounted did
/// not exist as far as the sidebar was concerned — and on a desktop that does
/// not automount, a stick plugged in never appeared at all. Dolphin lists every
/// volume, greyed, and mounts it when you click; the Place record has carried
/// an IsAvailable flag documented for exactly this since it was written, and
/// nothing had ever produced one.
///
/// Both sources go through seams, so these run on any machine — including one
/// with a single disk and nothing removable, which is where they would
/// otherwise be untestable.
/// </summary>
public sealed class UnmountedVolumeTests
{
    private static LinuxPlacesProvider Provider(string[] mounts, string[] devices, string[]? swaps = null)
        // Its own state directory, so a test never reads or writes the pins of
        // whoever is running it.
        => new(Directory.CreateTempSubdirectory("vaktari-places").FullName)
        {
            MountLines = () => mounts,
            FilesystemDevices = () => devices,
            SwapLines = () => swaps ?? [],
            VolumeLabels = () => new Dictionary<string, string>
            {
                ["/dev/sdb1"] = "STICK",
            },
        };

    private static async Task<IReadOnlyList<Place>> Devices(LinuxPlacesProvider provider)
    {
        var groups = await provider.GetPlacesAsync(CancellationToken.None);

        return groups.SingleOrDefault(g => g.Label.Equals("devices", StringComparison.OrdinalIgnoreCase))
                     ?.Places ?? [];
    }

    [Fact]
    public async Task An_unmounted_volume_is_listed_rather_than_dropped()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdb1"]));

        var stick = Assert.Single(places, p => p.Label == "STICK");

        Assert.False(stick.IsAvailable, "it should be listed dimmed, not as ready to open");
        Assert.Equal("", stick.Path);
    }

    /// <summary>A volume that IS mounted keeps its mount point and is not
    /// offered twice.</summary>
    [Fact]
    public async Task A_mounted_volume_is_not_listed_again()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0", "/dev/sdb1 /run/media/me/STICK vfat rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdb1"]));

        var stick = Assert.Single(places, p => p.Path == "/run/media/me/STICK");

        Assert.True(stick.IsAvailable);
        Assert.DoesNotContain(places, p => p.Path.Length == 0);
    }

    /// <summary>
    /// A loop device with a filesystem is a mounted disk image, which has its
    /// own row and its own way of going away. Offering to mount one would be a
    /// second, worse route to the same thing.
    /// </summary>
    [Fact]
    public async Task Loop_devices_are_left_alone()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/loop3"]));

        Assert.DoesNotContain(places, p => p.Id.Contains("loop", StringComparison.Ordinal));
    }

    /// <summary>Without a label it is named by its device, which is what
    /// Dolphin falls back to as well.</summary>
    [Fact]
    public async Task An_unlabelled_volume_is_named_by_its_device()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdc1"]));

        Assert.Contains(places, p => p.Label == "sdc1" && !p.IsAvailable);
    }

    /// <summary>
    /// The id carries the device, because mounting needs to name it — the path
    /// is deliberately empty until there is one.
    /// </summary>
    [Fact]
    public async Task The_id_says_which_device_to_mount()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdb1"]));

        var stick = Assert.Single(places, p => p.Label == "STICK");

        Assert.Equal("unmounted:/dev/sdb1", stick.Id);
    }

    /// <summary>
    /// The row says it can be mounted, which is the fact the click reads —
    /// the empty Path deliberately says nothing, and that is what used to
    /// stop it.
    /// </summary>
    [Fact]
    public async Task An_unmounted_volume_offers_to_be_mounted()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdb1"]));

        var stick = Assert.Single(places, p => p.Label == "STICK");
        var root = Assert.Single(places, p => p.Path == "/");

        Assert.True(stick.CanMount);

        // And the flag is about this row rather than about devices: a mounted
        // volume is not something to offer to mount.
        Assert.False(root.CanMount);
    }

    /// <summary>
    /// **Two of them took the whole sidebar down.** Both carry an empty path,
    /// and the provider's name table was built with ToDictionary over every
    /// row — so the second empty key threw, from inside a rebuild the
    /// sidebar had started fire-and-forget, and the sidebar stayed empty with
    /// nothing anywhere to say why. MEASURED on the Ubuntu CI runner, the
    /// first machine this ran on with two unmounted filesystems; an ordinary
    /// desktop with an EFI partition under /boot and a swap partition is
    /// that machine. Under WSL there was one and nothing showed.
    /// </summary>
    [Fact]
    public async Task Two_unmounted_volumes_are_both_listed_rather_than_taking_the_sidebar_down()
    {
        var provider = Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdb1", "/dev/sdc1"]);

        var places = await Devices(provider);

        Assert.Equal(2, places.Count(p => !p.IsAvailable));

        // And a row with no path has no name to be asked for.
        Assert.Null(provider.NameFor(""));
    }

    /// <summary>
    /// Two mounts at one mount point are a legal stack — a bind mount, an
    /// overlay — and were the same fault one step along: two rows with one
    /// path, and a table that throws on the second. The first is named.
    /// </summary>
    [Fact]
    public async Task Two_mounts_at_one_mount_point_are_named_once()
    {
        var provider = Provider(
            mounts:
            [
                "/dev/sda2 / ext4 rw 0 0",
                "/dev/sdb1 /mnt/data ext4 rw 0 0",
                "/dev/sdc1 /mnt/data ext4 rw 0 0",
            ],
            devices: ["/dev/sda2", "/dev/sdb1", "/dev/sdc1"]);

        await Devices(provider);

        Assert.Equal("STICK", provider.NameFor("/mnt/data"));
    }

    /// <summary>
    /// **A swap partition has a UUID and is not a volume.** by-uuid lists it
    /// beside the filesystems, so it was offered as a drive named by its
    /// device — "sdc" on the Fedora this was written on — with a Mount action
    /// udisksctl refuses. /proc/swaps says which devices are swap, header
    /// line and all.
    /// </summary>
    [Fact]
    public async Task A_swap_partition_is_not_offered_as_a_volume()
    {
        var places = await Devices(Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2", "/dev/sdb1", "/dev/sdc3"],
            swaps:
            [
                "Filename\t\t\t\tType\t\tSize\t\tUsed\t\tPriority",
                "/dev/sdc3                               partition\t8388604\t\t0\t\t-2",
            ]));

        Assert.DoesNotContain(places, p => p.Id == "unmounted:/dev/sdc3");
        Assert.Contains(places, p => p.Id == "unmounted:/dev/sdb1");
    }

    /// <summary>
    /// Mounting something that is not one of these is ignored rather than
    /// shelling out — a pinned folder's id must never reach udisksctl.
    /// </summary>
    [Fact]
    public async Task Mounting_something_that_is_not_a_volume_does_nothing()
    {
        var provider = Provider(
            mounts: ["/dev/sda2 / ext4 rw 0 0"],
            devices: ["/dev/sda2"]);

        var raised = 0;
        provider.PlacesChanged += (_, _) => raised++;

        await provider.MountAsync("pin:/home/me/work", CancellationToken.None);

        Assert.Equal(0, raised);
    }
}
