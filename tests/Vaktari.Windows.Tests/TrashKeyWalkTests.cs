using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the undo after a recycle asks the bin for.
///
/// **It asked for the whole listing, twice.** The difference across the recycle
/// is taken over trash names, and a trash name on Windows IS the <c>$I</c> path
/// that the directory entry already carries — but both ends of it went through
/// <see cref="ITrashMaintenance.List"/>, which opens every sidecar in every
/// volume's bin, parses it, and stats the payload beside it. Timed over a whole
/// Trash call against a real bin of 107 entries on two volumes, with the
/// recycle itself stubbed out: 21.8 ms of bookkeeping per Delete key press,
/// against 0.4 ms once both ends ask for keys instead.
///
/// Nothing here recycles anything, for the reason
/// <see cref="TrashUndoTests"/> gives at length.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrashKeyWalkTests
{
    /// <summary>
    /// A bin that records which of its two read routes the engine took, and
    /// whose two routes disagree the way the real one's do: a leftover
    /// <c>$I</c> with no payload has a key but no listed item. The real bin on
    /// the machine this was written on answered 114 keys to 107 entries.
    /// </summary>
    private sealed class CountingBin : ITrashMaintenance
    {
        private readonly List<string> _keys = [];
        private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

        public int Listings { get; private set; }

        public int KeyWalks { get; private set; }

        /// <summary>Run once, after the first walk has decided its answer —
        /// which is where a real recycle puts things.</summary>
        public Action? WhenFirstAsked { get; set; }

        public List<string> Restored { get; } = [];

        public void Arrive(string trashName) => _keys.Add(trashName);

        /// <summary>A metadata file whose payload has gone: a key, never an item.</summary>
        public void Leftover(string trashName)
        {
            _keys.Add(trashName);
            _unreadable.Add(trashName);
        }

        public IEnumerable<string> Keys()
        {
            KeyWalks++;

            var answer = _keys.ToList();

            if (KeyWalks == 1)
            {
                var first = WhenFirstAsked;
                WhenFirstAsked = null;
                first?.Invoke();
            }

            return answer;
        }

        public IReadOnlyList<TrashedItem> List()
        {
            Listings++;

            var answer = _keys
                .Where(key => !_unreadable.Contains(key))
                .Select(key => new TrashedItem(
                    key, @"C:\somewhere\notes.txt", "payload", DateTimeOffset.UnixEpoch, 0, false))
                .ToList();

            if (Listings == 1)
            {
                var first = WhenFirstAsked;
                WhenFirstAsked = null;
                first?.Invoke();
            }

            return answer;
        }

        public string Restore(string trashName)
        {
            Restored.Add(trashName);
            return @"C:\somewhere\notes.txt";
        }

        public void Delete(string trashName) => _keys.Remove(trashName);

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    private static (CountingBin Bin, WindowsFileOperations Ops) Recycling(string arrival)
    {
        var bin = new CountingBin();
        bin.WhenFirstAsked = () => bin.Arrive(arrival);

        return (bin, new WindowsFileOperations
        {
            Bin = bin,
            RecycleOverride = _ => new RecycleResult(0, false),
        });
    }

    /// <summary>
    /// The two walks a recycle needs are key walks, and the listing — the
    /// expensive half — is never reached at all.
    /// </summary>
    [WindowsFact]
    public async Task Working_out_what_arrived_never_lists_the_bin()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "keep me");

        var (bin, ops) = Recycling("R1A2B3");

        await ops.Trash([file]).Completion;

        Assert.Equal(2, bin.KeyWalks);
        Assert.Equal(0, bin.Listings);
    }

    /// <summary>
    /// **Both ends of the difference read the same way, or the difference is
    /// nonsense.** Keys is deliberately a wider answer than List — a leftover
    /// <c>$I</c> with no payload is a key and not an item — so a before taken
    /// from the listing and an after taken from the keys makes every such
    /// leftover look like something that just arrived, and Ctrl+Z then tries to
    /// restore a file the user never deleted. There were seven of them in the
    /// bin this was measured against.
    /// </summary>
    [WindowsFact]
    public async Task A_leftover_already_in_the_bin_is_not_mistaken_for_an_arrival()
    {
        using var tree = new TempTree();
        var file = tree.Write("notes.txt", "keep me");

        var (bin, ops) = Recycling("R1A2B3");

        // Sitting there before the Delete key was ever pressed.
        bin.Leftover("ORPHAN9");

        await ops.Trash([file]).Completion;
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["R1A2B3"], bin.Restored);
    }
}
