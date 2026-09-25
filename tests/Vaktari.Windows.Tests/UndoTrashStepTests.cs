using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What an undo leaves on the history, and what a delete's undo takes back.
///
/// **Undoing a copy put a delete on top of the history.** The undo sent the
/// copies to the bin through the same Trash the Delete key uses, which records
/// an undo of its own — so the next Ctrl+Z read "Undo delete", put the copies
/// straight back, and the redo history was gone.
///
/// **And a delete's undo took back other people's deletions.** What arrived in
/// the bin across the recycle was all it looked at; another program deleting
/// in the same seconds had its item restored with the rest.
///
/// Nothing is recycled for real: the bin is a stand-in and the shell's call is
/// replaced, as in <see cref="TrashUndoTests"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoTrashStepTests
{
    /// <summary>A bin whose items say where they came from, and which grows when told.</summary>
    private sealed class Bin : ITrashMaintenance
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

        public List<string> Restored { get; } = [];

        public void Arrive(string key, string from) => _items[key] = from;

        public IEnumerable<string> Keys() => _items.Keys.ToList();

        public string? OriginalPathOf(string key) => _items.GetValueOrDefault(key);

        public IReadOnlyList<TrashedItem> List()
            => _items.Select(p => new TrashedItem(p.Key, p.Value, "payload", DateTimeOffset.UnixEpoch, 0, false))
                     .ToList();

        public string Restore(string key)
        {
            Restored.Add(key);
            _items.Remove(key);
            return "restored";
        }

        public void Delete(string key) => _items.Remove(key);

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    private static Func<FileConflict, ValueTask<ConflictResolution>> Skip
        => _ => ValueTask.FromResult(ConflictResolution.Skip);

    /// <summary>
    /// Copy, then undo it: the history is back where it was before the copy —
    /// empty here — and not holding a delete of what the undo took away.
    /// </summary>
    [WindowsFact]
    public async Task Undoing_a_copy_leaves_no_delete_on_the_history()
    {
        using var tree = new TempTree();
        var file = tree.Write("src/notes.txt", "a");
        var into = tree.Dir("dst");

        var bin = new Bin();
        var n = 0;

        var ops = new WindowsFileOperations
        {
            Bin = bin,
            RecycleOverride = paths =>
            {
                foreach (var path in paths) bin.Arrive("K" + n++, Path.GetFullPath(path));
                return new RecycleResult(0, false);
            },
        };

        await ops.Copy([file], into, Skip).Completion;

        Assert.True(ops.CanUndo);

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(ops.CanUndo, $"the history still holds \"{ops.UndoDescription}\"");
    }

    /// <summary>
    /// Two items arrive in the bin during one delete — this one's, and another
    /// program's. The undo takes back this one's.
    /// </summary>
    [WindowsFact]
    public async Task A_deletes_undo_takes_back_only_what_it_deleted()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "mine");

        var bin = new Bin();

        var ops = new WindowsFileOperations
        {
            Bin = bin,
            RecycleOverride = paths =>
            {
                bin.Arrive("MINE", Path.GetFullPath(paths[0]));
                bin.Arrive("THEIRS", @"C:\elsewhere\someone-elses.txt");
                return new RecycleResult(0, false);
            },
        };

        await ops.Trash([file]).Completion;
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["MINE"], bin.Restored);
    }

    /// <summary>
    /// **Asked by one spelling, recorded by the bin in another.** The delete
    /// went through \\?\ and the bin wrote the plain path, so the text matched
    /// nothing and the delete had no undo. The folder it left is the same
    /// folder, and the undo still takes back only this one.
    /// </summary>
    [WindowsFact]
    public async Task A_delete_asked_by_another_spelling_still_has_its_undo()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "mine");

        var bin = new Bin();

        var ops = new WindowsFileOperations
        {
            Bin = bin,
            RecycleOverride = _ =>
            {
                bin.Arrive("MINE", file);
                bin.Arrive("THEIRS", @"C:\elsewhere\someone-elses.txt");
                return new RecycleResult(0, false);
            },
        };

        await ops.Trash([@"\\?\" + file]).Completion;

        Assert.True(ops.CanUndo, "the delete recorded no undo");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["MINE"], bin.Restored);
    }

    /// <summary>
    /// And when nothing that arrived can be matched to what was asked, the
    /// delete still has an undo — of everything that arrived.
    /// </summary>
    [WindowsFact]
    public async Task A_delete_whose_arrival_cannot_be_matched_still_has_its_undo()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "mine");

        var bin = new Bin();

        var ops = new WindowsFileOperations
        {
            Bin = bin,
            RecycleOverride = _ =>
            {
                bin.Arrive("ONLY", @"Z:\no\such\spelling.txt");
                return new RecycleResult(0, false);
            },
        };

        await ops.Trash([file]).Completion;

        Assert.True(ops.CanUndo, "the delete recorded no undo");
    }
}
