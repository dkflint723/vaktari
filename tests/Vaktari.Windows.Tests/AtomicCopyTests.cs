using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a copy leaves behind when it does not finish.
///
/// **An overwrite destroyed the original before the replacement was whole.**
/// The copy wrote straight into the target, opened Create — truncated from the
/// first byte — and deleted it on failure. So a source that failed part-way
/// through an Overwrite left the user with neither file, and a copy that died
/// left a file under the real name that looked complete and was not.
///
/// The copy now lands under a staging name beside the target and is renamed
/// over it only once every byte is down. These pin that from INSIDE the copy:
/// it is HELD part-written, the original is read there, and the cancel is
/// given from that held state. Cancelling before the copy began proved
/// nothing, and was measured proving nothing: the operation loop bails out
/// before it opens a single file, under the old code and the new alike.
///
/// **Waiting for the first progress report and asserting afterwards was a
/// race, and it lost about one run in ten.** <see cref="Held"/> says what was
/// measured.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AtomicCopyTests
{
    private const int BigEnough = 64 << 20;

    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    private static async Task Ended(IOperationHandle handle)
    {
        try { await handle.Completion; }
        catch (OperationCanceledException) { /* the point */ }
    }

    /// <summary>
    /// A copy that has written at least one buffer and is HELD there, with the
    /// replacement part-written under its staging name.
    ///
    /// **Waiting for the first progress report proved only that the copy had
    /// started, and every assertion after it was about a copy that had been
    /// left free to finish.** The report is raised on the copying thread from
    /// inside the write loop; the waiting continuation is scheduled on the
    /// pool. On a busy machine that continuation ran late, and by the time it
    /// did, the remaining 63 MB were down, the metadata was carried and the
    /// staging file had been renamed over the original — so the cancel arrived
    /// after the copy had landed, and the test asserting the original survived
    /// read the new file instead. Measured on 2026-09-13 with a 512 MB copy
    /// running alongside: the same run recorded "length=21 staging=1
    /// state=Running" at the mid-copy read and then "length=67108864
    /// staging=0 state=Completed" after the cancel. Unheld, it lost about one
    /// run in ten.
    ///
    /// Pause is called FROM the progress handler, which the engine raises
    /// synchronously from <c>BytesCopied</c> inside its write loop, so the gate
    /// is closed before that thread reaches the next <c>WaitIfPausedAsync</c>.
    /// Nothing here waits on a clock or on the pool being prompt: the copy is
    /// held wherever the machine happens to be, and the state below says so.
    /// </summary>
    private static async Task<IOperationHandle> Held(IOperationHandle handle)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handle.Progressed += (_, p) =>
        {
            if (p.BytesDone <= 0) return;

            handle.Pause();
            held.TrySetResult();
        };

        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The hold, asserted rather than assumed: everything after this reads a
        // copy that cannot move on.
        Assert.Equal(OperationState.Paused, handle.State);

        return handle;
    }

    [WindowsFact]
    public async Task The_original_is_untouched_while_its_replacement_is_being_written()
    {
        using var tree = new TempTree();
        tree.Dir("src");
        var source = tree.At("src", "report.docx");
        File.WriteAllBytes(source, new byte[BigEnough]);
        var original = tree.Write("dst/report.docx", "the original, in full");

        var ops = new WindowsFileOperations();
        var handle = await Held(ops.Copy([source], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        // Held part-way. Under the old code this read an empty, truncated file.
        Assert.Equal("the original, in full", File.ReadAllText(original));

        // And the replacement is somewhere: beside the target, under a staging
        // name, which is the half of the promise the read above cannot show.
        Assert.Single(Directory.GetFiles(tree.At("dst"), ".*.vaktari-*"));

        handle.Cancel();
        await Ended(handle);

        Assert.Equal(OperationState.Cancelled, handle.State);
        Assert.Equal("the original, in full", File.ReadAllText(original));
        Assert.Empty(Directory.GetFiles(tree.At("dst"), ".*.vaktari-*"));
    }

    [WindowsFact]
    public async Task A_cancelled_copy_leaves_nothing_under_the_final_name_and_no_staging_file()
    {
        using var tree = new TempTree();
        tree.Dir("src");
        tree.Dir("dst");
        var source = tree.At("src", "big.bin");
        File.WriteAllBytes(source, new byte[BigEnough]);

        var ops = new WindowsFileOperations();
        var handle = await Held(ops.Copy([source], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        // Held part-way: the bytes are going somewhere, and it is not the real
        // name. This one raced the same way the test above did — a copy left
        // free to finish lands under the real name, and then both of these read
        // a file that is there on purpose.
        Assert.False(File.Exists(tree.At("dst", "big.bin")), "a partial file sits under the real name");

        handle.Cancel();
        await Ended(handle);

        Assert.False(File.Exists(tree.At("dst", "big.bin")), "a partial file sits under the real name");
        Assert.Empty(Directory.GetFiles(tree.At("dst"), ".*.vaktari-*"));
    }

    /// <summary>And a copy that finishes lands whole, under the real name,
    /// with the staging name gone — the other half of the promise.</summary>
    [WindowsFact]
    public async Task A_finished_overwrite_lands_the_new_bytes_and_nothing_else()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/report.docx", "the replacement");
        tree.Write("dst/report.docx", "the original");

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([source], tree.At("dst"), Always(ConflictResolution.Overwrite));
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("the replacement", File.ReadAllText(tree.At("dst", "report.docx")));
        Assert.Equal(["report.docx"], Directory.GetFiles(tree.At("dst")).Select(Path.GetFileName));
    }

    /// <summary>
    /// A read-only original still gets replaced when the user says so — a
    /// rename over a read-only file is refused by Windows, and the old code
    /// never met the case because Create had already cleared the way.
    /// </summary>
    [WindowsFact]
    public async Task A_read_only_original_can_still_be_overwritten()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/notes.txt", "new");
        var original = tree.Write("dst/notes.txt", "old");
        File.SetAttributes(original, FileAttributes.ReadOnly);

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([source], tree.At("dst"), Always(ConflictResolution.Overwrite));
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal("new", File.ReadAllText(original));
    }
}
