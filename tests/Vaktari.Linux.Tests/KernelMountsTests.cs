using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The walks do not go inside the kernel's own filesystems.
///
/// **Properties on "/" measured /proc/kcore** — 128 TiB — and a walk of "/"
/// went through /sys, whose files are not files. The mount points are read
/// off the table; the walk lists such a folder and does not enter it.
/// </summary>
public sealed class KernelMountsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-kernelfs-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly Func<string, bool>? _before = SafeWalk.DoNotEnter;

    public KernelMountsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SafeWalk.DoNotEnter = _before;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Proc_sys_and_automounts_are_kernel_mounts_and_a_disk_is_not()
    {
        var points = KernelMounts.PointsIn(
        [
            "/dev/nvme0n1p2 / ext4 rw 0 0",
            "proc /proc proc rw 0 0",
            "sysfs /sys sysfs rw 0 0",
            "systemd-1 /mnt/auto autofs rw 0 0",
            "/dev/sdb1 /run/media/me/STICK vfat rw 0 0",
        ]);

        Assert.True(points.SetEquals(["/proc", "/sys", "/mnt/auto"]), string.Join(", ", points));
    }

    /// <summary>
    /// **The mount on top decides.** An automount point is autofs until used,
    /// then a real filesystem sits on it at the same place; read by its first
    /// line, /boot — or a NAS — was skipped whole.
    /// </summary>
    [Fact]
    public void An_automount_in_use_is_the_filesystem_on_top_of_it()
    {
        Assert.DoesNotContain("/boot", KernelMounts.PointsIn(
            ["systemd-1 /boot autofs rw 0 0", "/dev/nvme0n1p1 /boot vfat rw 0 0"]));

        Assert.Contains("/boot", KernelMounts.PointsIn(
            ["/dev/nvme0n1p1 /boot vfat rw 0 0", "systemd-1 /boot autofs rw 0 0"]));
    }

    /// <summary>
    /// **Nor is it measured as one row of many.** Space usage on "/" measured
    /// each child as the root of a walk, and a root is always entered: the
    /// /proc row read 128 TiB. It is a folder row with nothing counted in it,
    /// while measuring that folder on its own still counts what is there.
    /// </summary>
    [Fact]
    public void Space_usage_lists_a_marked_folder_without_measuring_it()
    {
        var kept = Path.Combine(_root, "proc");
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(kept, "kcore"), "huge");

        SafeWalk.DoNotEnter = path => path == kept;

        var row = SpaceUsage.Underneath(_root, null, default).Rows.Single(r => r.Path == kept);

        Assert.Equal(0, row.Usage.Bytes);
        Assert.Equal(0, row.Usage.Files);
        Assert.Equal(1, SpaceUsage.Measure(kept, null, default).Files);
    }

    /// <summary>The walk lists the folder and does not go into it.</summary>
    [Fact]
    public void A_folder_the_platform_marks_is_listed_and_not_entered()
    {
        var kept = Path.Combine(_root, "proc");
        Directory.CreateDirectory(Path.Combine(kept, "inside"));
        File.WriteAllText(Path.Combine(kept, "kcore"), "huge");
        var ordinary = Path.Combine(_root, "home", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(ordinary)!);
        File.WriteAllText(ordinary, "x");

        SafeWalk.DoNotEnter = path => path == kept;

        var found = SafeWalk.Descend(_root, default).Select(f => f.Path).ToList();

        Assert.Contains(kept, found);
        Assert.Contains(ordinary, found);
        Assert.DoesNotContain(Path.Combine(kept, "kcore"), found);
        Assert.DoesNotContain(Path.Combine(kept, "inside"), found);
    }
}
