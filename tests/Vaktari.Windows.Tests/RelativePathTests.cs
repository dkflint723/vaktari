using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A relative path is read the way Win32 resolves it: against the current
/// directory.** From the sixth review round (HOLE 6-A): with the process's
/// current directory under "\\.\GLOBALROOT\??\X:\x" — a device path every
/// rule refuses when it is written out — ".." had no prefix to read, so the
/// text called it a folder, and the engine's handle check calls a subst
/// drive's root a folder too. Delete emptied a subst drive, Trash handed
/// "\\.\GLOBALROOT\??\X:" to the recycler, and Copy and Move took a DOS-device
/// alias's entries. Nothing in Vaktari sets the current directory or hands an
/// engine a relative path; the guards' contract is that no route reaches a
/// root all the same.
///
/// The first theory is that round's repro, pasted with its letters moved to
/// ones no other class here takes, and every verb asked rather than two. Every
/// drive is a subst of a temporary folder and every alias names one, so an
/// engine without the guard deletes nothing but this class's own files. The
/// current directory is the process's, so the class runs alone, and puts it
/// back before it asserts anything.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(CurrentDirectoryCollection.Name)]
public sealed partial class RelativePathTests
{
    private const string Alias = "VAKTARIRELATIVECWD";

    [LibraryImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DefineDosDevice(uint flags, string name, string? target);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, nint info, uint length, out uint returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>
    /// HOLE 6-A, and its spellings: "{0}" is the drive's letter (or the
    /// alias's name), "{1}" this logon session's id. Each current directory
    /// is inside the drive or alias, and the relative path climbs to its root.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\GLOBALROOT\??\{0}:\x", "..", false)]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:\x", "..", false)]
    [InlineData(@"\\.\GLOBALROOT\??\{0}:\", ".", false)]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:\x", @"..\.", false)]
    [InlineData(@"\\.\GLOBALROOT\Sessions\0\DosDevices\{1}\{0}:\x", "..", false)]
    [InlineData(@"\\?\GLOBALROOT\Sessions\0\DosDevices\{1}\{0}:\x", "..", false)]
    [InlineData(@"\\.\{0}\x", "..", true)]
    [InlineData(@"\\.\GLOBALROOT\??\{0}\x", "..", true)]
    [InlineData(@"\\?\GLOBALROOT\??\{0}\", ".", true)]
    public async Task A_relative_path_under_a_device_directory_is_refused(string cwdShape, string relative, bool alias)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-relative").FullName;
        var into = Directory.CreateTempSubdirectory("vaktari-relative-into").FullName;
        var marker = Path.Combine(folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");
        Directory.CreateDirectory(Path.Combine(folder, "x"));

        var name = alias ? Alias : SubstOf(folder).ToString();

        if (alias) Assert.True(DefineDosDevice(0, Alias, folder));

        var cwd = Environment.CurrentDirectory;
        var asked = new List<string>();
        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        var refusals = new List<string?>();

        try
        {
            var device = string.Format(System.Globalization.CultureInfo.InvariantCulture, cwdShape, name, LogonSession());

            Environment.CurrentDirectory = device;

            // The pane's question, and each engine verb's.
            refusals.Add(VolumeRoots.Refuse([relative]));

            foreach (var handle in new[]
                     {
                         ops.Trash([relative]),
                         ops.Delete([relative]),
                         ops.Move([relative], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([relative], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                refusals.Add(handle.Error?.Message);
            }
        }
        finally
        {
            Environment.CurrentDirectory = cwd;

            if (alias) DefineDosDevice(2, Alias, folder);
            else Subst($"{name}: /D");
        }

        try
        {
            Assert.Empty(asked);
            Assert.True(File.Exists(marker), "the drive was emptied through a relative path");
            Assert.Empty(Directory.GetFileSystemEntries(into));

            // The pane reads the path as it resolves, and refuses it as the
            // root it reaches; the engines do not act on a path that is not
            // full at all (seventh review round), so each refuses it as that.
            Assert.True(refusals[0] is VolumeRoots.Refusal or VolumeRoots.DeviceRefusal, $"the pane did not refuse a root: {refusals[0] ?? "(nothing)"}");

            foreach (var refusal in refusals.Skip(1))
                Assert.Equal(VolumeRoots.NotFullRefusal, refusal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **IsVolumeRoot reads a relative path as it resolves, as Refuse does.**
    /// The pane and the menus ask it of a path on its own; the theory above
    /// asks Refuse and the verbs, and each has its own readings, so taking
    /// the resolved one away from IsVolumeRoot alone reddened nothing. Each
    /// current directory here is inside a subst drive or an alias of a
    /// temporary folder, and the relative path climbs to its root; nothing
    /// is handed to a verb.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\GLOBALROOT\??\{0}:\x", "..", false)]
    [InlineData(@"\\?\GLOBALROOT\??\{0}:\x", @"..\.", false)]
    [InlineData(@"\\.\GLOBALROOT\??\{0}:\", ".", false)]
    [InlineData(@"\\.\{0}:\x", "..", false)]
    [InlineData(@"\\.\{0}\x", "..", true)]
    [InlineData(@"\\?\GLOBALROOT\??\{0}\", ".", true)]
    public void IsVolumeRoot_reads_a_relative_path_as_it_resolves(string cwdShape, string relative, bool alias)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-relative").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "x"));

        var name = alias ? Alias : SubstOf(folder).ToString();

        if (alias) Assert.True(DefineDosDevice(0, Alias, folder));

        var cwd = Environment.CurrentDirectory;
        bool root, folderUnder;

        try
        {
            Environment.CurrentDirectory = string.Format(System.Globalization.CultureInfo.InvariantCulture, cwdShape, name);

            root = VolumeRoots.IsVolumeRoot(relative);
            folderUnder = VolumeRoots.IsVolumeRoot(Path.Combine(relative, "x"));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;

            if (alias) DefineDosDevice(2, Alias, folder);
            else Subst($"{name}: /D");

            Directory.Delete(folder, recursive: true);
        }

        Assert.True(root, $"\"{relative}\" under {cwdShape} is the root of {name} and was not called one");
        Assert.False(folderUnder, $"\"{Path.Combine(relative, "x")}\" under {cwdShape} is a folder and was called a root");
    }

    /// <summary>
    /// **A relative path that does not climb out is still a folder**, and
    /// "\x" — rooted, drive-relative in name only — is the folder on the
    /// current drive. Ordinary current directory; nothing is handed to a verb.
    /// </summary>
    [WindowsFact]
    public void A_relative_path_to_a_folder_is_not_refused()
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-relative").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "x"));
        var cwd = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = Path.Combine(folder, "x");

            Assert.Null(VolumeRoots.Refuse([".", "..", @"..\x", "y"]));
            Assert.Null(VolumeRoots.Refuse([@"\" + Path.GetRelativePath(Path.GetPathRoot(folder)!, folder)]));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A letter for a subst of this folder, from
    /// <see cref="CurrentDirectoryCollection.SubstLetters"/>, which no other
    /// test picks.</summary>
    private static char SubstOf(string folder)
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = CurrentDirectoryCollection.SubstLetters.First(c => !taken.Contains(c));

        Subst($"{letter}: \"{folder}\"");

        Assert.True(Directory.Exists($"{letter}:\\"), $"subst did not make {letter}:");

        return letter;
    }

    private static void Subst(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("subst.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })!;

        process.WaitForExit();
    }

    /// <summary>This logon session's id as the object manager spells it under
    /// \Sessions\0\DosDevices, from the process's token.</summary>
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
            CloseHandle(token);
        }
    }
}

/// <summary>
/// The process's current directory is one per process, and a test that moves
/// it moves it for every test running beside it — and for every process one
/// of them starts. So the classes that move it run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "CurrentDirectory";

    /// <summary>
    /// The letters this collection's classes subst temporary folders onto:
    /// ones no other test in the repository picks, for a subst or as a
    /// letter that answers to nothing (VolumeRootRefusalTests G–P,
    /// VolumeRootOnDiskTests M–W, the unused-letter probes V–Z). The classes
    /// here run alone, so they never meet each other.
    /// </summary>
    public const string SubstLetters = "EFBA";
}
