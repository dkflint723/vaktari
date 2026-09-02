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
    private static LinuxPlacesProvider Provider(string[] mounts, string[] devices)
        // Its own state directory, so a test never reads or writes the pins of
        // whoever is running it.
        => new(Directory.CreateTempSubdirectory("vaktari-places").FullName)
        {
            MountLines = () => mounts,
            FilesystemDevices = () => devices,
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
