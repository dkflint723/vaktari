using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Rename QA, round 2: a batch-rename SWAP whose undo is refused part-way.
///
/// A swap of a.txt and b.txt runs as three renames:
/// 1. a.txt is parked under a staging name;
/// 2. b.txt becomes a.txt;
/// 3. the parked file becomes b.txt.
///
/// Its undo runs the three backwards. If another program holds the file now
/// called a.txt, step 2's undo is refused as in use (kept, after the round 1
/// fix). Step 1's undo would then move the parked file back to a.txt, a name
/// that is still taken. That refusal is not "in use", so the batch undo
/// swallows it as before.
///
/// The person must still be able to get both files back under their own
/// names, with nothing left under a staging name, once the other program
/// lets go.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoSwapRefusalTests
{
    // **Red at bd74aa2, measured:** the folder ends holding the staging file and
    // b.txt, and a.txt is gone; its contents sit under ".vaktari-rename-<guid>".
    // Skipped so the QA branch stays green. Remove the Skip with the fix.
    [WindowsFact(Skip = "rename QA round 2: a swap whose undo is refused in the middle strands a file under its staging name")]
    public async Task Qa_a_swap_whose_undo_is_refused_in_the_middle_can_still_be_put_back_whole()
    {
        using var tree = new TempTree();
        var a = tree.Write("a.txt");
        var b = tree.Write("b.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        var ops = new WindowsFileOperations();

        var plan = new[]
        {
            new RenamePreview(a, "a.txt", "b.txt", null),
            new RenamePreview(b, "b.txt", "a.txt", null),
        };

        var steps = BatchRename.Sequence(plan);
        Assert.Equal(3, steps.Count);

        using (var group = ops.BeginRenameGroup())
        {
            foreach (var step in steps)
                await ops.RenameAsync(step.FromPath, step.NewName, CancellationToken.None);

            group!.Description = "rename of 2 items";
        }

        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));

        using (AnotherProgram.HoldingFile(a))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await ops.UndoAsync(CancellationToken.None));
        }

        // Once it is let go of, Ctrl+Z, as many times as the history offers,
        // has to bring both names back.
        for (var i = 0; i < 4 && ops.CanUndo; i++)
        {
            try { await ops.UndoAsync(CancellationToken.None); }
            catch (IOException) { /* reported to the person; keep pressing */ }
        }

        var left = Directory.GetFiles(tree.Root).Select(Path.GetFileName).Order().ToArray();

        Assert.True(File.Exists(a) && File.Exists(b),
            "the swap could not be put back: the folder holds " + string.Join(", ", left));
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.DoesNotContain(left, n => n!.StartsWith(".vaktari-rename-", StringComparison.Ordinal));
    }
}
