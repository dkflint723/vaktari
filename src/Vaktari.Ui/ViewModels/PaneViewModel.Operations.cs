using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// What a pane does TO files, as opposed to how it shows them: the clipboard,
/// paste, create, rename, trash, delete and undo.
/// </summary>
public sealed partial class PaneViewModel
{
    // Self-contained: the pane owns its clipboard rather than raising an event
    // for the window to service. The old chain had three links and no way to
    // tell which one had broken when copy silently did nothing.

    [RelayCommand]
    public Task CopySelectionToClipboardAsync() => WriteClipboardAsync(ClipboardAction.Copy);

    [RelayCommand]
    public Task CutSelectionToClipboardAsync() => WriteClipboardAsync(ClipboardAction.Cut);

    private async Task WriteClipboardAsync(ClipboardAction action)
    {
        if (_clipboard is null) { Status = "clipboard unavailable"; return; }

        var paths = SelectionPaths();
        if (paths.Count == 0) { Status = "nothing selected"; return; }

        try
        {
            var ok = await _clipboard.SetFilesAsync(action, paths).ConfigureAwait(false);
            var verb = action == ClipboardAction.Cut ? "cut" : "copied";

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Status = ok ? $"{paths.Count} item(s) {verb}" : "clipboard unavailable";

                // **Shown, not just remembered.** A cut used to look exactly
                // like nothing having happened; Explorer greys what is pending.
                // A copy clears the marks for the same reason the clipboard
                // does — the earlier cut is no longer going to happen.
                if (!ok) return;

                // Known without asking: this pane just put them there. The
                // probe runs when a menu opens, which is later than the moment
                // the row becomes true.
                CanPaste = true;

                if (action == ClipboardAction.Cut) CutMarks.Mark(paths);
                else CutMarks.Clear();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether there is anything to paste.
    ///
    /// **Paste was live with an empty clipboard.** The row was offered in every
    /// listing but the bin, and picking it posted "clipboard has no files" — an
    /// answer the row could have given by looking grey, which is what Explorer
    /// does with the same menu.
    ///
    /// It starts true and is corrected by the probe rather than the other way
    /// round. An over-offered Paste is exactly today's behaviour and the
    /// command still explains itself; an under-offered one refuses a paste that
    /// would have worked, and that is the worse of the two to be wrong about
    /// while the answer is still in flight.
    /// </summary>
    [ObservableProperty] private bool _canPaste = true;

    /// <summary>
    /// Asks the clipboard whether it holds files, for the menu that is about to
    /// show the Paste row.
    ///
    /// ConfigureAwait(true) rather than the hop through InvokeAsync the rest of
    /// this file makes: this is asked FROM the UI thread, as a menu opens, and
    /// the answer is written to a bound property.
    /// </summary>
    public async Task RefreshClipboardAsync()
    {
        if (_clipboard is null) return;

        try
        {
            CanPaste = await _clipboard.HasFilesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Fails OPEN. A probe that could not answer must not be the reason
            // a paste is refused — the command itself still says "clipboard has
            // no files" when there is nothing there.
            Quiet.Swallowed("clipboard-probe", ex);
        }
    }

    [RelayCommand]
    public async Task PasteAsync()
    {
        if (_clipboard is null) { Status = "clipboard unavailable"; return; }

        try
        {
            var payload = await _clipboard.GetFilesAsync().ConfigureAwait(false);

            if (payload is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = "clipboard has no files");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var moving = payload.Action == ClipboardAction.Cut;

                PasteInto(payload.Paths, moving);

                // The move is under way, so the marks have done their job.
                // Left up, they would grey rows in the folder the files just
                // left — which no longer contains them.
                if (moving) CutMarks.Clear();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"paste failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task NewFolderAsync()
    {
        if (RefusedVirtualDestination(CurrentPath)) return;

        var baseName = Path.Combine(CurrentPath, "New folder");
        var target = Directory.Exists(baseName) ? XdgDeduplicate(baseName) : baseName;

        try
        {
            Directory.CreateDirectory(target);

            // **Undoable, which it was not.** Ctrl+Z straight after
            // Ctrl+Shift+N did nothing at all, because a create writes to the
            // filesystem without passing through the copy engine and the undo
            // history never heard about it. The undo puts the new folder in the
            // bin, so nothing is destroyed.
            _ops?.RecordCreation(target);

            await RefreshAsync().ConfigureAwait(true);

            // Straight into rename — the same hand-off NewFromTemplateAsync has
            // always done, and for the same reason: "New folder" is a placeholder
            // nobody wants to keep, and making them find it and press F2 is a
            // second step for something they already told us they were doing.
            BeginRenameOf(target);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = Failures.Describe(ex));
        }
    }

    /// <summary>Selects a freshly created path and opens the rename prompt on
    /// it. Shared by new folder, new file and new-from-template.</summary>
    private void BeginRenameOf(string path)
    {
        var created = _all.FirstOrDefault(e => e.FullPath == path);

        if (created.FullPath is null) return;

        // **Selected as well as renamed.** The prompt opened on the new folder
        // while the listing had nothing selected at all, so cancelling the
        // rename — or just pressing Enter to keep the name — left the keyboard
        // pointing at nothing and the new folder no easier to find than before.
        // Both references select what they have just made.
        SelectedEntry = created;

        var selection = SelectedEntries;

        selection.Clear();
        selection.Add(created);

        RenameRequested?.Invoke(this, created);
    }

    /// <summary>
    /// Creates an empty file of the chosen kind and renames it immediately.
    /// </summary>
    [RelayCommand]
    public async Task NewFileAsync(NewFileKind? kind)
    {
        if (RefusedVirtualDestination(CurrentPath)) return;

        if (kind is null) return;

        try
        {
            var target = Path.Combine(CurrentPath, "New file" + kind.Extension);
            var unique = target;
            var counter = 2;

            while (File.Exists(unique) || Directory.Exists(unique))
            {
                unique = Path.Combine(CurrentPath,
                    $"New file {counter++}{kind.Extension}");
            }

            await Task.Run(() => File.Create(unique).Dispose()).ConfigureAwait(true);

            // A script nobody can run is half a file. Guarded because
            // SetUnixFileMode throws on Windows rather than being ignored.
            if (kind.Executable && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(unique,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Undoable, the same way a new folder is: into the bin.
            _ops?.RecordCreation(unique);

            await RefreshAsync().ConfigureAwait(true);

            BeginRenameOf(unique);
        }
        catch (Exception ex)
        {
            // **The status bar spoke .NET here and English one method up.** A
            // refused create said "could not create file: Access to the path
            // 'D:\x\New file.txt' is denied.", while NewFolderAsync — same
            // file, same gesture, one key different — said "you do not have
            // permission to". The plain sentence already existed; this was the
            // one create that did not ask for it.
            Status = Failures.Describe(ex, "make that file");
        }
    }

    /// <summary>The built-in kinds, for the menu.</summary>
    public IReadOnlyList<NewFileKind> NewFileKinds => FileKinds.Common;

    private static string XdgDeduplicate(string path)
    {
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{path} {i}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        return path + " " + Guid.NewGuid().ToString("N")[..6];
    }

    /// <summary>Copy or move into a specific folder — used when a drop lands on
    /// a folder row rather than on the listing's background.</summary>
    public void PasteIntoFolder(string destination, IReadOnlyList<string> paths, bool move)
    {
        // The DESTINATION, not CurrentPath. Dropping onto a real folder row
        // while a virtual listing is showing is legitimate — Recent rows carry
        // real paths — and guarding on CurrentPath would break it.
        if (RefusedVirtualDestination(destination)) return;

        if (_ops is null || paths.Count == 0) return;

        var conflicts = Conflicts();

        var handle = move
            ? _ops.Move(paths, destination, conflicts)
            : _ops.Copy(paths, destination, conflicts);

        Track(handle);
    }

    /// <summary>Runs a copy or move into this directory, from the view's paste.</summary>
    public void PasteInto(IReadOnlyList<string> paths, bool move)
    {
        if (_ops is null || paths.Count == 0) return;
        if (RefusedVirtualDestination(CurrentPath)) return;

        var conflicts = Conflicts();

        var handle = move
            ? _ops.Move(paths, CurrentPath, conflicts)
            : _ops.Copy(paths, CurrentPath, conflicts);

        Track(handle);
    }

    /// <summary>
    /// Refuses an operation that would act on a real path while the listing is
    /// showing the bin.
    ///
    /// **A bin row carries the item's ORIGINAL path**, which RecentListing says
    /// in as many words — that is what makes the Path column and Restore work.
    /// It also means every command that reads the selection is pointed at a
    /// location the item no longer occupies. Trash something called notes.txt,
    /// write a new notes.txt, then delete the bin row: the NEW file is
    /// destroyed, permanently, and the row is still there afterwards. The
    /// confirmation cannot help, because it names a count rather than a path.
    ///
    /// Refusal rather than redirection: the sensible action on a binned item is
    /// Restore or Empty, and both already exist.
    /// </summary>
    private bool RefusedInBin()
    {
        if (!IsTrashListing) return false;

        Status = $"already in {Vaktari.Core.Naming.TheBin} — use Restore, or empty it";
        return true;
    }

    /// <summary>
    /// Whether the rows on show can be picked up and dragged somewhere.
    ///
    /// **In the bin they could be, and the drag did nothing whatever.** A binned
    /// row carries the path the item used to occupy, and that path is gone — so
    /// the drag armed, the cursor showed a payload, every target read an empty
    /// set, and releasing the button had no effect and said nothing. A gesture
    /// that completes and achieves nothing is worse than one that is refused,
    /// because there is no way to tell it from the application being broken.
    ///
    /// Refused rather than restored: putting a binned item back is the trash
    /// backend's own operation, not a copy from a path that no longer exists,
    /// and Restore already does it. This says so.
    /// </summary>
    public bool CanDragOut()
    {
        if (!IsTrashListing) return true;

        Status = $"cannot drag out of {Vaktari.Core.Naming.TheBin} — use Restore";
        return false;
    }

    /// <summary>
    /// Refuses a write whose DESTINATION is one of the virtual listings.
    ///
    /// **In the bin, CurrentPath is the literal string "vaktari:trash"**, and on
    /// Linux that is a perfectly legal relative directory name. Pasting there
    /// created a folder called `vaktari:trash` in the process's working
    /// directory, moved the files into it, deleted the originals, and reported
    /// success. Windows escaped only because a colon is illegal in a path,
    /// which is luck rather than a guard.
    /// </summary>
    private bool RefusedVirtualDestination(string destination)
    {
        if (!VirtualPaths.IsVirtual(destination)) return false;

        Status = "this listing is a view, not a folder — open a real folder first";
        return true;
    }

    /// <summary>Delete key. Recoverable, so no confirmation prompt.</summary>
    [RelayCommand]
    public void TrashSelected()
    {
        if (_ops is null || RefusedInBin()) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) return;

        // **Delete, Delete, Delete did not work.** After the rows went, nothing
        // was selected, so the next Delete had nothing to act on and the
        // keyboard had lost its place entirely. Both references move to the
        // next row. Chosen before the operation, because afterwards the rows
        // it refers to are gone.
        SelectAfterRemoving(paths);

        Track(_ops.Trash(paths));
    }

    /// <summary>
    /// Picks what to select once these rows have gone: the first row after the
    /// last of them, or the new last row when the deletion reached the end.
    ///
    /// Recorded as a PATH rather than an index, because the rows arrive back
    /// through the watcher one at a time and any index is stale by then.
    /// </summary>
    private void SelectAfterRemoving(IReadOnlyList<string> going)
    {
        var doomed = new HashSet<string>(going, StringComparer.Ordinal);

        var survivors = Entries
            .Select(e => e.FullPath)
            .OfType<string>()
            .Where(p => !doomed.Contains(p))
            .ToList();

        if (survivors.Count == 0)
        {
            _selectAfterRemoval = null;
            return;
        }

        // The first survivor at or after the last doomed row, else the last.
        var lastDoomed = Entries
            .Select((e, i) => (e.FullPath, i))
            .Where(x => x.FullPath is { } p && doomed.Contains(p))
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .Max();

        var next = Entries
            .Select((e, i) => (e.FullPath, i))
            .Where(x => x.i > lastDoomed && x.FullPath is { } p && !doomed.Contains(p))
            .Select(x => x.FullPath)
            .FirstOrDefault();

        _selectAfterRemoval = next ?? survivors[^1];
    }

    /// <summary>
    /// Sends specific paths to the bin — what dropping onto the bin's row
    /// means, as distinct from the Delete key acting on a selection.
    /// </summary>
    public void TrashPaths(IReadOnlyList<string> paths)
    {
        if (_ops is null || paths.Count == 0) return;

        Track(_ops.Trash(paths));
    }

    /// <summary>Shift+Delete. Irreversible — the view must confirm first.</summary>
    [RelayCommand]
    public void DeleteSelected()
    {
        if (_ops is null || RefusedInBin()) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) return;

        Track(_ops.Delete(paths));
    }

    [RelayCommand]
    public void BeginRename()
    {
        // Renaming a bin row would rename whatever now occupies the original
        // path, which is the same hazard delete has and is just as invisible.
        if (RefusedInBin()) return;

        // **Four of five selections used to vanish.** F2 renamed the focused
        // row and ignored the rest without a word. Explorer renumbers them all
        // and Dolphin opens its batch dialog; Vaktari has that dialog already,
        // so several files go there and one goes to the inline prompt.
        if (Selection.Count > 1)
        {
            BatchRenameRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (SelectedEntry is { } entry) RenameRequested?.Invoke(this, entry);
    }

    /// <summary>Raised when F2 is pressed on more than one row; the shell owns
    /// the dialog.</summary>
    public event EventHandler? BatchRenameRequested;

    /// <summary>
    /// Renames, and says so by throwing.
    ///
    /// **Split out because a caller that needs to STOP cannot use the
    /// swallowing one.** Batch rename wrapped its call in a try/catch and
    /// counted successes, but the method it called caught everything itself and
    /// returned normally — so the catch was unreachable, every refusal counted
    /// as a success, and a batch that renamed nothing reported renaming all of
    /// it.
    /// </summary>
    public async Task RenameOrThrowAsync(FileEntry entry, string newName)
    {
        if (_ops is null) return;

        await _ops.RenameAsync(entry.FullPath, newName, CancellationToken.None)
                  .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() => _ = RefreshAsync());
    }

    /// <summary>
    /// The same, for callers with nowhere to report a failure — the inline
    /// rename in the listing is fire-and-forget and needs the status line.
    /// </summary>
    public async Task RenameAsync(FileEntry entry, string newName)
        => await TryRenameAsync(entry, newName).ConfigureAwait(false);

    /// <summary>
    /// Renames, and says whether it worked.
    ///
    /// **The failure was only ever a sentence in the status bar.** That is the
    /// right place for it when a person is watching, and useless to a CALLER —
    /// and renaming a run of files with Tab has a caller that must not step on
    /// past a name the file system refused. The commonest refusal of all, a
    /// name already taken, is not one the local check can see: it answers the
    /// SHAPE of a name and never asks the disk.
    ///
    /// The status line is still written, because the person is still watching.
    /// </summary>
    public async Task<bool> TryRenameAsync(FileEntry entry, string newName)
    {
        try
        {
            await RenameOrThrowAsync(entry, newName).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = Failures.Describe(ex));
            return false;
        }
    }

    [RelayCommand]
    public async Task UndoAsync()
    {
        if (_ops is null || !_ops.CanUndo) return;

        try
        {
            await _ops.UndoAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => _ = RefreshAsync());
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = Failures.Describe(ex));
        }
    }

