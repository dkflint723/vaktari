using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The copy and move engine, and the three ways its plan could disagree with
/// what actually happened on disk.
///
/// **What went wrong.** The plan pre-computes a target for every item before
/// anything runs, and three separate things then invalidated it. A folder
/// resolved with KeepBoth landed under a new name that its own descendants knew
/// nothing about, so they merged into the folder the user had just asked to
/// keep separate. The post-move sweep looked for empty directories among the
/// top-level sources only, and a root is never empty while its own
/// subdirectories are still standing — so a moved tree left its whole skeleton
/// behind. And the undo reconstructed where things went as destination + name,
/// which stops being true the moment any conflict is resolved.
/// </summary>
[SupportedOSPlatform("windows")]
public class CopyMoveTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    private static async Task<IOperationHandle> Finished(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        return handle;
    }

    // ---- Moving a tree -----------------------------------------------------

    [WindowsFact]
    public async Task A_moved_tree_arrives_whole()
    {
        using var tree = new TempTree();
        tree.Write("src/top.txt");
        tree.Write("src/a/b/deep.txt");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.True(tree.Exists("dst", "src", "top.txt"));
        Assert.True(tree.Exists("dst", "src", "a", "b", "deep.txt"));
    }

    /// <summary>
    /// The empty-skeleton bug. Everything moved correctly and the source still
    /// showed as a folder full of folders, which for a move is the one thing
    /// the user asked not to happen.
    /// </summary>
    [WindowsFact]
    public async Task A_moved_tree_leaves_nothing_at_the_source()
    {
        using var tree = new TempTree();
        tree.Write("src/top.txt");
        tree.Write("src/a/b/deep.txt");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.False(tree.Exists("src"));
    }

    /// <summary>
    /// And the consequence of leaving it: the undo's `Directory.Move` throws
    /// when the place it is moving back to is still occupied.
    /// </summary>
    [WindowsFact]
    public async Task A_moved_tree_can_be_undone()
    {
        using var tree = new TempTree();
        tree.Write("src/a/b/deep.txt", "kept");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([tree.At("src")], tree.At("dst"), Always(ConflictResolution.Overwrite)));
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("kept", tree.Read("src", "a", "b", "deep.txt"));
        Assert.False(tree.Exists("dst", "src"));
    }

    // ---- KeepBoth ----------------------------------------------------------

    /// <summary>
    /// The descendants-follow-their-ancestor case. Before the fix, the new
    /// folder was created empty and `sub/a.txt` went into the pre-existing
    /// `foo`.
    /// </summary>
    [WindowsFact]
    public async Task KeepBoth_puts_a_whole_folder_beside_the_one_it_clashed_with()
    {
        using var tree = new TempTree();
        tree.Write("foo/sub/a.txt", "mine");
        tree.Write("dst/foo/sub/existing.txt", "theirs");
        var ops = new WindowsFileOperations();

        await Finished(ops.Copy([tree.At("foo")], tree.At("dst"), Always(ConflictResolution.KeepBoth)));

        Assert.Equal("mine", tree.Read("dst", "foo (2)", "sub", "a.txt"));
    }

    [WindowsFact]
    public async Task KeepBoth_leaves_the_folder_it_clashed_with_untouched()
    {
        using var tree = new TempTree();
        tree.Write("foo/sub/a.txt", "mine");
        tree.Write("dst/foo/sub/existing.txt", "theirs");
        var ops = new WindowsFileOperations();

        await Finished(ops.Copy([tree.At("foo")], tree.At("dst"), Always(ConflictResolution.KeepBoth)));

        Assert.Equal(["existing.txt"], tree.Names("dst", "foo", "sub"));
    }

    /// <summary>
    /// A folder name is atomic. Splitting it at the last dot the way a file name
    /// is split produced `my (2).project`, which reads as a different project
    /// entirely.
    ///
    /// The suffix here is the numbered one because this is a conflict in
    /// ANOTHER folder — nothing about the arrival is a copy of anything the
    /// user can see.
    /// </summary>
    [WindowsTheory]
    [InlineData("my.project", "my.project (2)")]
    [InlineData("ver.1.2", "ver.1.2 (2)")]
    [InlineData("plain", "plain (2)")]
    public async Task KeepBoth_treats_a_folder_name_as_one_piece(string name, string expected)
    {
        using var tree = new TempTree();
        tree.Write($"{name}/a.txt");
        tree.Dir("dst", name);
        var ops = new WindowsFileOperations();

        await Finished(ops.Copy([tree.At(name)], tree.At("dst"), Always(ConflictResolution.KeepBoth)));

        Assert.True(tree.Exists("dst", expected, "a.txt"));
    }

    /// <summary>
    /// A file name is still split at its extension, which is the point — and
    /// the arrival is NUMBERED rather than called a copy.
    ///
    /// **This test used to assert " - Copy" and say Explorer did that.**
    /// Explorer reserves that for a duplicate in place, where the word is true;
    /// a conflict in another folder arrives as "(2)", which claims only that
    /// this is the second thing here wanting the name.
    /// </summary>
    [WindowsFact]
    public async Task KeepBoth_numbers_a_file_the_way_Explorer_does()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "mine");
        tree.Write("dst/notes.txt", "theirs");
        var ops = new WindowsFileOperations();

        await Finished(ops.Copy([file], tree.At("dst"), Always(ConflictResolution.KeepBoth)));

        Assert.Equal("mine", tree.Read("dst", "notes (2).txt"));
    }

    /// <summary>
    /// The undo that destroyed a bystander. `readme.txt` moved in beside an
    /// existing one and landed as `readme (2).txt`; undo reconstructed the
    /// landing site as destination + name, found the *other* file there, and
    /// moved that one back to the source instead.
    /// </summary>
    [WindowsFact]
    public async Task Undoing_a_KeepBoth_move_brings_back_the_file_that_moved()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/readme.txt", "mine");
        tree.Write("dst/readme.txt", "bystander");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.KeepBoth)));
        Assert.Equal("mine", tree.Read("dst", "readme (2).txt"));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("mine", tree.Read("src", "readme.txt"));
    }

    [WindowsFact]
    public async Task Undoing_a_KeepBoth_move_leaves_the_bystander_where_it_was()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/readme.txt", "mine");
        tree.Write("dst/readme.txt", "bystander");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.KeepBoth)));
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("bystander", tree.Read("dst", "readme.txt"));
    }

    // ---- Skip --------------------------------------------------------------

    [WindowsFact]
    public async Task A_skipped_file_stays_at_the_source_and_the_destination_is_unchanged()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/readme.txt", "mine");
        tree.Write("dst/readme.txt", "theirs");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.Skip)));

        Assert.Equal("mine", tree.Read("src", "readme.txt"));
        Assert.Equal("theirs", tree.Read("dst", "readme.txt"));
    }

    // ---- The rename fast path ----------------------------------------------
    //
    // A move within one volume renames rather than rewriting. Both paths must
    // leave the same files behind, which is exactly why the change was
    // invisible for so long — so these pin the OUTCOMES, and MoveFastPathTests
    // pins the rule that chooses between them.

    [WindowsFact]
    public async Task A_renamed_move_still_lands_the_content_and_clears_the_source()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/notes.txt", "the actual bytes");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.Equal("the actual bytes", tree.Read("dst", "notes.txt"));
        Assert.False(tree.Exists("src", "notes.txt"));
    }

    /// <summary>
    /// **Overwrite still overwrites on the fast path.** File.Move refuses an
    /// existing target unless told otherwise, so a rename path that forgot this
    /// would fail exactly where the slow path succeeded — and only for people
    /// who answered Replace.
    /// </summary>
    [WindowsFact]
    public async Task A_renamed_move_over_an_existing_file_replaces_it()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/readme.txt", "mine");
        tree.Write("dst/readme.txt", "theirs");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.Equal("mine", tree.Read("dst", "readme.txt"));
        Assert.False(tree.Exists("src", "readme.txt"));
    }

    /// <summary>
    /// A read-only file at the destination stopped the slow path too, but the
    /// rename path reaches the filesystem more directly — so the clearing has
    /// to happen on both.
    /// </summary>
    [WindowsFact]
    public async Task A_renamed_move_replaces_a_read_only_file()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/readme.txt", "mine");
        tree.Write("dst/readme.txt", "theirs");

        var existing = tree.At("dst", "readme.txt");
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        var ops = new WindowsFileOperations();

        try
        {
            await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.Overwrite)));

            Assert.Equal("mine", tree.Read("dst", "readme.txt"));
        }
        finally
        {
            if (File.Exists(existing)) File.SetAttributes(existing, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// The progress bar has to advance on the fast path too. A rename moves the
    /// bytes without reading any, so a bar left at zero through an otherwise
    /// instant operation reads as a hang.
    /// </summary>
    [WindowsFact]
    public async Task A_renamed_move_still_reports_its_bytes()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/notes.txt", "0123456789");
        tree.Dir("dst");
        var ops = new WindowsFileOperations();

        var handle = ops.Move([file], tree.At("dst"), Always(ConflictResolution.Overwrite));

        long reported = 0;
        handle.Progressed += (_, p) => reported = Math.Max(reported, p.BytesDone);

        await Finished(handle);

        Assert.True(
            reported > 0,
            "a renamed move reported no bytes, so the progress bar would sit at zero");
    }

    /// <summary>A copy keeps the file's own dates rather than stamping today
    /// on it — the loss nobody notices until a folder sorts wrongly.</summary>
    [WindowsFact]
    public async Task A_copied_file_keeps_the_time_it_was_written()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/old.txt", "aged");
        tree.Dir("dst");

        var when = new DateTime(2015, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, when);

        var ops = new WindowsFileOperations();

        await Finished(ops.Copy([file], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.Equal(
            when, File.GetLastWriteTimeUtc(tree.At("dst", "old.txt")), TimeSpan.FromSeconds(2));
    }

    // ---- pasting into the folder it is already in --------------------------

    /// <summary>
    /// **Copy then paste in the same folder made a duplicate impossible.** The
    /// target IS the source, so the prompt could only offer to replace a file
    /// with itself — and Replace could not work, because the copy opens the
    /// same path for reading and writing and the error blamed "something else
    /// has that file open". Explorer makes a second copy, which is plainly what
    /// was meant.
    /// </summary>
    [WindowsFact]
    public async Task Copying_a_file_into_its_own_folder_makes_a_duplicate()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/notes.txt", "mine");
        var ops = new WindowsFileOperations();

        await Finished(ops.Copy([file], tree.At("src"), Always(ConflictResolution.Overwrite)));

        // The original is untouched...
        Assert.Equal("mine", tree.Read("src", "notes.txt"));

        // ...and there is now a second one beside it.
        var copies = Directory.GetFiles(tree.At("src"));
        Assert.Equal(2, copies.Length);
    }

    /// <summary>A move to where it already is has nothing to do, and must not
    /// duplicate or delete anything.</summary>
    [WindowsFact]
    public async Task Moving_a_file_into_its_own_folder_changes_nothing()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/notes.txt", "mine");
        var ops = new WindowsFileOperations();

        await Finished(ops.Move([file], tree.At("src"), Always(ConflictResolution.Overwrite)));

        Assert.Equal("mine", tree.Read("src", "notes.txt"));
        Assert.Single(Directory.GetFiles(tree.At("src")));
    }
}
