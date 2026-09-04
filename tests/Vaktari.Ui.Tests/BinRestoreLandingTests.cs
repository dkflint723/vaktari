using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where a restored item actually landed.
///
/// **Restoring onto a name that is taken was completely silent.** Both bins
/// restore beside rather than over, and both return the landing path so the
/// caller can say so — the pane threw that answer away and reported "restored
/// 1 item(s)". The listing on screen at that moment is the bin, not the folder
/// the file went to, so nothing anywhere said the name had changed.
/// </summary>
public sealed class BinRestoreLandingTests : OwnedViewModels
{
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;

    public override void Dispose()
    {
        PaneViewModel.Trash = _trashBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A bin that decides, per item, where restoring it puts the payload — the
    /// one thing the real bins do that the pane could not see.
    /// </summary>
    private sealed class LandingBin : ITrashMaintenance
    {
        private readonly List<TrashedItem> _items = [];
        private readonly Dictionary<string, string> _landings = [];

        public LandingBin Holding(string key, string original, string landsAt)
        {
            _items.Add(new TrashedItem(
                key, original, "payload/" + key, DateTimeOffset.UnixEpoch, 1, false));

            _landings[key] = landsAt;

            return this;
        }

        public IReadOnlyList<TrashedItem> List() => _items.ToList();

        public string Restore(string trashName)
        {
            _items.RemoveAll(i => i.TrashName == trashName);

            return _landings[trashName];
        }

        public void Delete(string trashName) => _items.RemoveAll(i => i.TrashName == trashName);

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    private async Task<PaneViewModel> BinPane(LandingBin bin)
    {
        PaneViewModel.Trash = bin;

        var pane = Own(new PaneViewModel(new Empty()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(VirtualPaths.Trash);

        return pane;
    }

    private static void Select(PaneViewModel pane, params string[] names)
    {
        foreach (var name in names)
            pane.SelectedEntries.Add(pane.Entries.First(e => e.Name == name));
    }

    /// <summary>The finding itself: the new name is said out loud.</summary>
    [AvaloniaFact]
    public async Task A_restore_onto_a_taken_name_says_what_it_landed_as()
    {
        var bin = new LandingBin().Holding("k1", "/tmp/notes.txt", "/tmp/notes (1).txt");
        var pane = await BinPane(bin);

        Select(pane, "notes.txt");

        await pane.RestoreFromTrashCommand.ExecuteAsync(null);

        Assert.Contains("notes (1).txt", pane.Status, StringComparison.Ordinal);

        // The LEAF, not the whole path. Contains alone cannot tell the two
        // apart, because "/tmp/notes (1).txt" contains "notes (1).txt".
        Assert.DoesNotContain("/tmp/", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the ordinary restore is not made to sound like a clash. Without
    /// this, reporting the landing path unconditionally would read "the name
    /// was taken" every single time and mean nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_restore_that_gets_its_own_name_back_says_nothing_about_a_clash()
    {
        var bin = new LandingBin().Holding("k1", "/tmp/notes.txt", "/tmp/notes.txt");
        var pane = await BinPane(bin);

        Select(pane, "notes.txt");

        await pane.RestoreFromTrashCommand.ExecuteAsync(null);

        Assert.Equal("restored 1 item(s)", pane.Status);
    }

    /// <summary>
    /// Several clashes are counted rather than listed, because a selection of
    /// twenty would otherwise put a paragraph in the status bar.
    /// </summary>
    [AvaloniaFact]
    public async Task Several_taken_names_are_counted_rather_than_listed()
    {
        var bin = new LandingBin()
            .Holding("k1", "/tmp/notes.txt", "/tmp/notes (1).txt")
            .Holding("k2", "/tmp/other.txt", "/tmp/other (1).txt");

        var pane = await BinPane(bin);

        Select(pane, "notes.txt", "other.txt");

        await pane.RestoreFromTrashCommand.ExecuteAsync(null);

        Assert.Contains("2 names were taken", pane.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("notes (1).txt", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The RENAMED count chooses the wording, not the restored count: two go
    /// back and only one of them clashes, so the message names that one file
    /// rather than counting the pair.
    /// </summary>
    [AvaloniaFact]
    public async Task One_clash_among_several_still_names_the_file()
    {
        var bin = new LandingBin()
            .Holding("k1", "/tmp/notes.txt", "/tmp/notes (1).txt")
            .Holding("k2", "/tmp/other.txt", "/tmp/other.txt");

        var pane = await BinPane(bin);

        Select(pane, "notes.txt", "other.txt");

        await pane.RestoreFromTrashCommand.ExecuteAsync(null);

        Assert.Contains("restored 2 item(s)", pane.Status, StringComparison.Ordinal);
        Assert.Contains("notes (1).txt", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>A provider with nothing in it: the rows under test all come
    /// from the bin, never from a real folder.</summary>
    private sealed class Empty : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
