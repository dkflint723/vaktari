using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A drive-relative path resolved to a string with a NUL in it.** From the
/// seventh review round (HOLE 7-A): with the per-drive current folder "=W:"
/// set to the bare "W:" — a malformed value a parent process can hand down in
/// the environment block — GetFullPath("W:..\..") answers "W:\0". No reading
/// was a root, the handle check could not open it, and Trash handed "W:\0" to
/// SHFileOperation, whose NUL-separated list read it as "W:": the subst
/// drive's backing folder was deleted outright, and nothing arrived in the
/// bin. With "=W:=C:" the recycler was asked for "C:\0".
///
/// Three guards now, each asked alone here: the text refuses a reading with a
/// NUL in it (the pane's question), every engine verb refuses a path that is
/// not full before reading it, and nothing with a NUL or not full is handed
/// to the shell. The first theory is the round's repro, pasted with only its
/// "=W:" values widened. The drive is a subst of a temporary folder, and the
/// class runs alone: "=W:" is the process's.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(CurrentDirectoryCollection.Name)]
public sealed partial class DriveRelativeNulTests
{
    [LibraryImport("kernel32.dll", EntryPoint = "SetEnvironmentVariableW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEnv(string name, string? value);

    [WindowsTheory]
    [InlineData(@"{0}:..\..", "{0}:")]
    [InlineData(@"{0}:.\..", "{0}:")]
    [InlineData(@"{0}:..\..\..", "{0}:")]
    [InlineData(@"{0}:..\..", "{0}: ")]
    [InlineData(@"{0}:..\..", "C:")]
    public async Task A_drive_relative_path_under_a_malformed_drive_directory_is_refused(string shape, string value)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-hole7").FullName;
        var marker = Path.Combine(folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");

        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = CurrentDirectoryCollection.SubstLetters.First(c => !taken.Contains(c));
        Subst($"{letter}: \"{folder}\"");

        var asked = new List<string>();
        var ops = new WindowsFileOperations { RecycleOverride = paths => { asked.AddRange(paths); return new RecycleResult(0, false); } };
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, letter);
        string? refusal;
        IOperationHandle trash;

        try
        {
            Assert.True(SetEnv($"={letter}:", string.Format(System.Globalization.CultureInfo.InvariantCulture, value, letter)));

            refusal = VolumeRoots.Refuse([path]);

            trash = ops.Trash([path]);
            await trash.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            SetEnv($"={letter}:", null);
            Subst($"{letter}: /D");
        }

        try
        {
            Assert.Empty(asked);                // RED: the recycler is asked for "W:\0"
            Assert.NotNull(refusal);            // RED: the text allows it

            // The bare letter is the value that makes GetFullPath answer with
            // the NUL; the others resolve to something the text reads as well.
            if (value == "{0}:") Assert.Equal(VolumeRoots.NulRefusal, refusal);
            Assert.True(File.Exists(marker));
            Assert.Equal(VolumeRoots.NotFullRefusal, trash.Error?.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// **A resolution that is not full is refused by the text** — even one
    /// GetFullPath hands back as written. Under "=W:" set to the bare "W:",
    /// "W:x" resolves to "W:x"; under "=W:=C:", to "C:x". Neither is a path
    /// any rule reads as the folder it names. Text only, on a subst drive of a
    /// temporary folder, since GetFullPath answers like this only for a drive
    /// that exists.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"{0}:x", "{0}:")]
    [InlineData(@"{0}:x", "C:")]
    [InlineData(@"{0}:x\y", "{0}:")]
    public void A_drive_relative_path_that_resolves_to_no_full_path_is_refused_by_the_text(string shape, string value)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-hole7").FullName;
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = CurrentDirectoryCollection.SubstLetters.First(c => !taken.Contains(c));
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, letter);
        string resolved;
        string? refusal;

        Subst($"{letter}: \"{folder}\"");

        try
        {
            Assert.True(SetEnv($"={letter}:", string.Format(System.Globalization.CultureInfo.InvariantCulture, value, letter)));

            resolved = Path.GetFullPath(path);
            refusal = VolumeRoots.Refuse([path]);
        }
        finally
        {
            SetEnv($"={letter}:", null);
            Subst($"{letter}: /D");
            Directory.Delete(folder, recursive: true);
        }

        Assert.False(Path.IsPathFullyQualified(resolved), $"{path} resolved to {resolved}, so it proves nothing");
        Assert.Equal(VolumeRoots.NotFullRefusal, refusal);
    }
    /// <summary>
    /// **The pane's question, alone**: a reading with a NUL in it is refused
    /// in its own words — a NUL written into a path, in any spelling (the
    /// resolved "W:\0" is asked in the theory above, where W: exists: only
    /// then does GetFullPath answer with the NUL). ReachablePath, which every
    /// verb asks of each item, says the same of a NUL. Text only; the letter
    /// answers to nothing.
    /// </summary>
    [WindowsFact]
    public void A_nul_in_any_reading_is_refused_by_the_text()
    {

        // A "\\?\" one is refused before its NUL is looked at: Win32's fold
        // throws on it, and a device path that will not fold is refused as one.
        Assert.Equal(VolumeRoots.DeviceRefusal, VolumeRoots.Refuse(["\\\\?\\C:\\a\0b"]));

        foreach (var written in new[] { "C:\\a\0b", "\\\\?\\C:\\a\0b", "\\??\\C:\\a\0", "\\\\server\\share\\a\0b" })
        {
            if (!written.StartsWith(@"\\?\", StringComparison.Ordinal))
                Assert.Equal(VolumeRoots.NulRefusal, VolumeRoots.Refuse([written]));

            Assert.True(ReachablePath.Refuse(written) is not null, "ReachablePath let a NUL through");
            Assert.True(ReachablePath.RefuseLanding(written) is not null, "a landing with a NUL was let through");
        }
    }

    /// <summary>
    /// **Nothing with a NUL, and nothing not full, goes to the shell** —
    /// SHFileOperation's list is NUL-separated, so "W:\0" arrived as "W:". The
    /// entry refuses such a path first; this is the last word before the
    /// shell, asked alone.
    /// </summary>
    [WindowsFact]
    public void The_recycler_is_never_handed_a_nul_or_a_path_that_is_not_full()
    {
        foreach (var unfit in new[] { "W:\0", "C:\0", @"C:\a" + "\0" + "b", "W:", @"W:x", @"\x", "x" })
            Assert.True(WindowsFileOperations.Unrecyclable([@"C:\fine", unfit]) is not null, $"{unfit.Replace("\0", "\\0")} would reach the shell");

        Assert.Null(WindowsFileOperations.Unrecyclable([@"C:\fine", @"\\server\share\x", @"\\?\C:\report "]));
    }

    private static void Subst(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("subst.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
    }
}
