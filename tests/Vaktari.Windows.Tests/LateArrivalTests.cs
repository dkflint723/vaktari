using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A file that turns up at the destination while the copy is on its way.
///
/// **It was replaced without a word.** A clash is asked about when the engine
/// reaches an item, and the copy then renamed its finished staging file onto
/// the target with overwrite on, replacing whatever had arrived there in the
/// meantime and never asking.
///
/// Held half-way from inside the copy: the first progress report pauses the
/// handle on the engine's own thread, so the next buffer waits, and the staging
/// file is provably there and unfinished while the test writes the late file.
/// A move within one volume leaves no such window to hold open, being a rename
/// straight after the clash is asked about, so it is reached through
/// <see cref="WindowsFileOperations.BeforeLanding"/> instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LateArrivalTests
{
    private const int BigEnough = 64 << 20;
    private const string Late = "saved here by somebody else meanwhile";

    /// <summary>Answers every clash the same way, and keeps what it was asked.</summary>
    private sealed class Clashes(ConflictResolution answer)
    {
        public List<FileConflict> Asked { get; } = [];

        public ValueTask<ConflictResolution> Answer(FileConflict clash)
        {
            lock (Asked) Asked.Add(clash);

            return ValueTask.FromResult(answer);
        }
    }

    /// <summary>A copy held between two buffers, with its first one written.</summary>
    private static async Task<IOperationHandle> HeldHalfway(IOperationHandle handle)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handle.Progressed += (_, p) =>
        {
            if (p.BytesDone == 0 || held.Task.IsCompleted) return;

            // On the engine's own thread, between two buffers: the next waits.
            handle.Pause();
            held.TrySetResult();
        };

        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));

        return handle;
    }

    private static string[] Staging(TempTree tree) => Directory.GetFiles(tree.At("dst"), ".report.docx.vaktari-*");

    /// <summary>
    /// A big file to copy into an empty dst, and the copy held half-way.
    ///
    /// **Every caller releases it in a finally.** A handle left paused blocks
    /// the engine's thread inside the gate and holds the staging file open,
    /// which stops the temp folder being deleted for the rest of the run.
    /// </summary>
    private static async Task<(IOperationHandle Handle, string Source, string Target)> CopyHeldHalfway(
        TempTree tree, WindowsFileOperations ops, Clashes clashes)
    {
        tree.Dir("src");
        tree.Dir("dst");

        var source = tree.At("src", "report.docx");
        File.WriteAllBytes(source, new byte[BigEnough]);

        var handle = await HeldHalfway(ops.Copy([source], tree.At("dst"), clashes.Answer));
        var target = tree.At("dst", "report.docx");

        // Provably half-way: the staging file is there, the real name is not.
        Assert.Single(Staging(tree));
        Assert.False(File.Exists(target), "the copy had already landed, so this proves nothing");

        return (handle, source, target);
    }

    [WindowsFact]
    public async Task A_file_that_turns_up_while_its_copy_is_on_the_way_is_asked_about()
    {
        using var tree = new TempTree();
        var clashes = new Clashes(ConflictResolution.Skip);
        var ops = new WindowsFileOperations();

        var (handle, source, target) = await CopyHeldHalfway(tree, ops, clashes);

        try
        {
            File.WriteAllText(target, Late);

            handle.Resume();
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            handle.Cancel();
        }

        // Both at once, so a failure says what happened to each.
        var kept = File.ReadAllText(target) == Late;

        Assert.True(kept && clashes.Asked.Count == 1,
            $"the late file was {(kept ? "kept" : "replaced")}, and the clash was asked about {clashes.Asked.Count} time(s)");

        Assert.Equal(new FileConflict(source, target), clashes.Asked[0]);
        Assert.Empty(Staging(tree));
        Assert.Empty(handle.Landed);
        Assert.False(ops.CanUndo, "an undo would take the late file, which the copy never wrote");
    }

    [WindowsFact]
    public async Task Answered_replace_the_late_file_is_replaced()
    {
        using var tree = new TempTree();
        var clashes = new Clashes(ConflictResolution.Overwrite);
        var ops = new WindowsFileOperations();

        var (handle, _, target) = await CopyHeldHalfway(tree, ops, clashes);

        try
        {
            File.WriteAllText(target, Late);

            handle.Resume();
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            handle.Cancel();
        }

        Assert.Single(clashes.Asked);
        Assert.Equal(BigEnough, new FileInfo(target).Length);
        Assert.Empty(Staging(tree));
        Assert.Equal([target], handle.Landed);
    }

    [WindowsFact]
    public async Task Answered_keep_both_the_copy_lands_beside_the_late_file()
    {
        using var tree = new TempTree();
        var clashes = new Clashes(ConflictResolution.KeepBoth);
        var ops = new WindowsFileOperations();

        var (handle, _, target) = await CopyHeldHalfway(tree, ops, clashes);

        try
        {
            File.WriteAllText(target, Late);

            handle.Resume();
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            handle.Cancel();
        }

        Assert.Single(clashes.Asked);
        Assert.Equal(Late, File.ReadAllText(target));

        var landed = Assert.Single(handle.Landed);

        Assert.NotEqual(target, landed);
        Assert.Equal(BigEnough, new FileInfo(landed).Length);
    }

    /// <summary>Cancel at the late clash ends the run, as it does at any other:
    /// nothing lands, the staging file goes, and there is nothing to undo.</summary>
    [WindowsFact]
    public async Task Answered_cancel_the_run_stops_and_nothing_lands()
    {
        using var tree = new TempTree();
        var clashes = new Clashes(ConflictResolution.Cancel);
        var ops = new WindowsFileOperations();

        var (handle, _, target) = await CopyHeldHalfway(tree, ops, clashes);

        try
        {
            File.WriteAllText(target, Late);

            handle.Resume();
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            handle.Cancel();
        }

        Assert.Single(clashes.Asked);
        Assert.Equal(Late, File.ReadAllText(target));
        Assert.Empty(Staging(tree));
        Assert.Empty(handle.Landed);
        Assert.False(ops.CanUndo);
    }

    /// <summary>
    /// **A folder where the file was going is not something a file can
    /// replace.** Windows answers that rename with a permission error, and a
    /// permission error offers "try again as administrator" for something no
    /// privilege fixes; the item fails with a sentence saying what is in the
    /// way, and the staging file goes with it.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_in_the_way_answered_replace_fails_and_says_why()
    {
        using var tree = new TempTree();
        var clashes = new Clashes(ConflictResolution.Overwrite);
        var ops = new WindowsFileOperations();

        var (handle, _, target) = await CopyHeldHalfway(tree, ops, clashes);

        try
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "inside.txt"), Late);

            handle.Resume();
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            handle.Cancel();
        }

        Assert.Single(clashes.Asked);
        Assert.True(Directory.Exists(target), "the folder that was in the way is gone");
        Assert.Equal(Late, File.ReadAllText(Path.Combine(target, "inside.txt")));
        Assert.Empty(Staging(tree));
        Assert.Empty(handle.Landed);

        var problem = Assert.Single(handle.Problems);

        Assert.Contains("folder", problem.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Cancelled while the question stands, the answer is stale.** The prompt
    /// belongs to one window, and a transfer row in another can cancel the run
    /// while it waits; landing the file afterwards would leave one behind that
    /// the stopped run no longer counts.
    /// </summary>
    [WindowsFact]
    public async Task A_cancel_while_the_question_stands_stops_the_landing()
    {
        using var tree = new TempTree();
        tree.Dir("src");
        tree.Dir("dst");

        var source = tree.At("src", "report.docx");
        File.WriteAllBytes(source, new byte[BigEnough]);
        var target = tree.At("dst", "report.docx");

        IOperationHandle? running = null;
        var asked = 0;

        var handle = new WindowsFileOperations().Copy([source], tree.At("dst"), _ =>
        {
            asked++;

            // What the other window's Cancel does, in the moment the question
            // is on screen.
            running!.Cancel();

            return ValueTask.FromResult(ConflictResolution.Overwrite);
        });

        running = handle;

        await HeldHalfway(handle);

        try
        {
            File.WriteAllText(target, Late);

            handle.Resume();
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            handle.Cancel();
        }

        Assert.Equal(1, asked);
        Assert.Equal(Late, File.ReadAllText(target));
        Assert.Empty(Staging(tree));
        Assert.Empty(handle.Landed);
    }

    /// <summary>
    /// A clash answered at the start is not asked about again at the end: the
    /// answer is carried to the landing, so it replaces what it was told to.
    /// </summary>
    [WindowsFact]
    public async Task A_clash_answered_replace_at_the_start_is_not_asked_again_at_the_end()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/report.docx", "arriving");
        var target = tree.Write("dst/report.docx", "already there");
        var clashes = new Clashes(ConflictResolution.Overwrite);

        var handle = new WindowsFileOperations().Copy([source], tree.At("dst"), clashes.Answer);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(clashes.Asked);
        Assert.Equal("arriving", File.ReadAllText(target));
    }

    /// <summary>Puts the late file there in the one moment a rename leaves, and
    /// proves that is the moment: a rename has no staging file.</summary>
    private static WindowsFileOperations Moving(TempTree tree, string leaf = "report.docx")
        => new()
        {
            BeforeLanding = at =>
            {
                if (!at.EndsWith(leaf, StringComparison.Ordinal) || File.Exists(at)) return;

                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(at)!, ".*.vaktari-*"));

                File.WriteAllText(at, Late);
            },
        };

    /// <summary>
    /// **A move met the same moment, narrower.** Within one volume it is a
    /// rename straight after the clash is asked about, so nothing can hold it
    /// open. Answered Skip, nothing moves: the source stays where it was.
    /// </summary>
    [WindowsFact]
    public async Task A_file_that_turns_up_as_a_move_lands_is_asked_about_and_the_source_stays()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/report.docx", "moving");
        tree.Dir("dst");
        var target = tree.At("dst", "report.docx");
        var clashes = new Clashes(ConflictResolution.Skip);
        var ops = Moving(tree);

        var handle = ops.Move([source], tree.At("dst"), clashes.Answer);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var kept = File.ReadAllText(target) == Late;

        Assert.True(kept && clashes.Asked.Count == 1,
            $"the late file was {(kept ? "kept" : "replaced")}, and the clash was asked about {clashes.Asked.Count} time(s)");

        Assert.Equal("moving", File.ReadAllText(source));
        Assert.False(ops.CanUndo, "an undo would move the late file, which the move never touched");
    }

    /// <summary>Answered Replace, the move takes the name after all, and the
    /// source is gone because it really did move.</summary>
    [WindowsFact]
    public async Task A_move_answered_replace_takes_the_name_and_leaves_nothing_behind()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/report.docx", "moving");
        tree.Dir("dst");
        var target = tree.At("dst", "report.docx");
        var clashes = new Clashes(ConflictResolution.Overwrite);

        var handle = Moving(tree).Move([source], tree.At("dst"), clashes.Answer);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(clashes.Asked);
        Assert.Equal("moving", File.ReadAllText(target));
        Assert.False(File.Exists(source));
        Assert.Equal([target], handle.Landed);
    }

    /// <summary>
    /// **A folder holding a file this run did not write is not the run's to
    /// take back.** The folder is made by the run, so it was recorded as one
    /// thing to undo; a name that turned up inside it and was answered Skip
    /// leaves the source file standing there, and the undo of a folder onto a
    /// folder still standing copies over what is in it and then deletes the
    /// lot. So the folder is left out of the undo step: an undo that does less
    /// destroys nothing.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_left_holding_a_late_file_is_not_taken_back_by_undo()
    {
        using var tree = new TempTree();
        var inside = tree.Write("src/photos/x.txt", "mine");
        tree.Dir("dst");
        var clashes = new Clashes(ConflictResolution.Skip);
        var ops = Moving(tree, "x.txt");

        var handle = ops.Move([tree.At("src", "photos")], tree.At("dst"), clashes.Answer);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(clashes.Asked);
        Assert.Equal("mine", File.ReadAllText(inside));
        Assert.Equal(Late, File.ReadAllText(tree.At("dst", "photos", "x.txt")));
        Assert.False(ops.CanUndo, "undoing the folder would copy the late file over the one that stayed");
    }
}
