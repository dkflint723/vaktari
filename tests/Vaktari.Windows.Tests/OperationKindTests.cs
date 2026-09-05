using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Every handle this engine hands out says what it is DOING, not only where.
///
/// **A handle carried an id, a state, its paths and its problems, and nothing
/// that said what kind of work it was.** So with several operations going at
/// once there was no sentence to put on a row for any of them: the shell keeps
/// a list of running handles, the transfer bar follows the newest and reports
/// its progress line, and the others had nothing on screen at all — not even a
/// name.
///
/// Real files and a real engine, the way the rest of these tests work: a handle
/// built by hand would prove only that the property exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OperationKindTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Overwrite
        => _ => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion;
        return handle;
    }

    /// <summary>
    /// A copy says copy. Copy and move are the same private method underneath,
    /// so this and the test below it are the two halves of one branch — and
    /// getting them the wrong way round would put "Moving" on the row of an
    /// operation that leaves the originals where they are.
    /// </summary>
    [WindowsFact]
    public async Task A_copy_says_it_is_a_copy()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/one.txt");
        var destination = tree.Dir("dst");

        var handle = await Settled(
            new WindowsFileOperations().Copy([source], destination, Overwrite));

        Assert.Equal(OperationKind.Copy, handle.Kind);

        // And the destination is LAST, which is the arrangement the row reads
        // to say where the bytes are going.
        Assert.Equal(destination, handle.Paths[^1]);
    }

    [WindowsFact]
    public async Task A_move_says_it_is_a_move()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/one.txt");
        var destination = tree.Dir("dst");

        var handle = await Settled(
            new WindowsFileOperations().Move([source], destination, Overwrite));

        Assert.Equal(OperationKind.Move, handle.Kind);
        Assert.Equal(destination, handle.Paths[^1]);
    }

    /// <summary>
    /// The one that is recoverable, and the one that is not. A row that
    /// confused these two would be the worst sentence in the application.
    ///
    /// Through <c>RecycleOverride</c>, like the path tests beside it, so a green
    /// run leaves nothing in the developer's own bin.
    /// </summary>
    [WindowsFact]
    public async Task A_trash_says_it_is_a_trash()
    {
        using var tree = new TempTree();
        var doomed = tree.Write("one.txt");

        var ops = new WindowsFileOperations
        {
            Bin = null,
            RecycleOverride = _ => new RecycleResult(0, false),
        };

        var handle = await Settled(ops.Trash([doomed]));

        Assert.Equal(OperationKind.Trash, handle.Kind);
    }

    [WindowsFact]
    public async Task A_delete_says_it_is_a_delete()
    {
        using var tree = new TempTree();
        var doomed = tree.Write("one.txt");

        var handle = await Settled(new WindowsFileOperations().Delete([doomed]));

        Assert.Equal(OperationKind.Delete, handle.Kind);
    }
}
