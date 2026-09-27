using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Hiding a row does not gate its key.**
///
/// The menu stopped offering Cut, Rename and the bin on This PC's drives, and
/// Copy in the bin; the keys went on reaching them. Shift+Delete on a drive
/// row ran the prompt, and a yes went to WindowsFileOperations.Delete, which
/// cleared the read-only bits of the drive's whole tree and walked it deleting
/// files before Directory.Delete refused the root at the end. Ctrl+X on a
/// drive then Ctrl+V moved it. Ctrl+C in the bin put a binned row's old path on
/// the clipboard. The first two shipped in 0.11.0.
///
/// Every test here drives a KEY, or the command a key is bound to, never the
/// menu — and asserts that nothing reached the file operations at all, through
/// an engine that only writes down what it was asked. The drive is a folder
/// this class makes, listed in This PC by a fake places provider, so even an
/// unguarded route could only ever reach a temp folder; the path-based half of
/// the refusal is driven with a real root handed to that same recording engine.
/// </summary>
public sealed class VolumeRefusalTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private readonly IPlacesProvider? _placesBefore = PaneViewModel.Places;
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;
    private readonly IFolderViewStore? _viewsBefore = PaneViewModel.FolderViews;

    private readonly string _drive = Directory.CreateTempSubdirectory("vaktari-volume").FullName;
    private readonly string _file;

    public VolumeRefusalTests()
    {
        _file = Path.Combine(_drive, "keep.txt");
        File.WriteAllText(_file, "precious");
    }

    public override void Dispose()
    {
        ShellViewModel.OperationsOverride = null;
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        PaneViewModel.Places = _placesBefore;
        PaneViewModel.Trash = _trashBefore;
        PaneViewModel.FolderViews = _viewsBefore;

        base.Dispose();

        try { Directory.Delete(_drive, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Root => Path.GetPathRoot(Path.GetTempPath())!;

    // ---- the keys, on a real window -------------------------------------------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
            Settle();
        }

        Assert.True(done(), what);
    }

    private static ListBox Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(l => l.IsVisible && ReferenceEquals(l.DataContext, pane)
                         && l.SelectionMode.HasFlag(SelectionMode.Multiple));

    /// <summary>
    /// A real window in This PC, listing one "drive" — this class's folder —
    /// with that row selected and the keyboard on it, and an engine that only
    /// writes down what it is asked. The settings are applied after the window,
    /// whose constructor applies the ones on disk.
    /// </summary>
    private async Task InThisPc(bool confirm, Func<MainWindow, ShellViewModel, PaneViewModel, Recording, Task> body)
    {
        var ops = new Recording();
        ShellViewModel.OperationsOverride = _ => ops;

        var window = new MainWindow();

        window.Show();
        Settle();

        UseSearch(null);
        PaneViewModel.FolderViews = null;
        PaneViewModel.Places = new Drives(_drive);

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore with
        {
            General = _settingsBefore.General with
            {
                ConfirmPermanentDelete = confirm,
                ConfirmMoveToTrash = confirm,
            },
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;
        var was = pane.CurrentPath;

        try
        {
            pane.View = ViewMode.Details;

            await pane.NavigateAsync(VirtualPaths.Computer);
            await Until(() => pane.Entries.Count == 1, "This PC never listed the drive");

            window.UpdateLayout();

            var list = Listing(window, pane);

            list.SelectedIndex = 0;
            Settle();
            window.UpdateLayout();

            Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0)).Focus();
            Settle();

            // The premise: the row under the keys IS the drive.
            Assert.Equal(_drive, pane.SelectedEntry?.FullPath);

            await body(window, shell, pane, ops);
        }
        finally
        {
            if (!string.IsNullOrEmpty(was)) await pane.NavigateAsync(was);
            Settle();
            window.Close();
        }
    }

    private async Task NothingHappened(MainWindow window, PaneViewModel pane, Recording ops, string key)
    {
        var status = pane.Status;
        var prompted = window.FindControl<Border>("PromptBar")!.IsVisible;

        // Had a prompt come up, this is the yes that deleted the drive — pressed
        // so that a failure here shows the consequence, not just the question.
        // Only then: Enter on a drive row with no prompt opens the drive.
        if (prompted)
        {
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Task.Delay(50);
            Settle();
        }

        Assert.True(ops.Asked.Count == 0, $"{key} reached the engine: {string.Join(", ", ops.Asked)}");
        Assert.False(prompted, $"{key} asked about a drive");
        Assert.Equal(VolumeRoots.Refusal, status);
        Assert.True(File.Exists(_file), $"{key} removed what was on the drive");
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shift_delete_on_a_drive_deletes_nothing(bool confirm)
        => await InThisPc(confirm, async (window, _, pane, ops) =>
        {
            window.KeyPress(Key.Delete, RawInputModifiers.Shift, PhysicalKey.Delete, null);
            Settle();

            await NothingHappened(window, pane, ops, "Shift+Delete");
        });

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_on_a_drive_bins_nothing(bool confirm)
        => await InThisPc(confirm, async (window, _, pane, ops) =>
        {
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await Task.Delay(50);
            Settle();

            await NothingHappened(window, pane, ops, "Delete");
        });

    /// <summary>Ctrl+X marks nothing to be moved, so the Ctrl+V that moved the
    /// drive has nothing to move.</summary>
    [AvaloniaFact]
    public async Task Ctrl_x_on_a_drive_cuts_nothing()
        => await InThisPc(false, async (window, _, pane, ops) =>
        {
            CutMarks.Clear();

            window.KeyPress(Key.X, RawInputModifiers.Control, PhysicalKey.X, "x");
            await Task.Delay(50);
            Settle();

            await NothingHappened(window, pane, ops, "Ctrl+X");
            Assert.Empty(CutMarks.Paths);
        });

    /// <summary>
    /// **Ctrl+C on a drive copied it, and the paste landed on the drive
    /// itself** — a root has no leaf name to land under. Refused at the key.
    /// </summary>
    [AvaloniaFact]
    public async Task Ctrl_c_on_a_drive_copies_nothing()
        => await InThisPc(false, async (window, _, pane, ops) =>
        {
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            await Task.Delay(50);
            Settle();

            await NothingHappened(window, pane, ops, "Ctrl+C");
        });

    [AvaloniaFact]
    public async Task F2_and_shift_f2_rename_no_drive()
        => await InThisPc(false, async (window, shell, pane, ops) =>
        {
            var asked = 0;
            pane.RenameRequested += (_, _) => asked++;
            shell.BatchRenameRequested += (_, _) => asked++;

            window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
            Settle();

            Assert.Equal(0, asked);
            await NothingHappened(window, pane, ops, "F2");

            pane.Status = "";

            window.KeyPress(Key.F2, RawInputModifiers.Shift, PhysicalKey.F2, null);
            Settle();

            Assert.Equal(0, asked);
            await NothingHappened(window, pane, ops, "Shift+F2");
        });

    // ---- the commands the keys and the palette run ---------------------------

    private (ShellViewModel Shell, PaneViewModel Pane, Recording Ops, Board Clipboard) InThisPcModel()
    {
        var ops = new Recording();
        var clipboard = new Board();

        var shell = Own(new ShellViewModel(new Inert(), ops, clipboard: clipboard));
        shell.Start(null, Path.GetTempPath());

        var pane = shell.ActiveTab!;

        pane.CurrentPath = VirtualPaths.Computer;
        Dispatcher.UIThread.RunJobs();

        pane.SelectedEntry = new FileEntry("Test drive", _drive, 0, DateTimeOffset.Now, EntryFlags.Directory);

        return (shell, pane, ops, clipboard);
    }

    /// <summary>
    /// Every keymap command that moves, renames, bins or sends the selection,
    /// run as the key runs it — through the command the keymap names — in This
    /// PC. Copy to, Move to and the other-pane routes have no key but are in
    /// the palette.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Cut")]
    [InlineData("Rename")]
    [InlineData("BatchRename")]
    [InlineData("Duplicate")]
    [InlineData("Compress")]
    public async Task No_keymap_command_moves_renames_or_sends_a_drive(string id)
    {
        var (shell, pane, ops, clipboard) = InThisPcModel();

        var asked = 0;
        pane.RenameRequested += (_, _) => asked++;
        shell.BatchRenameRequested += (_, _) => asked++;

        var command = Commands.Find(id)?.Command?.Invoke(shell)
                      ?? throw new InvalidOperationException($"no command {id}");

        if (command.CanExecute(null)) command.Execute(null);

        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(ops.Asked);
        Assert.Empty(clipboard.Set);
        Assert.Equal(0, asked);
    }

    [AvaloniaFact]
    public void The_bin_and_delete_commands_refuse_a_drive_in_this_pc()
    {
        var (_, pane, ops, _) = InThisPcModel();

        pane.TrashSelectedCommand.Execute(null);
        Assert.Equal(VolumeRoots.Refusal, pane.Status);

        pane.Status = "";
        pane.DeleteSelectedCommand.Execute(null);
        Assert.Equal(VolumeRoots.Refusal, pane.Status);

        Assert.Empty(ops.Asked);
    }

    [AvaloniaFact]
    public void Copy_to_move_to_and_the_other_pane_refuse_a_drive()
    {
        var (shell, pane, ops, _) = InThisPcModel();

        var place = new PlaceItemViewModel(new Place
        {
            Id = "dev:elsewhere", Label = "Elsewhere", Path = Path.GetTempPath(), Kind = PlaceKind.Device, Icon = "folder",
        });

        shell.CopySelectionToCommand.Execute(place);
        Assert.Equal(VolumeRoots.Refusal, pane.Status);

        pane.Status = "";
        shell.MoveSelectionToCommand.Execute(place);
        Assert.Equal(VolumeRoots.Refusal, pane.Status);

        shell.ToggleSplitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        shell.ActivateGroup(shell.Left);
        shell.Left.ActiveTab = pane;

        // The other side in a real folder, or the move would be refused there
        // as a write into This PC, and prove nothing about the drive.
        shell.Right!.ActiveTab!.CurrentPath = Path.GetTempPath();
        Dispatcher.UIThread.RunJobs();

        pane.Status = "";
        shell.MoveToOtherPane();
        Assert.Equal(VolumeRoots.Refusal, pane.Status);

        Assert.Empty(ops.Asked);
    }

    /// <summary>
    /// **The path half, anywhere.** A drive named from outside This PC — a
    /// confirmation answered after the pane moved, a drop onto a folder, a
    /// drop onto the bin's row — is refused by its path. A real root, handed to
    /// the recording engine, so nothing can happen to it either way.
    /// </summary>
    [AvaloniaFact]
    public void A_drive_named_by_its_path_is_refused_in_an_ordinary_folder()
    {
        var ops = new Recording();
        var pane = Own(new PaneViewModel(new Inert(), ops) { CurrentPath = Path.GetTempPath() });

        pane.TrashChosen([Root]);
        pane.DeleteChosen([Root]);
        pane.TrashPaths([Root]);
        pane.PasteIntoFolder(Path.GetTempPath(), [Root], move: true);
        pane.PasteInto([Root], move: true);

        // And copied: a drive dragged onto a folder row copies by default,
        // and one on the clipboard from elsewhere pastes the same way — each
        // landing on the drive itself.
        pane.PasteIntoFolder(Path.GetTempPath(), [Root], move: false);
        pane.PasteInto([Root], move: false);

        Assert.Empty(ops.Asked);
        Assert.Equal(VolumeRoots.Refusal, pane.Status);

        // And a copy of a folder is not refused: the rule is about the drive
        // itself, and what is ON it copies as anything else does.
        pane.PasteIntoFolder(Path.GetTempPath(), [_drive], move: false);

        Assert.Single(ops.Asked);
    }

    /// <summary>
    /// **Five spellings of a root got past the pane**, each through to the
    /// engine: a UNC share with its trailing backslash, the same behind
    /// \\?\UNC\, a doubled separator, and "." and ".." left in. Handed to a
    /// recording engine, so nothing can reach a filesystem either way.
    /// </summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\UNC\server\share\")]
    [InlineData(@"Q:\\")]
    [InlineData(@"Q:\.")]
    [InlineData(@"Q:\x\..")]
    [InlineData(@"\\?\GLOBALROOT\??\Q:\")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume999\")]
    [InlineData(@"\\?\Q:\.")]
    [InlineData(@"\\?\Q:\x\..")]
    [InlineData(@"\\?\UNC\server\share\.")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-00000000dead}\.")]
    public void Every_spelling_of_a_root_is_refused_by_the_pane(string root)
    {
        var ops = new Recording();
        var pane = Own(new PaneViewModel(new Inert(), ops) { CurrentPath = Path.GetTempPath() });

        pane.TrashChosen([root]);
        pane.DeleteChosen([root]);
        pane.PasteIntoFolder(Path.GetTempPath(), [root], move: true);
        pane.PasteInto([root], move: false);

        Assert.Empty(ops.Asked);
        Assert.Equal(VolumeRoots.Refusal, pane.Status);
    }

    /// <summary>
    /// **A refused drop reported a Move.** The drop tells its source what it
    /// did, and it read that off the modifier keys rather than off the paste:
    /// a drive turned away by the guard still answered Move, which on X11
    /// tells the source to delete what it dragged. The paste now says whether
    /// it started anything, so the drop can answer None.
    /// </summary>
    [AvaloniaFact]
    public void A_refused_paste_says_it_started_nothing()
    {
        var ops = new Recording();
        var pane = Own(new PaneViewModel(new Inert(), ops) { CurrentPath = Path.GetTempPath() });

        Assert.False(pane.PasteIntoFolder(Path.GetTempPath(), [Root], move: true));
        Assert.False(pane.PasteInto([Root], move: true));
        Assert.False(pane.PasteIntoFolder(Path.GetTempPath(), [Root], move: false));
        Assert.False(pane.PasteInto([Root], move: false));
        Assert.Empty(ops.Asked);

        Assert.True(pane.PasteIntoFolder(Path.GetTempPath(), [_file], move: false));
        Assert.True(pane.PasteInto([_file], move: false));
        Assert.Equal(2, ops.Asked.Count);
    }

    /// <summary>
    /// **The bin's row reported a refused drop as a Move.** A drive dropped
    /// on the bin is refused by TrashPaths, and the drop went on to report a
    /// Move it performed — which on X11 tells the source to delete what it
    /// dragged. TrashPaths now says whether it asked the bin, as the paste does.
    /// </summary>
    [AvaloniaFact]
    public void A_refused_bin_drop_says_it_started_nothing()
    {
        var ops = new Recording();
        var pane = Own(new PaneViewModel(new Inert(), ops) { CurrentPath = Path.GetTempPath() });

        Assert.False(pane.TrashPaths([Root]));
        Assert.False(pane.TrashPaths([]));
        Assert.Empty(ops.Asked);

        Assert.True(pane.TrashPaths([_file]));
        Assert.Single(ops.Asked);
    }

    /// <summary>
    /// **A device path the text does not read is refused in the pane**, with
    /// its own sentence, before anything is queued — the logon session's name
    /// for a drive that emptied one in review among them. Handed to a
    /// recording engine; the LUID answers to nothing.
    /// </summary>
    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(@"\\?\GLOBALROOT\Sessions\0\DosDevices\00000000-0001e240\Q:\")]
    [InlineData(@"\\?\Global\C:\")]
    [InlineData(@"\\?\GLOBALROOT\Device\Harddisk0\Partition3\")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Users\me\a.txt")]
    public void A_device_path_is_refused_by_the_pane_in_its_own_words(string path)
    {
        var ops = new Recording();
        var pane = Own(new PaneViewModel(new Inert(), ops) { CurrentPath = Path.GetTempPath() });

        pane.TrashChosen([path]);
        pane.DeleteChosen([path]);
        Assert.False(pane.TrashPaths([path]));
        Assert.False(pane.PasteIntoFolder(Path.GetTempPath(), [path], move: true));
        Assert.False(pane.PasteInto([path], move: false));

        Assert.Empty(ops.Asked);
        Assert.Equal(VolumeRoots.DeviceRefusal, pane.Status);
    }

    /// <summary>
    /// **Duplicate on a drive answered "this listing is a view, not a
    /// folder"** — true of This PC, but not why a drive cannot be duplicated.
    /// It says what every other verb says of a drive.
    /// </summary>
    [AvaloniaFact]
    public void Duplicate_on_a_drive_says_what_every_other_verb_says()
    {
        var (_, pane, ops, _) = InThisPcModel();

        pane.DuplicateSelectedCommand.Execute(null);

        Assert.Equal(VolumeRoots.Refusal, pane.Status);
        Assert.Empty(ops.Asked);
    }

    /// <summary>
    /// **With nothing selected, This PC answered as though a drive had been
    /// refused.** Nothing was asked of anything; the pane says what an
    /// ordinary folder says to a Delete with nothing selected, which is
    /// nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void Delete_with_nothing_selected_in_this_pc_says_nothing_about_drives()
    {
        var (_, pane, ops, _) = InThisPcModel();

        pane.SelectedEntry = null;
        pane.SelectedEntries.Clear();
        pane.Status = "";

        pane.TrashSelectedCommand.Execute(null);
        pane.DeleteSelectedCommand.Execute(null);

        Assert.Equal("", pane.Status);
        Assert.Empty(ops.Asked);
    }

    /// <summary>
    /// **Ctrl+C in the bin put the old path on the clipboard.** A binned row
    /// names where a file USED to be; the next Paste copied whatever lives
    /// there now. Refused, with the bin's own sentence.
    /// </summary>
    [AvaloniaFact]
    public async Task Ctrl_c_in_the_bin_copies_nothing()
    {
        var clipboard = new Board();
        var pane = Own(new PaneViewModel(new Inert(), clipboard: clipboard) { CurrentPath = VirtualPaths.Trash });

        pane.SelectedEntry = new FileEntry("gone.txt", Path.Combine(Path.GetTempPath(), "gone.txt"),
                                           1, DateTimeOffset.Now, EntryFlags.None);

        // Ctrl+C runs the active pane's own command — asserted, so driving
        // that command here is driving the key.
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        Assert.Same(shell.ActiveTab!.CopySelectionToClipboardCommand,
                    Commands.Find("Copy")!.Command!.Invoke(shell));

        pane.CopySelectionToClipboardCommand.Execute(null);
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(clipboard.Set);
        Assert.StartsWith("already in", pane.Status, StringComparison.Ordinal);
    }

    // ---- doubles ---------------------------------------------------------------

    /// <summary>An engine that writes down everything it is asked, and does
    /// none of it.</summary>
    private sealed class Recording : IFileOperations
    {
        public List<string> Asked { get; } = [];

        private IOperationHandle Done(string what, IEnumerable<string> paths)
        {
            Asked.Add(what + " " + string.Join(";", paths));

            var handle = new OperationHandle();
            handle.Complete();
            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done("copy", sources);

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done("move", sources);

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Done("trash", paths);
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done("delete", paths);

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
        {
            Asked.Add("rename " + path);
            return ValueTask.CompletedTask;
        }

        public void RecordCreation(string path) { }
        public IUndoGroup? BeginRenameGroup() => null;
        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>A clipboard that writes down what it was handed.</summary>
    private sealed class Board : IClipboardService
    {
        public List<string> Set { get; } = [];

        public Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
        {
            Set.AddRange(paths);
            return Task.FromResult(true);
        }

        public Task<ClipboardPayload?> GetFilesAsync() => Task.FromResult<ClipboardPayload?>(null);
        public Task<bool> HasFilesAsync() => Task.FromResult(false);
        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
    }

    private sealed class Drives(string path) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public string? NameFor(string p) => null;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new("DEVICES",
                [
                    new Place
                    {
                        Id = "dev:test", Label = "Test drive", Path = path,
                        Kind = PlaceKind.Device, Icon = "drive-harddisk",
                    },
                ]),
            ]);

        public ValueTask PinAsync(string p, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
