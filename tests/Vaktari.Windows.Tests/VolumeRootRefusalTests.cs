using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The engine never deletes, bins or moves a drive's root.
///
/// **Shift+Delete on a drive in This PC deleted what was on it** (0.11.0).
/// Delete cleared the read-only bits of the whole tree and walked it deleting
/// files before Directory.Delete refused the root at the very end. The pane
/// refuses the verbs on a volume now as well; this is the engine's own refusal
/// under it, for a route nobody has thought of yet.
///
/// **Asked of a drive letter that does not exist.** The refusal's absence must
/// never be tried on a real volume — a revert-check of this guard would then
/// be a real deletion — so the root is a letter no drive answers to, where the
/// unguarded engine fails harmlessly with a different error, and the assertion
/// on the refusal's own sentence is what tells the two failures apart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeRootRefusalTests
{
    /// <summary>A drive root no volume on this machine answers to.</summary>
    private static string UnusedRoot()
    {
        var taken = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        var letter = "ZYXWVUTSRQPONMLKJIHG".First(c => !taken.Contains(c));

        return $"{letter}:\\";
    }

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    [Fact]
    public async Task Delete_refuses_a_drive_root()
    {
        var handle = await Settled(new WindowsFileOperations().Delete([UnusedRoot()]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
    }

    [Fact]
    public async Task Trash_refuses_a_drive_root()
    {
        // The recycle itself stands in, so an unguarded engine records the
        // attempt rather than asking the shell to recycle anything.
        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        var handle = await Settled(ops.Trash([UnusedRoot()]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Move_refuses_a_drive_root()
    {
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;

        try
        {
            var handle = await Settled(new WindowsFileOperations().Move(
                [UnusedRoot()], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Failed, handle.State);
            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **A copy of a drive landed on itself.** PathRules.LeafName("Z:\") is
    /// "Z:\", so the target under any folder combined back to the drive, and
    /// Replace or Keep both duplicated files in place on the source. Refused.
    /// </summary>
    [Fact]
    public async Task Copy_refuses_a_drive_root()
    {
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;

        try
        {
            var handle = await Settled(new WindowsFileOperations().Copy(
                [UnusedRoot()], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Failed, handle.State);
            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **The spellings that got past the guard**, through the real engine:
    /// "Z:\\", "Z:\.", "Z:\x\.." and "\\?\Z:\" each walked, recycled or moved
    /// before. Every verb refuses every one of them.
    /// </summary>
    [Theory]
    [InlineData("{0}:\\\\")]
    [InlineData("{0}:\\.")]
    [InlineData("{0}:\\x\\..")]
    [InlineData("\\\\?\\{0}:\\")]
    [InlineData("{0}:/")]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:\")]
    [InlineData(@"\\?\{0}:\.")]
    [InlineData(@"\\?\{0}:\x\..")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume999\")]
    [InlineData(@"\\?\Volume{{00000000-0000-0000-0000-00000000dead}}\.")]
    public async Task Every_verb_refuses_every_spelling_of_a_root(string shape)
    {
        var root = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, UnusedRoot()[0]);
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;
        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        try
        {
            foreach (var handle in new[]
                     {
                         ops.Delete([root]),
                         ops.Trash([root]),
                         ops.Move([root], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([root], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await Settled(handle);

                Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
            }

            Assert.Empty(asked);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// What the pane's gate hides is what the engine refuses: a rename of a
    /// drive root throws before touching anything.
    ///
    /// **Here, not in the Ui tests, where it was first written** — they build
    /// on Linux too, without Vaktari.Windows, and the reference broke that
    /// build (found running the Ui classes in WSL).
    /// </summary>
    [Fact]
    public async Task The_rename_engine_refuses_a_drive_root()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var refused = await Assert.ThrowsAsync<IOException>(
            async () => await new WindowsFileOperations().RenameAsync(root, "renamed", CancellationToken.None));

        Assert.Contains("drive root cannot be renamed", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A drive letter standing for a fresh temporary folder, made with subst
    /// and removed again — a real drive root whose contents are the test's
    /// own, so an engine that walks it deletes nothing that matters.
    ///
    /// Letters from the front of the alphabet, where
    /// <see cref="UnusedRoot"/> takes them from the back.
    /// </summary>
    private sealed class SubstDrive : IDisposable
    {
        public SubstDrive()
        {
            var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();

            Letter = "GHIJKLMNOP".First(c => !taken.Contains(c));
            Folder = Directory.CreateTempSubdirectory("vaktari-subst").FullName;

            Subst($"{Letter}: \"{Folder}\"");

            if (!Directory.Exists(Root)) throw new InvalidOperationException($"subst did not make {Root}");
        }

        public char Letter { get; }

        public string Folder { get; }

        public string Root => $"{Letter}:\\";

        public void Dispose()
        {
            Subst($"{Letter}: /D");

            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static void Subst(string arguments)
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("subst.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })!;

            process.WaitForExit();
        }
    }

    /// <summary>
    /// **The engine's Delete emptied a subst drive** named
    /// "\\?\GLOBALROOT\??\Z:\" — the object manager's name for it, which
    /// GetFullPath leaves as written and the check read as a folder. Every
    /// verb refuses each device spelling of a real (subst) drive, and the
    /// drive's file is still there after all four were asked. The file
    /// system's own answer does not cover this one — a subst root is a folder
    /// on another volume — so it is the text that must.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:\")]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:\.")]
    [InlineData(@"\\?\GLOBALROOT\DosDevices\{0}:\")]
    [InlineData(@"\\?\{0}:\.")]
    [InlineData(@"\\?\{0}:\x\..")]
    public async Task Every_verb_refuses_a_device_spelling_of_a_subst_drive(string shape)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");
        Directory.CreateDirectory(Path.Combine(drive.Folder, "x"));

        var root = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter);
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;
        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        try
        {
            foreach (var handle in new[]
                     {
                         ops.Delete([root]),
                         ops.Trash([root]),
                         ops.Move([root], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([root], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await Settled(handle);

                Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
            }

            Assert.Empty(asked);
            Assert.True(File.Exists(marker), "the drive's file was deleted");
            Assert.Empty(Directory.EnumerateFileSystemEntries(into));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **The file system's own answer, however the volume is named.** Asked,
    /// never acted on: the system volume's root by its letter, by its
    /// GLOBALROOT device name, by its Volume GUID and with a ".." folded by
    /// Win32 — each opens the volume's root, whose path within its volume is
    /// "\". A folder is not a root, nor is a path that is not there.
    /// </summary>
    [WindowsFact]
    public void The_file_system_calls_a_volume_root_a_root_however_it_is_named()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var guid = new char[64];
        Assert.True(Native.GetVolumeNameForVolumeMountPoint(root, guid, (uint)guid.Length));
        var byGuid = new string(guid, 0, Array.IndexOf(guid, '\0'));

        Assert.True(VolumeRootOnDisk.Is(root));
        Assert.True(VolumeRootOnDisk.Is(byGuid), byGuid);
        Assert.True(VolumeRootOnDisk.Is(@"\\?\GLOBALROOT" + DeviceOf(root) + @"\"), DeviceOf(root));
        Assert.True(VolumeRootOnDisk.Is(root + @"no-such-folder\.."));

        Assert.False(VolumeRootOnDisk.Is(Path.GetTempPath()));
        Assert.False(VolumeRootOnDisk.Is(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.False(VolumeRootOnDisk.Is(UnusedRoot()));
    }

    /// <summary>The NT device a drive letter stands for — "\Device\HarddiskVolume3"
    /// — read from the path a handle on its root reaches.</summary>
    private static unsafe string DeviceOf(string root)
    {
        var handle = Native.CreateFile(
            root, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS, 0);

        Assert.NotEqual(Native.INVALID_HANDLE_VALUE, handle);

        try
        {
            var buffer = stackalloc char[512];

            // VOLUME_NAME_NT: "\Device\HarddiskVolume3\".
            var length = Native.GetFinalPathNameByHandle(handle, buffer, 512, 0x2);

            return new string(buffer, 0, (int)length).TrimEnd('\\');
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// **A link to a root is the link**: a junction leading to the system
    /// volume's root, and a subst drive's root — a folder on another volume —
    /// are not roots to the file system. Removing the junction removes the
    /// junction; the subst drive is the text guard's to refuse.
    /// </summary>
    [WindowsFact]
    public void A_junction_to_a_root_and_a_subst_root_are_not_roots_to_the_file_system()
    {
        var holder = Directory.CreateTempSubdirectory("vaktari-junction").FullName;
        var junction = Path.Combine(holder, "to-root");

        try
        {
            Directory.CreateDirectory(junction);
            Native.CreateJunction(junction, Path.GetPathRoot(Path.GetTempPath())!);

            Assert.False(VolumeRootOnDisk.Is(junction));

            using var drive = new SubstDrive();

            Assert.False(VolumeRootOnDisk.Is(drive.Root));
            Assert.True(VolumeRoots.IsVolumeRoot(drive.Root));
        }
        finally
        {
            // The junction first, as a link: never recursively through it.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(holder);
        }
    }

    /// <summary>
    /// **Every verb asks the file system too, in its worker.** A temporary
    /// folder stands in for a root through the engine's seam — a real volume
    /// is never where a guard's absence is tried — and Delete, Trash, Move
    /// and Copy each refuse it with the refusal's sentence, leaving its file
    /// where it was.
    /// </summary>
    [WindowsFact]
    public async Task Every_verb_refuses_what_the_file_system_calls_a_root()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-onDisk").FullName;
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;
        var marker = Path.Combine(root, "marker.txt");
        File.WriteAllText(marker, "x");

        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RootOnDisk = path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase),
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        try
        {
            foreach (var handle in new[]
                     {
                         ops.Delete([root]),
                         ops.Trash([root]),
                         ops.Move([root], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([root], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await Settled(handle);

                Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
            }

            Assert.Empty(asked);
            Assert.True(File.Exists(marker));
            Assert.Empty(Directory.EnumerateFileSystemEntries(into));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>And a folder is not a root: the refusal does not reach past
    /// the thing it is for.</summary>
    [Fact]
    public void A_folder_is_not_a_volume_root()
    {
        Assert.False(VolumeRoots.IsVolumeRoot(Path.Combine(Path.GetTempPath(), "anything")));
        Assert.True(VolumeRoots.IsVolumeRoot(UnusedRoot()));
    }
}
