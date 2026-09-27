using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **A full path with a NUL in it is refused at every entry, in the NUL's own
/// words** — the Linux twin of the Windows engine's rule. VolumeRoots.RefuseNotFull
/// refuses a NUL first; taking that line out reddened nothing an engine verb
/// said (fix-7 round-2 verification): Copy, Move, Trash and Delete still met
/// the text's own NUL rule, while Rename reached Path.GetFullPath with the NUL
/// and a creation recorded at such a path was remembered for undo. A plain
/// fact, so it runs wherever the suite does: no file system opens a NUL.
/// </summary>
public sealed class EngineNulPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-nulpath").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("a.txt\0")]
    [InlineData("a\0b")]
    [InlineData("\0")]
    public async Task Every_verb_refuses_a_path_with_a_nul(string tail)
    {
        var kept = Path.Combine(_root, "a.txt");
        File.WriteAllText(kept, "kept");
        var into = Directory.CreateDirectory(Path.Combine(_root, "into")).FullName;
        var path = Path.Combine(_root, tail);
        var ops = new LinuxFileOperations();

        foreach (var handle in new[]
                 {
                     ops.Delete([path]),
                     ops.Trash([path]),
                     ops.Move([path], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     ops.Copy([path], into, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     ops.Copy([kept], path, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                     ops.Move([kept], path, _ => ValueTask.FromResult(ConflictResolution.Skip)),
                 })
        {
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(VolumeRoots.NulRefusal, handle.Error?.Message);
        }

        var thrown = await Assert.ThrowsAsync<IOException>(async () => await ops.RenameAsync(path, "renamed", CancellationToken.None));
        Assert.Equal(VolumeRoots.NulRefusal, thrown.Message);

        ops.RecordCreation(path);
        Assert.False(ops.CanUndo, "a creation at a path with a NUL was remembered for undo");

        Assert.Empty(Directory.GetFileSystemEntries(into));
        Assert.Equal("kept", File.ReadAllText(kept));
    }
}
