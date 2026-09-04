using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The answer <see cref="ITrashMaintenance.Keys"/> gives an implementation that
/// does not override it.
///
/// **Windows overrides it, and every test that exercised it did too**, so the
/// default itself was reached by nothing: emptying it left the whole Windows
/// suite green. Linux never overrides it — XdgTrash has no Keys of its own — so
/// the default is the entire Linux answer, and a delete there works out what
/// arrived by taking a difference across it. A default answering nothing would
/// make every recycle look as though it moved nothing, and Ctrl+Z would have
/// nothing to put back.
/// </summary>
public sealed class TrashKeyDefaultTests
{
    /// <summary>Implements only what the interface requires, so the default is
    /// what answers.</summary>
    private sealed class OnlyLists(params string[] names) : ITrashMaintenance
    {
        public IReadOnlyList<TrashedItem> List() =>
        [
            .. names.Select(n => new TrashedItem(
                n, "/home/flint/" + n + ".txt", "/trash/" + n,
                DateTimeOffset.UnixEpoch, 0, IsDirectory: false)),
        ];

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public string Restore(string trashName) => trashName;

        public void Delete(string trashName) { }
    }

    /// <summary>Through the INTERFACE, because a default member is only
    /// reachable that way — which is itself the reason it went untested.
    /// </summary>
    private static ITrashMaintenance Bin(params string[] names) => new OnlyLists(names);

    [Fact]
    public void The_default_answers_the_key_of_every_listed_item()
        => Assert.Equal(["A1", "B2", "C3"], Bin("A1", "B2", "C3").Keys());

    /// <summary>And an empty bin is an empty answer rather than a throw: the
    /// difference across a recycle is taken on a bin that may well be
    /// empty.</summary>
    [Fact]
    public void An_empty_bin_answers_no_keys()
        => Assert.Empty(Bin().Keys());
}
