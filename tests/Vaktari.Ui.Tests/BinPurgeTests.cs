using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Deleting one thing out of the bin.
///
/// **The prompt was shown, confirmed, and then refused.** Shift+Delete on a bin
/// row asked "delete permanently?", took the answer, and declined with "already
/// in the bin — use Restore, or empty it" — because a bin row carries the path
/// the file USED to occupy, which the file operations cannot act on. Asked and
/// answered and nothing happened is worse than never having offered, and the
/// only way to remove one item was to empty the lot.
/// </summary>
public sealed class BinPurgeTests : OwnedViewModels
{
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;

    public override void Dispose()
    {
        PaneViewModel.Trash = _trashBefore;
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Whether the permanent-delete prompt is shown at all.</summary>
    private static void Confirming(bool ask)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(before with
        {
            General = before.General with { ConfirmPermanentDelete = ask },
        });
    }

    /// <summary>
    /// A bin that remembers what it was told to destroy, and really loses it —
    /// so a second purge of the same key cannot pass by accident.
    /// </summary>
    private sealed class RecordingBin : ITrashMaintenance
    {
        private readonly List<TrashedItem> _items = [];

        public List<string> Purged { get; } = [];

        public RecordingBin Holding(string key, string original, DateTimeOffset deleted)
        {
            _items.Add(new TrashedItem(key, original, "payload/" + key, deleted, 1, false));
            return this;
        }

        public IReadOnlyList<TrashedItem> List() => _items.ToList();

        public void Delete(string trashName)
        {
            Purged.Add(trashName);
            _items.RemoveAll(i => i.TrashName == trashName);
        }

        public string Restore(string trashName) => trashName;

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    private async Task<PaneViewModel> BinPane(RecordingBin bin)
    {
        PaneViewModel.Trash = bin;

        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(VirtualPaths.Trash);

        return pane;
    }

    /// <summary>Through <c>SelectedEntries</c>, so the layout the pane happens
    /// to be in cannot make the selection invisible to the command.</summary>
    private static void Select(PaneViewModel pane, params string[] names)
    {
        foreach (var name in names)
            pane.SelectedEntries.Add(pane.Entries.First(e => e.Name == name));
    }

    /// <summary>The whole finding: one item goes, the rest stay.</summary>
    [AvaloniaFact]
    public async Task One_binned_item_can_be_destroyed_on_its_own()
    {
        var bin = new RecordingBin()
            .Holding("k1", "/tmp/notes.txt", DateTimeOffset.UnixEpoch)
            .Holding("k2", "/tmp/other.txt", DateTimeOffset.UnixEpoch);

        var pane = await BinPane(bin);

        Select(pane, "notes.txt");

        await pane.PurgeFromTrashAsync();

        Assert.Equal(["k1"], bin.Purged);
        Assert.Contains("1", pane.Status);
    }

    /// <summary>
    /// **The row that was clicked, not the newest sharing its path.** Trash a
    /// file, restore it, trash it again and the bin holds two rows whose
    /// ORIGINAL path is identical. Restore resolves that by taking the newest,
    /// because the loser stays put and can be restored next — here the loser is
    /// gone for good, so taking the newest destroys the item nobody pointed at
    /// and leaves the row they did point at sitting on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task The_row_that_was_selected_is_the_one_destroyed()
    {
        var older = DateTimeOffset.UnixEpoch;
        var newer = older.AddDays(1);

        var bin = new RecordingBin()
            .Holding("k-old", "/tmp/notes.txt", older)
            .Holding("k-new", "/tmp/notes.txt", newer);

        var pane = await BinPane(bin);

        pane.SelectedEntries.Add(pane.Entries.First(e => e.LastWriteTime == older));

        await pane.PurgeFromTrashAsync();

        Assert.Equal(["k-old"], bin.Purged);
    }

    /// <summary>And both rows selected destroy both items, not one.</summary>
    [AvaloniaFact]
    public async Task Two_rows_sharing_a_path_destroy_two_items()
    {
        var older = DateTimeOffset.UnixEpoch;

        var bin = new RecordingBin()
            .Holding("k-old", "/tmp/notes.txt", older)
            .Holding("k-new", "/tmp/notes.txt", older.AddDays(1));

        var pane = await BinPane(bin);

        foreach (var row in pane.Entries.ToList()) pane.SelectedEntries.Add(row);

        await pane.PurgeFromTrashAsync();

        Assert.Equal(2, bin.Purged.Count);
        Assert.Contains("2", pane.Status);
    }

