using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What a move leaves behind at the source.
///
/// **A moved tree left its whole skeleton standing.** The post-move sweep
/// walked `sources` — the caller's top-level list — so a nested folder was
/// never a candidate for removal, and a root is never empty while its own
/// subdirectories are still in it. The Windows twin was fixed for exactly this
/// and carries a comment saying so, and there is a Windows test named after the
/// bug; the port to Linux never happened, and there was no test here to notice.
///
/// Runs on any platform: the code under test is path arithmetic and ordinary
/// file I/O.
/// </summary>
public sealed class MoveSweepTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-movesweep-" + Guid.NewGuid().ToString("N"));

    private readonly string _from;
    private readonly string _into;

    public MoveSweepTests()
    {
        _from = Path.Combine(_root, "from");
        _into = Path.Combine(_root, "into");

        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_into);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_from, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// **The regression.** Move a tree and nothing of it is left at the source
    /// — not the root, and not the empty folders that used to hold its files.
    /// </summary>
    [Fact]
    public async Task A_moved_tree_leaves_nothing_at_the_source()
    {
        Write(Path.Combine("tree", "top.txt"), "top");
        Write(Path.Combine("tree", "a", "b", "deep.txt"), "deep");

        var ops = new LinuxFileOperations();
        var source = Path.Combine(_from, "tree");

        var handle = ops.Move([source], _into, Always(ConflictResolution.Overwrite));
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);

        // It all arrived.
        Assert.True(File.Exists(Path.Combine(_into, "tree", "top.txt")));
        Assert.True(File.Exists(Path.Combine(_into, "tree", "a", "b", "deep.txt")));

        // And none of it is still there — the skeleton included.
        Assert.False(Directory.Exists(Path.Combine(_from, "tree", "a", "b")));
        Assert.False(Directory.Exists(Path.Combine(_from, "tree", "a")));
        Assert.False(Directory.Exists(source));
    }

    /// <summary>
    /// A copy leaves the source exactly as it was — the sweep must run for
    /// moves only, or copying would delete what it just copied from.
    /// </summary>
    [Fact]
    public async Task A_copied_tree_is_left_completely_alone()
    {
        Write(Path.Combine("tree", "a", "keep.txt"), "keep");

        var ops = new LinuxFileOperations();
        var source = Path.Combine(_from, "tree");

        await ops.Copy([source], _into, Always(ConflictResolution.Overwrite)).Completion;

        Assert.True(File.Exists(Path.Combine(_from, "tree", "a", "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(_from, "tree", "a")));
    }

}
