using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What counts as a volume's root — the paths no engine moves, bins or deletes.
///
/// **A USB stick on Linux is a directory under /media**, not a root by
/// PathRules, and removing it would empty the stick; so a mount point counts
/// as well. Driven through the seam rather than /proc/mounts, which differs by
/// machine and on Windows is not there at all.
/// </summary>
public sealed class VolumeRootsTests
{
    [Fact]
    public void A_root_and_a_mount_point_are_volumes_and_a_folder_under_one_is_not()
    {
        var before = VolumeRoots.MountPointsOverride;

        VolumeRoots.MountPointsOverride = () => ["/", "/media/me/STICK"];

        try
        {
            Assert.True(VolumeRoots.IsVolumeRoot(Path.GetPathRoot(Path.GetTempPath())));
            Assert.True(VolumeRoots.IsVolumeRoot("/media/me/STICK"));
            Assert.True(VolumeRoots.IsVolumeRoot("/media/me/STICK/"));

            Assert.False(VolumeRoots.IsVolumeRoot("/media/me/STICK/photos"));
            Assert.False(VolumeRoots.IsVolumeRoot("/media/me"));
            Assert.False(VolumeRoots.IsVolumeRoot(""));

            Assert.Equal(VolumeRoots.Refusal, VolumeRoots.Refuse(["/tmp/a", "/media/me/STICK"]));
            Assert.Null(VolumeRoots.Refuse(["/tmp/a", "/media/me/STICK/a"]));
        }
        finally
        {
            VolumeRoots.MountPointsOverride = before;
        }
    }

    /// <summary>
    /// **Five spellings of a root got past both guards** — PathRules.IsRoot
    /// compares a path with its root as text — and the real engine then
    /// walked, recycled or moved the root. Every spelling here names a root
    /// and is refused; none of them is ever handed to a filesystem. The drive
    /// letter is one no drive on this machine answers to.
    /// </summary>
    [WindowsFact]
    public void Every_spelling_of_a_windows_root_is_a_root_and_a_folder_is_not()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXWVUTSRQPONMLKJIHG".First(c => !taken.Contains(c));

        string[] roots =
        [
            $"{q}:\\", $"{q}:", $"{q}:/", $"{q}:\\\\", $"{q}:\\.", $"{q}:\\x\\..", $"{q}:\\.\\",
            $"\\\\?\\{q}:\\", $"\\\\.\\{q}:\\",
            @"\\server\share", @"\\server\share\", @"\\server\share\\",
            @"\\?\UNC\server\share\", @"\\?\UNC\server\share",
        ];

        foreach (var root in roots)
            Assert.True(VolumeRoots.IsVolumeRoot(root), $"{root} was not taken for a root");

        string[] folders = [$"{q}:\\data", $"{q}:\\data\\", $"\\\\?\\{q}:\\data", @"\\server\share\dir", $"{q}:\\x\\..\\y"];

        foreach (var folder in folders)
            Assert.False(VolumeRoots.IsVolumeRoot(folder), $"{folder} was taken for a root");
    }

    /// <summary>
    /// **The mount table was read once per path**: a thousand selected files
    /// read /proc/mounts a thousand times, on the UI thread and again in the
    /// engine. Refuse reads it once for the whole list.
    /// </summary>
    [Fact]
    public void Refusing_a_list_reads_the_mount_table_once()
    {
        var before = VolumeRoots.MountPointsOverride;
        var reads = 0;

        VolumeRoots.MountPointsOverride = () =>
        {
            reads++;
            return ["/", "/media/me/STICK"];
        };

        try
        {
            var paths = Enumerable.Range(0, 1000).Select(i => $"/home/me/f{i}").ToList();

            Assert.Null(VolumeRoots.Refuse(paths));
            Assert.Equal(1, reads);
        }
        finally
        {
            VolumeRoots.MountPointsOverride = before;
        }
    }

    /// <summary>And the machine's own table is kept for a moment, so the pane's
    /// ask and the engine's, a keystroke apart, read it once between them.</summary>
    [Fact]
    public void The_mount_table_is_read_again_only_once_it_is_stale()
    {
        var before = VolumeRoots.ReadMountTable;
        var reads = 0;

        VolumeRoots.ReadMountTable = () =>
        {
            reads++;
            return ["/"];
        };

        VolumeRoots.ForgetMountTable();

        try
        {
            _ = VolumeRoots.CachedMountTable();
            _ = VolumeRoots.CachedMountTable();

            Assert.Equal(1, reads);

            VolumeRoots.ForgetMountTable();
            _ = VolumeRoots.CachedMountTable();

            Assert.Equal(2, reads);
        }
        finally
        {
            VolumeRoots.ReadMountTable = before;
            VolumeRoots.ForgetMountTable();
        }
    }

    /// <summary>The operation an engine hands back instead of starting: failed
    /// already, with the sentence the pane would have said.</summary>
    [Fact]
    public void A_refused_operation_has_failed_before_it_began()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var handle = VolumeRoots.RefusedOperation([root], OperationKind.Delete);

        Assert.NotNull(handle);
        Assert.Equal(OperationState.Failed, handle.State);
        Assert.True(handle.Completion.IsCompleted);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);

        Assert.Null(VolumeRoots.RefusedOperation([Path.Combine(root, "folder")], OperationKind.Delete));
    }
}
