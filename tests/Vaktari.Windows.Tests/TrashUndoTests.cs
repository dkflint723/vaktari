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

        /// <summary>Keys whose restore is refused, with the real bin's kind of reason.</summary>
        public HashSet<string> Stuck { get; } = [];

        public string Restore(string trashName)
        {
            // The real bin's two refusals: nothing under that key, which is
            // what it says after another program has purged the item, and a
            // move that fails with the item still listed.
            var item = _items.FirstOrDefault(i => i.TrashName == trashName)
                ?? throw new FileNotFoundException("Nothing in the Recycle Bin for " + trashName);

            if (Stuck.Contains(trashName)) throw new IOException("the disk is full");

            Restored.Add(trashName);
            _items.RemoveAll(i => i.TrashName == trashName);

            return item.OriginalPath;
        }

        public void Delete(string trashName) => _items.RemoveAll(i => i.TrashName == trashName);

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

        public void Delete(string trashName) => inner.Delete(trashName);

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

        var ops = new WindowsFileOperations { Bin = arriving, RecycleOverride = _ => new RecycleResult(0, false) };

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

        var ops = new WindowsFileOperations { Bin = arriving, RecycleOverride = _ => new RecycleResult(0, false) };

        await ops.Trash([file]).Completion;
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["NEW222"], bin.Restored);
        Assert.DoesNotContain("OLD111", bin.Restored);
    }

    private static Task<Exception?> Undo(WindowsFileOperations ops)
        => Record.ExceptionAsync(() => ops.UndoAsync(CancellationToken.None).AsTask());

    /// <summary>
    /// Recycled together, with the bin driven so that they all arrive at once,
    /// the way one SHFileOperation puts them there: K1 for the first name, K2
    /// for the second, and so on.
    /// </summary>
    private static async Task<(WindowsFileOperations Ops, FakeBin Bin)> Recycled(
        TempTree tree, params string[] names)
    {
        var files = names.Select(name => tree.Write(name, "keep " + name)).ToList();

        var bin = new FakeBin();

        var arriving = new ArrivingBin(bin, () =>
        {
            for (var i = 0; i < files.Count; i++) bin.Arrive($"K{i + 1}", files[i]);
        });

        var ops = new WindowsFileOperations { Bin = arriving, RecycleOverride = _ => new RecycleResult(0, false) };

        await ops.Trash(files).Completion;

        Assert.True(ops.CanUndo, "a recycle left nothing to undo");

        return (ops, bin);
    }

    /// <summary>
    /// **Each refusal was swallowed and the undo reported done.** The other two
    /// came back, as they do now, and the status line said "undid delete of 3
    /// items" over a bin that still held the third. A key here is a $I path
    /// recorded by difference, so an item that is no longer in the bin has no
    /// name to be said by — it is counted.
    /// </summary>
    [WindowsFact]
    public async Task A_purged_item_is_said_rather_than_swallowed()
    {
        using var tree = new TempTree();
        var (ops, bin) = await Recycled(tree, "a.txt", "b.txt", "c.txt");

        // Purged since, by another program.
        bin.Delete("K2");

        var said = await Undo(ops);

        Assert.Equal(["K1", "K3"], bin.Restored);

        Assert.Equal(
            "one item could not go back: not in the bin any more",
            Assert.IsAssignableFrom<IOException>(said).Message);

        // The one still missing is in the bin, not on the stack — an entry that
        // can only fail again would wedge Ctrl+Z against itself.
        Assert.False(ops.CanUndo, "an entry that can only fail again went back on the stack");
    }

    /// <summary>
    /// An item the bin still lists is named by where it came from, never by
    /// its key, with the bin's own reason.
    /// </summary>
    [WindowsFact]
    public async Task What_did_not_come_back_is_named_by_where_it_came_from()
    {
        using var tree = new TempTree();
        var (ops, bin) = await Recycled(tree, "a.txt", "notes.txt", "c.txt");

        bin.Stuck.Add("K2");

        var said = await Undo(ops);

        Assert.Equal(["K1", "K3"], bin.Restored);

        Assert.Equal(
            "notes.txt could not go back: the disk is full",
            Assert.IsAssignableFrom<IOException>(said).Message);
    }

    /// <summary>
    /// One refused with the bin still listing it, one purged: the named one
    /// leads, the nameless one joins the count, and the reason is the first
    /// there is — the shape the undo of a move uses past three names.
    /// </summary>
    [WindowsFact]
    public async Task A_purged_item_beside_a_refused_one_is_counted()
    {
        using var tree = new TempTree();
        var (ops, bin) = await Recycled(tree, "a.txt", "notes.txt", "c.txt");

        bin.Stuck.Add("K2");
        bin.Delete("K3");

        var said = await Undo(ops);

        Assert.Equal(["K1"], bin.Restored);

        Assert.Equal(
            "notes.txt and 1 more could not go back: the disk is full",
            Assert.IsAssignableFrom<IOException>(said).Message);
    }

    /// <summary>The sentence the undo of a move uses: three names, then a count.</summary>
    [WindowsFact]
    public async Task Past_three_the_rest_are_counted()
    {
        using var tree = new TempTree();
        var (ops, bin) = await Recycled(tree, "a.txt", "b.txt", "c.txt", "d.txt", "e.txt");

        foreach (var key in new[] { "K1", "K2", "K3", "K4", "K5" }) bin.Stuck.Add(key);

        var said = await Undo(ops);

        Assert.Empty(bin.Restored);

        Assert.Equal(
            "a.txt, b.txt, c.txt and 2 more could not go back: the disk is full",
            Assert.IsAssignableFrom<IOException>(said).Message);
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

        var ops = new WindowsFileOperations { Bin = null, RecycleOverride = _ => new RecycleResult(0, false) };

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

        var ops = new WindowsFileOperations { Bin = new ThrowingBin(), RecycleOverride = _ => new RecycleResult(0, false) };

        var handle = ops.Trash([file]);
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.False(ops.CanUndo);
    }

    private sealed class ThrowingBin : ITrashMaintenance
    {
        public IReadOnlyList<TrashedItem> List() => throw new IOException("the bin is unavailable");
        public string Restore(string trashName) => throw new IOException("no");

        public void Delete(string trashName) => throw new IOException("no");

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }
}
