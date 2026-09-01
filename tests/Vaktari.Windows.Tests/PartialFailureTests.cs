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


    /// <summary>
    /// **A cancelled copy must not leave a truncated file under the final
    /// name.** The target is opened Create, so it exists and is truncated from
    /// the first byte — cancelling a large copy left something that looked like
    /// the file, opened, and was silently incomplete. Worse after Replace,
    /// where the original had already been destroyed to make room for it.
    ///
    /// Cancelled rather than locked, deliberately: a locked SOURCE fails before
    /// the target is ever created, so that version of this test passes whether
    /// or not the cleanup exists.
    /// </summary>
    [WindowsFact]
    public async Task A_cancelled_copy_leaves_no_half_written_file()
    {
        using var tree = new TempTree();

        // Big enough that the copy is still running when it is cancelled: the
        // engine reads in one-megabyte blocks.
        var source = tree.At("src", "big.bin");
        Directory.CreateDirectory(tree.At("src"));
        await File.WriteAllBytesAsync(source, new byte[24 * 1024 * 1024]);
        tree.Dir("dst");

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([source], tree.At("dst"), Always(ConflictResolution.Overwrite));

        // Cancel as soon as the first bytes have moved, so the target exists.
        var cancelled = new TaskCompletionSource();
        handle.Progressed += (_, p) => { if (p.BytesDone > 0) cancelled.TrySetResult(); };

        await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        handle.Cancel();

        await handle.Completion;

        Assert.False(
            tree.Exists("dst", "big.bin"),
            "a partly-written file was left where a complete one should be");
    }

    /// <summary>
    /// **An unreadable folder used to end the whole operation**, thrown from
    /// during planning before a single file had been copied — one protected
    /// directory anywhere under the selection and nothing happened at all. The
    /// Linux twin had the opposite fault and swallowed it, so the plan was
    /// short and the copy reported success having quietly left files behind.
    ///
    /// Tested against the walk directly, with a root that cannot be enumerated
    /// — denying read access on Windows needs an ACL, and a test that rewrites
    /// permissions on the machine running it is not worth the coverage.
    /// </summary>
    [WindowsFact]
    public void A_folder_that_cannot_be_read_is_reported_rather_than_thrown_from()
    {
        using var tree = new TempTree();

        var missing = tree.At("gone");
        var unreadable = new List<(string Path, Exception Error)>();

        // Enumerating it throws; the walk has to survive that and say so.
        var found = WindowsFileOperations
            .Descend(missing, CancellationToken.None, unreadable)
            .ToList();

        Assert.Empty(found);

        var problem = Assert.Single(unreadable);
        Assert.Equal(missing, problem.Path);
    }

    /// <summary>The ordinary case still walks everything, and reports nothing
    /// — the list is not a dumping ground for folders that were fine.</summary>
    [WindowsFact]
    public void A_readable_tree_reports_nothing()
    {
        using var tree = new TempTree();

        tree.Write("src/a/deep.txt", "x");

        var unreadable = new List<(string Path, Exception Error)>();

        var found = WindowsFileOperations
            .Descend(tree.At("src"), CancellationToken.None, unreadable)
            .ToList();

        Assert.Contains(found, f => f.Path.EndsWith("deep.txt"));
        Assert.Empty(unreadable);
    }
}
