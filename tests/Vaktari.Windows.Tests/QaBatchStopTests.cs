using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Rename QA, round 3: the batch undo and redo that stop at the first step
/// something has open. The fixer's own tests cover a two-name swap; these
/// cover a three-name cycle held at every position, a refusal mixed with an
/// "already exists", and a redo after a partial undo, checked name by name
/// and byte by byte.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QaBatchStopTests
{
    private static string[] Names(TempTree tree) =>
        [.. Directory.GetFileSystemEntries(tree.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static string Contents(TempTree tree) =>
        string.Join(",", Directory.GetFiles(tree.Root).Order(StringComparer.Ordinal)
                                  .Select(f => Path.GetFileName(f) + "=" + File.ReadAllText(f)));

    /// <summary>a→b, b→c, c→a in one batch: the sequencer parks one of them
    /// under a staging name, so every step leans on the one before it.</summary>
    private static async Task<WindowsFileOperations> Cycled(TempTree tree)
    {
        foreach (var n in new[] { "a", "b", "c" }) File.WriteAllText(tree.Write(n + ".txt"), n.ToUpperInvariant());

        var ops = new WindowsFileOperations();
        var steps = BatchRename.Sequence(
        [
            new RenamePreview(tree.At("a.txt"), "a.txt", "b.txt", null),
            new RenamePreview(tree.At("b.txt"), "b.txt", "c.txt", null),
            new RenamePreview(tree.At("c.txt"), "c.txt", "a.txt", null),
        ]);
        Assert.True(steps.Count >= 4, $"expected a staged cycle, got {steps.Count} steps");

        using (var group = ops.BeginRenameGroup())
        {
            foreach (var step in steps) await ops.RenameAsync(step.FromPath, step.NewName, CancellationToken.None);
            group!.Description = "rename of 3 items";
        }

        return ops;
    }

    private const string Original = "a.txt=A,b.txt=B,c.txt=C";
    private const string Renamed = "a.txt=C,b.txt=A,c.txt=B";

    /// <summary>
    /// Held at any of the three names, the undo either changes nothing or
    /// stops part way. Never a file under a staging name with no step left
    /// that knows about it: once let go, undo puts all three back exactly,
    /// and redo then puts the rename back exactly, and a second undo again.
    /// </summary>
    [WindowsTheory]
    [InlineData("a.txt")]
    [InlineData("b.txt")]
    [InlineData("c.txt")]
    public async Task Qa_a_three_way_cycle_held_anywhere_undoes_and_redoes_exactly(string held)
    {
        using var tree = new TempTree();
        var ops = await Cycled(tree);
        Assert.Equal(Renamed, Contents(tree));

        using (AnotherProgram.HoldingFile(tree.At(held)))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));
        }

        // Whatever the stop left, every name is accounted for: three files,
        // the parked one included, and something still to undo.
        Assert.Equal(3, Directory.GetFiles(tree.Root).Length);
        Assert.True(ops.CanUndo, "the refused part was lost from the history");

        for (var i = 0; i < 4 && ops.CanUndo; i++) await ops.UndoAsync(CancellationToken.None);
        Assert.Equal(Original, Contents(tree));

        for (var i = 0; i < 4 && ops.CanRedo; i++) await ops.RedoAsync(CancellationToken.None);
        Assert.Equal(Renamed, Contents(tree));

        for (var i = 0; i < 4 && ops.CanUndo; i++) await ops.UndoAsync(CancellationToken.None);
        Assert.Equal(Original, Contents(tree));
    }

    /// <summary>
    /// A partial undo, then redo straight away (the file let go of): the redo
    /// puts back exactly what the partial undo took away, and nothing it did
    /// not — the refused part was never undone, so it is not redone either.
    /// </summary>
    [WindowsTheory]
    [InlineData("a.txt")]
    [InlineData("b.txt")]
    [InlineData("c.txt")]
    public async Task Qa_a_redo_after_a_partial_undo_restores_the_rename_exactly(string held)
    {
        using var tree = new TempTree();
        var ops = await Cycled(tree);

        using (AnotherProgram.HoldingFile(tree.At(held)))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));
        }

        for (var i = 0; i < 4 && ops.CanRedo; i++) await ops.RedoAsync(CancellationToken.None);
        Assert.Equal(Renamed, Contents(tree));

        // And the whole batch is still one thing to undo, however it was cut.
        for (var i = 0; i < 4 && ops.CanUndo; i++) await ops.UndoAsync(CancellationToken.None);
        Assert.Equal(Original, Contents(tree));
        Assert.DoesNotContain(Names(tree), n => n.StartsWith(".vaktari-rename-", StringComparison.Ordinal));
    }

    private static async Task<WindowsFileOperations> ThreeRenamed(TempTree tree)
    {
        foreach (var n in new[] { "a", "b", "c" }) File.WriteAllText(tree.Write(n), n);

        var ops = new WindowsFileOperations();
        using (var group = ops.BeginRenameGroup())
        {
            foreach (var n in new[] { "a", "b", "c" }) await ops.RenameAsync(tree.At(n), n + "2", CancellationToken.None);
            group!.Description = "rename of 3 items";
        }

        return ops;
    }

    /// <summary>
    /// Mixed, "already exists" met first in the walk: c goes back, b's old
    /// name has been taken (that step is dropped, as before), a is held (the
    /// walk stops; a is kept). Let go, a goes back; b2 stays as it is, the
    /// file that took its name untouched; redo then re-renames a and c only.
    /// </summary>
    [WindowsFact]
    public async Task Qa_a_refusal_after_an_already_exists_keeps_the_held_one_and_drops_the_taken_one()
    {
        using var tree = new TempTree();
        var ops = await ThreeRenamed(tree);
        File.WriteAllText(tree.At("b"), "squatter");

        using (AnotherProgram.HoldingFile(tree.At("a2")))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));
        }

        Assert.Equal(["a2", "b", "b2", "c"], Names(tree));
        Assert.True(ops.CanUndo);

        await ops.UndoAsync(CancellationToken.None);
        Assert.Equal(["a", "b", "b2", "c"], Names(tree));
        Assert.Equal("squatter", File.ReadAllText(tree.At("b")));
        Assert.Equal("b", File.ReadAllText(tree.At("b2")));

        for (var i = 0; i < 4 && ops.CanRedo; i++) await ops.RedoAsync(CancellationToken.None);
        Assert.Equal(["a2", "b", "b2", "c2"], Names(tree));
        Assert.Equal("squatter", File.ReadAllText(tree.At("b")));
    }

    /// <summary>
    /// Mixed the other way: the held one met first. The walk stops before it
    /// ever reaches the taken name, so nothing at all changes and the batch
    /// stays whole; let go, the undo goes on and only the taken one is dropped.
    /// </summary>
    [WindowsFact]
    public async Task Qa_a_refusal_before_an_already_exists_changes_nothing()
    {
        using var tree = new TempTree();
        var ops = await ThreeRenamed(tree);
        File.WriteAllText(tree.At("a"), "squatter");
        var described = ops.UndoDescription;

        using (AnotherProgram.HoldingFile(tree.At("c2")))
        {
            await Assert.ThrowsAsync<InUseException>(async () => await ops.UndoAsync(CancellationToken.None));
        }

        Assert.Equal(["a", "a2", "b2", "c2"], Names(tree));
        Assert.Equal(described, ops.UndoDescription);
        Assert.False(ops.CanRedo, "a refusal that changed nothing left something to redo");

        await ops.UndoAsync(CancellationToken.None);
        Assert.Equal(["a", "a2", "b", "c"], Names(tree));
        Assert.Equal("squatter", File.ReadAllText(tree.At("a")));
    }
}
