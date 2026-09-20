using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The one bar at the bottom of the window that asks a question and waits.
///
/// **One bar and one mode, which is why these belong together.** Every prompt
/// here — a pinned place's name, a server address, and the four confirmations —
/// borrows the same PromptLabel, PromptInput, PromptConfirm, PromptCancel and
/// PromptHint, so each has to put back what the last one changed. The mode says
/// whose turn it is, and the only way that stays true is if everything that
/// sets it, reads it and clears it can be seen at once.
///
/// The file rename is the exception that proves it: it is asked for here and
/// answered on the row itself, and its state — which pane, which entry, which
/// run is still going — sits alongside the rest because ClosePrompt has to put
/// the editor away whichever way the question was answered.
/// </summary>
public partial class MainWindow
{
    private enum PromptMode { None, Rename, RenamePlace, ConfirmDelete, ConfirmTrash, ConfirmEmptyTrash, ConfirmCopyAcross, Connect }

    private PromptMode _prompt = PromptMode.None;

    /// <summary>
    /// Whether a yes/no prompt is open and should own the keyboard.
    ///
    /// **One predicate, because the list was written out by hand and lost a
    /// member.** The keyboard check named ConfirmDelete and ConfirmEmptyTrash
    /// and not ConfirmTrash, so with "confirm move to trash" turned on, Enter at
    /// the trash prompt fell straight through to the ordinary key handling
    /// below — where Enter means OPEN. Answering "yes, bin these" launched them
    /// instead, and Escape cleared the filter rather than cancelling.
    ///
    /// It went unseen because the setting is off by default, so the prompt it
    /// breaks is one most people never see. The three text-entry modes are
    /// deliberately excluded: those are guarded by the focused-TextBox rule
    /// further down, which is a different question — whether something is being
    /// typed into, not whether a decision is pending.
    /// </summary>
    private bool IsConfirming => _prompt
        is PromptMode.ConfirmDelete
        or PromptMode.ConfirmTrash
        or PromptMode.ConfirmEmptyTrash
        or PromptMode.ConfirmCopyAcross;

    /// <summary>What the copy-across prompt asked about, so a yes copies
    /// exactly that rather than the marks as they stand when it comes.</summary>
    private CopyAcrossPlan? _copyAcross;
    private FileEntry _renameTarget;

    /// <summary>
    /// The pane holding the row being renamed, or null.
    ///
    /// Held rather than re-read from <c>ActiveTab</c>, because the editor is
    /// drawn by that pane's own listing: clicking the other side while a name
    /// is being typed changes which tab is active and changes nothing about
    /// where the box is.
    /// </summary>
    private PaneViewModel? _renamePane;

    /// <summary>The rename the last confirm started, or null. Read only by
    /// <see cref="StepRenameAsync"/>, which must not step past a failure.</summary>
    private Task<bool>? _lastRename;

    /// <summary>The pinned row being renamed, for the same reason
    /// _renameTarget exists: the prompt bar is one bar for every prompt.</summary>
    private PlaceItemViewModel? _renamePlace;

    /// <summary>
    /// Naming a pinned place.
    ///
    /// Its own prompt mode rather than reusing Rename: that one is about a file
    /// and applies FileNames — a slash and a colon are refused there and are
    /// perfectly good text for a caption, and nothing is written to disk under
    /// this name.
    /// </summary>
    private void OnRenamePlaceRequested(object? sender, PlaceItemViewModel place)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.RenamePlace;
        _renamePlace = place;

