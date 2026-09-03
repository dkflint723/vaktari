using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Going again on the items that could not be done.
///
/// **Skip and Cancel already worked; only Retry was missing.** The batch
/// carries on past a failure and names what it left behind, and cancel has been
/// on the bar for the whole run — so the verb with nothing behind it was the
/// one that needs the person to go and DO something first, which is exactly the
/// one a modal cannot express. "Wait, I need a minute" is not an answer a
/// dialog can take.
///
/// So the batch never stops. It finishes, and the offer is made afterwards over
/// just the failures.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RetryAfterFailureTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    /// <summary>
    /// The whole feature, end to end: something else has the file open, the
    /// batch finishes without it, the program is closed, and the retry lands it.
    /// </summary>
    [WindowsFact]
    public async Task Closing_the_program_and_retrying_copies_the_file()
    {
        using var tree = new TempTree();

        tree.Write("src/first.txt", "one");
        var locked = tree.Write("src/second.txt", "two");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();
        IOperationHandle handle;

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            handle = ops.Copy(
                [tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

            await handle.Completion;

            Assert.Equal(OperationState.Completed, handle.State);
            Assert.False(tree.Exists("dst", "src", "second.txt"));
            Assert.NotNull(handle.Retry);
        }

        // The program has been closed. Now the offer is taken.
        var second = handle.Retry!();
        await second.Completion;

        Assert.Equal(OperationState.Completed, second.State);
        Assert.Equal("two", tree.Read("dst", "src", "second.txt"));
        Assert.Empty(second.Problems);
    }

    /// <summary>
    /// It goes again on the FAILURES and nothing else. Re-copying the
    /// successes would re-ask every conflict they already answered, and on a
    /// batch of a thousand it is a thousand files of work to land one.
    /// </summary>
    [WindowsFact]
    public async Task And_only_on_those()
    {
        using var tree = new TempTree();

        tree.Write("src/first.txt", "one");
        var locked = tree.Write("src/second.txt", "two");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();
        IOperationHandle handle;

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            handle = ops.Copy(
                [tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

            await handle.Completion;
        }

        // The neighbour arrived on the first pass. Take it away again: a retry
        // that re-planned the whole operation would put it back, and on a batch
        // of a thousand that is a thousand files of work to land one — plus
        // every conflict those successes already answered, asked again.
        File.Delete(tree.At("dst", "src", "first.txt"));

        var second = handle.Retry!();
        await second.Completion;

        Assert.False(tree.Exists("dst", "src", "first.txt"));
        Assert.True(tree.Exists("dst", "src", "second.txt"));
    }

    /// <summary>
    /// A clean run offers nothing. The button is ABSENT rather than present and
    /// doing nothing — the rule the platform capabilities already follow.
    /// </summary>
    [WindowsFact]
    public async Task A_clean_run_offers_nothing_to_go_again_on()
    {
        using var tree = new TempTree();

        tree.Write("src/a.txt", "a");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Null(handle.Retry);
    }

    /// <summary>
    /// **A retried item goes back to where THIS run decided to put it, not
    /// where "source into destination" would say.** Keep both renamed the
    /// arriving folder, and recomputing the target would merge the retry into
    /// the folder the user asked to keep separate — the documented fault the
    /// redirect map exists to prevent, arriving a second time by another road.
    /// </summary>
    [WindowsFact]
    public async Task A_retry_after_keep_both_lands_in_the_folder_that_was_kept()
    {
        using var tree = new TempTree();

        tree.Write("src/A/first.txt", "one");
        var locked = tree.Write("src/A/second.txt", "two");

        // Something of that name is already there, so Keep both renames the
        // arriving folder.
        tree.Write("dst/A/theirs.txt", "theirs");

        var ops = new WindowsFileOperations();
        IOperationHandle handle;

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            handle = ops.Copy(
                [tree.At("src", "A")], tree.At("dst"), Always(ConflictResolution.KeepBoth));

            await handle.Completion;
        }

        var second = handle.Retry!();
        await second.Completion;

        // Into the kept-separate folder...
        Assert.Equal("two", tree.Read("dst", "A (2)", "second.txt"));

        // ...and nothing new in the one that was already there.
        Assert.Equal(
            ["theirs.txt"],
            Directory.EnumerateFiles(tree.At("dst", "A")).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// And the duplicate route, which needs no conflict at all: a duplicate in
    /// place has target == source, so a recomputed retry would create a
    /// " - Copy" INSIDE the user's original folder and call the run clean.
    /// </summary>
    [WindowsFact]
    public async Task A_retry_after_a_duplicate_puts_nothing_inside_the_original()
    {
        using var tree = new TempTree();

        tree.Write("Alpha/first.txt", "one");
        var locked = tree.Write("Alpha/second.txt", "two");

        var ops = new WindowsFileOperations();
        IOperationHandle handle;

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Duplicate is a copy into the PARENT, resolved by keeping both.
            handle = ops.Copy(
                [tree.At("Alpha")], tree.Root, Always(ConflictResolution.KeepBoth));

            await handle.Completion;
        }

        var second = handle.Retry!();
        await second.Completion;

        Assert.Equal("two", tree.Read("Alpha - Copy", "second.txt"));

        // The original gained nothing at all.
        Assert.Equal(
            ["first.txt", "second.txt"],
            Directory.EnumerateFiles(tree.At("Alpha")).Select(Path.GetFileName).Order());

        Assert.Empty(Directory.EnumerateDirectories(tree.At("Alpha")));
    }

    /// <summary>
    /// A whole FOLDER that could not be created is retried as a folder, back to
    /// the name this run gave it — the directory branch of the plan, which a
    /// failed file never reaches.
    ///
    /// A file sitting where the folder must go is what stops it: the create
    /// throws, and the folder and everything planned inside it are recorded.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_that_could_not_be_created_is_retried_as_a_folder()
    {
        using var tree = new TempTree();

        tree.Write("src/A/first.txt", "one");
        tree.Dir("dst");

        // Not a folder. Directory.CreateDirectory refuses over it.
        File.WriteAllText(tree.At("dst", "A"), "in the way");

        var ops = new WindowsFileOperations();

        var handle = ops.Copy(
            [tree.At("src", "A")], tree.At("dst"), Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.NotEmpty(handle.Problems);
        Assert.NotNull(handle.Retry);

        // The obstruction is removed, the way closing the program would be.
        File.Delete(tree.At("dst", "A"));

        var second = handle.Retry!();
        await second.Completion;

        Assert.Equal("one", tree.Read("dst", "A", "first.txt"));
    }

    /// <summary>
    /// A delete that could not be done goes again on the same path — there is
    /// no target to carry and no conflict machinery to land in.
    /// </summary>
    [WindowsFact]
    public async Task A_delete_that_failed_goes_on_the_second_try()
    {
        using var tree = new TempTree();

        var locked = tree.Write("doomed.txt", "x");
        var ops = new WindowsFileOperations();
        IOperationHandle handle;

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            handle = ops.Delete([locked]);

            await handle.Completion;

            Assert.Single(handle.Problems);
            Assert.NotNull(handle.Retry);
        }

        var second = handle.Retry!();
        await second.Completion;

        Assert.False(File.Exists(locked));
        Assert.Empty(second.Problems);
    }

    /// <summary>
    /// Cancelling offers nothing. Somebody who pressed cancel is not asking to
    /// be handed the same work back.
    /// </summary>
    [WindowsFact]
    public async Task A_cancelled_run_offers_nothing()
    {
        using var tree = new TempTree();

        for (var i = 0; i < 200; i++) tree.Write($"src/f{i}.txt", "x");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite));

        handle.Cancel();
        await handle.Completion;

        Assert.Null(handle.Retry);
    }
}
