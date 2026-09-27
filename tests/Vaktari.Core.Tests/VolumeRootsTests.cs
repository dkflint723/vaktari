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

    /// <summary>
    /// **A mount made between two asks is refused by the second.** The table
    /// was kept for two seconds and shared by the pane and the engine, so a
    /// stick mounted inside that window was a folder to both — measured by
    /// the review: pane and engine each answered "allowed". Through the
    /// machine-table seam, not the override, because the override was never
    /// kept.
    /// </summary>
    [Fact]
    public void A_mount_that_appears_between_two_asks_is_refused_by_the_second()
    {
        var before = VolumeRoots.ReadMountTable;
        var beforeOverride = VolumeRoots.MountPointsOverride;
        IReadOnlyList<string> table = ["/"];

        VolumeRoots.MountPointsOverride = null;
        VolumeRoots.ReadMountTable = () => table;

        try
        {
            Assert.Null(VolumeRoots.Refuse(["/media/me/STICK"]));

            table = ["/", "/media/me/STICK"];

            Assert.Equal(VolumeRoots.Refusal, VolumeRoots.Refuse(["/media/me/STICK"]));
            Assert.True(VolumeRoots.IsVolumeRoot("/media/me/STICK"));
        }
        finally
        {
            VolumeRoots.ReadMountTable = before;
            VolumeRoots.MountPointsOverride = beforeOverride;
        }
    }

    /// <summary>
    /// **A device path is taken apart, because GetFullPath leaves it as
    /// written.** "\\?\GLOBALROOT\??\Z:\" had the engine's Delete empty a
    /// subst drive in review; "\\?\GLOBALROOT\Device\HarddiskVolume3\" names
    /// a real volume the same way; "\\?\Z:\.", "\\?\Z:\x\..", "\\?\Volume{…}\."
    /// and "\\?\UNC\server\share\." were folders to the check and failed only
    /// because the file system happened to reject them. Every one is a root
    /// here, asked of text alone — the volume numbers and the GUID answer to
    /// nothing on any machine, and nothing is opened. A folder under each
    /// kind stays a folder, a shadow copy's included, so its files can still
    /// be copied out.
    /// </summary>
    [WindowsFact]
    public void Every_device_spelling_of_a_root_is_a_root_and_a_folder_under_one_is_not()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXWVUTSRQPONMLKJIHG".First(c => !taken.Contains(c));
        const string guid = "Volume{00000000-0000-0000-0000-00000000dead}";

        string[] roots =
        [
            $@"\\?\GLOBALROOT\??\{q}:\", $@"\\?\GLOBALROOT\??\{q}:", $@"\\?\GLOBALROOT\DosDevices\{q}:\",
            $@"\\?\GLOBALROOT\GLOBAL??\{q}:\x\..", $@"\\?\globalroot\??\{q}:\.",
            @"\\?\GLOBALROOT\Device\HarddiskVolume999\", @"\\?\GLOBALROOT\Device\HarddiskVolume999",
            @"\\?\GLOBALROOT\Device\HarddiskVolume999\x\..", @"\\?\GLOBALROOT\Device\HarddiskVolume999\.",
            @"\\?\GLOBALROOT\Device\Mup\server\share\", @"\\?\GLOBALROOT\Device\Mup\;LanmanRedirector\server\share",
            @"\\?\GLOBALROOT\??\UNC\server\share\",
            $@"\\?\{q}:\.", $@"\\?\{q}:\x\..", $@"\\?\{q}:\x\..\..", $@"\\?\{q}:", $@"//?/{q}:/./",
            $@"\\.\{q}:\x\..",
            $@"\\?\{guid}\", $@"\\?\{guid}", $@"\\?\{guid}\.", $@"\\.\{guid}\x\..",
            @"\\?\UNC\server\share\.", @"\\?\UNC\server\share\x\..", @"\\?\unc\server\share\",
        ];

        foreach (var root in roots)
            Assert.True(VolumeRoots.IsVolumeRoot(root), $"{root} was not taken for a root");

        string[] folders =
        [
            $@"\\?\GLOBALROOT\??\{q}:\data", @"\\?\GLOBALROOT\Device\HarddiskVolume999\Windows",
            @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Users",
            @"\\?\GLOBALROOT\Device\Mup\server\share\dir",
            $@"\\?\{q}:\data", $@"\\?\{q}:\x\..\y", $@"\\?\{guid}\Windows", @"\\?\UNC\server\share\dir",
        ];

        foreach (var folder in folders)
            Assert.False(VolumeRoots.IsVolumeRoot(folder), $"{folder} was taken for a root");
    }

    /// <summary>
    /// **What the engines ask as well, off the key's thread**: the file
    /// system's own answer, through a predicate each engine supplies. Any path
    /// it calls a root refuses the whole list; an empty name is never asked.
    /// </summary>
    [Fact]
    public void The_filesystem_answer_refuses_a_list_with_any_root_in_it()
    {
        var asked = new List<string>();

        bool RootIfNamedRoot(string path)
        {
            asked.Add(path);
            return path.EndsWith("root", StringComparison.Ordinal);
        }

        Assert.Equal(VolumeRoots.Refusal, VolumeRoots.RefuseOnDisk(["a", "", "the root", "b"], RootIfNamedRoot));
        Assert.Equal(["a", "the root"], asked);

        Assert.Null(VolumeRoots.RefuseOnDisk(["a", "b"], RootIfNamedRoot));
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

    /// <summary>
    /// **An odd path is answered, never thrown.** Every guard asks this first,
    /// on a key press and again as an engine starts, so an exception here
    /// would come out of a command rather than a sentence. Path.GetFullPath
    /// refuses a NUL on both platforms and, on Windows, a path too long to
    /// resolve and one that is only a space — each is asked here, and each is
    /// a folder as far as a refusal is concerned; the filesystem refuses it
    /// with its own words when it gets there.
    /// </summary>
    [Fact]
    public void A_path_that_cannot_be_resolved_is_answered_rather_than_thrown()
    {
        var before = VolumeRoots.MountPointsOverride;

        VolumeRoots.MountPointsOverride = () => ["/", "/media/me/STICK"];

        try
        {
            var odd = OperatingSystem.IsWindows()
                ? new[] { "C:\\a\0b", " ", "C:\\" + new string('a', 40000) }
                : ["/tmp/a\0b", "/tmp/" + new string('a', 40000)];

            foreach (var path in odd)
            {
                Assert.False(VolumeRoots.IsVolumeRoot(path), $"{path[..Math.Min(path.Length, 12)]}… was taken for a root");
                Assert.Null(VolumeRoots.Refuse([path]));
            }
        }
        finally
        {
            VolumeRoots.MountPointsOverride = before;
        }
    }

    /// <summary>
    /// **The trimmed spelling asked as written, alone.** VolumeRoots asks a
    /// path with its trailing separators taken off both as written and
    /// resolved, and each hides the other's absence for every ordinary
    /// spelling. The written one is the only answer for a root that
    /// Path.GetFullPath refuses: a drive letter followed by more backslashes
    /// than the resolver will take, which it throws on as too long.
    /// </summary>
    [WindowsFact]
    public void A_root_the_resolver_refuses_is_still_a_root()
    {
        var root = "Q:" + new string('\\', 40000);

        Assert.Throws<PathTooLongException>(() => Path.GetFullPath(root));
        Assert.True(VolumeRoots.IsVolumeRoot(root));
    }
}
