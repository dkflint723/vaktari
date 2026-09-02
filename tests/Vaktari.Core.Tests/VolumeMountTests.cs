using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Which volume a path lives on, and what asking costs.
///
/// **The question froze the window.** Working out whether a drag should copy or
/// move enumerated DriveInfo and read IsReady on each — which on Unix is
/// Directory.Exists, a stat() on the mount point — so it touched every mount on
/// the machine, and a stat on a hung NFS or sshfs mount does not return.
///
/// **And it asked once per file.** The drag path calls it twice for every path
/// in the selection, so a plain drag of a 200-file selection was 400 mount-table
/// enumerations, on an ordinary box with dozens of mounts, for every drag-over
/// event — and drag-over fires continuously while the pointer moves.
///
/// /proc/mounts is a text file, and reading it once per drag answers the same
/// question without touching a single filesystem.
/// </summary>
public sealed class VolumeMountTests
{
    /// <summary>A believable /proc/mounts, including the awkward parts.</summary>
    private static readonly string[] Mounts =
    [
        "/dev/sda2 / ext4 rw,relatime 0 0",
        "proc /proc proc rw,nosuid 0 0",
        "/dev/sda1 /boot/efi vfat rw 0 0",
        "tmpfs /run tmpfs rw,nosuid 0 0",
        "/dev/sdb1 /media/flint/My\\040Backup ext4 rw 0 0",
        "server:/export /mnt/nfs nfs4 rw 0 0",
    ];

    private static IReadOnlyList<string> Table => Volumes.MountPointsIn(Mounts);

    [Fact]
    public void Every_boundary_is_read_from_the_table()
        => Assert.Equal(
            ["/", "/proc", "/boot/efi", "/run", "/media/flint/My Backup", "/mnt/nfs"],
            Table);

    /// <summary>
    /// A mount point with a space in it is ordinary on a removable disk, and
    /// /proc/mounts escapes it as octal.
    /// </summary>
    [Fact]
    public void A_space_in_a_mount_point_survives()
        => Assert.Contains("/media/flint/My Backup", Table);

    /// <summary>One left-to-right scan, never chained replaces: doing "\040"
    /// before "\134" turns a literal backslash-zero-four-zero into a space.</summary>
    [Fact]
    public void A_literal_backslash_is_not_re_read_as_an_escape()
        => Assert.Equal(@"/mnt/a\040b", Volumes.UnescapeMountField(@"/mnt/a\134040b"));

    [Fact]
    public void A_short_line_is_skipped_rather_than_throwing()
        => Assert.Equal(["/"], Volumes.MountPointsIn(["/dev/sda2 / ext4 rw 0 0", "rubbish"]));

    // ---- the longest mount wins ---------------------------------------------

    [Theory]
    [InlineData("/home/flint/notes.txt", "/")]
    [InlineData("/boot/efi/EFI/BOOT", "/boot/efi")]
    [InlineData("/mnt/nfs/share/a.txt", "/mnt/nfs")]
    [InlineData("/media/flint/My Backup/photos", "/media/flint/My Backup")]
    public void The_longest_mount_that_prefixes_the_path_wins(string path, string mount)
        => Assert.Equal(mount, Volumes.MountForIn(Table, path));

    /// <summary>
    /// A prefix has to end at a separator, or "/run" claims "/runner" and two
    /// unrelated paths are called one volume — which on a plain drag is a move
    /// that should have been a copy.
    /// </summary>
    [Fact]
    public void A_prefix_must_end_at_a_separator()
        => Assert.Equal("/", Volumes.MountForIn(Table, "/runner/build"));

    [Fact]
    public void The_mount_point_itself_belongs_to_itself()
        => Assert.Equal("/mnt/nfs", Volumes.MountForIn(Table, "/mnt/nfs"));

    // ---- and what that means for a drag -------------------------------------

    [Fact]
    public void Two_paths_on_one_volume_are_the_same_volume()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.True(Volumes.Same("/home/flint/a.txt", "/home/flint/b", Table));
    }

    /// <summary>
    /// The case the whole rule exists for: dragging to another volume copies
    /// and leaves the original alone.
    /// </summary>
    [Fact]
    public void A_path_on_a_removable_disk_is_a_different_volume()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.False(Volumes.Same("/home/flint/a.txt", "/media/flint/My Backup", Table));
    }

    /// <summary>
    /// **A stale mount belongs in the table.** It used to be filtered out by
    /// the IsReady check that caused the freeze — and leaving it out is also
    /// the wrong answer, because a path under it really does live on it. In it,
    /// source and destination differ and a plain drag copies, which is what
    /// leaves the original where it is.
    /// </summary>
    [Fact]
    public void An_unreachable_mount_is_still_a_boundary()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Contains("/mnt/nfs", Table);
        Assert.False(Volumes.Same("/home/flint/a.txt", "/mnt/nfs/share", Table));
    }

    /// <summary>Nothing to compare against is "different", which errs towards
    /// copying.</summary>
    [Fact]
    public void An_empty_table_answers_different()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.False(Volumes.Same("/home/a.txt", "/home/b.txt", []));
    }

    /// <summary>
    /// The drag path reads the table once and asks it many times. The overload
    /// that takes no table reads it per call, and the caller asks per file.
    /// </summary>
    [Fact]
    public void The_drag_path_reads_the_table_once()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var source = File.ReadAllText(
            Path.Combine(here!, "src", "Vaktari.Ui", "Input", "DragEffect.cs"));

        Assert.Contains("Volumes.MountPoints()", source);
        Assert.Contains("Volumes.Same(s, destination, mounts)", source);

        // The per-call overload would put the read back inside the loop.
        Assert.DoesNotContain("SameVolume(s, destination)", source);
    }
}
