using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Every handle this engine hands out says where it is working.
///
/// **Nothing could ask a running operation what drive it was on.** A handle
/// carried an id, a state and a progress count, so the eject command could not
/// tell a paste onto the stick from a paste on the other side of the machine —
/// and it went ahead with both. The engine is the only thing that knows: the
/// sources and the destination are arguments to the call that makes the handle,
/// and they are gone the moment it returns.
///
/// Real files and a real copy, the way the rest of these tests work: a handle
/// built by hand would prove only that the property exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OperationPathsTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Overwrite
        => _ => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion;
        return handle;
    }

    /// <summary>
    /// Both ends. A copy ONTO a drive claims it through the destination and a
    /// copy OFF one claims it through the sources, so a handle carrying only
    /// half of that answers only half the ejects.
    /// </summary>
    [WindowsFact]
    public async Task A_copy_reports_its_sources_and_its_destination()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/one.txt");
        var destination = tree.Dir("dst");

        var handle = await Settled(
            new WindowsFileOperations().Copy([source], destination, Overwrite));

        Assert.Contains(source, handle.Paths);
        Assert.Contains(destination, handle.Paths);
    }

    /// <summary>A move is the same call underneath, and the same claim: the
    /// bytes still to be read are on the source drive.</summary>
    [WindowsFact]
    public async Task A_move_reports_its_sources_and_its_destination()
    {
        using var tree = new TempTree();
        var source = tree.Write("src/one.txt");
        var destination = tree.Dir("dst");

        var handle = await Settled(
            new WindowsFileOperations().Move([source], destination, Overwrite));

        Assert.Contains(source, handle.Paths);
        Assert.Contains(destination, handle.Paths);
    }

    /// <summary>
    /// A recycle writes to the drive it recycles from — the spec's trash lives
    /// on the volume — so a trash in flight holds the drive like any transfer.
    ///
    /// Through <c>RecycleOverride</c>, so a green run leaves nothing in the
    /// developer's own bin.
    /// </summary>
    [WindowsFact]
    public async Task A_trash_reports_what_it_is_recycling()
    {
        using var tree = new TempTree();
        var doomed = tree.Write("one.txt");

        var ops = new WindowsFileOperations
        {
            Bin = null,
            RecycleOverride = _ => new RecycleResult(0, false),
        };

        var handle = await Settled(ops.Trash([doomed]));

        Assert.Contains(doomed, handle.Paths);
    }

    [WindowsFact]
    public async Task A_delete_reports_what_it_is_destroying()
    {
        using var tree = new TempTree();
        var doomed = tree.Write("one.txt");

        var handle = await Settled(new WindowsFileOperations().Delete([doomed]));

        Assert.Contains(doomed, handle.Paths);
    }
}
