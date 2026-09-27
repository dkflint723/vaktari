using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **The engine acts only on a full path.** Every caller in Vaktari hands it
/// one — a listed row's, a clipboard's, a drop's, the folder a pane stands in
/// — and a path that is not full is resolved by Win32 against a current
/// folder the whole process shares, per drive for "X:foo". The sixth and
/// seventh review rounds reached a drive's root through ".." and "W:..\..";
/// the text now reads those as they resolve, and the engine does not act on
/// one at all: each verb refuses it before anything is read, in one sentence,
/// and a creation recorded under one is not remembered for undo.
///
/// Every name here is one nothing on the machine answers to, and the
/// recycler is a recording, so an engine without the refusal finds nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EngineFullPathTests
{
    public static TheoryData<string> NotFull()
    {
        const string nowhere = "vaktari-not-full-7a3c9e";
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXWV".First(c => !taken.Contains(c));

        return [nowhere, $@"..\{nowhere}", $@"\{nowhere}", $"/{nowhere}", $"{q}:{nowhere}", $"{q}:", $"C:{nowhere}"];
    }

    [WindowsTheory]
    [MemberData(nameof(NotFull))]
    public async Task Every_verb_refuses_a_path_that_is_not_full(string path)
    {
        var asked = new List<string>();
        var into = Directory.CreateTempSubdirectory("vaktari-notfull").FullName;
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
                await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(VolumeRoots.NotFullRefusal, handle.Error?.Message);
            }

            var thrown = await Assert.ThrowsAsync<IOException>(async () => await ops.RenameAsync(path, "renamed", CancellationToken.None));
            Assert.Equal(VolumeRoots.NotFullRefusal, thrown.Message);

            ops.RecordCreation(path);
            Assert.False(ops.CanUndo, $"a creation at {path} was remembered for undo");

            Assert.Empty(asked);
            Assert.Empty(Directory.GetFileSystemEntries(into));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>Destinations that are not full, each one that, unguarded,
    /// could only reach this test's own output folder or a letter nothing
    /// answers to — never the root of a real drive.</summary>
    public static TheoryData<string> NotFullDestinations()
    {
        const string nowhere = "vaktari-not-full-7a3c9e";
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var q = "ZYXWV".First(c => !taken.Contains(c));

        return [nowhere, $@".\{nowhere}", $"{q}:", $"{q}:{nowhere}"];
    }

    /// <summary>
    /// **A destination that is not full is refused as well**, before anything
    /// is read: a copy into "X:" lands in X:'s current folder, whatever that is.
    /// </summary>
    [WindowsTheory]
    [MemberData(nameof(NotFullDestinations))]
    public async Task A_copy_or_move_into_a_destination_that_is_not_full_is_refused(string destination)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-notfull").FullName;
        var file = Path.Combine(folder, "keep.txt");
        File.WriteAllText(file, "kept");

        try
        {
            var ops = new WindowsFileOperations();

            foreach (var handle in new[]
                     {
                         ops.Move([file], destination, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                         ops.Copy([file], destination, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     })
            {
                await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(VolumeRoots.NotFullRefusal, handle.Error?.Message);
            }

            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A full path is not refused for being one, in any spelling a
    /// listing hands out.</summary>
    [WindowsFact]
    public void A_full_path_in_any_spelling_is_not_refused_as_not_full()
    {
        foreach (var path in new[] { @"C:\x", @"\\server\share\x", @"\\?\C:\x", @"\??\C:\x", @"\\.\C:\x", "//?/C:/x", @"\\?\UNC\server\share\x" })
            Assert.Null(VolumeRoots.RefuseNotFull([path]));
    }
}
