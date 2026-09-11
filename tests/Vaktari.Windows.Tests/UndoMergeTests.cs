using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Undo takes back what a copy or a move wrote there, and nothing else.
///
/// **Undoing a copy merged into a folder sent the whole folder to the bin.**
/// The undo recorded each root's landing, and a folder that already existed
/// was merged into rather than made — so copying "photos" onto an existing
/// "photos", choosing to overwrite, and pressing Ctrl+Z binned the files that
/// were there before along with the ones that arrived. Measured here with
/// the fix taken out, and on Linux against a real trash. The bin below is a
/// folder inside the tree, because the real one is the user's Recycle Bin.
/// </summary>
[SupportedOSPlatform("windows")]
public class UndoMergeTests
{
    private static ValueTask<ConflictResolution> Overwrite(FileConflict _)
        => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task Done(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
    }

    /// <summary>The engine with its undo's bin swapped for a folder in the
    /// tree, and the names of what went into it.</summary>
    private static (WindowsFileOperations Ops, List<string> Binned) Engine(TempTree tree)
    {
        var bin = tree.Dir("bin");
        var binned = new List<string>();

        var ops = new WindowsFileOperations
        {
            TrashForUndo = paths =>
            {
                foreach (var path in paths)
                {
                    var name = Path.GetFileName(path);
                    var into = Path.Combine(bin, name);

                    binned.Add(name);

                    if (Directory.Exists(path)) Directory.Move(path, into);
                    else File.Move(path, into);
                }

                var handle = new OperationHandle { Paths = paths };
                handle.Complete();
                return handle;
            },
        };

        return (ops, binned);
    }

    [WindowsFact]
    public async Task Undoing_a_copy_merged_into_a_folder_takes_back_only_what_arrived()
    {
        using var tree = new TempTree();
        tree.Write("src/photos/new.jpg", "new");
        tree.Write("src/photos/raw/one.cr2", "new");
        tree.Write("dst/photos/already-here.jpg", "old");
        var (ops, binned) = Engine(tree);

        await Done(ops.Copy([tree.At("src", "photos")], tree.At("dst"), Overwrite));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("old", tree.Read("dst", "photos", "already-here.jpg"));
        Assert.False(tree.Exists("dst", "photos", "new.jpg"));
        Assert.False(tree.Exists("dst", "photos", "raw"));
        Assert.Equal(["new.jpg", "raw"], binned.Order());
    }

    /// <summary>
    /// And the move: only what moved goes back, into a source folder the move
    /// had swept away as empty. What was already at the destination stays
    /// there.
    /// </summary>
    [WindowsFact]
    public async Task Undoing_a_move_merged_into_a_folder_moves_back_only_what_moved()
    {
        using var tree = new TempTree();
        tree.Write("src/photos/new.jpg", "new");
        tree.Write("dst/photos/already-here.jpg", "old");
        var (ops, _) = Engine(tree);

        await Done(ops.Move([tree.At("src", "photos")], tree.At("dst"), Overwrite));

        Assert.False(tree.Exists("src", "photos"), "the move did not sweep its emptied source, so this proves nothing");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("new", tree.Read("src", "photos", "new.jpg"));
        Assert.Equal("old", tree.Read("dst", "photos", "already-here.jpg"));
        Assert.False(tree.Exists("dst", "photos", "new.jpg"));
        Assert.False(tree.Exists("src", "photos", "already-here.jpg"));
    }

    /// <summary>A folder the copy made is still taken back whole — the case
    /// that was always right, kept right.</summary>
    [WindowsFact]
    public async Task Undoing_a_copy_into_a_new_folder_still_takes_the_folder()
    {
        using var tree = new TempTree();
        tree.Write("src/photos/new.jpg", "new");
        tree.Dir("dst");
        var (ops, binned) = Engine(tree);

        await Done(ops.Copy([tree.At("src", "photos")], tree.At("dst"), Overwrite));

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(tree.Exists("dst", "photos"));
        Assert.Equal(["photos"], binned);
    }

    /// <summary>
    /// A file the copy replaced is something it wrote, so undo still takes it
    /// back, as it always did. The version it replaced cannot come back from
    /// anywhere, and leaving the copy in place would make Ctrl+Z quietly reach
    /// past it to an older step.
    /// </summary>
    [WindowsFact]
    public async Task Undoing_a_copy_that_replaced_a_file_still_takes_it_back()
    {
        using var tree = new TempTree();
        tree.Write("src/notes.txt", "new");
        tree.Write("dst/notes.txt", "old");
        var (ops, binned) = Engine(tree);

        await Done(ops.Copy([tree.At("src", "notes.txt")], tree.At("dst"), Overwrite));

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(tree.Exists("dst", "notes.txt"));
        Assert.Equal(["notes.txt"], binned);
    }
}
