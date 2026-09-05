using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A batch rename is one undo step — the Linux twin, because both engines keep
/// their own undo stack and their own composite.
///
/// **It used to be one step per file.** Every rename pushed its own entry, so
/// taking back a renumbered folder of forty photographs meant forty presses of
/// Ctrl+Z — and a swap pushed MORE entries than there were files, because
/// breaking the cycle costs a staging rename and that landed on the stack too.
///
/// Like the other Linux operation tests, these run on any platform: the code
/// under test is path arithmetic and ordinary file I/O.
/// </summary>
public sealed class RenameGroupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-renamegroup-" + Guid.NewGuid().ToString("N")[..8]);

    public RenameGroupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

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
    private async Task RenumberAsync(LinuxFileOperations ops, IUndoGroup group)
    {
        await ops.RenameAsync(At("img003.jpg"), "img004.jpg", CancellationToken.None);
        await ops.RenameAsync(At("img002.jpg"), "img003.jpg", CancellationToken.None);
        await ops.RenameAsync(At("img001.jpg"), "img002.jpg", CancellationToken.None);

        group.Description = "rename of 3 items";
    }

    /// <summary>The finding itself: three files, one press of Ctrl+Z.</summary>
    [Fact]
    public async Task A_group_of_renames_is_one_undo_step()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = new LinuxFileOperations();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        Assert.Equal(["img002.jpg", "img003.jpg", "img004.jpg"], Names());

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["img001.jpg", "img002.jpg", "img003.jpg"], Names());

        Assert.False(ops.CanUndo, "the batch should have been one step");
    }

    [Fact]
    public async Task The_group_is_named_by_what_it_was_told()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = new LinuxFileOperations();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        Assert.Equal("rename of 3 items", ops.UndoDescription);
    }

    /// <summary>
    /// One rename is not a batch. It goes on unwrapped so it keeps its own
    /// name, which flips to the old one when it is undone.
    /// </summary>
    [Fact]
    public async Task A_group_holding_one_rename_keeps_that_renames_own_name()
    {
        File_("readme.txt");

        var ops = new LinuxFileOperations();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(At("readme.txt"), "notes.txt", CancellationToken.None);
            group.Description = "rename of notes.txt";
        }

        Assert.Equal("rename of notes.txt", ops.UndoDescription);

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("rename of readme.txt", ops.RedoDescription);
    }

    /// <summary>
    /// And Ctrl+Y puts the whole batch back, in the order that works: the
    /// inverses come out of the undo in the reverse of the order the redo needs
    /// them.
    /// </summary>
    [Fact]
    public async Task Redoing_puts_the_whole_batch_back()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = new LinuxFileOperations();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        await ops.UndoAsync(CancellationToken.None);
        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal(["img002.jpg", "img003.jpg", "img004.jpg"], Names());
        Assert.False(ops.CanRedo, "the batch should have been one step going forward too");
    }

    /// <summary>
    /// A swap, which is where the staging rename comes from. All three moves
    /// come back on one press, the temporary included.
    /// </summary>
    [Fact]
    public async Task A_swap_comes_back_through_its_staging_name()
    {
        File_("a.txt", "the a file");
        File_("b.txt", "the b file");

        var ops = new LinuxFileOperations();

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
    /// per-file history this replaces lost only the press it was on; a
    /// composite that let the IOException out abandoned the rest of the batch
    /// and, because UndoAsync pops before it awaits, lost the batch itself as
    /// well. Measured on the Windows twin, whose composite is the same code.
    /// </summary>
    [Fact]
    public async Task An_obstructed_name_does_not_cost_the_rest_of_the_batch()
    {
        File_("a.txt");
        File_("b.txt");
        File_("c.txt");

        var ops = new LinuxFileOperations();

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
    /// A batch that put nothing back offers no redo: every file it renamed has
    /// gone, and a step that replayed "rename of 3 items" and moved nothing
    /// would be a row that lies.
    /// </summary>
    [Fact]
    public async Task A_batch_that_puts_nothing_back_leaves_no_redo()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = new LinuxFileOperations();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        foreach (var name in new[] { "img002.jpg", "img003.jpg", "img004.jpg" })
            File.Delete(At(name));

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(ops.CanRedo, "nothing came back, so nothing can go forward");
        Assert.Null(ops.RedoDescription);
    }

    /// <summary>
    /// **A lone step wears the name the group was given, not its own.** A batch
    /// rename that stops on the rename after a swap's staging move leaves
    /// exactly that move in the group, and unwrapping it made the parked file's
    /// machine name the whole Undo row.
    /// </summary>
    [Fact]
    public async Task A_group_holding_only_a_staging_move_is_named_by_the_group()
    {
        File_("a.txt");

        var ops = new LinuxFileOperations();

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
    /// group closes — the engine is one instance for the whole application and
    /// the dialog is modal only to its own window.
    /// </summary>
    [Fact]
    public async Task A_rename_inside_an_open_group_has_already_dropped_the_redo()
    {
        File_("old.txt");
        File_("one.txt");

        var ops = new LinuxFileOperations();

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
    [Fact]
    public async Task An_empty_group_leaves_the_history_alone()
    {
        File_("readme.txt");

        var ops = new LinuxFileOperations();

        await ops.RenameAsync(At("readme.txt"), "notes.txt", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        ops.BeginRenameGroup().Dispose();

        Assert.True(ops.CanRedo, "an empty batch must not cost the redo");
        Assert.False(ops.CanUndo, "an empty batch must not push an empty step");
    }

    /// <summary>
    /// Once the group is closed the engine records renames one at a time again
    /// — a group left standing would swallow every later rename into a step
    /// that has already been pushed.
    /// </summary>
    [Fact]
    public async Task A_rename_after_the_group_closes_is_its_own_step()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");
        File_("later.txt");

        var ops = new LinuxFileOperations();

        using (var group = ops.BeginRenameGroup()) await RenumberAsync(ops, group);

        await ops.RenameAsync(At("later.txt"), "afterwards.txt", CancellationToken.None);

        Assert.Equal("rename of afterwards.txt", ops.UndoDescription);
    }

    /// <summary>
    /// Closing twice is what a `using` around a batch that threw halfway would
    /// do, and the same renames must not go on the stack a second time.
    /// </summary>
    [Fact]
    public async Task Closing_the_group_twice_pushes_one_step()
    {
        File_("img001.jpg");
        File_("img002.jpg");
        File_("img003.jpg");

        var ops = new LinuxFileOperations();

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
    [Fact]
    public async Task Opening_a_second_group_keeps_the_first_ones_renames()
    {
        File_("one.txt");
        File_("two.txt");

        var ops = new LinuxFileOperations();

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
    [Fact]
    public async Task A_copy_finishing_during_a_group_is_still_its_own_step()
    {
        File_("notes.txt");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        var ops = new LinuxFileOperations();

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
