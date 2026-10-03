using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which refused undos stay in the history (rename QA, notes 1 and 2).
///
/// **Only a refusal that passes.** Something having the folder open passes —
/// the next press can work — so the step stays to be pressed again. A name
/// taken since does not pass: put back, it met the same refusal on every press
/// and walled off every step beneath it, so it is dropped as before. And a
/// batch rename's undo kept nothing it was refused: the step it could not put
/// back was gone from the history.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoRefusalTests
{
    /// <summary>
    /// **The old name taken since** is not put back, so Ctrl+Z reaches the
    /// step beneath it rather than meeting the same refusal for ever.
    /// </summary>
    [WindowsFact]
    public async Task An_undo_refused_because_the_old_name_is_taken_does_not_wedge_the_history()
    {
        using var tree = new TempTree();
        var file = tree.Write("older.txt");
        var folder = tree.Dir("photos");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(file, "older2.txt", CancellationToken.None);
        var beneath = ops.UndoDescription;

        await ops.RenameAsync(folder, "pictures", CancellationToken.None);

        Directory.CreateDirectory(folder);

        var refused = await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));

        Assert.IsNotType<InUseException>(refused);
        Assert.Equal(beneath, ops.UndoDescription);
    }

    private static async Task<WindowsFileOperations> TwoRenamedInOneStep(TempTree tree)
    {
        tree.Dir("a");
        tree.Dir("b");

        var ops = new WindowsFileOperations();

        using (var group = ops.BeginRenameGroup())
        {
            await ops.RenameAsync(tree.At("a"), "a2", CancellationToken.None);
            await ops.RenameAsync(tree.At("b"), "b2", CancellationToken.None);
            group.Description = "rename of 2 items";
        }

        return ops;
    }

    /// <summary>
    /// **A batch undo refused for one folder kept nothing of it.** What could
    /// go back goes back and becomes the redo; what something had open stays
    /// as the undo, and goes back once it is let go of.
    /// </summary>
    [WindowsFact]
    public async Task A_batch_undo_keeps_the_folder_something_had_open()
    {
        using var tree = new TempTree();
        var ops = await TwoRenamedInOneStep(tree);

        using (AnotherProgram.HoldingFile(tree.At("b2", "held.txt")))
        {
            var refused = await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));

            Assert.Contains("b2", refused.Message);
            Assert.Contains("something inside that folder is open", refused.Message);
        }

        Assert.True(Directory.Exists(tree.At("a")), "the folder nothing had open did not go back");
        Assert.True(Directory.Exists(tree.At("b2")));
        Assert.True(ops.CanUndo, "the refused folder was lost from the history");
        Assert.True(ops.CanRedo, "what did go back cannot be redone");

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(Directory.Exists(tree.At("b")), "pressing again once it was let go did not put it back");
    }

    /// <summary>A batch whose every folder was held changed nothing, and goes
    /// back on its stack whole.</summary>
    [WindowsFact]
    public async Task A_batch_undo_refused_for_everything_goes_back_whole()
    {
        using var tree = new TempTree();
        var ops = await TwoRenamedInOneStep(tree);
        var described = ops.UndoDescription;

        using (AnotherProgram.HoldingFile(tree.At("a2", "held.txt")))
        using (AnotherProgram.HoldingFile(tree.At("b2", "held.txt")))
        {
            await Assert.ThrowsAsync<InUseException>(async () => await ops.UndoAsync(CancellationToken.None));
        }

        Assert.Equal(described, ops.UndoDescription);

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(Directory.Exists(tree.At("a")));
        Assert.True(Directory.Exists(tree.At("b")));
    }
}
