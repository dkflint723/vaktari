using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vaktari.Ui;

/// <summary>
/// Select all, select none, invert — the commands, and the boxes that run them.
///
/// **Reachable from two places on purpose.** Each of these is called both by a
/// menu row and by the keymap's command host, which is what a command IS; a
/// handler with one caller would be a handler, not a command. So the several
/// entry points here are dispatch rather than coupling.
///
/// SelectAllFrom comes with them from the far end of the file. It answers what
/// a three-state heading box should DO when it is clicked — which is not the
/// same question as what it should show — and its only caller is the box's own
/// handler, about seventeen hundred lines from where it had been sitting.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// What clicking the heading's box means, given what is currently chosen.
    ///
    /// **A three-state CheckBox cycles unchecked, checked, indeterminate on
    /// click**, so the box's own value after a click is not an answer anybody
    /// meant to give — clicking a full listing would ask for "some". The
    /// question is asked of the PANE instead: everything ticked means clear,
    /// and anything else means tick the lot. That also makes a half-ticked
    /// listing go to all rather than to none, which is what both references do.
    /// </summary>
    internal static bool SelectAllFrom(bool? chosen) => chosen != true;

    /// <summary>
    /// Selects everything that is not selected, and deselects everything that
    /// is — Explorer's Ctrl+Shift+A, and the fastest way to say "all of these
    /// except those".
    ///
    /// Through the ListBox for the same reason SelectAll is: filling the bound
    /// collection row by row fires CollectionChanged once per file, and each
    /// one refreshes the details panel and recomputes the summary.
    /// </summary>
    private void InvertSelection()
    {
        if (ActiveListing() is not { } list) return;
        if (list.SelectedItems is not { } selected) return;

        var wanted = list.Items
            .OfType<object>()
            .Where(item => !selected.Contains(item))
            .ToList();

        selected.Clear();

        foreach (var item in wanted) selected.Add(item);
    }

    /// <summary>Clears the selection without touching what is focused.</summary>
    private void SelectNone() => ActiveListing()?.SelectedItems?.Clear();

    /// <summary>
    /// Ticks the FOLDER's rows, not the screen's.
    ///
    /// **A folder opened in place put rows from two folders in one listing**,
    /// and select-everything is the gesture that takes them both without being
    /// asked. Everything downstream is written for a selection that lives in
    /// ONE folder — read the three call sites: BatchRenameViewModel is handed
    /// pane.Entries as "the whole folder, so the preview can see the files that
    /// are already there", the rename run steps within one list, and the drop
    /// refusals ask whether a target is inside the selection. A copy of a
    /// folder together with a file inside it asks for that file at the
    /// destination twice — once inside the copied folder and once beside it.
    ///
    /// Ctrl+clicking across the boundary is still allowed — somebody who does
    /// that meant it — but the one keystroke that does it without being asked
    /// no longer does.
    ///
    /// The whole listing IS the folder until somebody presses a triangle, so
    /// the ordinary case still goes through the framework's bulk path: filling
    /// the bound collection row by row fires a change per file, and each one
    /// refreshes the details panel and recomputes the summary.
    /// </summary>
    internal static void SelectWholeFolder(ListBox list, ViewModels.PaneViewModel pane)
    {
        if (!pane.RowsAreSpliced) { list.SelectAll(); return; }

        list.SelectedItems?.Clear();

        foreach (var entry in pane.Entries) list.SelectedItems?.Add(entry);
    }

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        if (ActiveListing() is { } list && _shell.ActiveTab is { } pane)
            SelectWholeFolder(list, pane);
    }

    /// <summary>
    /// The heading's box. Asks the pane what is chosen rather than the box what
    /// it has just become — see <see cref="SelectAllFrom"/> for why those are
    /// different questions.
    ///
    /// Through the ListBox for the same reason the Select ▸ All menu row is:
    /// filling the bound collection row by row fires a change per file, and
    /// each one refreshes the details panel and recomputes the summary.
    ///
    /// **A three-state box keeps whatever the click cycled it to unless it is
    /// told otherwise.** IsChecked is bound OneWay, so the click's own value
    /// sits on the control until the pane raises AllChosen again — and a click
    /// that changes nothing raises nothing. In an empty folder SelectAll had
    /// nothing to select, so the box was left showing a tick over a listing
    /// with no rows in it: the one thing AllChosen refuses to say anywhere
    /// else. Asking the pane again puts the answer back.
    /// </summary>
    private void OnSelectAllBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (_shell.ActiveTab is not { } pane) return;

        if (!SelectAllFrom(pane.AllChosen)) SelectNone();
        else if (ActiveListing() is { } list) SelectWholeFolder(list, pane);

        pane.RefreshSelectionBoxes();
    }

    private void OnSelectNoneClicked(object? sender, RoutedEventArgs e) => SelectNone();

    private void OnInvertSelectionClicked(object? sender, RoutedEventArgs e)
        => InvertSelection();
}
