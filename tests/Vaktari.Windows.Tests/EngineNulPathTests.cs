using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A full path with a NUL in it is refused at every entry, in the NUL's own
/// words.** VolumeRoots.RefuseNotFull refuses a NUL before anything else, as
/// the engines' first question (seventh review round) — but the only test that
/// asked it did so through the recycler's last guard: taking the NUL line out
/// of RefuseNotFull reddened nothing an engine verb said (fix-7 round-2
/// verification). Copy, Move, Trash and Delete are covered twice over, since
/// the text's own NUL rule answers the same sentence; Rename then fell through
/// to ReachablePath's sentence, and a creation recorded at such a path was
/// remembered for undo.
///
/// Each path is fully qualified and names a file inside a temporary folder
/// before its NUL; a verb that let it through could reach nothing else. The
/// recycler is a recording.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EngineNulPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-nulpath").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [WindowsTheory]
    [InlineData("a.txt\0")]
    [InlineData("a\0b")]
    [InlineData("\0")]
    public async Task Every_verb_refuses_a_path_with_a_nul(string tail)
    {
        var kept = Path.Combine(_root, "a.txt");
        File.WriteAllText(kept, "kept");
        var into = Directory.CreateDirectory(Path.Combine(_root, "into")).FullName;
        var path = Path.Combine(_root, tail);
        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

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

        Assert.Empty(asked);
        Assert.Empty(Directory.GetFileSystemEntries(into));
        Assert.Equal("kept", File.ReadAllText(kept));
    }
}
