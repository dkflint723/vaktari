using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The Linux engine never deletes, bins or moves a volume's root — the twin of
/// the Windows tests of the same name.
///
/// **A mount point that is not there**, declared one through the seam: the
/// refusal's absence must never be tried on a real volume, so the path is one
/// no filesystem holds, where the unguarded engine fails harmlessly with a
/// different error and the assertion on the refusal's own sentence tells the
/// two apart.
/// </summary>
public sealed class VolumeRootRefusalTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>>? _before = VolumeRoots.MountPointsOverride;

    private readonly string _mount =
        Path.Combine(Path.GetTempPath(), "vaktari-no-such-mount-" + Guid.NewGuid().ToString("N"));

    public VolumeRootRefusalTests() => VolumeRoots.MountPointsOverride = () => ["/", _mount];

    public void Dispose() => VolumeRoots.MountPointsOverride = _before;

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    [PosixFact]
    public async Task Delete_refuses_a_mount_point()
    {
        var handle = await Settled(new LinuxFileOperations().Delete([_mount]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
    }

    [PosixFact]
    public async Task Trash_refuses_a_mount_point()
    {
        var handle = await Settled(new LinuxFileOperations().Trash([_mount]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
    }

    [PosixFact]
    public async Task Copy_refuses_a_mount_point()
    {
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;

        try
        {
            var handle = await Settled(new LinuxFileOperations().Copy(
                [_mount], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Failed, handle.State);
            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **A mount point spelled another way got past the guard**: a trailing
    /// "/.", a doubled leading slash and a "." inside all name the stick, and
    /// all read as folders while the comparison was by text.
    /// </summary>
    [PosixFact]
    public void Every_spelling_of_a_mount_point_is_a_mount_point()
    {
        var name = Path.GetFileName(_mount);
        var parent = Path.GetDirectoryName(_mount)!;

        foreach (var spelling in new[]
                 {
                     _mount, _mount + "/", _mount + "/.", "/" + _mount, parent + "/./" + name,
                     _mount + "/x/..",
                 })
            Assert.True(VolumeRoots.IsVolumeRoot(spelling), $"{spelling} was not taken for the mount point");

        Assert.False(VolumeRoots.IsVolumeRoot(_mount + "/x"));
        Assert.False(VolumeRoots.IsVolumeRoot(parent));
    }

    /// <summary>
    /// **With no seam, the machine's own mount table is what is read.** /proc
    /// is a mount point on every Linux there is, so it is a volume here — and
    /// a folder inside it is not.
    /// </summary>
    [PosixFact]
    public void The_real_mount_table_is_read_when_no_seam_is_set()
    {
        VolumeRoots.MountPointsOverride = null;

        Assert.True(VolumeRoots.IsVolumeRoot("/proc"), "/proc was not found in the mount table");
        Assert.False(VolumeRoots.IsVolumeRoot("/proc/self"));
    }

    /// <summary>
    /// **A stick reached through a linked folder was a folder**, and the
    /// engine's Delete emptied it in review: the table names the real path,
    /// and the text of "~/media/STICK", where ~/media is a link to the folder
    /// holding the stick, never met it. Fedora Silverblue ships /mnt, /media
    /// and /home as links. The "stick" is a temporary folder declared a mount
    /// through the seam, so an unguarded engine could only delete the test's
    /// own file.
    /// </summary>
    [PosixFact]
    public async Task A_mount_point_reached_through_a_linked_folder_is_refused()
    {
        var media = Directory.CreateTempSubdirectory("vaktari-media").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("vaktari-home").FullName;
        var stick = Path.Combine(media, "STICK");
        var photo = Path.Combine(stick, "photo.jpg");

        Directory.CreateDirectory(stick);
        File.WriteAllText(photo, "the stick's own file");
        File.CreateSymbolicLink(Path.Combine(elsewhere, "media"), media);

        VolumeRoots.MountPointsOverride = () => ["/", stick];

        try
        {
            var via = Path.Combine(elsewhere, "media", "STICK");

            foreach (var spelling in new[] { via, via + "/", via + "/.", elsewhere + "/./media/STICK", elsewhere + "//media/x/../STICK" })
                Assert.True(VolumeRoots.IsVolumeRoot(spelling), $"{spelling} was not taken for the mount point");

            Assert.False(VolumeRoots.IsVolumeRoot(Path.Combine(via, "photo.jpg")));
            Assert.False(VolumeRoots.IsVolumeRoot(Path.Combine(elsewhere, "media")));

            var handle = await Settled(new LinuxFileOperations().Delete([via]));

            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
            Assert.True(File.Exists(photo), "the stick's file was deleted");
        }
        finally
        {
            File.Delete(Path.Combine(elsewhere, "media"));
            Directory.Delete(elsewhere, recursive: true);
            Directory.Delete(media, recursive: true);
        }
    }

    /// <summary>
    /// **A link to a mount is the link; what it leads to is the mount.** With
    /// the machine's own table: "toproc" — a link to /proc — is a link, and
    /// removing it removes only that; "toproc/." and "toproc/" are /proc
    /// itself; "todev/shm" is /dev/shm, a mount on every Linux Vaktari runs
    /// on, reached through a linked parent. And "/dev/fd/../shm" is /dev/shm
    /// to .NET, which folds ".." as text before the kernel sees it, though
    /// the kernel would read it through the /dev/fd link — the engine acts on
    /// what .NET resolves, so that is a mount too. Asked, never acted on.
    /// </summary>
    [PosixFact]
    public void Through_a_link_the_mount_it_leads_to_is_a_mount_and_the_link_is_not()
    {
        VolumeRoots.MountPointsOverride = null;

        var holder = Directory.CreateTempSubdirectory("vaktari-links").FullName;
        var toProc = Path.Combine(holder, "toproc");
        var toDev = Path.Combine(holder, "todev");

        File.CreateSymbolicLink(toProc, "/proc");
        File.CreateSymbolicLink(toDev, "/dev");

        try
        {
            foreach (var spelling in new[] { toProc + "/.", toProc + "/", toDev + "/shm", toDev + "/./shm", toDev + "/shm/.", "/dev/fd/../shm" })
                Assert.True(VolumeRoots.IsVolumeRoot(spelling), $"{spelling} was not taken for a mount point");

            Assert.False(VolumeRoots.IsVolumeRoot(toProc));
            Assert.False(VolumeRoots.IsVolumeRoot(toDev));
            Assert.False(VolumeRoots.IsVolumeRoot(toProc + "/self"));
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **A link that leads to "/" made "//proc"**, which the table does not
    /// have: "toroot/proc", "up/proc" (up is "../..") and
    /// "/proc/self/root/proc" were folders to the pane though the kernel
    /// refused each in the engine. With the machine's own table; asked, never
    /// acted on.
    /// </summary>
    [PosixFact]
    public void A_mount_reached_through_a_link_to_the_root_is_a_mount()
    {
        VolumeRoots.MountPointsOverride = null;

        var holder = Directory.CreateTempSubdirectory("vaktari-links").FullName;
        var toRoot = Path.Combine(holder, "toroot");
        var up = Path.Combine(holder, "up");

        File.CreateSymbolicLink(toRoot, "/");
        File.CreateSymbolicLink(up, string.Join('/', holder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(_ => "..")));

        try
        {
            foreach (var spelling in new[] { toRoot + "/proc", toRoot + "/proc/.", up + "/proc", "/proc/self/root/proc", toRoot + "/" })
                Assert.True(VolumeRoots.IsVolumeRoot(spelling), $"{spelling} was not taken for a mount point");

            Assert.False(VolumeRoots.IsVolumeRoot(toRoot + "/proc/self"));
            Assert.False(VolumeRoots.IsVolumeRoot(toRoot));
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **More ways through a link to the root, and the names that only look
    /// like a device.** The fourth review round's spellings of /proc — a "."
    /// or a doubled separator after the link, this process's own root under
    /// /proc by its number — are mounts to the pane as they are to the kernel.
    /// And the Windows device-path refusal is Windows' alone: a folder here
    /// called "a::b", "C:" or "\\?\GLOBALROOT" is an ordinary folder. With
    /// the machine's own table; asked, never acted on.
    /// </summary>
    [PosixFact]
    public void Through_a_dot_after_a_link_to_the_root_proc_is_a_mount_and_windows_names_are_folders()
    {
        VolumeRoots.MountPointsOverride = null;

        var holder = Directory.CreateTempSubdirectory("vaktari-links").FullName;
        var toRoot = Path.Combine(holder, "toroot");

        File.CreateSymbolicLink(toRoot, "/");

        try
        {
            foreach (var spelling in new[]
                     {
                         toRoot + "/./proc", toRoot + "/.//proc", toRoot + "//proc", toRoot + "/proc/./",
                         $"/proc/{Environment.ProcessId}/root/proc", "/proc/self/root/./proc", "/proc/self/root/proc/.",
                     })
                Assert.True(VolumeRoots.IsVolumeRoot(spelling), $"{spelling} was not taken for a mount point");

            foreach (var name in new[] { "a::b", "C:", @"\\?\GLOBALROOT", "??", "x::$INDEX_ALLOCATION" })
            {
                var folder = Directory.CreateDirectory(Path.Combine(holder, name)).FullName;

                Assert.Null(VolumeRoots.Refuse([folder]));
            }
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **The kernel's own answer**, which the engines ask in their workers:
    /// statx's mount-root attribute, and where the kernel does not report it,
    /// the device against the parent's. "/", /proc, /dev/shm and each reached
    /// through a link are mounts; the link itself, a temporary folder and a
    /// path that is not there are not. "/dev/fd/../shm" is a mount as .NET
    /// resolves it, which is what an engine acts on. Both answers are asked
    /// of every path.
    /// </summary>
    [PosixFact]
    public void The_kernel_calls_a_mount_a_mount_however_it_is_reached()
    {
        var holder = Directory.CreateTempSubdirectory("vaktari-links").FullName;
        var toProc = Path.Combine(holder, "toproc");
        var toDev = Path.Combine(holder, "todev");

        File.CreateSymbolicLink(toProc, "/proc");
        File.CreateSymbolicLink(toDev, "/dev");

        try
        {
            foreach (var attribute in new[] { true, false })
            {
                foreach (var mount in new[] { "/", "//", "/.", "/proc", "/proc/", "/proc/.", "/dev/shm", "/dev/./shm", toProc + "/.", toProc + "/", toDev + "/shm", "/dev/fd/../shm" })
                    Assert.True(MountRootOnDisk.Is(mount, attribute), $"{mount} was not a mount (attribute {attribute})");

                foreach (var folder in new[] { toProc, toDev, holder, "/proc/self", holder + "/nothing-here" })
                    Assert.False(MountRootOnDisk.Is(folder, attribute), $"{folder} was a mount (attribute {attribute})");
            }
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **Every verb asks the kernel too, in its worker.** A temporary folder
    /// stands in for a mount through the engine's seam — a real mount is
    /// never where a guard's absence is tried — and Delete, Trash, Move and
    /// Copy each refuse it, leaving its file where it was.
    /// </summary>
    [PosixFact]
    public async Task Every_verb_refuses_what_the_kernel_calls_a_mount()
    {
        VolumeRoots.MountPointsOverride = () => ["/"];

        var mount = Directory.CreateTempSubdirectory("vaktari-onDisk").FullName;
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;
        var marker = Path.Combine(mount, "marker.txt");
        File.WriteAllText(marker, "x");

        var ops = new LinuxFileOperations { RootOnDisk = path => path == mount };

        try
        {
            foreach (var handle in new[]
                     {
                         ops.Delete([mount]),
                         ops.Trash([mount]),
                         ops.Move([mount], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([mount], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await Settled(handle);

                Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
            }

            Assert.True(File.Exists(marker));
            Assert.Empty(Directory.EnumerateFileSystemEntries(into));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
            if (Directory.Exists(mount)) Directory.Delete(mount, recursive: true);
        }
    }

    [PosixFact]
    public async Task Move_refuses_a_mount_point()
    {
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;

        try
        {
            var handle = await Settled(new LinuxFileOperations().Move(
                [_mount], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Failed, handle.State);
            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }
}
