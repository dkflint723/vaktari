using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Which folders are polled rather than watched, and how that is decided
/// from the mount table.
///
/// **A folder on a network mount never updated on its own.** The provider
/// handed every folder to inotify, which reports what passed through this
/// kernel — and a file written by another machine, or by the desktop's own
/// gvfs or KIO daemon behind a FUSE mount, is not that. A share listed in
/// Vaktari sat as it was until F5, which on a share is exactly where somebody
/// else is putting files for you to see.
///
/// The mount table is text, as everywhere else in this assembly, so the
/// decision is checked here on a Windows desktop as well as on Linux.
/// </summary>
public sealed class NetworkWatchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-netwatch-" + Guid.NewGuid().ToString("N")[..12]);

    public NetworkWatchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    /// <summary>One /proc/mounts line, with the field escaping the kernel applies.</summary>
    private static string Line(string source, string mountPoint, string fsType)
        => $"{source} {mountPoint.Replace(" ", "\\040")} {fsType} rw,relatime 0 0";

    // ---- the mount table -------------------------------------------------------

    [Fact]
    public void A_folder_under_a_cifs_mount_is_on_the_network()
        => Assert.True(MountTable.IsOnNetworkMount(
            [Line("/dev/sda1", "/", "ext4"), Line("//nas/media", "/mnt/nas", "cifs")],
            "/mnt/nas/photos"));

    [Fact]
    public void A_local_folder_is_not()
        => Assert.False(MountTable.IsOnNetworkMount(
            [Line("/dev/sda1", "/", "ext4"), Line("//nas/media", "/mnt/nas", "cifs")],
            "/home/f/photos"));

    /// <summary>The deepest mount point that contains the path is the one that
    /// counts: a local disk mounted inside a share is local, and a share
    /// mounted inside a local folder is not.</summary>
    [Fact]
    public void The_deepest_mount_point_decides()
    {
        string[] mounts =
        [
            Line("/dev/sda1", "/", "ext4"),
            Line("//nas/media", "/mnt/nas", "cifs"),
            Line("/dev/sdb1", "/mnt/nas/local", "ext4"),
        ];

        Assert.True(MountTable.IsOnNetworkMount(mounts, "/mnt/nas/photos"));
        Assert.False(MountTable.IsOnNetworkMount(mounts, "/mnt/nas/local/photos"));
    }

    /// <summary>A sibling whose name merely starts the same is outside the mount.</summary>
    [Fact]
    public void A_name_that_starts_like_the_mount_point_is_not_under_it()
        => Assert.False(MountTable.IsOnNetworkMount(
            [Line("/dev/sda1", "/", "ext4"), Line("//nas/media", "/mnt/nas", "cifs")],
            "/mnt/nasty/photos"));

    [Theory]
    [InlineData("gvfsd-fuse", "/run/user/1000/gvfs", "fuse.gvfsd-fuse", "/run/user/1000/gvfs/smb-share:server=nas,share=media/photos")]
    [InlineData("kio-fuse", "/run/user/1000/kio-fuse-abc", "fuse.kio", "/run/user/1000/kio-fuse-abc/sftp/box/photos")]
    [InlineData("nas:/export", "/mnt/nfs", "nfs4", "/mnt/nfs/photos")]
    [InlineData("box:", "/home/f/box", "fuse.sshfs", "/home/f/box/photos")]
    public void The_desktops_own_mounts_and_the_classic_ones_all_count(
        string source, string mountPoint, string fsType, string path)
        => Assert.True(MountTable.IsOnNetworkMount(
            [Line("/dev/sda1", "/", "ext4"), Line(source, mountPoint, fsType)], path));

    /// <summary>A mount point with a space arrives escaped, and is unescaped
    /// before it is compared — the same rule the sidebar reads by.</summary>
    [Fact]
    public void An_escaped_mount_point_is_read_as_written()
        => Assert.True(MountTable.IsOnNetworkMount(
            [Line("//nas/media", "/mnt/my share", "cifs")], "/mnt/my share/photos"));

    // ---- the provider's choice ------------------------------------------------

    /// <summary>
    /// The finding, at the seam: a folder the mount table places on a network
    /// filesystem is polled, and any other folder is watched the way it always
    /// was. The mount point IS this test's folder, so the choice is made about
    /// a real directory on either platform.
    /// </summary>
    [Fact]
    public void A_folder_on_a_network_mount_is_polled()
    {
        var provider = new LinuxFileSystemProvider
        {
            MountLines = () => [Line("//nas/media", _root, "cifs")],
        };

        using var watch = provider.Watch(Path.Combine(_root), _ => { });

        Assert.IsType<PollingWatch>(watch);
    }

    [Fact]
    public void A_local_folder_is_watched_as_before()
    {
        var provider = new LinuxFileSystemProvider
        {
            MountLines = () => [Line("/dev/sda1", "/", "ext4")],
        };

        using var watch = provider.Watch(_root, _ => { });

        Assert.IsType<FileSystemWatcher>(watch);
    }
}
