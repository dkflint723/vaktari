using System.Windows.Input;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One command the palette can run: its name, the key that also runs it
/// (empty when none does), and how to reach it on the shell of the window
/// the palette was opened from.
/// </summary>
public sealed record PaletteEntry(
    string Name,
    string Keys,
    Func<ShellViewModel, ICommand?> Command,
    object? Parameter = null);

/// <summary>
/// Every command the window answers, by name — for the box Ctrl+Shift+P
/// opens.
///
/// **A hundred and twenty-five menu rows and seventy-five keys, and no way
/// to run one by its name.** A command that lives three levels down a
/// context menu, or on a key nobody remembers, is a command nobody uses;
/// the palette is the same list as a flat, typed-at box, and the sheet of
/// keys beside each name is how the key gets learnt.
///
/// **Written out, like the sheet, and held to it by a test.** Every key
/// here is spelled exactly as the F1 sheet spells it, so a key that is
/// renamed or removed reddens the palette as well as the sheet. The
/// commands are reached through the shell rather than captured, because
/// the palette is built once and the window it runs in is not.
///
/// What is NOT here: anything that needs a row under the pointer or a
/// parameter the palette cannot supply — Open with, Copy to, the per-place
/// verbs. Those are what the right-click menu is for.
/// </summary>
public static class Palette
{
    private static Func<ShellViewModel, ICommand?> Pane(Func<PaneViewModel, ICommand?> pick)
        => shell => shell.ActiveTab is { } pane ? pick(pane) : null;

    public static IReadOnlyList<PaletteEntry> Entries { get; } =
    [
        // ---- tabs and panes ------------------------------------------------
        new("New tab", "Ctrl+T", s => s.NewTabCommand),
        new("New window", "Ctrl+N", s => s.NewWindowCommand),
        new("Close tab", "Ctrl+W", s => s.CloseTabCommand),
        new("Reopen closed tab", "Ctrl+Shift+T", s => s.ReopenClosedTabCommand),
        new("Duplicate tab", "", s => s.DuplicateTabCommand),
        new("Next tab", "Ctrl+Tab", s => s.NextTabCommand),
        new("Previous tab", "Ctrl+Shift+Tab", s => s.PreviousTabCommand),
        new("Split the window", "F3", s => s.ToggleSplitCommand),
        new("Details panel", "F11", s => s.ToggleInfoCommand),
        new("Sidebar", "Ctrl+B", s => s.Sidebar.CycleRailCommand),

        // ---- getting around ------------------------------------------------
        new("Back", "Alt+←", Pane(p => p.GoBackCommand)),
        new("Forward", "Alt+→", Pane(p => p.GoForwardCommand)),
        new("Up one folder", "Alt+↑", Pane(p => p.GoUpCommand)),
        new("Home folder", "Alt+Home", Pane(p => p.GoHomeCommand)),
        new("Refresh", "F5", Pane(p => p.RefreshCommand)),
        new("Type a path", "Ctrl+L", Pane(p => p.BeginEditPathCommand)),

        // ---- finding things ------------------------------------------------
        new("Search", "Ctrl+F", Pane(p => p.BeginSearchCommand)),
        new("Filter this listing", "Ctrl+I", Pane(p => p.ToggleFilterCommand)),

        // ---- looking at things ---------------------------------------------
        new("List", "Ctrl+Shift+1", Pane(p => p.ShowAsDetailsCommand)),
        new("Small grid", "Ctrl+Shift+2", Pane(p => p.ShowAsCompactCommand)),
        new("Large grid", "Ctrl+Shift+3", Pane(p => p.ShowAsGridCommand)),
        new("Change the view", "F8", Pane(p => p.ToggleViewCommand)),
        new("Show hidden files", "Ctrl+H", Pane(p => p.ToggleHiddenCommand)),
        new("Sort by name", "", Pane(p => p.SortByNameCommand)),
        new("Sort by size", "", Pane(p => p.SortBySizeCommand)),
        new("Sort by date modified", "", Pane(p => p.SortByModifiedCommand)),
        new("Sort by date created", "", Pane(p => p.SortByCreatedCommand)),
        new("Sort by type", "", Pane(p => p.SortByCommand), "kind"),
        new("Group by nothing", "", Pane(p => p.GroupByNoneCommand)),
        new("Group by name", "", Pane(p => p.GroupByNameCommand)),
        new("Group by size", "", Pane(p => p.GroupBySizeCommand)),
        new("Group by date modified", "", Pane(p => p.GroupByModifiedCommand)),
        new("Group by type", "", Pane(p => p.GroupByKindCommand)),
        new("Zoom in", "Ctrl++", s => s.ZoomInCommand),
        new("Zoom out", "Ctrl+-", s => s.ZoomOutCommand),
        new("Reset the zoom", "Ctrl+0", s => s.ZoomResetCommand),
        new("Use this view for all folders", "", s => s.UseThisViewEverywhereCommand),
        new("Reset column widths", "", s => s.ResetColumnWidthsCommand),

        // ---- working with files --------------------------------------------
        new("New folder", "Ctrl+Shift+N", Pane(p => p.NewFolderCommand)),
        new("New file", "", Pane(p => p.NewFileCommand)),
        new("Rename", "F2", Pane(p => p.BeginRenameCommand)),
        new("Rename in bulk", "Shift+F2", s => s.BatchRenameCommand),
        new("Copy", "Ctrl+C", Pane(p => p.CopySelectionToClipboardCommand)),
        new("Cut", "Ctrl+X", Pane(p => p.CutSelectionToClipboardCommand)),
        new("Paste", "Ctrl+V", Pane(p => p.PasteCommand)),
        new("Undo", "Ctrl+Z", Pane(p => p.UndoCommand)),
        new("Redo", "Ctrl+Y", Pane(p => p.RedoCommand)),
        new("Move to the bin", "Delete", Pane(p => p.TrashSelectedCommand)),
        new("Delete for good", "Shift+Delete", Pane(p => p.DeleteSelectedCommand)),
        new("Duplicate", "", Pane(p => p.DuplicateSelectedCommand)),
        new("Create shortcut", "", Pane(p => p.CreateShortcutCommand)),
        new("Compress to ZIP", "", Pane(p => p.CompressSelectionCommand)),
        new("Extract all", "", Pane(p => p.ExtractSelectionCommand)),
        new("Copy as path", "Ctrl+Shift+C", s => s.CopyLocationCommand),
        new("Properties", "Alt+Enter", s => s.ShowPropertiesCommand),
        new("Open a terminal here", "F4", Pane(p => p.OpenTerminalHereCommand)),
        new("Add this folder to places", "Ctrl+D", s => s.PinCurrentCommand),
        new("Empty the bin", "", s => s.EmptyTrashCommand),

        // ---- the application -----------------------------------------------
        new("Settings", "Ctrl+Shift+,", s => s.OpenSettingsCommand),
        new("Keyboard shortcuts", "F1", s => s.ShowShortcutsCommand),
        new("Take the tour", "", s => s.ShowTourCommand),
        new("Quit", "Ctrl+Q", s => s.QuitCommand),
    ];

    /// <summary>
    /// The entries a typed query leaves, in the order the box shows them.
    ///
    /// Every word of the query has to appear somewhere in the name, in any
    /// order — "tab close" finds Close tab — and a name that BEGINS with the
    /// query comes first: "up" is Up one folder before it is Duplicate tab,
    /// because somebody typing a command's name types it from the front.
    /// Within a tier the table's own order holds, which is grouped, so the
    /// empty query reads as the menus do rather than as an index.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Match(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var whole = query.Trim();

        return Entries
            .Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Name.StartsWith(whole, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}