        PromptLabel.Text = "Call it";
        PromptInput.Text = place.Label;
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "Rename";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "enter to confirm · esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();
        PromptInput.SelectAll();
    }

    /// <summary>
    /// Opens the editor ON THE ROW.
    ///
    /// **A name was typed at the bottom of the window instead.** This filled
    /// the shared <c>PromptBar</c> — a bottom-docked strip whose TextBox is a
    /// fixed 320 wide — so renaming a row in a full-height listing happened as
    /// far from that row as the window is tall, with nothing at either end
    /// naming the file the other meant, and a listing that stays live behind an
    /// inline bar can move the selection out from under it. The bar is still
    /// there for the confirmations, for a pinned place's name and for a server
    /// address; it is the file rename that has left it.
    /// </summary>
    private void OnRenameRequested(object? sender, FileEntry entry)
    {
        if (_shell.ActiveTab is not { } pane) return;

        // **Any second request re-pointed an editor that was already open.** F2
        // was the loud way in; Ctrl+Shift+N is the quiet one — new folder, new
        // file and new-from-template all hand off to the rename when they are
        // done, and each raised this straight over a name somebody was still
        // typing. One editor, one tenant: whoever got here first keeps it, and
        // a caller that wants it must close the one it has.
        if (_prompt is not PromptMode.None) return;

        _prompt = PromptMode.Rename;
        _renameTarget = entry;
        _renamePane = pane;

        pane.RenameText = entry.Name;

        // LAST, and after the text: this is the line that puts a box on the row
        // and hands it the keyboard, and RenameBox selects the name out of
        // whatever the box already holds.
        pane.RenamingPath = entry.FullPath;
    }

    /// <summary>
    /// The single place a confirmed prompt is acted on, so the button and the
    /// keyboard cannot drift apart.
    /// </summary>
    private void ConfirmPrompt()
    {
        var mode = _prompt;
        var target = _shell.ActiveTab;

        // Read before closing: the action must not depend on UI state that the
        // closing itself tears down.
        //
        // A rename is typed on the row and every other prompt in the bar, so
        // the name comes from whichever one is open.
        var name = mode is PromptMode.Rename
            ? _renamePane?.RenameText ?? ""
            : PromptInput?.Text ?? "";

        var entry = _renameTarget;
        var copyAcross = _copyAcross;

        // **A refused name used to close the bar and report afterwards.** By
        // the time "that name is not one Windows will take" reached the status
        // line, the box holding the typed name was gone — so correcting one
        // character meant F2 and retyping the lot. A name of nothing but spaces
        // matched no case at all below and vanished without a word.
        //
        // Asked while the box is still open, so a refusal can stay in it.
        if (mode == PromptMode.Rename)
        {
            var decision = Input.RenamePrompt.Decide(name, entry.Name);

            if (decision.Verdict == Input.RenameVerdict.Refused)
            {
                // Held open under the box on the row, rather than written into
                // a hint line at the far end of the window. Nothing re-focuses:
                // the box already has the keyboard, which is where the Enter
                // that got here came from.
                if (_renamePane is not null) _renamePane.RenameRefusal = decision.Reason;

                return;
            }

            ClosePrompt();

            // **The file you had just renamed came back unselected**, in a
            // folder that had lost the rest of the selection with it. Named
            // here because this is the only place that knows the name: the
            // reload builds its rows from the file system and has never heard
            // of the one that was typed.
            //
            // HERE rather than in RenameOrThrowAsync, which is where it looks
            // like it belongs. A Tab that is stepping through a run renames and
            // then puts the keyboard on the NEXT row — and a request registered
            // from inside the rename lands after that, so the bar edited b.txt
            // while the listing highlighted the file just finished. Registered
            // from the prompt, the reload settles before the step chooses, and
            // the step has the last word.
            //
            // From the entry's own folder rather than CurrentPath: a search
            // listing holds rows from all over the machine.
            if (decision.Verdict == Input.RenameVerdict.Rename
                && target is not null
                && PathRules.Parent(entry.FullPath) is { } folder)
                target.SelectAfterLoad(Path.Combine(folder, decision.Name));

            // Kept, so a Tab that is stepping through a run can wait for it
            // and stop when the file system says no. Nothing else reads it.
            _lastRename = decision.Verdict == Input.RenameVerdict.Rename
                ? target?.TryRenameAsync(entry, decision.Name)
                : Task.FromResult(true);

            return;
        }

        ClosePrompt();

        switch (mode)
        {
            case PromptMode.RenamePlace:
                _ = _shell.RenamePlaceAsync(_renamePlace, name);
                break;

            // **The bin refused what this had just confirmed.** Its rows carry
            // the path the file used to occupy, which the file operations
            // cannot act on — so the prompt was shown, answered, and then
            // declined with "already in the bin". Asked and answered and
            // nothing happened is worse than never having offered.
            case PromptMode.ConfirmDelete when target is { IsTrashListing: true }:
                _ = target.PurgeFromTrashAsync();
                break;

            case PromptMode.ConfirmDelete:
                target?.DeleteSelectedCommand.Execute(null);
                break;

            case PromptMode.ConfirmTrash:
                target?.TrashSelectedCommand.Execute(null);
                break;

            case PromptMode.ConfirmEmptyTrash:
                _ = target?.EmptyTrashAsync();
                break;

            case PromptMode.ConfirmCopyAcross when copyAcross is not null:
                _shell.RunCopyAcross(copyAcross);
                break;

            // Rename is answered above, before the bar closes, so that a
            // refusal can keep the typed name on screen. Tidying still happens
            // there: Windows drops a trailing space or dot at the API level, so
            // a name typed with one asks for something and gets something else.

            case PromptMode.Connect when !string.IsNullOrWhiteSpace(name):
                _ = _shell.ConnectToAsync(name.Trim());
                break;
        }
    }

    /// <summary>
    /// Reuses the prompt bar rather than adding a dialog: it already handles
    /// focus, Enter and Escape, and a server address is just another line of
    /// text to type.
    /// </summary>
    private void OnConnectRequested(object? sender, EventArgs e)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.Connect;

        PromptLabel.Text = "Connect to";

        // From the mounter, not from here: gio takes smb:// and the Windows
        // redirector takes \\server\share, and offering the wrong one is worse
        // than offering nothing.
        PromptInput.Text = _shell.ConnectPrefill;
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "Connect";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = $"{_shell.ConnectHint} — esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();

        // Caret at the end, not a selection: the scheme is a starting point to
        // type after, not something to overwrite.
        PromptInput.CaretIndex = PromptInput.Text.Length;
    }

    /// <summary>
    /// Emptying the trash is the only action here with no undo AND no per-item
    /// review, so unlike trashing it is never unprompted — that is not a
    /// preference.
    /// </summary>
    private void AskConfirmEmptyTrash()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is null) return;

        var held = ViewModels.PaneViewModel.Trash?.List() ?? [];
        if (held.Count == 0) { _shell.ActiveTab.Status = $"{Naming.TheBin} is already empty"; return; }

        _prompt = PromptMode.ConfirmEmptyTrash;

        PromptLabel.Text = ViewModels.Confirmations.EmptyBin(held);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = $"Empty {Naming.BinName}";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        PromptConfirm.Focus();
    }

    /// <summary>
    /// What a confirmation is about: the multi-selection, or the one focused
    /// row when nothing is properly selected. Both prompts read it the same
    /// way, and the count they used to print was derived from exactly this.
    /// </summary>
    private static IReadOnlyList<FileEntry> Chosen(PaneViewModel pane)
        => pane.Selection.Count > 0
            ? pane.Selection.ToList()
            : pane.SelectedEntry is { } one ? [one] : [];

    /// <summary>
    /// Deleting for good, from wherever it was asked for.
    ///
    /// **The bin is where a confirmed yes was refused.** Its rows carry the
    /// path the file USED to occupy, so they cannot go through the file
    /// operations at all — the trash's own key is the only safe route.
    ///
    /// One helper rather than the same three lines in two places, because the
    /// setting is a preference about ASKING and must not also decide WHICH
    /// deletion happens: with the confirmation turned off, the key took the
    /// branch that refuses in the bin while the menu row purged, and the two
    /// then meant different things on the same rows.
    /// </summary>
    private void PermanentlyDelete(PaneViewModel pane)
    {
        if (AppSettings.Current.General.ConfirmPermanentDelete) AskConfirmDelete();
        else if (pane.IsTrashListing) _ = pane.PurgeFromTrashAsync();
        else pane.DeleteSelectedCommand.Execute(null);
    }

    private void AskConfirmDelete()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        // **The entries, not just how many of them.** A count cannot name the
        // one thing being destroyed, and one thing is the case where naming it
        // costs nothing at all.
        var chosen = Chosen(pane);

        if (chosen.Count == 0) return;

        _prompt = PromptMode.ConfirmDelete;

        PromptLabel.Text = ViewModels.Confirmations.Delete(chosen);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = "Delete permanently";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, not the bar: a focused Button takes Enter and Space
        // itself, which is a route nothing else can swallow.
        PromptConfirm.Focus();
    }

    /// <summary>
    /// Off by default, because trash is reversible and a prompt on a reversible
    /// action trains people to dismiss prompts. Dolphin offers it, so it is
    /// here for anyone who wants it.
    /// </summary>
    private void AskConfirmTrash()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        var chosen = Chosen(pane);

        if (chosen.Count == 0) return;

        _prompt = PromptMode.ConfirmTrash;

        PromptLabel.Text = ViewModels.Confirmations.MoveToBin(chosen);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = $"Move to {Naming.TheBin}";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, for the same reason the delete prompt does: a
        // focused Button takes Enter and Space itself.
        PromptConfirm.Focus();
    }

    /// <summary>
    /// Copying what is newer or missing here to the other side. **Always
    /// asked**, whatever the confirmation settings say about the bin: it can
    /// replace files, and a replaced file goes nowhere it could come back
    /// from.
    /// </summary>
    private void AskConfirmCopyAcross(CopyAcrossPlan plan)
    {
        if (PromptBar is null) return;

        _prompt = PromptMode.ConfirmCopyAcross;
        _copyAcross = plan;

        PromptLabel.Text = ViewModels.Confirmations.CopyAcross(plan);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = "Copy";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, for the reason the delete prompt does: a focused
        // Button takes Enter and Space itself.
        PromptConfirm.Focus();
    }

    private void ClosePrompt()
    {
        _prompt = PromptMode.None;

        // The row's editor, which has no field here to hide: it is drawn by the
        // listing's item template, so putting it away is done through the pane
        // it belongs to. The PANE it was opened on rather than the active one —
        // the other side can be made active while a name is being typed, and
        // clearing the wrong pane would leave a box on a row nothing can close.
        if (_renamePane is { } renaming)
        {
            renaming.RenamingPath = "";
            renaming.RenameRefusal = null;

            // A GUARD, and no mutation can redden it: every route back into a
            // rename assigns _renamePane before it reads it, and the reads that
            // remain are all behind the `_prompt is Rename` test that the line
            // above this block has just failed. It is here so the window does
            // not keep a closed tab's pane alive, which is not something a test
            // in this suite can see.
            _renamePane = null;
        }

        if (PromptBar is not null) PromptBar.IsVisible = false;
        if (PromptInput is not null) PromptInput.IsVisible = false;
        if (PromptConfirm is not null) PromptConfirm.IsVisible = false;
        if (PromptCancel is not null) PromptCancel.IsVisible = false;

        // The rename box and every confirmation leave through here, and the
        // control they were focusing has just been hidden.
        FocusListingSoon();
    }

    /// <summary>
    /// Keeps the reason under the row's box honest as the name is typed.
    ///
    /// The reason arrives while the name is being typed, not only when Enter is
    /// pressed: a colon is refused the moment it appears, which is when it can
    /// be fixed without thinking about it.
    ///
    /// Through the pane's property rather than the box's TextChanged, because
    /// the box lives in a DataTemplate and there is no control here to
    /// subscribe to — the same reason the tapping and key handlers are hung on
    /// the window.
    /// </summary>
    private void OnRenameTyped(object? sender, PropertyChangedEventArgs e)
    {
        if (_prompt is not PromptMode.Rename) return;
        if (e.PropertyName != nameof(PaneViewModel.RenameText)) return;
        if (sender is not PaneViewModel pane || !ReferenceEquals(pane, _renamePane)) return;

        // The reason alone, and null while there is none. The bar's hint line
        // read "enter to confirm · esc to cancel" the rest of the time; held
        // open over a listing that is what a popup saying nothing would be.
        pane.RenameRefusal = Input.RenamePrompt.Decide(pane.RenameText, _renameTarget.Name).Reason;
    }

    /// <summary>
    /// Puts the row's editor away when the keyboard leaves it.
    ///
    /// **An inline editor that outlives its focus is litter.** The bar this
    /// replaces was docked at the window's edge and obviously a prompt; a box
    /// left sitting on a row halfway down the listing after you have clicked
    /// somewhere else reads as part of the listing. Cancelling rather than
    /// committing, matching Escape: a name nobody confirmed must not be applied
    /// by a click aimed at something else.
    ///
    /// Immediate, and it does not have to work out whether the keyboard is on
    /// its way to the NEXT box in a Tab run. Measured by listening for the same
    /// event during a step: the old box loses the keyboard SYNCHRONOUSLY, from
    /// inside <see cref="ClosePrompt"/>, on the line that clears RenamingPath
    /// and hides it — and ClosePrompt sets <c>_prompt</c> to None before that
    /// line, so the first guard below has already returned. The box's own
    /// Editing flag has gone false by then too, so the second guard would stop
    /// it as well. There is exactly one such event per step, and it never
    /// reaches the body.
    /// </summary>
    private void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_prompt is not PromptMode.Rename) return;
        if (e.Source is not TextBox box || !Input.RenameBox.GetEditing(box)) return;

        // **A box's own context menu is not somewhere else.** A TextBox carries
        // a Cut/Copy/Paste flyout, and opening it takes the keyboard — measured
        // here, a right press inside the editor closed the rename and left the
        // menu standing over a row with no box under it, which is the gesture
        // most likely to be wanted: pasting a name in. FocusBehavior makes the
        // same exception, for the same gesture, on the address bar.
        if (box.ContextFlyout is { IsOpen: true }) return;

        ClosePrompt();
    }

    /// <summary>
    /// Whether the keyboard is in the box a rename opened on a row.
    ///
    /// Both the key routing and the recovery above hang on this rather than on
    /// "a text box has focus": the path box and the filter are text boxes too,
    /// and a rename that has lost the keyboard to one of them has lost it.
    /// </summary>
    private bool RenameHasTheKeyboard()
        => FocusManager?.GetFocusedElement() is TextBox box && Input.RenameBox.GetEditing(box);

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prompt != PromptMode.Rename) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ConfirmPrompt();
                break;

            case Key.Escape:
                e.Handled = true;
                ClosePrompt();
                break;

            // **Renaming a run cost three keystrokes each — Enter, arrow, F2.**
            // Explorer answers Tab, which is how anybody who has tidied a
            // folder of photographs does it, and the arrow was the worst of the
            // three: a rename can re-sort the folder, so the row under the one
            // just finished is not the file that was under it a moment ago.
            // Two labels rather than one and a ternary, for two reasons. The
            // shortcuts sheet's cross-check reads case labels and their when
            // clauses, so this is what makes both gestures findable there. And
            // it leaves every other modifier alone: Ctrl+Tab means "next tab"
            // whether or not a name is being typed, and folding it into "not
            // Shift, so forward" would quietly take that away.
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                _ = StepRenameAsync(1);
                break;

            case Key.Tab when e.KeyModifiers == KeyModifiers.Shift:
                e.Handled = true;
                _ = StepRenameAsync(-1);
                break;
        }
    }

    /// <summary>
    /// Commits the name being typed and opens the next row's.
    /// </summary>
    private async Task StepRenameAsync(int step)
    {
        if (_shell?.ActiveTab is not { } pane) return;

        // **The neighbour is chosen BEFORE the rename lands.** Renaming
        // re-lists the folder and can re-sort it, so asked afterwards "the next
        // one" means whichever file has closed the gap behind the row just
        // finished — which is not the row anybody was looking at.
        // The rows on SCREEN. A run that started on a row from inside a folder
        // opened in place stopped dead against Entries: RenameRun.Next answers
        // null for a path the list it was given does not hold, so Tab did
        // nothing and said nothing.
        var next = Input.RenameRun.Next(pane.Rows, _renameTarget.FullPath, step);

        ConfirmPrompt();

        // Still open means the name was refused before it ever left this
        // window — a colon, a reserved name, nothing but spaces. The bar keeps
        // the text and says why, and stepping on would throw both away.
        if (_prompt is not PromptMode.None) return;

        if (next is not { } row) return;

        // **And the local check is only half of "did that rename happen".**
        // It answers the SHAPE of a name and never asks the disk, so the
        // commonest refusal in a run — the name is already taken — is not among
        // the ones caught above. It arrives on a continuation, into a status
        // line the next step's refresh would clear. Stepping past one is how a
        // run skips a file in silence.
        if (_lastRename is { } pending && !await pending) return;

        pane.SelectedEntry = row;
        OnRenameRequested(this, row);
    }
}
