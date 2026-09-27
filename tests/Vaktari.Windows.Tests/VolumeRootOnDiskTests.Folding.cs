using System.Runtime.InteropServices;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **Device paths as Win32 folds them**, from the fifth review round: ".."
/// right after a device takes the device away, and every spelling of "\\?\"
/// but the literal one is folded like "\\.\". The first two tests are that
/// round's repros, pasted as given (only their names changed); the rest are
/// the ".." combinations under each prefix, on subst drives of temporary
/// folders and on a DOS device named without a colon.
/// </summary>
public sealed partial class VolumeRootOnDiskTests
{
    [LibraryImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DefineDosDevice(uint flags, string name, string? target);

    /// <summary>
    /// HOLE 5-A: ".." after a device path's first name takes the device name away (Win32 folds
    /// "\\.\" and "//?/" paths with "\\.\" as the root), so "\\.\W:\..\NAME\" opens "\\.\NAME\"
    /// while the text reads a folder under W: - past the deny-by-default. NAME here is a
    /// DOS device without a colon (a subst without a letter, made with DefineDosDevice in
    /// this session), which the text calls a drive when it is named directly. Measured at
    /// 8fdb786: Delete emptied the folder, Trash handed "\\.\NAME\" to the recycler.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\{1}:\..\{0}\")]
    [InlineData(@"\\.\{1}:\..\{0}")]
    [InlineData(@"//./{1}:/../{0}/")]
    [InlineData(@"//?/{1}:/../{0}/")]
    [InlineData(@"\\.\{1}:\..\GLOBALROOT\??\{0}\")]
    [InlineData(@"\\.\{1}:\x\..\..\{0}\")]
    public async Task A_device_name_popped_by_dot_dot_is_refused(string shape)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-alias").FullName;
        var marker = Path.Combine(folder, "marker.txt");
        File.WriteAllText(marker, "the alias's own file");
        const string alias = "VAKTARIHOLEREPRO5";
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var nowhere = "ZYXW".First(c => !taken.Contains(c));

        Assert.True(DefineDosDevice(0, alias, folder));

        try
        {
            var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, alias, nowhere);

            Assert.True(File.Exists(Path.Combine(path, "marker.txt")), $"{path} does not reach the folder, so it proves nothing");
            Assert.Equal(VolumeRoots.Refusal, VolumeRoots.Refuse([$@"\\.\{alias}\"]));

            await AssertEveryVerbRefuses(path, VolumeRoots.DeviceRefusal, marker);
        }
        finally
        {
            DefineDosDevice(2, alias, folder);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// GAP 5-B (text only; no data reached - ReachablePath refuses the engine's side):
    /// "//?/X:/ ", "\\?/X:/...", "/\?\X:\ " are normalised by Win32 like "\\.\" (only "\\?\"
    /// with backslashes skips normalisation, per .NET's PathInternal.IsExtended), so each
    /// opens X:'s root, while VolumeRoots reads them as "\\?\" and keeps the " " / "..." name.
    /// The pane lets them through to the engine (measured: TrashPaths, PasteInto, DeleteChosen).
    /// </summary>
    [WindowsTheory]
    [InlineData(@"//?/{0}:/ ")]
    [InlineData(@"//?/{0}:/...")]
    [InlineData(@"\\?/{0}:/ ")]
    [InlineData(@"/\?\{0}:\ ")]
    [InlineData(@"\/?/{0}:/ ")]
    public void A_normalised_device_prefix_is_read_as_normalised(string shape)
    {
        using var drive = new SubstDrive();
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter);

        Assert.Equal(@"\\?\" + drive.Letter + @":\", Path.GetFullPath(path));
        Assert.Equal(VolumeRoots.Refusal, VolumeRoots.Refuse([path]));
    }

    /// <summary>
    /// A drive letter for a fresh temporary folder, made with subst and
    /// removed again — from P down to M, clear of <see cref="SubstDrive"/>'s
    /// Q to U and of VolumeRootRefusalTests', which counts up from G.
    /// </summary>
    private sealed class LowSubstDrive : IDisposable
    {
        public LowSubstDrive()
        {
            var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();

            Letter = "PONM".First(c => !taken.Contains(c));
            Folder = Directory.CreateTempSubdirectory("vaktari-subst").FullName;

            Subst($"{Letter}: \"{Folder}\"");

            if (!Directory.Exists($@"{Letter}:\")) throw new InvalidOperationException($"subst did not make {Letter}:");
        }

        public char Letter { get; }

        public string Folder { get; }

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
    /// **Every verb, on a real (subst) drive, under every prefix Win32 folds**:
    /// a ".." that climbs from one drive to another ("\\.\W:\..\X:\") is a
    /// device path that left the one it was written under, and is refused as
    /// one — no longer only because the stream rule happened to see X:'s
    /// colon — while a spelling that folds to the drive's own root without
    /// leaving it ("//?/X:/ ", "\\?/X:/...", "/\?\X:\. ", "\/?/X:/x/..") is
    /// the drive, and refused as it. Each is asserted to reach the drive, so
    /// an engine without the refusal would act on it; the drive's file and
    /// folder are there at the end, and nothing landed.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\{1}:\..\{0}:\", true)]
    [InlineData(@"//./{1}:/../{0}:/", true)]
    [InlineData(@"//?/{1}:/x/../../{0}:/", true)]
    [InlineData(@"\\?/{1}:/../{0}:", true)]
    [InlineData(@"/\?\{1}:\..\{0}:\", true)]
    [InlineData(@"\/?/{1}:/../{0}:/", true)]
    [InlineData(@"//?/{0}:/ ", false)]
    [InlineData(@"\\?/{0}:/...", false)]
    [InlineData(@"/\?\{0}:\. ", false)]
    [InlineData(@"\/?/{0}:/x/..", false)]
    [InlineData(@"\\.\{0}:\x\..\.", false)]
    public async Task Every_verb_refuses_a_drive_under_a_prefix_win32_folds(string shape, bool climbs)
    {
        using var drive = new LowSubstDrive();

        var marker = Path.Combine(drive.Folder, "marker.txt");
        File.WriteAllText(marker, "the drive's own file");
        Directory.CreateDirectory(Path.Combine(drive.Folder, "x"));

        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var nowhere = "ZYXW".First(c => !taken.Contains(c));
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, drive.Letter, nowhere);

        Assert.True(Directory.Exists(path), $"{path} does not reach the drive, so it proves nothing");

        await AssertEveryVerbRefuses(path, climbs ? VolumeRoots.DeviceRefusal : VolumeRoots.Refusal, marker);

        Assert.True(Directory.Exists(Path.Combine(drive.Folder, "x")), $"the drive's folder went through {path}");
    }

    /// <summary>
    /// **A literal "\\?\" or "\??\" is opened as written**, so a "." or ".."
    /// in it is a name, not a step. Refused as a device path, deliberately —
    /// unless the whole reads as the drive's root, which is refused as that.
    /// Text only; the letter answers to nothing.
    /// </summary>
    [WindowsFact]
    public void A_dot_in_a_literal_device_path_is_refused_as_a_device_path()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXW".First(c => !taken.Contains(c));

        foreach (var path in new[] { $@"\\?\{q}:\..\NAME", $@"\??\{q}:\..\NAME\", $@"\\?\{q}:\x\..\y", $@"\??\{q}:\x\.\y", @"\\?\UNC\server\share\..\other\x" })
            Assert.True(VolumeRoots.Refuse([path]) == VolumeRoots.DeviceRefusal, $"{path} was not refused as a device path");

        foreach (var root in new[] { $@"\\?\{q}:\.", $@"\\?\{q}:\x\..", $@"\??\{q}:\x\.." })
            Assert.True(VolumeRoots.Refuse([root]) == VolumeRoots.Refusal, $"{root} was not refused as the drive");
    }
}
