using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// What is picked, and everything that has to survive the rows underneath it
/// being replaced.
///
/// **Three collections, one selection.** Each layout owns its own list because
/// each is a separate control, and the pane's job is to keep them saying the
/// same thing — which is why the gates below ask this file rather than a
/// control, and why a reload works in PATHS: the rows a listing comes back
/// with are new objects carrying a length and a timestamp, so the entry that
/// was selected is never equal to the one that replaces it.
///
/// **Gathered from four places rather than lifted from one.** Before this file
/// the machinery sat in separate regions of PaneViewModel — the collections
/// and their gates, SelectionPaths alone thirteen hundred lines below, and the
/// reload half again beyond that — so a change to how a selection survives a
/// refresh was made in one of them without the others in view.
///
/// Roadmap 22; nothing moved changed.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// One selection collection PER LAYOUT, and the reason is not cosmetic.
    ///
    /// Details, grid and compact are three separate ListBoxes that all stay
    /// alive when hidden. Pointing their SelectedItems at a single shared
    /// collection made each one write its own idea of the selection into it, so
    /// clicking one row produced a union of whatever the other two still held —
    /// three different files selected from one click. Deduplicating does not
    /// help, because the entries genuinely differ.
    ///
    /// Separate collections mean a hidden list can only ever disturb its own.
    /// </summary>
    public ObservableCollection<FileEntry> DetailsSelection { get; } = new();
    public ObservableCollection<FileEntry> GridSelection { get; } = new();
    public ObservableCollection<FileEntry> CompactSelection { get; } = new();

    /// <summary>
    /// Subscribes to all three, not just the active one — a hidden list can
    /// still be told to sync, and the active one changes as the layout does.
    /// Without this nothing recomputed the selection count, which is why the
    /// status bar reported only the item total.
    /// </summary>
    private void WatchSelections()
    {
        DetailsSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
        GridSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
        CompactSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(CanCreateShortcut));
        OnPropertyChanged(nameof(CanPurgeFromBin));
        OnPropertyChanged(nameof(CanRenameInBulk));
        OnPropertyChanged(nameof(HasDirectorySelected));
        OnPropertyChanged(nameof(HasAnyDirectorySelected));
        OnPropertyChanged(nameof(CanRunSelection));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
        OnPropertyChanged(nameof(CanCompressSelection));
        OnPropertyChanged(nameof(CanExtractSelection));

        // The heading's box is a function of the selection and nothing else, so
        // it belongs on the one notification every route to a selection change
        // already goes through.
        OnPropertyChanged(nameof(AllChosen));
    }

    /// <summary>The collection belonging to the layout currently on screen.</summary>
    public ObservableCollection<FileEntry> SelectedEntries => View switch
    {
        ViewMode.Grid => GridSelection,
        ViewMode.Compact => CompactSelection,
        _ => DetailsSelection,
    };

    /// <summary>What everything else should read. Never the raw collections.</summary>
    public IReadOnlyList<FileEntry> Selection => SelectedEntries.ToList();

    /// <summary>
    /// True when the right-click landed on something. One menu serves both a
    /// row and the empty space below it, so the entries that act on a selection
    /// hide there rather than sit enabled and do nothing — which is what they
    /// did: every one of them guards on the selection and returns quietly.
    ///
    /// Gating on the selection is safe because a right-click on a row selects
    /// that row first, so the entries are still there when you want them.
    /// </summary>
    public bool HasSelection => SelectedEntry is not null || SelectedEntries.Count > 0;

    /// <summary>Cut, Rename and Move to bin: needs a selection, and the bin
    /// listing is a view rather than a folder.</summary>
    public bool CanActOnSelection => HasSelection && !IsTrashListing;

    /// <summary>
    /// Whether to offer "Create shortcut".
    ///
    /// **Three conditions, and each hides a row that could only disappoint.**
    /// A platform with no idea of a shortcut leaves <see cref="Shortcuts"/>
    /// null — the same gate the right-drag menu already applies before it
    /// offers "Create shortcuts here". <see cref="IsRealFolder"/> because the
    /// shortcut is written INTO the listing being looked at, and in the bin,
    /// Recent, This PC and a search that destination is the literal string
    /// "vaktari:trash" and its kin. And something has to be selected, because
    /// the shortcut has to point at something.
    ///
    /// **This is narrower than <see cref="HasShellMenu"/>, which excludes only
    /// the bin and Recent** — so on Windows a search listing and This PC still
    /// offer the shell's own "Create shortcut", which writes beside the item
    /// and needs no folder on screen. That is the reason the verb was left in
    /// the hosted menu rather than filtered out of it as a native twin: doing
    /// so would have left those two listings with no "Create shortcut" in
    /// either menu.
    /// </summary>
    public bool CanCreateShortcut => Shortcuts is not null && IsRealFolder && HasSelection;

    /// <summary>
    /// **"Rename in bulk…" was offered for a single file.** It is the entry
    /// whose name says how many it is for, sitting directly under plain
    /// Rename, which is the one that handles that case -- so the menu asked a
    /// question that has an obvious answer and put the wrong route first for
    /// anyone who read it as "the thorough one". F2 already sends more than one
    /// row here on its own.
    /// </summary>
    public bool CanRenameInBulk => CanActOnSelection && SelectedEntries.Count > 1;

    /// <summary>
    /// Whether a FOLDER is selected, which is the only case where adding "the
    /// selection" to places differs from adding the current folder.
    ///
    /// **Two entries did the same thing.** Splitting "Add to places" into a
    /// selection one and a current-folder one read well until you noticed that
    /// the selection command falls back to the current folder for anything that
    /// is not a directory — so with nothing selected, or a file selected, both
    /// rows pinned the same path under two different labels, and the one naming
    /// a selection was not acting on it.
    /// </summary>
    public bool HasDirectorySelected => SelectedEntry is { IsDirectory: true };

    /// <summary>
    /// Whether a folder is anywhere in the selection, not just under the focus.
    ///
    /// **The row hid whenever the FOCUSED row was not one.** Click a file, then
    /// ctrl-click two folders, and "Open in new tab" — which now opens both —
    /// was not there to be clicked, because the focus was still on the file.
    /// HasDirectorySelected asks about one row and is right for the entries
    /// that act on one; this asks the question the verb actually answers.
    /// </summary>
    public bool HasAnyDirectorySelected
        => HasDirectorySelected || SelectedEntries.Any(e => e.IsDirectory);

    /// <summary>
    /// Whether the rows draw a tick box, and whether the column heading draws
    /// the one that ticks them all.
    ///
    /// **The listing had no pointer-only route to a multi-selection.** Every
    /// one of them went through a modifier — ctrl+click, shift+click — or
    /// through a rubber band, which is a drag.
    ///
    /// Read from the live settings rather than stored on the pane, so a save
    /// only has to raise the notification; see <see cref="RefreshSelectionBoxes"/>.
    /// Off by default, which is Explorer's answer rather than Dolphin's, and
    /// off means the boxes cost nothing at all: the markup collapses the slot
    /// rather than drawing it empty.
    /// </summary>
    public bool ShowSelectionBoxes => Settings.AppSettings.Current.Views.ShowSelectionBoxes;

    /// <summary>
    /// What the heading's box shows: all of the rows ticked, some of them, or
    /// none. Null is "some", which is the dash a three-state box draws.
    ///
    /// **An empty listing is NONE, not all.** "All of nothing" is true by
    /// arithmetic and reads, in a folder with no files in it, as a listing that
    /// has selected itself.
    /// </summary>
    public bool? AllChosen
    {
        get
        {
            if (SelectedEntries.Count == 0) return false;

            // **The FOLDER's rows, still, with a folder opened in place.**
            // Every select-everything gesture takes exactly these — see
            // MainWindow.SelectWholeFolder for why — so this is the count the
            // box's own click produces, and a selection larger than it can only
            // have been built by ctrl+clicking across the boundary on purpose.
            return SelectedEntries.Count >= Entries.Count ? true : null;
        }
    }

    /// <summary>
    /// Re-asks both selection-box questions after settings have been saved.
    ///
    /// Neither is stored, so neither raises anything on its own — a pane
    /// already on screen would keep the boxes it had until it was rebuilt.
    /// That is the trap the font setting fell into for weeks: saved, recorded,
    /// and invisible until the next launch.
    /// </summary>
    public void RefreshSelectionBoxes()
    {
        OnPropertyChanged(nameof(ShowSelectionBoxes));
        OnPropertyChanged(nameof(AllChosen));
    }

    /// <summary>
    /// What the desktop is set to, when it says so. Null means it did not, and
    /// this application's own default (double) applies. Written by
    /// <c>ThemeApplier.Apply</c> from the theme palette, which is re-read on
    /// startup, on a Plasma change, and on save.
    ///
    /// It lives here rather than on the window because the window is not the
    /// only thing that has to know: the click handlers ask it to decide what a
    /// tap means, and the three listings ask it to decide what the POINTER
    /// looks like over a row.
    /// </summary>
    public static bool? SystemSingleClick { get; set; }

    /// <summary>
    /// Single click when the preference says so, or when it defers to a desktop
    /// that says so. The one place the rule is written; MainWindow's own
    /// <c>OpensOnSingleClick</c> reads this.
    /// </summary>
    internal static bool SingleClickOpens
        => Settings.AppSettings.Current.Navigation.OpenItemsWith switch
        {
            Vaktari.Core.Settings.ActivationClick.Single => true,
            Vaktari.Core.Settings.ActivationClick.Double => false,
            _ => SystemSingleClick ?? false,
        };

    /// <summary>
    /// Whether one click opens, asked by the LISTING rather than by a click
    /// handler.
    ///
    /// **A single-click listing looked exactly like a double-click one.** The
    /// preference changed what a tap did and nothing about what a row looked
    /// like under the pointer: no hand, no underline, nothing anywhere in the
    /// window saying that the next click was going to open something. Measured
    /// before this went in, the only <c>Cursor="Hand"</c> in the whole window
    /// was the sidebar's section fold, and the decision was a private static
    /// read by two click handlers — there was nothing a style could bind to.
    ///
    /// Read from the live settings rather than stored on the pane, so a save
    /// only has to raise the notification; see <see cref="RefreshActivation"/>.
    /// The same shape as <see cref="ShowSelectionBoxes"/>, for the same reason.
    ///
    /// **Not in the bin, where a click opens nothing.** The rule the click
    /// handlers ask has no term for WHERE the pane is, and the behaviour does:
    /// <see cref="OpenAsync"/> refuses on a binned row before it reaches the
    /// launcher or the navigation, because the path on that row is the one the
    /// item USED to occupy. Measured with single click set and a pane at
    /// <c>vaktari:trash</c>, every row wore the hand and underlined its name
    /// under the pointer — advertising an open that the guard one layer down
    /// then declined. Same gate the drag payload carries for the same reason,
    /// see <c>CanDragOut</c>.
    /// </summary>
    public bool OpensOnSingleClick => SingleClickOpens && !IsTrashListing;

    /// <summary>
    /// Re-asks the activation question after a save, or after the desktop has
    /// changed its own mind.
    ///
    /// Nothing is stored, so nothing raises on its own — a pane already on
    /// screen would keep the pointer it had until it was rebuilt, which is the
    /// trap <see cref="RefreshSelectionBoxes"/> was written for.
    /// </summary>
    public void RefreshActivation() => OnPropertyChanged(nameof(OpensOnSingleClick));

    /// <summary>
    /// Carries the selection to the layout being switched to, so changing view
    /// does not silently drop what you had chosen.
    /// </summary>
    private void CarrySelection(ViewMode from, ViewMode to)
    {
        if (from == to) return;

        var source = from switch
        {
            ViewMode.Grid => GridSelection,
            ViewMode.Compact => CompactSelection,
            _ => DetailsSelection,
        };

        var target = to switch
        {
            ViewMode.Grid => GridSelection,
            ViewMode.Compact => CompactSelection,
            _ => DetailsSelection,
        };

        var carried = source.ToList();

        target.Clear();
        foreach (var entry in carried) target.Add(entry);

        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Applications offered by the "open with" submenu.</summary>
    public ObservableCollection<LaunchOption> OpenWithOptions { get; } = new();

    /// <summary>
    /// Whether "Open with" has anything to offer.
    ///
    /// **Gated on a count, like every neighbour.** It was gated on HasSelection
    /// alone, so every folder on both platforms — and on Linux any file whose
    /// MIME will not resolve — drew a row with a chevron and an empty popup
    /// behind it. HasScripts, HasTemplates and HasSeveralTerminals all guard on
    /// a count for exactly this reason.
    /// </summary>
    public bool HasOpenWithOptions => OpenWithOptions.Count > 0;
    /// <summary>Paths of the selection, falling back to the focused row.</summary>
    public IReadOnlyList<string> SelectionPaths()
        => Selection.Count > 0
            ? Selection.Select(e => e.FullPath).ToList()
            : SelectedEntry is { } one ? [one.FullPath] : [];
    /// <summary>What is selected, by path, so it can survive the rows being
    /// replaced by equal-but-different ones.</summary>
    /// <summary>
    /// **A focused row with nothing in the selection counted as nothing.** In a
    /// real list the two cannot come apart — SelectedItem and SelectedItems sit
    /// behind one selection model, so the binding that sets SelectedEntry fills
    /// DetailsSelection with it — but this view model is written to be driven
    /// without one, and the places that set the focused row on its own
    /// (ApplyBatch after a delete, a restored session, a test) would hand a
    /// rebuild nothing to keep. Belt and braces: the collection is still the
    /// answer whenever it has one.
    /// </summary>
    private List<string> SelectedPaths()
    {
        var paths = SelectedEntries.Select(e => e.FullPath).OfType<string>().ToList();

        if (paths.Count == 0 && SelectedEntry?.FullPath is { } focused) paths.Add(focused);

        return paths;
    }

    /// <summary>
    /// Puts the selection back after the rows have been rebuilt.
    ///
    /// **ReplaceAll clears the collection the view binds its selection to**, so
    /// every rebuild dropped it: F5 lost your place in a long folder, and
    /// sorting or filtering with files selected quietly deselected them.
    /// Explorer and Dolphin both keep the selection across all three.
    ///
    /// By path rather than by value: FileEntry is a record struct carrying a
    /// timestamp and a length, so the row for a file that changed while the
    /// listing was open is not equal to the one that was selected.
    /// </summary>
    private void Reselect(List<string> paths, IReadOnlyList<string>? arrived = null)
    {
        // Ordinal, and it stays ordinal: both ends of this set are spellings
        // the SAME provider produced, so they cannot disagree about case.
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);

        // What an operation just put here wins outright, and only when at least
        // one of it is on screen — see SelectOnlyAfterLoad for both halves. The
        // test is against the rows rather than against a parent path because a
        // details listing splices an expanded folder's rows in underneath it,
        // and because a match is exactly what the loop below is about to look
        // for anyway.
        //
        // The outer test buys time and nothing else, and that is measured:
        // widened to `arrived is not null` the whole of
        // PastedItemsAreSelectedTests stays green, because an empty set matches
        // no row and the probe inside already answers "none of it is here". It
        // is here because that probe WALKS every visible row when the answer is
        // no, and this method runs on every rebuild — a sort of a hundred
        // thousand rows would pay for a question with one possible answer.
        if (arrived is { Count: > 0 })
        {
            // **PathRules.Comparer, unlike the set above, because these two
            // spellings came from different places and did disagree.** The
            // engine reports the path it wrote to, built from the SOURCE's
            // name; the rows carry what the directory actually holds. Measured
            // on WindowsFileOperations: copying src\report.txt over an existing
            // dst\Report.TXT with Overwrite left the folder listing Report.TXT
            // — a copy writes through the existing entry and does not rename it
            // — while the handle reported dst\report.txt. Ordinal called those
            // two files two, so that arrival was silently not selected, and
            // where it was the only one the whole paste came back with nothing
            // picked out. This is the rule the rest of the application compares
            // paths with.
            var landed = new HashSet<string>(arrived, PathRules.Comparer);

            if (VisibleRows.Any(e => e.FullPath is { } path && landed.Contains(path)))
                wanted = landed;
        }

        if (wanted.Count == 0) return;

        var selection = SelectedEntries;

        selection.Clear();

        // The rows on SCREEN rather than the folder's own, or a selected row
        // inside a folder opened in place would be dropped by every rebuild —
        // and a rebuild is what a sort, a filter and a refresh all are. The
        // same object as Entries whenever nothing is expanded.
        foreach (var entry in VisibleRows)
            if (entry.FullPath is { } path && wanted.Contains(path))
                selection.Add(entry);

        // The focused row too, or the keyboard would carry on from wherever it
        // happened to be rather than from what is selected.
        if (selection.Count > 0) SelectedEntry = selection[0];
    }

    /// <summary>
    /// Puts back a selection that a GESTURE had to collapse, rather than one a
    /// rebuild dropped.
    ///
    /// **A drag begun on one of three selected rows ended with one row
    /// selected.** The press underneath the drag reduces the selection to the
    /// row it landed on before the drag starts — the window snapshots the
    /// three on the tunnelled press, so the payload does carry all three — but
    /// nothing wrote them back afterwards. Measured in a bin listing, where
    /// the drag is refused outright: three rows selected, a press, and the
    /// gesture ended with one, the other two deselected and no way to tell
    /// which.
    ///
    /// **Nothing happens when none of the paths is still on screen**, and that
    /// is a floor under the call rather than a case a drag was seen in.
    /// <see cref="Reselect"/> clears the selection before it re-adds, so a
    /// snapshot naming only rows the listing no longer has would empty what
    /// the listing holds now instead of restoring anything — measured by
    /// handing this two absent paths over a pane with one row selected. The
    /// drag route was measured NOT to reach it: three files dropped on a
    /// folder row in the same pane hand the move to a background operation and
    /// return, and this call was reached with all four rows still listed and
    /// all three wanted paths among them, so the snapshot went straight back.
    /// </summary>
    public void ReselectPaths(IReadOnlyList<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);

        if (!VisibleRows.Any(e => e.FullPath is { } path && wanted.Contains(path))) return;

        Reselect([.. paths]);
    }

    /// <summary>
    /// Paths for the next load to select, put there by an operation that knows
    /// what it has just made.
    ///
    /// **A rename came back with nothing selected, including the file you had
    /// renamed.** The refresh that follows one rebuilds the listing from the
    /// file system, and the path that was selected went with the old name — so
    /// carrying the selection over, which is all a reload can do on its own,
    /// restores a row that is not there any more. The new name is known only to
    /// the rename.
    ///
    /// A path rather than a row, because the row does not exist yet: it arrives
    /// with the listing, out of the provider, carrying a length and a timestamp
    /// this side has never seen. The same reason Reselect works in paths, and
    /// the same reason the deletion counterpart _selectAfterRemoval does.
    /// </summary>
    private List<string> _selectAfterLoad = [];

    /// <summary>
    /// Asks the next load to select this path if the listing has it.
    ///
    /// Read and cleared by that load whether or not it found anything — a
    /// request older than that belongs to a listing that has gone.
    ///
    /// Public because the caller that knows the new name is the prompt, not
    /// this file: the rename engine is handed a name and never sees the
    /// gesture, and only the gesture knows whether the keyboard is staying on
    /// the file or moving to the next one.
    /// </summary>
    public void SelectAfterLoad(string path) => _selectAfterLoad.Add(path);

    /// <inheritdoc cref="SelectOnlyAfterLoad"/>
    private List<string> _selectInsteadAfterLoad = [];

    /// <summary>
    /// Asks the next load to select these paths INSTEAD of what is selected
    /// now, if the listing has any of them.
    ///
    /// **After a paste the arrivals were nowhere to be found.** A copy of
    /// twenty files into a folder finished, the listing came back, and the
    /// twenty were somewhere in a thousand rows sorted by name with nothing
    /// pointing at them — so the one thing everybody does next, look at what
    /// they just moved, meant finding them by hand. Explorer and Dolphin both
    /// leave the arrival selected.
    ///
    /// **Instead of, rather than as well as** — which is the difference from
    /// <see cref="SelectAfterLoad"/>, and the reason this is a second door.
    /// Pasting into a folder where a row happened to be selected would
    /// otherwise leave that row selected alongside the twenty, and the next
    /// Delete would take a bystander with them. A rename is the opposite case
    /// and keeps its adding behaviour: the other rows of a selection are still
    /// the same files, and a Tab stepping through a run relies on it.
    ///
    /// **Only if any of them is actually here.** A drop onto a folder ROW lands
    /// the files inside that folder, not in this listing, and a move takes them
    /// out of it altogether — so a request that matches nothing on screen must
    /// leave the selection alone rather than clearing it. Decided against the
    /// rows in <see cref="Reselect"/>, where they exist, rather than by
    /// comparing parent paths here: a details listing splices an expanded
    /// folder's rows in underneath it, so "in this folder" and "on screen" are
    /// not the same question.
    ///
    /// Private, unlike <see cref="SelectAfterLoad"/> next door, which is public
    /// because the caller that knows a rename's new name is MainWindow's
    /// prompt. Nothing outside this class knows an arrival: the only registrar
    /// is Track, which is where every operation this pane owns finishes.
    /// </summary>
    private void SelectOnlyAfterLoad(IReadOnlyList<string> paths)
        => _selectInsteadAfterLoad.AddRange(paths);
}
