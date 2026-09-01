using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What happens to the rest of the batch when one item cannot be done.
///
/// **One locked file used to end everything after it.** The engine wrapped its
/// whole item loop in a single try, so copying twelve files with the third open
/// in another program copied two and abandoned nine — and the message named
/// neither the file nor what had been left undone. Explorer finishes the rest
/// and tells you which ones it could not do.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PartialFailureTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    /// <summary>
    /// A file held open with no sharing is the everyday case: something else is
    /// reading it, and the other eleven files are fine.
    /// </summary>
    [WindowsFact]
    public async Task A_locked_file_does_not_stop_the_files_after_it()
    {
        using var tree = new TempTree();

        tree.Write("src/first.txt", "one");
        var locked = tree.Write("src/second.txt", "two");
        tree.Write("src/third.txt", "three");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();

        // Held with no sharing at all, which is what makes the copy fail.
        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var handle = ops.Copy(
                [tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

            await handle.Completion;

            // The batch finished — it did not fail — and the neighbours arrived.
            Assert.Equal(OperationState.Completed, handle.State);
            Assert.True(tree.Exists("dst", "src", "first.txt"));
            Assert.True(tree.Exists("dst", "src", "third.txt"));

            // And the one that could not be done is named.
            var problem = Assert.Single(handle.Problems);
            Assert.EndsWith("second.txt", problem.Path);
        }
    }

    /// <summary>
    /// A clean run reports no problems at all — the list is not a dumping
    /// ground for things that went fine.
    /// </summary>
    [WindowsFact]
    public async Task A_clean_copy_reports_nothing_left_behind()
    {
        using var tree = new TempTree();

        tree.Write("src/a.txt", "a");
        tree.Write("src/b.txt", "b");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Empty(handle.Problems);
    }

    /// <summary>
    /// **A move must not delete what it could not copy.** This is the case
    /// where carrying on past a failure could destroy data, so it is pinned:
    /// the locked file is still at the source afterwards.
    /// </summary>
    [WindowsFact]
    public async Task A_move_leaves_the_file_it_could_not_take()
    {
        using var tree = new TempTree();

        tree.Write("src/fine.txt", "fine");
        var locked = tree.Write("src/held.txt", "held");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var handle = ops.Move(
                [tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

            await handle.Completion;

            Assert.Equal(OperationState.Completed, handle.State);
            Assert.True(tree.Exists("dst", "src", "fine.txt"));

            // Still where it was. Only existence while the handle is open —
            // reading it here would fail on the test's own lock, not the
            // product's behaviour.
            Assert.True(tree.Exists("src", "held.txt"));
            Assert.NotEmpty(handle.Problems);
        }

        // Released: now the content can be checked, which is the real proof it
        // was never moved and half-written somewhere else.
        Assert.Equal("held", tree.Read("src", "held.txt"));
        Assert.False(tree.Exists("dst", "src", "held.txt"));
    }

}
