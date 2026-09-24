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
/// A confirmation acts on what it asked about.
///
/// **A yes deleted whatever was selected when it was given.** The prompt is a
/// bar, not a dialog, so the listing stays live beneath it and the selection
/// can change between the question and the answer — a click on another row,
/// or an operation finishing and selecting the files it had just put in the
/// folder. Enter then deleted, for good, a set the prompt never named. The
/// prompt now keeps the rows it asked about, and the pane they were in.
///
/// Real windows, because the gap is between the key handler, the bar and the
/// pane, and no one of them can show it alone. Every file touched is in a
/// folder this class makes, and the bin is a stand-in.
/// </summary>
public sealed class ConfirmPromptTargetTests : OwnedViewModels
{
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-confirm").FullName;

    public override void Dispose()
    {
        ShellViewModel.OperationsOverride = null;
        PaneViewModel.Trash = _trashBefore;
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Confirming()
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(before with
        {
            General = before.General with { ConfirmPermanentDelete = true },
        });
    }

    /// <summary>A bin that remembers what it was told to destroy.</summary>
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

    /// <summary>File operations that only write down what they were asked
    /// to send to the bin.</summary>
    private sealed class RecordingOperations : IFileOperations
    {
        public List<string> Trashed { get; } = [];

        private static IOperationHandle Done()
        {
            var handle = new OperationHandle();
            handle.Begin(0, 0);
            handle.Complete();
            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
                                     Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
                                     Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Trash(IReadOnlyList<string> paths)
        {
            Trashed.AddRange(paths);
            return Done();
        }

        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct) => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }

        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
            Settle();
        }
    }

    private static void Only(PaneViewModel pane, FileEntry row)
    {
        pane.SelectedEntries.Clear();
        pane.SelectedEntries.Add(row);
        pane.SelectedEntry = row;
    }

    private static void AskToDelete(Window window)
    {
        window.KeyPress(Key.Delete, RawInputModifiers.Shift, PhysicalKey.Delete, null);
        Settle();

        Assert.True(window.FindControl<Border>("PromptBar")!.IsVisible, "the prompt did not open");
    }

    private static void Answer(Window window)
    {
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle();
    }

    /// <summary>
    /// In a folder: the file named is the file deleted, though the selection
    /// has moved to another one by the time the answer comes.
    /// </summary>
    [AvaloniaFact]
    public async Task A_yes_deletes_the_file_it_named_not_the_one_selected_now()
    {
        UseSearch(PaneViewModel.Search);

        var named = Path.Combine(_root, "a.txt");
        var other = Path.Combine(_root, "b.txt");
        File.WriteAllText(named, "a");
        File.WriteAllText(other, "b");

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;
            Confirming();

            await pane.NavigateAsync(_root);
            await pane.RefreshAsync();
            await WaitUntil(() => pane.Entries.Count == 2);

            // Nothing is pressed until the pane is provably in this class's
            // own folder: Shift+Delete anywhere else deletes real files.
            Assert.Equal(_root, pane.CurrentPath);

            Only(pane, pane.Entries.Single(e => e.Name == "a.txt"));
            AskToDelete(window);

            Only(pane, pane.Entries.Single(e => e.Name == "b.txt"));
            Answer(window);

            await WaitUntil(() => !File.Exists(named));

            Assert.False(File.Exists(named), "the file the prompt named is still there");
            Assert.True(File.Exists(other), "a file the prompt never named was deleted");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And the Delete key's question, "move to the bin?": the file named is
    /// the file sent, though the selection has moved on. Sent to a bin that
    /// only writes it down.
    /// </summary>
    [AvaloniaFact]
    public async Task A_yes_to_the_bin_sends_the_file_it_named_not_the_one_selected_now()
    {
        UseSearch(PaneViewModel.Search);

        var named = Path.Combine(_root, "a.txt");
        var other = Path.Combine(_root, "b.txt");
        File.WriteAllText(named, "a");
        File.WriteAllText(other, "b");

        var ops = new RecordingOperations();
        ShellViewModel.OperationsOverride = _ => ops;

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            var before = Vaktari.Ui.Settings.AppSettings.Current;
            Vaktari.Ui.Settings.AppSettings.Apply(before with
            {
                General = before.General with { ConfirmMoveToTrash = true },
            });

            await pane.NavigateAsync(_root);
            await pane.RefreshAsync();
            await WaitUntil(() => pane.Entries.Count == 2);

            Assert.Equal(_root, pane.CurrentPath);

            Only(pane, pane.Entries.Single(e => e.Name == "a.txt"));
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Settle();
            Assert.True(window.FindControl<Border>("PromptBar")!.IsVisible, "the prompt did not open");

            Only(pane, pane.Entries.Single(e => e.Name == "b.txt"));
            Answer(window);

            await WaitUntil(() => ops.Trashed.Count > 0);

            Assert.Equal([named], ops.Trashed);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **In the bin, "move to the bin?" is not asked.** Its rows carry where
    /// their items used to be; a yes given after the pane left the bin sent
    /// whatever lives at that path now.
    /// </summary>
    [AvaloniaFact]
    public async Task In_the_bin_moving_to_the_bin_is_refused_not_asked()
    {
        UseSearch(PaneViewModel.Search);

        var living = Path.Combine(_root, "notes.txt");
        File.WriteAllText(living, "the file that lives here now");

        var bin = new RecordingBin().Holding("k1", living, DateTimeOffset.UnixEpoch);
        var ops = new RecordingOperations();
        ShellViewModel.OperationsOverride = _ => ops;

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            PaneViewModel.Trash = bin;
            var before = Vaktari.Ui.Settings.AppSettings.Current;
            Vaktari.Ui.Settings.AppSettings.Apply(before with
            {
                General = before.General with { ConfirmMoveToTrash = true },
            });

            await pane.NavigateAsync(VirtualPaths.Trash);
            await pane.RefreshAsync();
            Settle();

            Assert.True(pane.IsTrashListing);

            Only(pane, pane.Entries.Single());
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Settle();

            Assert.False(window.FindControl<Border>("PromptBar")!.IsVisible, "the question was asked in the bin");

            await pane.NavigateAsync(_root);
            await WaitUntil(() => pane.Entries.Count == 1);
            Answer(window);
            Settle();

            Assert.Empty(ops.Trashed);
            Assert.True(File.Exists(living));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **Asked in a folder, answered in the bin.** The bin's refusal read where
    /// the pane was at the answer, so a yes to "delete a.txt for good?" given
    /// after the pane had moved into the bin was declined as "already in the
    /// bin" — asked, answered, and nothing done.
    /// </summary>
    [AvaloniaFact]
    public async Task A_yes_asked_in_a_folder_stays_a_folder_answer_after_the_pane_enters_the_bin()
    {
        UseSearch(PaneViewModel.Search);

        var named = Path.Combine(_root, "a.txt");
        File.WriteAllText(named, "a");

        var bin = new RecordingBin().Holding("k1", Path.Combine(_root, "old.txt"), DateTimeOffset.UnixEpoch);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            PaneViewModel.Trash = bin;
            Confirming();

            await pane.NavigateAsync(_root);
            await pane.RefreshAsync();
            await WaitUntil(() => pane.Entries.Count == 1);

            Assert.Equal(_root, pane.CurrentPath);

            Only(pane, pane.Entries.Single());
            AskToDelete(window);

            await pane.NavigateAsync(VirtualPaths.Trash);
            await pane.RefreshAsync();
            Settle();
            Assert.True(pane.IsTrashListing);

            Answer(window);

            await WaitUntil(() => !File.Exists(named));

            Assert.False(File.Exists(named), "the file the prompt named is still there");
            Assert.Empty(bin.Purged);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The same in the bin, where the rows are the bin's own items.</summary>
    [AvaloniaFact]
    public async Task In_the_bin_a_yes_destroys_the_item_it_named()
    {
        UseSearch(PaneViewModel.Search);

        var bin = new RecordingBin()
            .Holding("k1", "/tmp/one.txt", DateTimeOffset.UnixEpoch)
            .Holding("k2", "/tmp/two.txt", DateTimeOffset.UnixEpoch);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            PaneViewModel.Trash = bin;
            Confirming();

            await pane.NavigateAsync(VirtualPaths.Trash);
            await pane.RefreshAsync();
            Settle();

            Assert.True(pane.IsTrashListing);

            Only(pane, pane.Entries.Single(e => e.Name == "one.txt"));
            AskToDelete(window);

            Only(pane, pane.Entries.Single(e => e.Name == "two.txt"));
            Answer(window);

            await WaitUntil(() => bin.Purged.Count > 0);

            Assert.Equal(["k1"], bin.Purged);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **Asked in the bin, answered after the pane left it.** A bin row carries
    /// the path its item used to occupy. Had the answer gone to the file
    /// operations because the pane was now in a folder, it would have deleted
    /// whatever lives at that path today — here, a real file — instead of the
    /// binned item the question was about.
    /// </summary>
    [AvaloniaFact]
    public async Task A_yes_asked_in_the_bin_stays_a_bin_answer_after_the_pane_moves()
    {
        UseSearch(PaneViewModel.Search);

        var living = Path.Combine(_root, "notes.txt");
        File.WriteAllText(living, "the file that lives here now");

        var bin = new RecordingBin().Holding("k1", living, DateTimeOffset.UnixEpoch);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            PaneViewModel.Trash = bin;
            Confirming();

            await pane.NavigateAsync(VirtualPaths.Trash);
            await pane.RefreshAsync();
            Settle();

            Assert.True(pane.IsTrashListing);

            Only(pane, pane.Entries.Single());
            AskToDelete(window);

            await pane.NavigateAsync(_root);
            await WaitUntil(() => pane.Entries.Count == 1);
            Only(pane, pane.Entries.Single());

            Answer(window);

            await WaitUntil(() => bin.Purged.Count > 0);
            await Task.Delay(300);
            Settle();

            Assert.Equal(["k1"], bin.Purged);
            Assert.True(File.Exists(living), "the answer deleted the file at the binned item's old path");
        }
        finally
        {
            window.Close();
        }
    }
}
