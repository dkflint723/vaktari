using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **The engine acts only on a full path** — the Linux twin of the Windows
/// engine's rule (seventh review round). Every caller in Vaktari hands it
/// one; a relative path is read against a current folder the whole process
/// shares, so each verb refuses it before anything is read, in one sentence,
/// and a creation recorded under one is not remembered for undo. A plain fact,
/// so it runs wherever the suite does: a relative path is not full anywhere.
/// Every name answers to nothing, so an engine without the refusal finds
/// nothing.
/// </summary>
public sealed class EngineFullPathTests
{
    private const string Nowhere = "vaktari-not-full-7a3c9e";

    [Theory]
    [InlineData(Nowhere)]
    [InlineData("../" + Nowhere)]
    [InlineData("./" + Nowhere + "/x")]
    public async Task Every_verb_refuses_a_path_that_is_not_full(string path)
    {
        var into = Directory.CreateTempSubdirectory("vaktari-notfull").FullName;
        var ops = new LinuxFileOperations();

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

            Assert.Empty(Directory.GetFileSystemEntries(into));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>A destination that is not full is refused as well, and the
    /// file stays where it was.</summary>
    [Theory]
    [InlineData(Nowhere)]
    [InlineData("./" + Nowhere)]
    public async Task A_copy_or_move_into_a_destination_that_is_not_full_is_refused(string destination)
    {
        var folder = Directory.CreateTempSubdirectory("vaktari-notfull").FullName;
        var file = Path.Combine(folder, "keep.txt");
        File.WriteAllText(file, "kept");

        try
        {
            var ops = new LinuxFileOperations();

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
}
