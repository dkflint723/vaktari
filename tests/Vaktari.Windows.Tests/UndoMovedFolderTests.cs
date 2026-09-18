using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Undoing the move of a folder puts back what the move took, and nothing else.
///
/// **The undo of a moved folder could replace what stood at the source, and a
/// part-way failure lost the undo altogether.** When the folder the move came
/// out of was standing again, the undo copied the moved tree back over it with
/// <c>File.Copy(overwrite: true)</c> and then removed the whole landed tree.
/// Measured on 2026-09-12 before this change: a file written at the source
/// since was overwritten without a word; a read-only file there, or a file
/// where a moved subfolder had been, stopped the undo part-way with Ctrl+Z and
/// Ctrl+Y both gone; and a redo after the undo had merged into a re-created
/// folder moved that whole folder, taking a file nobody had moved.
/// </summary>
[SupportedOSPlatform("windows")]
public class UndoMovedFolderTests
{
    private static ValueTask<ConflictResolution> Overwrite(FileConflict _)
        => ValueTask.FromResult(ConflictResolution.Overwrite);

    /// <summary>The move of src\A into dst, which must have been one rename
    /// with nothing left behind, or what follows measures something else.</summary>
    private static async Task Moved(WindowsFileOperations ops, TempTree tree)
    {
        var handle = ops.Move([tree.At("src", "A")], tree.At("dst"), Overwrite);

        await handle.Completion;

        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.False(tree.Exists("src", "A"), "the move left its source standing, so this measures nothing");
    }

    private static Task<Exception?> Undo(WindowsFileOperations ops)
        => Record.ExceptionAsync(() => ops.UndoAsync(CancellationToken.None).AsTask());

    /// <summary>
    /// **A file written at the source since is not replaced.** It is left where
    /// it is, the moved file it would have been replaced by stays where the move
    /// put it, everything else goes back, and the undo says what it could not
    /// do. Ctrl+Z is still offered for the rest, and once the way is clear it
    /// finishes the job.
    /// </summary>
    [WindowsFact]
    public async Task Undoing_a_move_never_replaces_a_file_written_at_the_source_since()
    {
        using var tree = new TempTree();
        tree.Write("src/A/a.txt", "moved");
        tree.Write("src/A/b.txt", "moved too");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);
        tree.Write("src/A/a.txt", "written since");

        var said = await Undo(ops);

