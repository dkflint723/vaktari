using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **sshfs and rclone mounts are remote too.** Only gvfs and kio-fuse mounts
/// were known to be, and .NET calls every FUSE filesystem local — so a plain
/// sshfs mount was probed row by row over the wire, and properties on the
/// folder holding it walked the share without being asked.
/// </summary>
public sealed class NetworkRootsTests
{
    [Fact]
    public void Fuse_network_mounts_are_among_the_roots_and_a_disk_is_not()
        => Assert.Equal(
            ["/home/me/nas", "/mnt/cloud", "/mnt/share"],
            LinuxRemoteMounts.NetworkRootsIn(
            [
                "/dev/nvme0n1p2 / ext4 rw 0 0",
                "me@host: /home/me/nas fuse.sshfs rw 0 0",
                "remote: /mnt/cloud fuse.rclone rw 0 0",
                "//nas/share /mnt/share cifs rw 0 0",
                "/dev/sdb1 /run/media/me/STICK vfat rw 0 0",
            ]));
}
