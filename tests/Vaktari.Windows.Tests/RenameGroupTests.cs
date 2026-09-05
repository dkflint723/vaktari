using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A batch rename is one undo step.
///
/// **It used to be one step per file.** Every rename pushed its own entry, so
/// taking back a renumbered folder of forty photographs meant forty presses of
/// Ctrl+Z, each naming a single file — and a swap pushed MORE entries than
/// there were files, because breaking the cycle costs a staging rename and that
/// landed on the stack too.
///
/// The renames here are performed in the order
/// <see cref="Vaktari.Core.BatchRename.Sequence"/> produces, because that order
/// is what makes the undo order matter: a chain is drained from its far end, so
/// putting the first rename back before the last one has left is a collision.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RenameGroupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-renamegroup-" + Guid.NewGuid().ToString("N")[..8]);

    public RenameGroupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>The bin is faked, so nothing here goes near the real one.</summary>
    private static WindowsFileOperations Ops()
        => new()
        {
            RecycleOverride = paths =>
            {
                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }

                return new RecycleResult(0, false);
            },
        };

    private string File_(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private string At(string name) => Path.Combine(_root, name);

    private string[] Names()
        => [.. Directory.EnumerateFiles(_root).Select(Path.GetFileName)
                        .OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>
    /// Renumbering img001…img003 to start at 2, in the order the sequencer
    /// gives: the far end of the chain first.
    /// </summary>
    private async Task RenumberAsync(WindowsFileOperations ops, IUndoGroup group)
    {
        await ops.RenameAsync(At("img003.jpg"), "img004.jpg", CancellationToken.None);
        await ops.RenameAsync(At("img002.jpg"), "img003.jpg", CancellationToken.None);
        await ops.RenameAsync(At("img001.jpg"), "img002.jpg", CancellationToken.None);

        group.Description = "rename of 3 items";
    }

    /// <summary>The finding itself: three files, one press of Ctrl+Z.</summary>
    [WindowsFact]
    public async Task A_group_of_renames_is_one_undo_step()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        Assert.Equal(["img002.jpg", "img003.jpg", "img004.jpg"], Names());

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["img001.jpg", "img002.jpg", "img003.jpg"], Names());

        // The whole point: nothing is left behind for a second press.
        Assert.False(ops.CanUndo, "the batch should have been one step");
    }

    /// <summary>And the menu row names the batch, not one file of it.</summary>
    [WindowsFact]
    public async Task The_group_is_named_by_what_it_was_told()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        Assert.Equal("rename of 3 items", ops.UndoDescription);
    }

    /// <summary>
    /// One rename is not a batch. It goes on unwrapped so it keeps its own
    /// name, which flips to the old one when it is undone — a group's name is
    /// fixed and would go on naming a file by a name it no longer has.
    /// </summary>
    [WindowsFact]
    public async Task A_group_holding_one_rename_keeps_that_renames_own_name()
    {
        File_("readme.txt");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(At("readme.txt"), "notes.txt", CancellationToken.None);
            group.Description = "rename of notes.txt";
        }

        Assert.Equal("rename of notes.txt", ops.UndoDescription);

        await ops.UndoAsync(CancellationToken.None);

        // The name flipped, which is what a group could not have done.
        Assert.Equal("rename of readme.txt", ops.RedoDescription);
    }

    /// <summary>
    /// And Ctrl+Y puts the whole batch back, in the order that works. The
    /// inverses come out of the undo in the reverse of the order the redo needs
    /// them, so a redo that replayed them as collected would ask img001 for
    /// img002 while img002 was still there.
    /// </summary>
    [WindowsFact]
    public async Task Redoing_puts_the_whole_batch_back()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        await ops.UndoAsync(CancellationToken.None);
        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal(["img002.jpg", "img003.jpg", "img004.jpg"], Names());
        Assert.False(ops.CanRedo, "the batch should have been one step going forward too");
    }

    /// <summary>
    /// A swap, which is where the staging rename comes from: a becomes a
    /// temporary name, b takes a's name, and the parked file takes b's. All
    /// three come back on one press, the temporary included.
    /// </summary>
    [WindowsFact]
    public async Task A_swap_comes_back_through_its_staging_name()
    {
        File_("a.txt", "the a file");
        File_("b.txt", "the b file");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(At("a.txt"), "parked.tmp", CancellationToken.None);
            await ops.RenameAsync(At("b.txt"), "a.txt", CancellationToken.None);
            await ops.RenameAsync(At("parked.tmp"), "b.txt", CancellationToken.None);

            group.Description = "rename of 2 items";
        }

        Assert.Equal("the b file", File.ReadAllText(At("a.txt")));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["a.txt", "b.txt"], Names());
        Assert.Equal("the a file", File.ReadAllText(At("a.txt")));
        Assert.Equal("the b file", File.ReadAllText(At("b.txt")));
    }

    /// <summary>
    /// **One name that will not come back must not cost the other two.** The
    /// per-file history this replaces lost only the press it was on: measured
    /// on this same folder without a group, two of the three files came back
    /// and a redo was still offered. A composite that let the IOException out
    /// gave `names=c.txt,x.txt,y.txt,z.txt undo=- redo=-` — it abandoned the
    /// rest of the batch, and UndoAsync pops before it awaits, so the batch was
    /// gone from both stacks as well.
    ///
    /// Three independent renames, which is what a find-and-replace produces:
    /// no chain and no cycle, so the two that can come back do not depend on
    /// the one that cannot.
    /// </summary>
    [WindowsFact]
    public async Task An_obstructed_name_does_not_cost_the_rest_of_the_batch()
    {
        File_("a.txt");
        File_("b.txt");
        File_("c.txt");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(At("a.txt"), "x.txt", CancellationToken.None);
            await ops.RenameAsync(At("b.txt"), "y.txt", CancellationToken.None);
            await ops.RenameAsync(At("c.txt"), "z.txt", CancellationToken.None);

            group.Description = "rename of 3 items";
        }

        // Somebody has taken the old name back in the meantime, so z.txt has
        // nowhere to go — and it is the one the undo reaches first.
        File_("c.txt", "taken back");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["a.txt", "b.txt", "c.txt", "z.txt"], Names());
        Assert.Equal("rename of 3 items", ops.RedoDescription);
    }

    /// <summary>
    /// A batch that put nothing back offers no redo. Every file it renamed has
    /// gone, so a step that replayed "rename of 3 items" and moved nothing
    /// would be a row that lies — which a single rename already avoids on its
    /// own by offering no redo when the file is not there.
    /// </summary>
    [WindowsFact]
    public async Task A_batch_that_puts_nothing_back_leaves_no_redo()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        foreach (var name in new[] { "img002.jpg", "img003.jpg", "img004.jpg" })
            File.Delete(At(name));

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(ops.CanRedo, "nothing came back, so nothing can go forward");
        Assert.Null(ops.RedoDescription);
    }

    /// <summary>
    /// **A lone step wears the name the group was given, not its own.** A batch
    /// rename that stops on the rename after the staging move that breaks a
    /// swap leaves exactly that move in the group, and unwrapping it made the
    /// parked file's machine name the whole Undo row — measured as
    /// "rename of .vaktari-rename-0123456789abcdef".
    /// </summary>
    [WindowsFact]
    public async Task A_group_holding_only_a_staging_move_is_named_by_the_group()
    {
        File_("a.txt");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(
                At("a.txt"), ".vaktari-rename-0123456789abcdef", CancellationToken.None);

            group.Description = "rename of a.txt";
        }

        Assert.Equal("rename of a.txt", ops.UndoDescription);
    }

    /// <summary>
    /// A rename that has only joined a group has still departed from the
    /// history, so the redo it invalidates goes at once rather than when the
    /// group closes. Measured without that: a rename, an undo, then a group
    /// performing a rename, and RedoDescription still answered
    /// "rename of old.txt" with the group's file already renamed on disk. The
    /// engine is one instance for the whole application and the dialog is modal
    /// only to its own window, so a Ctrl+Y elsewhere during a forty-file batch
    /// would replay a step against files the batch had already moved.
    /// </summary>
    [WindowsFact]
    public async Task A_rename_inside_an_open_group_has_already_dropped_the_redo()
    {
        File_("old.txt");
        File_("one.txt");

        var ops = Ops();

        await ops.RenameAsync(At("old.txt"), "new.txt", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        Assert.True(ops.CanRedo, "the undo should have left something to redo");

        using var group = ops.BeginRenameGroup();

        await ops.RenameAsync(At("one.txt"), "uno.txt", CancellationToken.None);

        Assert.False(ops.CanRedo, "the group's first rename departed from the history");
    }

    /// <summary>
    /// A dialog opened and cancelled renames nothing, so the history has not
    /// been departed from and the redo stack keeps what it had.
    /// </summary>
    [WindowsFact]
    public async Task An_empty_group_leaves_the_history_alone()
    {
        File_("readme.txt");

        var ops = Ops();

        await ops.RenameAsync(At("readme.txt"), "notes.txt", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        ops.BeginRenameGroup().Dispose();

        Assert.True(ops.CanRedo, "an empty batch must not cost the redo");
        Assert.False(ops.CanUndo, "an empty batch must not push an empty step");
    }

    /// <summary>
    /// Once the group is closed, the engine goes back to recording renames one
    /// at a time — a group left standing would swallow every later rename into
    /// a step that has already been pushed.
    /// </summary>
    [WindowsFact]
    public async Task A_rename_after_the_group_closes_is_its_own_step()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");
        File_("later.txt");

        var ops = Ops();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        await ops.RenameAsync(At("later.txt"), "afterwards.txt", CancellationToken.None);

        Assert.Equal("rename of afterwards.txt", ops.UndoDescription);
    }

    /// <summary>
    /// Closing twice is what a `using` around a batch that threw halfway would
    /// do, and the same renames must not go on the stack a second time.
    /// </summary>
    [WindowsFact]
    public async Task Closing_the_group_twice_pushes_one_step()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = Ops();

        var group = ops.BeginRenameGroup();

        await RenumberAsync(ops, group);

        group.Dispose();
        group.Dispose();

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(ops.CanUndo, "the second close pushed the batch again");
    }

    /// <summary>
    /// Groups do not nest, and opening a second one does not throw away what
    /// the first had collected.
    /// </summary>
    [WindowsFact]
    public async Task Opening_a_second_group_keeps_the_first_ones_renames()
    {
        File_("one.txt");
        File_("two.txt");

        var ops = Ops();

        var first = ops.BeginRenameGroup();
        await ops.RenameAsync(At("one.txt"), "uno.txt", CancellationToken.None);

        using (var second = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(At("two.txt"), "dos.txt", CancellationToken.None);
            second.Description = "rename of dos.txt";
        }

        first.Dispose();

        await ops.UndoAsync(CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["one.txt", "two.txt"], Names());
    }

    /// <summary>
    /// **Renames only.** Copy, move and trash record from inside the Task.Run
    /// that carries out the work, so a group that caught everything would
    /// swallow a copy that merely happened to finish while the batch rename
    /// dialog was open — and taking the rename back would then delete the
    /// copied files as well.
    /// </summary>
    [WindowsFact]
    public async Task A_copy_finishing_during_a_group_is_still_its_own_step()
    {
        File_("notes.txt");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        var ops = Ops();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.Copy([At("notes.txt")], elsewhere,
                           _ => ValueTask.FromResult(ConflictResolution.KeepBoth)).Completion;

            group.Description = "rename of nothing at all";

            Assert.Equal("copy of notes.txt", ops.UndoDescription);
        }

        Assert.Equal("copy of notes.txt", ops.UndoDescription);
    }
}
