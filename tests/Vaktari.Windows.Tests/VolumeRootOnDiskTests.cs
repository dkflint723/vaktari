using System.Runtime.InteropServices;
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
public sealed partial class VolumeRootOnDiskTests
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

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, nint info, uint length, out uint returned);

    /// <summary>This logon session's id as the object manager spells it under
    /// \Sessions\0\DosDevices — "00000000-0004a48b" — read from the process's
    /// token (TokenStatistics' AuthenticationId).</summary>
    private static string LogonSession()
    {
        Assert.True(OpenProcessToken(-1, 0x8, out var token));

        var buffer = Marshal.AllocHGlobal(256);

        try
        {
            Assert.True(GetTokenInformation(token, 10, buffer, 256, out _));

            var low = (uint)Marshal.ReadInt32(buffer, 8);
            var high = (uint)Marshal.ReadInt32(buffer, 12);

            return $"{high:x8}-{low:x8}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Native.CloseHandle(token);
        }
    }

    private static async Task AssertEveryVerbRefuses(string path, string sentence, string marker)
    {
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
                         ops.Delete([path]),
                         ops.Trash([path]),
                         ops.Move([path], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([path], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await Settled(handle);

                Assert.Equal(sentence, handle.Error?.Message);
            }

            Assert.Empty(asked);
            Assert.True(File.Exists(marker), $"the drive's file was deleted through {path}");
            Assert.Empty(Directory.EnumerateFileSystemEntries(into));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **The third round's names for a real drive, refused before any is
    /// opened.** Delete emptied a subst drive's folder through the logon
    /// session's own name for the letter, in both prefixes; "Global" is one
    /// more. None is a form the text reads, so every verb refuses it with the
    /// device sentence — the session id is this process's real one, so an
    /// engine without the refusal would reach the drive. "\\.\X:\ " and
    /// "\\.\X:\..." are read the way Win32 reads them, as the drive's root,
    /// and get the drive's sentence. The drive's file is there at the end.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\?\GLOBALROOT\Sessions\0\DosDevices\{2}\{0}:\", true, true)]
    [InlineData(@"\\.\GLOBALROOT\Sessions\0\DosDevices\{2}\{0}:\", true, true)]
    [InlineData(@"\\?\GLOBALROOT\Sessions\0\DosDevices\{2}\{0}:", true, false)]
    [InlineData(@"\\?\Global\{0}:\", true, false)]
    [InlineData(@"\\?\GLOBALROOT\??\Global\{0}:\", true, false)]
    [InlineData(@"\\?\GLOBALROOT\GLOBAL??\Global\{0}:\", true, false)]
    [InlineData(@"\\.\{0}:\ ", false, true)]
    [InlineData(@"\\.\{0}:\...", false, true)]
    public async Task Every_verb_refuses_a_device_path_it_does_not_read(string shape, bool device, bool reaches)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");

        var path = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter, char.ToLowerInvariant(drive.Letter), LogonSession());

        // Where the name reaches the drive, an engine without the refusal
        // empties it. "Global" names the machine's drives rather than this
        // session's subst, so those reach nothing here and would fail
        // harmlessly unrefused — still refused, with the device sentence.
        if (reaches) Assert.True(Directory.Exists(path), $"{path} does not reach the drive, so it proves nothing");

        await AssertEveryVerbRefuses(path, device ? VolumeRoots.DeviceRefusal : VolumeRoots.Refusal, marker);
    }

    /// <summary>
    /// **The NT prefix "\??\" is the device namespace too.** Win32 hands a
    /// path that starts "\??\" to the object manager as written, and .NET
    /// treats it as a device path (PathInternal.IsDevice) and folds nothing,
    /// so "\??\GLOBALROOT\…" reaches every name "\\?\GLOBALROOT\…" does.
    ///
    /// *(From the fourth review round's repro, with one change: it expected
    /// the device sentence for all four. The first two are names this class
    /// reads as the drive's root — as their "\\?\" twins are, which
    /// Every_verb_refuses_another_device_name_of_a_subst_drive pins to the
    /// drive's sentence — so they are refused in the drive's words, and the
    /// session's names, which are not read, in the device's.)*
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\??\GLOBALROOT\??\{0}:\", false)]
    [InlineData(@"\??\GLOBALROOT\DosDevices\{0}:\", false)]
    [InlineData(@"\??\GLOBALROOT\Sessions\0\DosDevices\{2}\{0}:\", true)]
    [InlineData(@"\??\GLOBALROOT\Sessions\0\DosDevices\{2}\{0}:", true)]
    public async Task Every_verb_refuses_a_device_path_under_the_nt_prefix(string shape, bool device)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");

        var path = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter, char.ToLowerInvariant(drive.Letter), LogonSession());

        await AssertEveryVerbRefuses(path, device ? VolumeRoots.DeviceRefusal : VolumeRoots.Refusal, marker);
    }

    /// <summary>
    /// **A directory's index stream names the directory.** "X:\::$INDEX_ALLOCATION"
    /// and "X:\:$I30:$INDEX_ALLOCATION" open the root of X: — measured on the
    /// system drive (asked only): VolumeRootOnDisk says root, the text says
    /// folder. On a subst drive the engine handed each to the recycler.
    /// Whatever sentence is chosen, the recycler must not be asked.
    ///
    /// *(From the fourth review round's repro, which asserted only that the
    /// text refuses; here every verb is asked, and the drive's file checked.)*
    /// </summary>
    [WindowsTheory]
    [InlineData(@"{0}:\::$INDEX_ALLOCATION")]
    [InlineData(@"{0}:\:$I30:$INDEX_ALLOCATION")]
    [InlineData(@"\\?\{0}:\::$INDEX_ALLOCATION")]
    [InlineData(@"\??\{0}:\::$INDEX_ALLOCATION")]
    [InlineData(@"{0}::$INDEX_ALLOCATION")]
    public async Task Every_verb_refuses_a_drive_named_by_its_index_stream(string shape)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");

        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter);

        Assert.NotNull(VolumeRoots.Refuse([path]));

        await AssertEveryVerbRefuses(path, VolumeRoots.StreamRefusal, marker);
    }

    /// <summary>
    /// **A junction whose stored target is a device name is copied and moved
    /// as the link it is — or says why not.** The fourth review round saw
    /// Move and Copy of one "complete and do nothing". They do not do nothing
    /// silently: a junction can only say a drive-rooted path, so the copy is
    /// a symbolic link, which an unprivileged process cannot make, and the
    /// item fails with that reason while the operation completes. Nothing
    /// lands, the junction stays where it was, and what it names is never
    /// entered. The target's letter answers to nothing.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_to_a_device_name_is_moved_as_a_link_or_reported()
    {
        var holder = Directory.CreateTempSubdirectory("vaktari-devjunction").FullName;
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var nowhere = "ZYXW".First(c => !taken.Contains(c));

        try
        {
            foreach (var move in new[] { true, false })
            foreach (var suffix in new[] { "", @"\", @"\." })
            {
                var junction = Path.Combine(holder, $"j{(move ? "m" : "c")}{suffix.Length}");
                Directory.CreateDirectory(junction);
                Native.CreateJunction(junction, $@"\??\GLOBALROOT\GLOBAL??\{nowhere}:\");

                var ops = new WindowsFileOperations();
                var handle = await Settled(move
                    ? ops.Move([junction + suffix], into, _ => ValueTask.FromResult(ConflictResolution.Skip))
                    : ops.Copy([junction + suffix], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

                var landed = Directory.EnumerateFileSystemEntries(into).ToList();

                // Either the link went across as a link, or the item says why not.
                if (landed.Count == 0) Assert.NotEmpty(handle.Problems);
                else Assert.All(landed, entry => Assert.NotNull(new DirectoryInfo(entry).LinkTarget));

                Assert.True(Directory.Exists(junction) || landed.Count > 0, "the junction went and nothing arrived");

                foreach (var entry in landed) Directory.Delete(entry);
                if (Directory.Exists(junction)) Directory.Delete(junction);
            }
        }
        finally
        {
            Directory.Delete(into, recursive: true);
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **A drive mapped to a folder on a share, by every name the review
    /// used.** Delete emptied the mapped folder through the session's name for
    /// the letter. The letter is mapped with net use to a temporary folder
    /// reached through this machine's own administrative share, and unmapped
    /// again; each name is refused, and the folder's file is still there.
    /// To the file system the mapped root is a folder inside a share — it is
    /// the text that refuses the letter itself.
    /// </summary>
    [WindowsFact]
    public async Task Every_verb_refuses_every_name_of_a_mapped_drive()
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-mapped").FullName;
        var marker = Path.Combine(folder, "marker.txt");
        File.WriteAllText(marker, "the mapped folder's own file");

        var share = $@"\\localhost\{folder[0]}$\{folder[3..]}";
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "VW".First(c => !taken.Contains(c));

        Assert.Equal(0, NetUse($"{letter}: \"{share}\""));

        try
        {
            Assert.True(File.Exists($@"{letter}:\marker.txt"), $"{letter}: does not reach {share}");
            Assert.False(VolumeRootOnDisk.Is($@"{letter}:\"));

            await AssertEveryVerbRefuses($@"{letter}:\", VolumeRoots.Refusal, marker);
            await AssertEveryVerbRefuses($@"\\?\{letter}:\", VolumeRoots.Refusal, marker);
            await AssertEveryVerbRefuses($@"\\.\{letter}:\ ", VolumeRoots.Refusal, marker);
            await AssertEveryVerbRefuses(
                $@"\\?\GLOBALROOT\Sessions\0\DosDevices\{LogonSession()}\{letter}:\", VolumeRoots.DeviceRefusal, marker);
            await AssertEveryVerbRefuses(
                $@"\??\GLOBALROOT\Sessions\0\DosDevices\{LogonSession()}\{letter}:\", VolumeRoots.DeviceRefusal, marker);
            await AssertEveryVerbRefuses($@"{letter}:\::$INDEX_ALLOCATION", VolumeRoots.StreamRefusal, marker);
        }
        finally
        {
            NetUse($"{letter}: /delete /y");
            Directory.Delete(folder, recursive: true);
        }
    }

    private static int NetUse(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("net.exe", "use " + arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode;
    }

    /// <summary>
    /// **A share's root is a root to the file system too.** Without the
    /// volume, "\\localhost\c$\" answers "\localhost\c$", not "\", so the
    /// engine's own question never refused a share root or a drive mapped to
    /// one; with the share it answers "\\?\UNC\localhost\c$\", which the text
    /// reads as the share's root. Asked, never acted on: this machine's
    /// administrative share for the temporary folder's drive, and a folder
    /// under it.
    /// </summary>
    [WindowsFact]
    public void The_file_system_calls_a_share_root_a_root()
    {
        var temp = Path.GetTempPath();
        var share = $@"\\localhost\{temp[0]}$\";

        Assert.True(Directory.Exists(share), $"{share} cannot be reached");

        Assert.True(VolumeRootOnDisk.Is(share));
        Assert.True(VolumeRootOnDisk.Is($@"\\?\UNC\localhost\{temp[0]}$\"));
        Assert.False(VolumeRootOnDisk.Is(share + temp[3..]));
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

    /// <summary>
    /// **More spellings of a real (subst) drive, each refused by every verb**
    /// — the fourth review round's: the NT form "\??\X:\" that Win32 and .NET
    /// both pass through as written, forward slashes throughout, "." and ".."
    /// folded, "??" inside an ordinary path, and a path past 260 characters
    /// that folds back to the root. Each is opened by Win32 as the drive's
    /// root; to the file system a subst root is a folder, so these rest on the
    /// text, and the drive's file is there at the end.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\??\{0}:\")]
    [InlineData(@"\??\{0}:")]
    [InlineData(@"{0}:\.\")]
    [InlineData(@"{0}:/./")]
    [InlineData(@"//?/{0}:/")]
    [InlineData(@"/\?\{0}:\")]
    [InlineData(@"{0}:\x\..\??\..")]
    [InlineData(@"{0}:\x\.\..\")]
    [InlineData(@"long")]
    public async Task Every_verb_refuses_more_spellings_of_a_subst_drive(string shape)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");
        Directory.CreateDirectory(Path.Combine(drive.Folder, "x"));

        var root = shape == "long"
            ? drive.Root + string.Concat(Enumerable.Repeat(@"x\..\", 80))
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter);

        Assert.True(Directory.Exists(root), $"{root} does not reach the drive, so it proves nothing");

        await AssertEveryVerbRefuses(root, VolumeRoots.Refusal, marker);
    }

    /// <summary>
    /// **A junction to a drive, named with a trailing separator, is still the
    /// junction to every verb.** "j\" and "j\." name what the link leads to,
    /// and neither guard calls them a root — the text sees a folder, and the
    /// file system is asked of the link itself — so what keeps the drive safe
    /// is that each verb acts on the link: Delete and Move take the link away
    /// or elsewhere, Copy copies a link, and the bin is handed the link's own
    /// path. Measured in the fourth review round; the drive is a subst of a
    /// temporary folder, and its file and folder are there after each verb.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\")]
    [InlineData(@"\.")]
    public async Task Each_verb_on_a_junction_to_a_drive_named_through_it_acts_on_the_link(string tail)
    {
        using var drive = new SubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");
        Directory.CreateDirectory(Path.Combine(drive.Folder, "x"));

        var holder = Directory.CreateTempSubdirectory("vaktari-junction-tail").FullName;
        var into = Directory.CreateTempSubdirectory("vaktari-junction-into").FullName;
        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                lock (asked) asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        string Junction(string name)
        {
            var at = Path.Combine(holder, name);
            Directory.CreateDirectory(at);
            Native.CreateJunction(at, drive.Root);
            return at;
        }

        try
        {
            var deleted = Junction("deleted");
            Assert.Equal(OperationState.Completed, (await Settled(ops.Delete([deleted + tail]))).State);
            Assert.False(Path.Exists(deleted), "Delete left the junction");

            var binned = Junction("binned");
            Assert.Equal(OperationState.Completed, (await Settled(ops.Trash([binned + tail]))).State);
            Assert.Single(asked);
            Assert.Equal(binned, asked[0].TrimEnd('\\', '.'), ignoreCase: true);

            var moved = Junction("moved");
            Assert.Equal(OperationState.Completed,
                (await Settled(ops.Move([moved + tail], into, _ => ValueTask.FromResult(ConflictResolution.Skip)))).State);

            var copied = Junction("copied");
            Assert.Equal(OperationState.Completed,
                (await Settled(ops.Copy([copied + tail], into, _ => ValueTask.FromResult(ConflictResolution.Skip)))).State);

            // What landed is two links, not the drive's contents.
            var landed = Directory.EnumerateFileSystemEntries(into).Select(e => new DirectoryInfo(e)).ToList();
            Assert.Equal(["copied", "moved"], landed.Select(d => d.Name).Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(landed, d => Assert.NotNull(d.LinkTarget));

            Assert.True(File.Exists(marker), "the drive's file went through a junction to it");
            Assert.True(Directory.Exists(Path.Combine(drive.Folder, "x")), "the drive's folder went through a junction to it");
        }
        finally
        {
            // Links first, as links, so nothing is removed through one.
            foreach (var dir in new[] { holder, into })
                foreach (var entry in Directory.EnumerateDirectories(dir))
                    if (new DirectoryInfo(entry).LinkTarget is not null) Directory.Delete(entry);

            Directory.Delete(holder, recursive: true);
            Directory.Delete(into, recursive: true);
        }
    }
}
