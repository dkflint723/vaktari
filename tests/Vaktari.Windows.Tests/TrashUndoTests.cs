using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Ctrl+Z after a delete, on Windows.
///
/// **It did nothing here while working on Linux**, and the reason was a comment
/// rather than a limitation: the header said ITrashMaintenance was still null
/// and restoring needed a COM decision that was outstanding.
/// WindowsTrashMaintenance shipped and made every word of that false, and
/// nobody came back to delete the claim — so the feature stayed absent behind
/// an explanation of why it could not exist.
///
/// SHFileOperation genuinely reports nothing about what it recycled, which is
/// the real obstacle. The bin knows, so the engine reads it before and after
/// and takes the difference.
///
/// **Nothing here actually recycles.** The first version of these tests called
/// the real thing and quietly filled the developer's Recycle Bin with test
/// files; everything worth checking is the bookkeeping around the call, not
/// the call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrashUndoTests
{
    /// <summary>
    /// A bin that can be driven without recycling anything on the machine
    /// running the tests.
    /// </summary>
    private sealed class FakeBin : ITrashMaintenance
    {
        private readonly List<TrashedItem> _items = [];

        public List<string> Restored { get; } = [];

        /// <summary>Stands in for SHFileOperation having put something in.</summary>
        public void Arrive(string trashName, string original)
            => _items.Add(new TrashedItem(
                trashName, original, "payload", DateTimeOffset.UnixEpoch, 0, false));

        public IReadOnlyList<TrashedItem> List() => _items.ToList();

        public string Restore(string trashName)
        {
            Restored.Add(trashName);

            var item = _items.FirstOrDefault(i => i.TrashName == trashName);
            _items.RemoveAll(i => i.TrashName == trashName);

            return item?.OriginalPath ?? "";
        }

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    /// <summary>
    /// A bin whose listing arrives only after the recycle, which is what the
    /// real one does — the engine takes the difference across the call.
    /// </summary>
    private sealed class ArrivingBin(FakeBin inner, Action onFirstList) : ITrashMaintenance
    {
        private bool _listed;

        public IReadOnlyList<TrashedItem> List()
        {
            if (!_listed)
            {
                _listed = true;
                var before = inner.List();
                onFirstList();
                return before;
            }

            return inner.List();
        }

        public string Restore(string trashName) => inner.Restore(trashName);

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => inner.SweepAsync(policy, ct);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => inner.EmptyAsync(ct);
    }

    [WindowsFact]
    public async Task Recycling_can_be_undone()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "keep me");

        var bin = new FakeBin();

        // The first listing is the "before"; the item appears between then and
        // the "after", exactly as a real recycle would put it there.
        var arriving = new ArrivingBin(bin, () => bin.Arrive("R1A2B3", file));

        var ops = new WindowsFileOperations { Bin = arriving, RecycleOverride = _ => true };

        var handle = ops.Trash([file]);
        await handle.Completion;

        Assert.True(ops.CanUndo, "a recycle left nothing to undo");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["R1A2B3"], bin.Restored);
    }

    /// <summary>
    /// **Matched by trash name, not by where the file came from.** The same
    /// path can be in the bin several times over — delete, restore, delete
    /// again is exactly when somebody reaches for undo — and matching on the
    /// original path would put back the wrong one.
    /// </summary>
    [WindowsFact]
    public async Task An_earlier_copy_of_the_same_path_is_not_the_one_restored()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "keep me");

        var bin = new FakeBin();

        // An older entry for the very same original path is already in there.
        bin.Arrive("OLD111", file);

        var arriving = new ArrivingBin(bin, () => bin.Arrive("NEW222", file));

        var ops = new WindowsFileOperations { Bin = arriving, RecycleOverride = _ => true };

        await ops.Trash([file]).Completion;
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["NEW222"], bin.Restored);
        Assert.DoesNotContain("OLD111", bin.Restored);
    }

    /// <summary>
    /// With no bin to read there is no undo entry — never one that would claim
    /// a success it cannot deliver.
    /// </summary>
    [WindowsFact]
    public async Task Without_a_readable_bin_there_is_no_undo_entry()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "keep me");

        var ops = new WindowsFileOperations { Bin = null, RecycleOverride = _ => true };

        await ops.Trash([file]).Completion;

        Assert.False(ops.CanUndo);
    }

    /// <summary>
    /// A bin that throws does not fail the delete: the files really did go
    /// where the user asked, and only the undo is unavailable.
    /// </summary>
    [WindowsFact]
    public async Task A_bin_that_will_not_answer_still_lets_the_delete_succeed()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "keep me");

        var ops = new WindowsFileOperations { Bin = new ThrowingBin(), RecycleOverride = _ => true };

        var handle = ops.Trash([file]);
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.False(ops.CanUndo);
    }

    private sealed class ThrowingBin : ITrashMaintenance
    {
        public IReadOnlyList<TrashedItem> List() => throw new IOException("the bin is unavailable");
        public string Restore(string trashName) => throw new IOException("no");

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }
}