        Assert.Equal("written since", tree.Read("src", "A", "a.txt"));
        Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));

        Assert.Equal("moved too", tree.Read("src", "A", "b.txt"));
        Assert.False(tree.Exists("dst", "A", "b.txt"), "b.txt went back and was also left at the destination");

        Assert.Contains("a.txt", Assert.IsAssignableFrom<IOException>(said).Message);
        Assert.True(ops.CanUndo, "what could not go back is no longer undoable");

        File.Delete(tree.At("src", "A", "a.txt"));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("src", "A", "a.txt"));
        Assert.False(tree.Exists("dst", "A"), "the emptied destination folder was left behind");
    }

    /// <summary>A read-only file at the name is left the same way, still
    /// read-only, rather than stopping the undo part-way.</summary>
    [WindowsFact]
    public async Task Undoing_a_move_leaves_a_read_only_file_at_the_source_and_puts_back_the_rest()
    {
        using var tree = new TempTree();
        tree.Write("src/A/a.txt", "moved");
        tree.Write("src/A/z.txt", "moved too");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);

        var since = tree.Write("src/A/a.txt", "written since");
        File.SetAttributes(since, FileAttributes.ReadOnly);

        try
        {
            var said = await Undo(ops);

            Assert.Equal("written since", File.ReadAllText(since));
            Assert.True((File.GetAttributes(since) & FileAttributes.ReadOnly) != 0, "the file at the source lost its read-only mark");
            Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));

            Assert.Equal("moved too", tree.Read("src", "A", "z.txt"));

            Assert.IsAssignableFrom<IOException>(said);
            Assert.True(ops.CanUndo, "what could not go back is no longer undoable");
        }
        finally
        {
            File.SetAttributes(since, FileAttributes.Normal);
        }
    }

    /// <summary>A file standing where a moved subfolder goes: the subfolder
    /// stays where the move put it, and the rest goes back.</summary>
    [WindowsFact]
    public async Task Undoing_a_move_leaves_a_subfolder_whose_name_is_now_a_file()
    {
        using var tree = new TempTree();
        tree.Write("src/A/sub/x.txt", "moved");
        tree.Write("src/A/y.txt", "moved too");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);
        tree.Write("src/A/sub", "a file where the folder was");

        var said = await Undo(ops);

        Assert.Equal("a file where the folder was", tree.Read("src", "A", "sub"));
        Assert.Equal("moved", tree.Read("dst", "A", "sub", "x.txt"));

        Assert.Equal("moved too", tree.Read("src", "A", "y.txt"));

        Assert.IsAssignableFrom<IOException>(said);
        Assert.True(ops.CanUndo, "what could not go back is no longer undoable");
    }

    /// <summary>
    /// **A redo moves only what the undo put back.** The undo merged into a
    /// folder somebody had re-created at the source, holding a file of their
    /// own; the redo used to move that whole folder, their file with it.
    /// </summary>
    [WindowsFact]
    public async Task Redoing_an_undo_that_merged_leaves_what_was_already_at_the_source()
    {
        using var tree = new TempTree();
        tree.Write("src/A/a.txt", "moved");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);
        tree.Write("src/A/other.txt", "somebody else's");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("src", "A", "a.txt"));

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("somebody else's", tree.Read("src", "A", "other.txt"));
        Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));
        Assert.False(tree.Exists("dst", "A", "other.txt"), "the redo moved a file the undo never put back");
    }

    /// <summary>
    /// GUARD, not a test of this change, and it says so: a read-only file
    /// inside the moved folder comes back read-only when the folder it came
    /// out of is standing again. Measured passing before this change; kept so
    /// the new walk cannot lose it.
    /// </summary>
    [WindowsFact]
    public async Task A_read_only_file_inside_comes_back_read_only()
    {
        using var tree = new TempTree();
        var locked = tree.Write("src/A/locked.txt", "moved");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        try
        {
            await Moved(ops, tree);
            tree.Dir("src", "A");

            await ops.UndoAsync(CancellationToken.None);

            Assert.True((File.GetAttributes(locked) & FileAttributes.ReadOnly) != 0, "the file came back without its read-only mark");
            Assert.False(tree.Exists("dst", "A"), "the emptied destination folder was left behind");
        }
        finally
        {
            foreach (var path in new[] { locked, tree.At("dst", "A", "locked.txt") })
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// **A folder the move made travels back whole, and forward whole again.**
    /// Somebody saves a file into it between the Ctrl+Z and the Ctrl+Y; the redo
    /// takes the folder, and the file goes with it. Moving a folder moves what is
    /// in it now, and that has to be as true of the redo as of the move.
    ///
    /// Run twice: once where the folder rename works, and once where it is
    /// refused so the walk that carries child by child runs instead. **The whole
    /// point is that the two arms agree**, and which one ran is not something a
    /// person can see — it turns on whether the destination was another volume.
    /// The second run reaches that arm on one volume through BeforeRenaming,
    /// refusing only the folder's own rename, because a Windows folder rename
    /// refused for any reason but a taken name takes the same road.
    ///
    /// Without the inverse collapsing to the one whole-folder step, the second
    /// run redoes the children it put back and leaves the file saved since
    /// standing in a folder of the same name at the source — measured.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_the_move_made_travels_whole_whichever_arm_runs()
    {
        await Whole(refusingTheFolderRename: false);
        await Whole(refusingTheFolderRename: true);
    }

    private static async Task Whole(bool refusingTheFolderRename)
    {
        var arm = refusingTheFolderRename ? "the walk" : "the one rename";

        using var tree = new TempTree();
        tree.Write("src/A/a.txt", "moved");
        tree.Dir("dst");

        var ops = new WindowsFileOperations
        {
            BeforeRenaming = (from, _) =>
            {
                // The folder itself and nothing under it: its children still
                // rename as they would, so this stands for another volume
                // rather than for a disk that has stopped working.
                if (refusingTheFolderRename && PathRules.LeafName(from) == "A")
                    throw new IOException("the folder rename was refused");
            },
        };

        await Moved(ops, tree);

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("src", "A", "a.txt"));
        Assert.False(tree.Exists("dst", "A"), $"{arm}: the emptied destination folder was left behind");

        // Saved into the folder after it came back, by somebody who has no idea
        // an undo is a thing that can itself be taken back.
        tree.Write("src/A/after.txt", "saved since");

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));
        Assert.Equal("saved since", tree.Read("dst", "A", "after.txt"));
        Assert.False(tree.Exists("src", "A"), $"{arm}: the folder was left standing at the source");
    }

    /// <summary>
    /// **The same bystander, by the road the test above cannot take.** When the
    /// folder rename is refused rather than answered "that name is taken" —
    /// which is what a cross-volume undo gets, since Directory.Move rejects
    /// different roots before it looks at the destination at all — the walk runs
    /// with the source folder standing. It must still not collapse: the folder
    /// at the source is somebody else's, not one this walk made.
    ///
    /// Measured before this was fixed: the collapse fired on the caller's
    /// intention to create rather than on whether anything was created, so the
    /// redo took the whole folder and carried other.txt to the destination —
    /// across a volume, in the real case — and deleted the folder the person had
    /// made. The test above stays green over that, because its rename comes back
    /// "taken" and never reaches the arm.
    /// </summary>
    [WindowsFact]
    public async Task Redoing_leaves_the_bystander_when_the_rename_was_refused_rather_than_taken()
    {
        using var tree = new TempTree();
        tree.Write("src/A/a.txt", "moved");
        tree.Dir("dst");

        var ops = new WindowsFileOperations
        {
            BeforeRenaming = (from, _) =>
            {
                // Refused, not taken: what Directory.Move answers across roots.
                if (PathRules.LeafName(from) == "A")
                    throw new IOException("Source and destination path must have identical roots.");
            },
        };

        await Moved(ops, tree);

        tree.Write("src/A/other.txt", "somebody else's");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("src", "A", "a.txt"));
        Assert.Equal("somebody else's", tree.Read("src", "A", "other.txt"));

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));
        Assert.Equal("somebody else's", tree.Read("src", "A", "other.txt"));
        Assert.False(tree.Exists("dst", "A", "other.txt"), "the redo carried off a file the undo never put back");
    }

    /// <summary>
    /// **A press that put nothing back does not push itself back on top.** The
    /// obstacle is still there, so the retry would meet it again — and anything
    /// recorded since would be buried under it, so Ctrl+Z would start taking
    /// back the person's own way out instead of the history they meant. Dropped
    /// instead, which leaves the older step where the next press can reach it.
    /// </summary>
    [WindowsFact]
    public async Task With_nothing_put_back_a_second_undo_reaches_the_older_step()
    {
        using var tree = new TempTree();
        tree.Write("src/old.txt", "the older move");
        tree.Write("src/a.txt", "moved");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        var older = ops.Move([tree.At("src", "old.txt")], tree.At("dst"), Overwrite);
        await older.Completion;
        Assert.Null(older.Error);

        var newer = ops.Move([tree.At("src", "a.txt")], tree.At("dst"), Overwrite);
        await newer.Completion;
        Assert.Null(newer.Error);

        // Standing where the newer move's file belongs, so its undo can put
        // nothing back at all.
        tree.Write("src/a.txt", "written since");

        Assert.IsAssignableFrom<IOException>(await Undo(ops));

        Assert.Equal("written since", tree.Read("src", "a.txt"));
        Assert.Equal("moved", tree.Read("dst", "a.txt"));

        // The older move is what Ctrl+Z now offers, and pressing it reaches it.
        Assert.True(ops.CanUndo, "the older step was lost with the one that could do nothing");
        Assert.False(ops.CanRedo, "a press that put nothing back left something to redo");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("the older move", tree.Read("src", "old.txt"));
        Assert.False(tree.Exists("dst", "old.txt"), "the older move did not come back");
    }

    /// <summary>
    /// **A folder the walk has to make is a step like any other.** The move
    /// swept the emptied source folders away, so putting the entry back means
    /// making them again — and if that is done quietly, the redo leaves them
    /// standing for ever, because nothing recorded that they were ever made.
    /// Recorded, the reversed log takes each one away once the child has left.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_the_walk_had_to_make_is_taken_away_again_by_the_redo()
    {
        using var tree = new TempTree();
        tree.Write("src/deep/A/a.txt", "moved");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        var handle = ops.Move([tree.At("src", "deep", "A")], tree.At("dst"), Overwrite);
        await handle.Completion;
        Assert.Null(handle.Error);

        // The folder the move came out of, cleared away by hand, so the undo has
        // to make it again before anything can go back into it.
        Directory.Delete(tree.At("src", "deep"));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("src", "deep", "A", "a.txt"));

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));
        Assert.False(tree.Exists("src", "deep"), "the folder the undo made was left standing after the redo");
    }

    /// <summary>
    /// **And a link standing where one of those folders belongs is never written
    /// through.** Following it would put the moved entry inside whatever the
    /// link points at — a place the move never touched and the person never
    /// named — and the emptied folder afterwards would be removed from there.
    /// The step waits instead.
    /// </summary>
    [WindowsFact]
    public async Task A_link_where_a_made_folder_belongs_is_not_written_through()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("src/deep/A/a.txt", "moved");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        var handle = ops.Move([tree.At("src", "deep", "A")], tree.At("dst"), Overwrite);
        await handle.Completion;
        Assert.Null(handle.Error);

        Directory.Delete(tree.At("src", "deep"));

        // Somebody put a junction where the folder used to be.
        tree.Junction("src/deep", outside);

        Assert.IsAssignableFrom<IOException>(await Undo(ops));

        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal("moved", tree.Read("dst", "A", "a.txt"));

        Assert.True(
            (File.GetAttributes(tree.At("src", "deep")) & FileAttributes.ReparsePoint) != 0,
            "the junction standing where the folder belonged stopped being a junction");
    }

    /// <summary>
    /// **An emptied folder wearing ReadOnly is still taken away.** Windows
    /// refuses to remove one, with the same generic refusal it gives for every
    /// other reason, so there is nothing to react to after the fact — the mark
    /// comes off before the attempt, and goes back on if the removal is refused
    /// anyway. The walk this replaces never met the case because it cleared the
    /// whole tree before deleting it whole.
    /// </summary>
    [WindowsFact]
    public async Task An_emptied_read_only_folder_is_still_removed_from_the_destination()
    {
        using var tree = new TempTree();
        tree.Write("src/A/a.txt", "moved");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);

        var landed = tree.At("dst", "A");
        File.SetAttributes(landed, File.GetAttributes(landed) | FileAttributes.ReadOnly);

        // Re-created by hand, so the undo merges into it and the folder at the
        // destination is emptied child by child rather than renamed away whole.
        tree.Dir("src", "A");

        try
        {
            await ops.UndoAsync(CancellationToken.None);

            Assert.Equal("moved", tree.Read("src", "A", "a.txt"));
            Assert.False(tree.Exists("dst", "A"), "the emptied read-only folder was left standing");
            Assert.True(ops.CanRedo, "the undo put something back, so there is something to redo");
        }
        finally
        {
            if (Directory.Exists(landed))
                File.SetAttributes(landed, File.GetAttributes(landed) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// **A link of the same kind pointing at the same place is already put
    /// back.** A link's whole content is where it points, and this application
    /// would reproduce it from that same text — CopyLink hands LinkTarget to
    /// Native.CreateJunction — so the one that travelled has nothing in it that
    /// the one standing does not. It goes, and the step counts as done.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_already_standing_at_the_source_is_taken_as_already_put_back()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        tree.Junction("src/A/link", outside);
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);

        // Put back by hand, pointing where it always did.
        tree.Junction("src/A/link", outside);

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(
            (File.GetAttributes(tree.At("src", "A", "link")) & FileAttributes.ReparsePoint) != 0,
            "the junction at the source stopped being a junction");

        Assert.False(tree.Exists("dst", "A"), "the emptied destination folder was left behind");
        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
    }

    /// <summary>
    /// **And one pointing anywhere else is not.** This is the only place in the
    /// walk that removes something on the strength of comparing text, so the
    /// case that must never widen is the one where the texts differ: the link
    /// standing at the source is somebody else's, the one that travelled stays
    /// where the move left it, and the step waits.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_pointing_somewhere_else_is_left_and_the_step_waits()
    {
        using var tree = new TempTree();
        var mine = tree.Dir("mine");
        var theirs = tree.Dir("theirs");
        tree.Write("mine/kept.txt", "the one that moved");
        tree.Write("theirs/kept.txt", "somebody else's");
        tree.Junction("src/A/link", mine);

        // A sibling that can go back, so the press puts SOMETHING back and the
        // rest stays undoable. With only the link in the folder nothing travels,
        // and the step is dropped rather than offered again — which is the
        // separate rule With_nothing_put_back_a_second_undo_reaches_the_older_step
        // is about, and not this one.
        tree.Write("src/A/a.txt", "moved");

        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Moved(ops, tree);

        // A junction of the same kind at the same name, pointing elsewhere.
        tree.Junction("src/A/link", theirs);

        var said = await Undo(ops);

        Assert.Equal("moved", tree.Read("src", "A", "a.txt"));

        Assert.Equal(
            theirs,
            new DirectoryInfo(tree.At("src", "A", "link")).LinkTarget);

        Assert.True(
            (File.GetAttributes(tree.At("dst", "A", "link")) & FileAttributes.ReparsePoint) != 0,
            "the junction that travelled was removed although the one at the source points elsewhere");

        Assert.Equal("the one that moved", tree.Read("mine", "kept.txt"));
        Assert.Equal("somebody else's", tree.Read("theirs", "kept.txt"));

        Assert.IsAssignableFrom<IOException>(said);
        Assert.True(ops.CanUndo, "what could not go back is no longer undoable");
    }

    /// <summary>
    /// **The row names the roots the move was asked about, not the steps the
    /// walk took.** Read off the steps it would say "move of 3 items" after a
    /// merge, or name a child file, or — since UndoNames answers "move of 0
    /// items" for none — say "0 items" for a folder with nothing in it. The name
    /// is computed once, from the names the person saw, and handed to each
    /// inverse.
    ///
    /// The merge is what makes this bite: there the inverse is the list of
    /// children and a folder to make, so a name read off the steps would be
    /// wrong in a way the one-rename case would hide.
    /// </summary>
    [WindowsFact]
    public async Task The_rows_name_the_folder_that_moved_and_not_its_children()
    {
        using var tree = new TempTree();
        tree.Write("src/photos/a.txt", "moved");
        tree.Write("src/photos/b.txt", "moved too");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        var handle = ops.Move([tree.At("src", "photos")], tree.At("dst"), Overwrite);
        await handle.Completion;
        Assert.Null(handle.Error);

        Assert.Equal("move of photos", ops.UndoDescription);

        // Re-created by hand, so the undo merges into it and its inverse is the
        // children one by one rather than the folder whole.
        tree.Dir("src", "photos");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("move of photos", ops.RedoDescription);
    }
}
