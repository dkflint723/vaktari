using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The redo half of <see cref="UndoSwapRefusalTests"/> (rename QA, round 2):
/// a swap undone, then redone while another program holds one of the two
/// files. The redo walks the same kind of batch the undo did, so a refused
/// step in the middle must stop it there too, or the step after it meets a
/// name still taken and leaves a file under its staging name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RedoSwapRefusalTests
{
    [WindowsTheory]
    [InlineData("a.txt")]
    [InlineData("b.txt")]
    public async Task A_swap_redone_while_one_file_is_held_can_still_be_redone_whole(string held)
    {
        using var tree = new TempTree();
        var a = tree.Write("a.txt");
        var b = tree.Write("b.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        var ops = new WindowsFileOperations();

        var steps = BatchRename.Sequence(
        [
            new RenamePreview(a, "a.txt", "b.txt", null),
            new RenamePreview(b, "b.txt", "a.txt", null),
        ]);

        using (var group = ops.BeginRenameGroup())
        {
            foreach (var step in steps)
                await ops.RenameAsync(step.FromPath, step.NewName, CancellationToken.None);

            group!.Description = "rename of 2 items";
        }

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("A", File.ReadAllText(a));
        Assert.True(ops.CanRedo);

        using (AnotherProgram.HoldingFile(tree.At(held)))
        {
            try { await ops.RedoAsync(CancellationToken.None); }
            catch (IOException) { /* refused, as it should be */ }
        }

        for (var i = 0; i < 4 && ops.CanRedo; i++)
        {
            try { await ops.RedoAsync(CancellationToken.None); }
            catch (IOException) { /* reported to the person; keep pressing */ }
        }

        var left = Directory.GetFiles(tree.Root).Select(Path.GetFileName).Order().ToArray();

        Assert.True(File.Exists(a) && File.Exists(b), "the swap could not be redone: the folder holds " + string.Join(", ", left));
        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));
        Assert.DoesNotContain(left, n => n!.StartsWith(".vaktari-rename-", StringComparison.Ordinal));
    }
}
