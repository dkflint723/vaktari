using System.Runtime.Versioning;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A move within one filesystem is a rename, and is not held to the space it
/// would take to copy.
///
/// **Every move was.** The check compared DriveInfo names, and on Linux a
/// DriveInfo is named by whatever path it was given — so two folders side by
/// side were "different volumes", and 80 GB moved across a /home with 30 GB
/// free failed with "not enough room" before a byte was renamed.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class MoveFreeSpaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-movespace-" + Guid.NewGuid().ToString("N")[..8]);

    public MoveFreeSpaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    [PosixFact]
    public void Two_folders_side_by_side_are_one_volume()
    {
        var from = Directory.CreateDirectory(Path.Combine(_root, "Videos", "big")).FullName;
        var into = Directory.CreateDirectory(Path.Combine(_root, "Archive")).FullName;

        Assert.True(LinuxFileOperations.SameVolume([from], into));
    }

    /// <summary>
    /// **Through a link, the volume is where the link leads.** ~/Videos
    /// linking to another drive read as /home by its text, and a move out of
    /// it skipped the space check it needed. /dev/shm stands in for the other
    /// drive: its own filesystem, and writable without privilege.
    /// </summary>
    [PosixFact]
    public void A_folder_reached_through_a_link_to_another_filesystem_is_not()
    {
        // Every Linux this runs on has one; a box without it has nothing to
        // stand in for the other drive, and this says nothing there.
        if (!Directory.Exists("/dev/shm")) return;

        var elsewhere = Directory.CreateDirectory(
            Path.Combine("/dev/shm", "vaktari-movespace-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(elsewhere, "big"));
            var link = Path.Combine(_root, "Videos");
            Directory.CreateSymbolicLink(link, elsewhere);
            var into = Directory.CreateDirectory(Path.Combine(_root, "Archive")).FullName;

            Assert.False(LinuxFileOperations.SameVolume([Path.Combine(link, "big")], into));
            Assert.False(LinuxFileOperations.SameVolume([Path.Combine(into)], link));
        }
        finally
        {
            try { Directory.Delete(elsewhere, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// And /proc is not the temp folder's volume, so the answer is not simply
    /// yes.
    /// </summary>
    [PosixFact]
    public void A_folder_on_another_mount_is_not()
    {
        var from = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;

        Assert.False(LinuxFileOperations.SameVolume([from, "/proc/self"], _root));
    }
}
