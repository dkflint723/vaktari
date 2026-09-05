using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The transfer submenus after the menu was regrouped: "Copy to" and "Move to"
/// carry the other pane as their first row, replacing the two extra top-level
/// entries — four flat transfer rows collapsed to two submenus.
///
/// And, at the other end of the same list, the folder that is on no list at
/// all: "Choose a folder…" opens a picker, which is what the submenus were
/// missing for every destination nobody had pinned.
/// </summary>
public sealed class TransferTargetTests : OwnedViewModels
{
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

    /// <summary>
    /// With a split, the other pane leads the list — it is the destination
    /// people reach for most, and burying it under the places would make the
    /// fold a demotion rather than a tidying.
    /// </summary>
    [AvaloniaFact]
    public void In_a_split_the_other_pane_is_the_first_target()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        shell.ToggleSplitCommand.Execute(null);
        Assert.True(shell.IsSplit);

        // The transfer entries only exist for a selection — the sentinel obeys
        // the same gate as the submenu that holds it.
        shell.ActiveTab!.SelectedEntry = new FileEntry(
            "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
            1, DateTimeOffset.UnixEpoch, EntryFlags.None);

        var targets = shell.TransferTargets;

        Assert.NotEmpty(targets);
        Assert.Equal(ShellViewModel.OtherPaneTargetId, targets[0].Id);
        Assert.Equal("The other pane", targets[0].Label);
    }

    /// <summary>Without a split there is no other pane to offer.</summary>
    [AvaloniaFact]
    public void Without_a_split_it_is_absent()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        Assert.False(shell.IsSplit);
        Assert.DoesNotContain(
            shell.TransferTargets, t => t.Id == ShellViewModel.OtherPaneTargetId);
    }

    // ---- the destination that is not on the list ----------------------------

    /// <summary>
    /// Accepts a copy or a move, does neither, and remembers what it was asked
    /// for. The transfer has to reach an engine for these tests to mean
    /// anything, and it must not reach a disk.
    /// </summary>
    private sealed class Recording : IFileOperations
    {
        public List<(string What, string Destination, IReadOnlyList<string> Sources)> Calls { get; }
            = [];

        private IOperationHandle Record(
            string what, IReadOnlyList<string> sources, string destination)
        {
            Calls.Add((what, destination, sources));

            var handle = new OperationHandle();

            handle.Begin(0, 0);
            handle.Complete();

            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => Record("copy", sources, destination);

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => Record("move", sources, destination);

        public IOperationHandle Trash(IReadOnlyList<string> paths)
            => Record("trash", paths, "");

        public IOperationHandle Delete(IReadOnlyList<string> paths)
            => Record("delete", paths, "");

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }

        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Real folders, because the transfer refuses a destination that
    /// is not there and the picker's answer has to survive that check.</summary>
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-transfer-" + Guid.NewGuid().ToString("N")[..8]);

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);

        return path;
    }

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing a test over */ }
    }

    /// <summary>
    /// One folder pinned, and nothing else — the sidebar's places are the whole
    /// of the transfer list, so a fixture with no provider at all has a target
    /// list of exactly one row and cannot tell "last" from "only".
    /// </summary>
    private sealed class OnePinned(string path) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("PLACES",
                [
                    new Place
                    {
                        Id = "pin:archive",
                        Label = "Archive",
                        Path = path,
                        Kind = PlaceKind.Bookmark,
                        Icon = "folder",
                        IsUserPinned = true,
                    },
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing here ejects"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<int> ImportExistingAsync(CancellationToken ct)
            => ValueTask.FromResult(0);
    }

    /// <summary>The shell, its pane sitting in <see cref="_root"/> with one
    /// file picked.</summary>
    private (ShellViewModel Shell, Recording Ops, string Picked) Selecting(
        string name, IPlacesProvider? places = null)
    {
        Directory.CreateDirectory(_root);

        var ops = new Recording();
        var shell = Own(new ShellViewModel(new Inert(), ops, places: places));

        shell.Start(null, _root);

        var picked = Path.Combine(_root, name);

        shell.ActiveTab!.SelectedEntry = new FileEntry(
            name, picked, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

        return (shell, ops, picked);
    }

    /// <summary>
    /// **Copy to could only reach a folder somebody had pinned**, so the
    /// one-off destination — which is what a Copy to is usually for — was the
    /// one thing the menu could not name. Last, after the places, which is
    /// where Dolphin's picker sits too.
    ///
    /// With a place in the list, so "last" is a position among rows rather than
    /// the only seat in the room: a one-row list makes [^1] and [0] the same
    /// assertion, and the row could then be moved in front of every pinned
    /// folder with this still green.
    /// </summary>
    [AvaloniaFact]
    public async Task Choosing_a_folder_is_the_last_row()
    {
        var pinned = Folder("archive");
        var (shell, _, _) = Selecting("notes.txt", new OnePinned(pinned));

        await shell.Sidebar.ReloadAsync();

        var targets = shell.TransferTargets;

        Assert.Equal(2, targets.Count);
        Assert.Equal(pinned, targets[0].Path);

        Assert.Equal(ShellViewModel.BrowseTargetId, targets[^1].Id);
        Assert.Equal("Choose a folder…", targets[^1].Label);
    }

    /// <summary>
    /// The rows that were always there still work, and are still named by their
    /// own label rather than by their path: the picked folder joins the route
    /// the places take, it does not replace it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pinned_place_still_receives_the_files()
    {
        var pinned = Folder("archive");
        var (shell, ops, picked) = Selecting("notes.txt", new OnePinned(pinned));

        await shell.Sidebar.ReloadAsync();

        shell.CopySelectionToCommand.Execute(shell.TransferTargets[0]);

        var call = Assert.Single(ops.Calls);

        Assert.Equal(pinned, call.Destination);
        Assert.Equal([picked], call.Sources);
        Assert.Equal("copying 1 item(s) to Archive", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// Picking it asks the window for a picker rather than opening one: a view
    /// model that opened a dialog could not be built in a test, which is the
    /// shape the settings dialog's own folder browse already has.
    ///
    /// It starts where the files are. A destination is more often beside the
    /// source than at home, and it is the folder the person can see.
    /// </summary>
    [AvaloniaFact]
    public void Picking_it_asks_the_window_for_a_picker()
    {
        var (shell, _, _) = Selecting("notes.txt");

        var asked = new List<TransferBrowseRequest>();

        shell.TransferBrowseRequested += (_, request) => asked.Add(request);
        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Single(asked);
        Assert.False(asked[0].Move);
        Assert.True(PathRules.Same(_root, asked[0].StartAt));
    }

    /// <summary>Which of the two submenus it was picked from is the only thing
    /// left that says whether this is a copy or a move — the picker's title is
    /// built from it.</summary>
    [AvaloniaFact]
    public void A_move_says_so_in_the_request()
    {
        var (shell, _, _) = Selecting("notes.txt");

        var asked = new List<TransferBrowseRequest>();

        shell.TransferBrowseRequested += (_, request) => asked.Add(request);
        shell.MoveSelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Single(asked);
        Assert.True(asked[0].Move);
    }

    /// <summary>
    /// With nothing to send, nothing is asked for. Opening a folder picker and
    /// then saying "nothing selected" spends a dialog on a question whose
    /// answer was already known.
    /// </summary>
    [AvaloniaFact]
    public void With_nothing_selected_no_picker_is_asked_for()
    {
        var (shell, _, _) = Selecting("notes.txt");

        shell.ActiveTab!.SelectedEntry = null;

        var asked = 0;

        shell.TransferBrowseRequested += (_, _) => asked++;
        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Equal(0, asked);
        Assert.Equal("nothing selected", shell.ActiveTab.Status);
    }

    /// <summary>
    /// The folder that comes back is copied into, and the status names it by
    /// its leaf: a place brings its own label, and a whole path in a one-line
    /// bar pushes the count off the end of it.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_that_comes_back_receives_the_files()
    {
        var (shell, ops, picked) = Selecting("notes.txt");

        var destination = Folder("out");

        shell.TransferBrowseRequested += (_, request) => request.Chose(destination);
        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        var call = Assert.Single(ops.Calls);

        Assert.Equal("copy", call.What);
        Assert.Equal(destination, call.Destination);
        Assert.Equal([picked], call.Sources);
        Assert.Equal("copying 1 item(s) to out", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// And the other submenu moves rather than copies. The direction travels
    /// the whole way — into the request, out to the picker's title, and back
    /// through the callback to the engine — so it is worth reading at the far
    /// end and not only at the near one.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_that_comes_back_from_Move_to_is_moved_into()
    {
        var (shell, ops, picked) = Selecting("notes.txt");

        var destination = Folder("out");

        shell.TransferBrowseRequested += (_, request) => request.Chose(destination);
        shell.MoveSelectionToCommand.Execute(shell.TransferTargets[^1]);

        var call = Assert.Single(ops.Calls);

        Assert.Equal("move", call.What);
        Assert.Equal(destination, call.Destination);
        Assert.Equal([picked], call.Sources);
        Assert.Equal("moving 1 item(s) to out", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// **The files are the ones selected when the row was picked, not the ones
    /// selected when the picker closes.** A folder picker stands open for as
    /// long as somebody browses, and the listing underneath goes on living: a
    /// watcher firing on the folder being copied out of rebuilds every row, and
    /// a rebuilt row is a new object the old SelectedEntry no longer matches —
    /// so a transfer that read the selection back on the way out would send
    /// fewer files than the row was picked for, or none at all.
    ///
    /// Held rather than answered inside the handler, which is the whole point:
    /// the gap this closes only exists while the dialog is up.
    /// </summary>
    [AvaloniaFact]
    public void The_files_are_the_ones_selected_when_the_row_was_picked()
    {
        var (shell, ops, picked) = Selecting("notes.txt");

        var destination = Folder("out");
        var held = new List<TransferBrowseRequest>();

        shell.TransferBrowseRequested += (_, request) => held.Add(request);
        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        // The listing moves on under the open dialog.
        shell.ActiveTab!.SelectedEntry = null;

        Assert.Single(held).Chose(destination);

        var call = Assert.Single(ops.Calls);

        Assert.Equal([picked], call.Sources);
    }

    /// <summary>
    /// **The picker opens at the folder the files are already in**, which is the
    /// one destination the target list deliberately refuses to offer — so it is
    /// the answer a single wrong click gives.
    ///
    /// Measured before this refusal existed: "Move to → Choose a folder…"
    /// answered with the pane's own folder issued the move and reported
    /// "moving 1 item(s) to …", and WindowsFileOperations answered that move
    /// <c>state=Completed</c> with no error and the folder unchanged — a target
    /// that IS the source has nothing to do. The copy half answered Completed
    /// too and left a "notes - Copy.txt", which is what Duplicate is for.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_the_files_are_already_in_is_refused()
    {
        var (shell, ops, _) = Selecting("notes.txt");

        shell.TransferBrowseRequested += (_, request) => request.Chose(_root);
        shell.MoveSelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Empty(ops.Calls);
        Assert.Equal($"already in {Path.GetFileName(_root)}", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// A folder that is not there when the answer comes back. The picker hands
    /// over a path rather than a handle, and a share can be reachable when the
    /// dialog opens and gone when it closes — so the destination is checked on
    /// arrival, and named by its leaf exactly as a successful one is.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_that_has_gone_is_reported_rather_than_transferred_to()
    {
        var (shell, ops, _) = Selecting("notes.txt");

        shell.TransferBrowseRequested += (_, request) =>
            request.Chose(Path.Combine(_root, "gone"));

        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Empty(ops.Calls);
        Assert.Equal("gone is not reachable", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// A picker that was dismissed transfers nothing and says nothing. The row
    /// carries no path of its own, so a fall-through would reach the ordinary
    /// place route with an empty destination and report "Choose a folder… is
    /// not reachable" at somebody who had just pressed Cancel.
    /// </summary>
    [AvaloniaFact]
    public void A_dismissed_picker_transfers_nothing()
    {
        var (shell, ops, _) = Selecting("notes.txt");

        shell.TransferBrowseRequested += (_, _) => { };
        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Empty(ops.Calls);
        Assert.DoesNotContain("not reachable", shell.ActiveTab!.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A picked folder can be inside the thing being sent, and the target
    /// list's own filter cannot help.** That filter drops a place inside the
    /// SelectedEntry — one item — while the picker reaches any folder on the
    /// machine, including a subfolder of the folder being copied.
    ///
    /// Not a new refusal: both engines already fail such a transfer before a
    /// byte moves, measured as <c>state=Failed</c> saying "work" cannot be
    /// copied into a folder inside it. What is new is that it is refused
    /// BEFORE the status line has claimed "copying 1 item(s) to deep", which
    /// the engine's answer would otherwise arrive after.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_inside_the_selection_is_refused()
    {
        var (shell, ops, _) = Selecting("work");

        var inside = Folder(Path.Combine("work", "deep"));

        shell.ActiveTab!.SelectedEntry = new FileEntry(
            "work", Path.Combine(_root, "work"), 0, DateTimeOffset.UnixEpoch,
            EntryFlags.Directory);

        shell.TransferBrowseRequested += (_, request) => request.Chose(inside);
        shell.CopySelectionToCommand.Execute(shell.TransferTargets[^1]);

        Assert.Empty(ops.Calls);
        Assert.Equal("that folder cannot be sent into itself", shell.ActiveTab.Status);
    }

    /// <summary>
    /// The picker itself belongs to the window, read out of MainWindow rather
    /// than driven: a folder picker in a test is a modal dialog nothing here
    /// could dismiss. So the title it is given, where it opens, and the answer
    /// coming back are all read as source — they are the whole of what the
    /// window contributes, and nothing else here can see any of it.
    /// </summary>
    [Fact]
    public void The_window_answers_the_request_with_a_folder_picker()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        Assert.Contains("_shell.TransferBrowseRequested += OnTransferBrowseRequested;",
                        source, StringComparison.Ordinal);

        var body = RepoSource.Body(
            source, "private async void OnTransferBrowseRequested(");

        Assert.Contains("OpenFolderPickerAsync", body, StringComparison.Ordinal);

        // One folder, because the transfer takes one destination — and the
        // guard below leans on it.
        Assert.Contains("AllowMultiple = false,", body, StringComparison.Ordinal);

        // The window's half of "a dismissed picker does nothing", and the line
        // that decides it: the shell's half is pinned by
        // A_dismissed_picker_transfers_nothing, but the shell is never reached
        // unless this passes. With AllowMultiple false a successful pick is
        // always exactly one — so a guard that compared against ONE rather than
        // zero made every pick a silent no-op, and nothing here noticed.
        Assert.Contains(
            "if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;",
            body, StringComparison.Ordinal);

        // And hands the answer back. Without this the picker opens, the folder
        // is chosen, and nothing at all happens.
        Assert.Contains("request.Chose(folder);", body, StringComparison.Ordinal);

        // The submenu it was opened from is off screen by the time the picker
        // is up, and a folder picker looks identical either way, so the title
        // is the only thing left that says whether this is a copy or a move.
        Assert.Contains("\"Choose a folder to move to\"", body, StringComparison.Ordinal);
        Assert.Contains("\"Choose a folder to copy to\"", body, StringComparison.Ordinal);

        // And it opens where the files are, through the same helper the
        // settings dialog's startup-folder picker already asks that question
        // with — which is why that helper stopped being a local function.
        Assert.Contains("await Suggested(this, request.StartAt)", body, StringComparison.Ordinal);
        Assert.Contains("SuggestedStartLocation = start,", body, StringComparison.Ordinal);
    }
}
