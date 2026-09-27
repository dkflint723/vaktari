using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The engine's file-system question from the other side: what it must NOT
/// call a root, where it is asked, and three more device spellings of a real
/// (subst) drive — pinned by the third review round.
///
/// **A guard that refuses too much is a broken delete.** The question opens
/// every path the engine is handed, so a folder, a file, a junction (to a
/// drive's root or to anything else), a path past 260 characters and a path
/// that is not there must all still be deletable as themselves, and a
/// junction must go as a junction, leaving what it led to alone.
///
/// Every drive here is a subst of a temporary folder, so an engine that got it
/// wrong deletes nothing but the test's own files; the system drive is only
/// ever ASKED, never handed to a verb.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeRootOnDiskTests
{
    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    /// <summary>A drive letter for a fresh temporary folder, made with subst
    /// and removed again. Letters from Q to U: VolumeRootRefusalTests takes its
    /// subst drives from G and its unused letters from Z, and runs alongside.</summary>
    private sealed class SubstDrive : IDisposable
    {
        public SubstDrive()
        {
            var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();

            Letter = "QRSTU".First(c => !taken.Contains(c));
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
    /// **Nothing ordinary is a root to the file system, and each goes as
    /// itself.** Asked of a folder, a file, a junction to the system drive's
    /// root, a junction to a subst drive's root, a folder past 260 characters
    /// and a path that is not there: none is a root. Then the engine deletes
    /// each of them but the junction to the system drive — which the test
    /// removes itself, as a link — and the subst drive's own file is still
    /// there after its junction has gone.
    /// </summary>
    [WindowsFact]
    public async Task The_file_system_calls_nothing_ordinary_a_root_and_each_is_deleted_as_itself()
    {
        using var drive = new SubstDrive();

        var driveFile = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(driveFile, "the drive's own file");

        var holder = Directory.CreateTempSubdirectory("vaktari-ondisk").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(holder, "plain")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var file = Path.Combine(holder, "file.txt");
        File.WriteAllText(file, "f");
        var missing = Path.Combine(holder, "not-there");
        var toSystem = Path.Combine(holder, "to-system-root");
        var toSubst = Path.Combine(holder, "to-subst-root");
        Directory.CreateDirectory(toSystem);
        Native.CreateJunction(toSystem, Path.GetPathRoot(Path.GetTempPath())!);
        Directory.CreateDirectory(toSubst);
        Native.CreateJunction(toSubst, drive.Root);

        var deep = holder;
        while (deep.Length < 300) deep = Path.Combine(deep, "a-folder-name-of-some-length");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "leaf.txt"), "l");

        try
        {
            foreach (var path in new[] { folder, file, missing, toSystem, toSubst, deep })
            {
                Assert.False(VolumeRootOnDisk.Is(path), $"{path} was called a root");
                Assert.Null(VolumeRoots.RefuseOnDisk([path], VolumeRootOnDisk.Is));
            }

            var ops = new WindowsFileOperations();

            foreach (var path in new[] { toSubst, folder, file, deep })
            {
                var handle = await Settled(ops.Delete([path]));

                Assert.Equal(OperationState.Completed, handle.State);
                Assert.False(Path.Exists(path), $"{path} is still there");
            }

            Assert.True(File.Exists(driveFile), "the drive's file went with the junction to it");

            var nothing = await Settled(ops.Delete([missing]));
            Assert.NotEqual(VolumeRoots.Refusal, nothing.Error?.Message);
        }
        finally
        {
            // The junction to the system drive as a link, never recursively.
            if (Directory.Exists(toSystem)) Directory.Delete(toSystem);
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **Asked in the worker, not on the thread that pressed the key.** The
    /// question opens a handle per path — 10,000 paths measured at about a
    /// quarter of a second — so it must not run where the Delete was called.
    /// The seam waits for the call to have returned before it answers; asked
    /// on the caller's thread it would be answering before the call returned.
    /// </summary>
    [WindowsFact]
    public async Task The_file_system_is_asked_after_the_call_has_returned()
    {
        var holder = Directory.CreateTempSubdirectory("vaktari-offthread").FullName;
        var file = Path.Combine(holder, "a.txt");
        File.WriteAllText(file, "a");

        using var returned = new ManualResetEventSlim();
        var askedAfterReturn = new List<bool>();

        var ops = new WindowsFileOperations
        {
            RootOnDisk = path =>
            {
                var after = returned.Wait(TimeSpan.FromSeconds(5));
                lock (askedAfterReturn) askedAfterReturn.Add(after);
                return VolumeRootOnDisk.Is(path);
            },
        };

        try
        {
            var handle = ops.Delete([file]);
            returned.Set();
            await Settled(handle);

            Assert.Equal(OperationState.Completed, handle.State);
            Assert.NotEmpty(askedAfterReturn);
            Assert.All(askedAfterReturn, Assert.True);
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **Three more names for a real drive**, each opened by Win32 and each
    /// refused by every verb: the "\\.\" prefix, the object manager's names in
    /// another case, and "??" with no backslash after the letter. The drive is
    /// a subst of a temporary folder — to the file system a folder, so these
    /// are the text guard's to refuse — and its file is still there after all
    /// four verbs were asked.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\GLOBALROOT\??\{0}:\")]
    [InlineData(@"\\?\globalroot\dosdevices\{1}:\")]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:")]
    [InlineData(@"\\.\{0}:\x\..")]
    public async Task Every_verb_refuses_another_device_name_of_a_subst_drive(string shape)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");
        Directory.CreateDirectory(Path.Combine(drive.Folder, "x"));

        var root = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter, char.ToLowerInvariant(drive.Letter));
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
}
