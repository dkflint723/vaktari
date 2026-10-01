using System.Globalization;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The sixth review round's guards on reading a device path as Win32 folds
/// it**, from the other side: what the fold must still let through, and the
/// two public questions that fold beside <see cref="VolumeRoots.Refuse"/>.
/// </summary>
public sealed partial class VolumeRootOnDiskTests
{
    /// <summary>
    /// **"\\.\X:\x\...\.." is the folder x, and every verb acts on x alone.**
    /// Win32 folds "...\.." away before it opens the path — measured with
    /// GetFinalPathNameByHandle on a read-only handle, which named x — and the
    /// text reads it the same way, so it is not refused. On a subst drive, so a
    /// fold that reached the root instead would take the drive's own file: a
    /// copy lands as one "x", a move takes x and leaves the drive's file, a
    /// delete removes x and nothing above it, and the bin is asked about x.
    /// </summary>
    [WindowsTheory]
    [InlineData(@"\\.\{0}:\x\...\..")]
    [InlineData(@"\\.\{0}:\x\....\..")]
    [InlineData(@"\\.\{0}:\x\. \..")]
    public async Task A_folder_a_device_path_folds_to_is_acted_on_as_that_folder(string shape)
    {
        using var drive = new LowSubstDrive();

        var x = Path.Combine(drive.Folder, "x");
        var marker = Path.Combine(drive.Folder, "marker.txt");
        var path = string.Format(CultureInfo.InvariantCulture, shape, drive.Letter);
        var folded = $@"\\.\{drive.Letter}:\x";

        void Restock()
        {
            File.WriteAllText(marker, "the drive's own file");
            Directory.CreateDirectory(x);
            File.WriteAllText(Path.Combine(x, "inner.txt"), "x's own file");
        }

        Restock();

        Assert.Equal(folded, Path.GetFullPath(path));
        Assert.True(File.Exists(Path.Combine(path, "inner.txt")), $"{path} does not reach x, so it proves nothing");
        Assert.Null(VolumeRoots.Refuse([path]));

        var asked = new List<string>();
        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        var into = Directory.CreateTempSubdirectory("vaktari-folded").FullName;

        try
        {
            var copy = await Settled(ops.Copy([path], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Completed, copy.State);
            Assert.Equal(["x"], Directory.EnumerateFileSystemEntries(into).Select(Path.GetFileName));
            Assert.True(File.Exists(Path.Combine(into, "x", "inner.txt")));
            Directory.Delete(Path.Combine(into, "x"), recursive: true);

            var move = await Settled(ops.Move([path], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Completed, move.State);
            Assert.Equal(["x"], Directory.EnumerateFileSystemEntries(into).Select(Path.GetFileName));
            Assert.False(Directory.Exists(x), "the move left x behind");
            Assert.True(File.Exists(marker), $"the drive's file went with the move of {path}");

            Restock();

            await Settled(ops.Trash([path]));

            // By x's ordinary spelling: the shell's parser refuses every
            // device spelling, "\\.\" included (TrashExtendedSpellingTests).
            Assert.Equal([folded[4..]], asked);
            Assert.True(File.Exists(marker));

            var delete = await Settled(ops.Delete([path]));

            Assert.Equal(OperationState.Completed, delete.State);
            Assert.False(Directory.Exists(x), "the delete left x behind");
            Assert.True(File.Exists(marker), $"the drive's file was deleted through {path}");
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>
    /// **IsVolumeRoot and IsRootSpelling fold a device path as Refuse does.**
    /// Both read the path through the same Win32 fold, so "//?/X:/ " — which
    /// opens X:'s root — is a root to each, and "\\.\X:\x\...\.." — which opens
    /// x — is not. Neither was asked this by any test: a copy of either with the
    /// fold taken out passed everything. Text only; the letter answers to nothing.
    /// </summary>
    [WindowsFact]
    public void Every_public_root_question_folds_a_device_path_the_same_way()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXW".First(c => !taken.Contains(c));

        foreach (var root in new[] { $@"//?/{q}:/ ", $@"\\?/{q}:/...", $@"/\?\{q}:\. ", $@"\/?/{q}:/x/.." })
        {
            Assert.True(VolumeRoots.IsVolumeRoot(root), $"IsVolumeRoot did not call {root} a root");
            Assert.True(VolumeRoots.IsRootSpelling(root), $"IsRootSpelling did not call {root} a root");
        }

        foreach (var folder in new[] { $@"\\.\{q}:\x\...\..", $@"//?/{q}:/x/y/..", $@"\\.\{q}:\data" })
        {
            Assert.False(VolumeRoots.IsVolumeRoot(folder), $"IsVolumeRoot called {folder} a root");
            Assert.False(VolumeRoots.IsRootSpelling(folder), $"IsRootSpelling called {folder} a root");
        }
    }

    /// <summary>
    /// **A dot Win32 takes off the drive's own name leaves the drive it was
    /// written under**: "\\.\X:.\" opens X:'s root (measured on a subst drive),
    /// so it is refused as the drive and not as a device path that climbed —
    /// the written name is read with its trailing dots and spaces taken off,
    /// the way Win32 reads it. A space it does not take ("\\.\X: \") names the
    /// device "X: ", which is a device path. Text only.
    /// </summary>
    [WindowsFact]
    public void A_drive_name_with_a_dot_win32_takes_off_is_the_drive()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXW".First(c => !taken.Contains(c));

        Assert.Equal(VolumeRoots.Refusal, VolumeRoots.Refuse([$@"\\.\{q}:.\"]));
        Assert.Equal(VolumeRoots.Refusal, VolumeRoots.Refuse([$@"//./{q}:./"]));
        Assert.Equal(VolumeRoots.DeviceRefusal, VolumeRoots.Refuse([$@"\\.\{q}: \"]));
    }
}