    /// <summary>Ctrl+Y, which every editor and file manager answers and this
    /// one did not.</summary>
    [RelayCommand]
    public async Task RedoAsync()
    {
        if (_ops is null || !_ops.CanRedo) return;

        try
        {
            await _ops.RedoAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => _ = RefreshAsync());
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = Failures.Describe(ex));
        }
    }

    [RelayCommand]
    public void DuplicateSelected()
    {
        // **The one write in this file that had no guard**, missed when the
        // menu consolidation moved it. Its destination is CurrentPath, which in
        // the bin and in Recent is the literal string "vaktari:trash" — a legal
        // relative directory name on Linux, so duplicating from Recent created
        // a folder called vaktari:recent-files in the working directory and
        // copied into it. In the bin the row names where a file USED to be, so
        // the copy is of whatever occupies that path now.
        if (RefusedInBin() || RefusedVirtualDestination(CurrentPath)) return;

        if (_ops is null) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) { Status = "select something to duplicate"; return; }

        // **KeepBoth without asking, and this is the one place that is right.**
        // Duplicate exists to make a second copy beside the first; prompting
        // "there is already a file called that" would be asking about the thing
        // that was just requested.
        Track(_ops.Copy(paths, CurrentPath,
            _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));
    }

    /// <summary>
    /// How a clash is settled: by asking, once per operation unless told to
    /// stop.
    ///
    /// **Every call site used to pass KeepBoth outright**, so dropping a newer
    /// copy of a file over an older one silently produced "name (1)" and there
    /// was no way to say otherwise. The engine has understood Overwrite, Skip
    /// and Cancel the whole time.
    ///
    /// A fresh closure per operation, so "do the same for the rest" means this
    /// copy and not every copy from now on — and no answer at all outlives the
    /// operation it was given for.
    /// </summary>
    private static Func<FileConflict, ValueTask<ConflictResolution>> Conflicts()
    {
        ConflictResolution? remembered = null;

        return async conflict =>
        {
            if (remembered is { } answer) return answer;

            // Nothing to ask with — a headless run, or a test. Behaving as the
            // application did before there was a prompt is the safe default:
            // it never destroys anything.
            if (AskConflict is not { } ask) return ConflictResolution.KeepBoth;

            var (resolution, applyToRest) = await ask(conflict).ConfigureAwait(false);

            if (applyToRest) remembered = resolution;

            return resolution;
        };
    }

    /// <summary>
    /// Asks somebody what to do about a clash. Set by the window, because a
    /// view model has no business owning a dialog — the same seam the shell
    /// menu and the theme installer use.
    /// </summary>
    public static Func<FileConflict, ValueTask<ConflictAnswer>>? AskConflict { get; set; }
}