    /// <summary>
    /// Outside the bin this does nothing, whatever is selected.
    ///
    /// The folder's selected path is deliberately one the bin also holds — with
    /// any other path the listing guard could be deleted and the test would
    /// still pass, because nothing would match either way.
    /// </summary>
    [AvaloniaFact]
    public async Task Outside_the_bin_it_does_nothing()
    {
        var bin = new RecordingBin()
            .Holding("k1", Path.Combine(Path.GetTempPath(), "notes.txt"), DateTimeOffset.UnixEpoch);

        PaneViewModel.Trash = bin;

        var pane = Own(new PaneViewModel(
            new Silent(Path.Combine(Path.GetTempPath(), "notes.txt"))) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        pane.SelectedEntries.Add(pane.Entries[0]);

        await pane.PurgeFromTrashAsync();

        Assert.Empty(bin.Purged);
    }

    /// <summary>
    /// **A confirmed yes that produces nothing at all is the finding itself.**
    /// Restore gets away with a silent return because it is an inert button;
    /// this arrives from a confirmation.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_bin_at_all_it_says_so()
    {
        var bin = new RecordingBin().Holding("k1", "/tmp/notes.txt", DateTimeOffset.UnixEpoch);
        var pane = await BinPane(bin);

        Select(pane, "notes.txt");

        PaneViewModel.Trash = null;

        await pane.PurgeFromTrashAsync();

        Assert.NotEqual("", pane.Status);
    }

    /// <summary>The row is offered only where it can act.</summary>
    [AvaloniaFact]
    public async Task It_is_offered_in_the_bin_with_something_picked()
    {
        var bin = new RecordingBin().Holding("k1", "/tmp/notes.txt", DateTimeOffset.UnixEpoch);
        var pane = await BinPane(bin);

        Assert.False(pane.CanPurgeFromBin);

        Select(pane, "notes.txt");

        Assert.True(pane.CanPurgeFromBin);
    }

    /// <summary>
    /// **The key and the menu row have to mean the same thing.** The setting is
    /// a preference about ASKING, and it was also deciding WHICH deletion
    /// happened: with the confirmation turned off, Shift+Delete took the branch
    /// that refuses in the bin while the menu row purged.
    ///
    /// A real window, because that split lives in the key handler and nowhere
    /// a view model can be asked about it.
    /// </summary>
    [AvaloniaFact]
    public async Task With_the_confirmation_off_the_key_still_deletes_one_binned_item()
    {
        UseSearch(PaneViewModel.Search);

        var bin = new RecordingBin().Holding("k1", "/tmp/notes.txt", DateTimeOffset.UnixEpoch);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            // AFTER the window, both of them: its constructor installs the
            // platform's own bin over whatever was there, and applies the
            // settings it loaded from disk over whatever a test had set.
            PaneViewModel.Trash = bin;
            Confirming(ask: false);

            await pane.NavigateAsync(VirtualPaths.Trash);

            // **And then loaded again, deliberately.** Navigating to the path
            // a pane is already on returns early as "already there" — and a
            // restored session can have had the bin open, in which case the
            // rows on screen came from the window's own startup load, which
            // ran before this test installed its bin and found nothing. The
            // refresh is what makes the listing this test's to reason about.
            await pane.RefreshAsync();
            Settle();

            // **Nothing is pressed until the pane is provably the bin holding
            // exactly the one made-up row.** Shift+Delete anywhere else deletes
            // files for real, and a real window opens on whatever folders the
            // session restored.
            Assert.True(pane.IsTrashListing);
            Assert.Equal(["/tmp/notes.txt"], pane.Entries.Select(e => e.FullPath));

            pane.SelectedEntries.Add(pane.Entries[0]);

            window.KeyPress(Key.Delete, RawInputModifiers.Shift, PhysicalKey.Delete, null);
            Settle();

            // Positive, not "it did not say the refusal": Status starts empty,
            // so the absence of a complaint is also what the broken code did.
            Assert.Equal(["k1"], bin.Purged);
        }
        finally
        {
            // Closing flushes the session. That used to be the developer's
            // own — a test ending on the bin made the bin the folder the
            // application opened on next launch, which then failed two
            // unrelated tests — and it is now this run's, per TestState.
            window.Close();
        }
    }

    /// <summary>
    /// **The prompt, answered, and then obeyed.** This is the finding as it was
    /// reported: Shift+Delete on a bin row asked "delete permanently?", took
    /// the yes, and refused — the prompt is where the refusal was reached from,
    /// so a test that never shows one cannot see it.
    /// </summary>
    [AvaloniaFact]
    public async Task Confirming_the_prompt_destroys_the_binned_item()
    {
        UseSearch(PaneViewModel.Search);

        var bin = new RecordingBin().Holding("k1", "/tmp/notes.txt", DateTimeOffset.UnixEpoch);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            PaneViewModel.Trash = bin;

            // Explicitly on. The default says yes, but a default is not the
            // same as a fact, and the machine running this has a settings file.
            Confirming(ask: true);

            await pane.NavigateAsync(VirtualPaths.Trash);
            await pane.RefreshAsync();
            Settle();

            Assert.True(pane.IsTrashListing);
            Assert.Equal(["/tmp/notes.txt"], pane.Entries.Select(e => e.FullPath));

            pane.SelectedEntries.Add(pane.Entries[0]);
            pane.SelectedEntry = pane.Entries[0];

            window.KeyPress(Key.Delete, RawInputModifiers.Shift, PhysicalKey.Delete, null);
            Settle();

            var bar = window.FindControl<Border>("PromptBar");

            Assert.True(bar!.IsVisible, "the prompt did not open");
            Assert.Empty(bin.Purged);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();

            Assert.Equal(["k1"], bin.Purged);
        }
        finally
        {
            // Closing flushes the session. That used to be the developer's
            // own — a test ending on the bin made the bin the folder the
            // application opened on next launch, which then failed two
            // unrelated tests — and it is now this run's, per TestState.
            window.Close();
        }
    }

    /// <summary>Down to Background, where the window queues its focus work.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private sealed class Silent(string? row = null) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return row is null
                ? []
                : [new FileEntry(Path.GetFileName(row), row, 1,
                                 DateTimeOffset.UnixEpoch, EntryFlags.None)];
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
