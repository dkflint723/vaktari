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
