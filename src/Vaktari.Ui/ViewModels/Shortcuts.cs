namespace Vaktari.Ui.ViewModels;

/// <summary>One key and what it does.</summary>
public sealed record Shortcut(string Keys, string Does);

/// <summary>A heading and the keys under it.</summary>
public sealed record ShortcutGroup(string Name, IReadOnlyList<Shortcut> Keys);

/// <summary>
/// Every key Vaktari answers, for the window F1 opens.
///
/// **An application whose pitch is the keyboard had nowhere to look them up.**
/// A shortcut appears beside a context-menu entry when it happens to have one,
/// so a handful were discoverable and the rest — the filter, the view, redo,
/// the two spellings of the path bar — were not findable at all.
///
/// **Written out rather than generated, and cross-checked by a test.** Most of
/// these are KeyBindings in the markup, but several are handled in code-behind
/// where a key means different things depending on what has focus, and no
/// generator could describe those honestly. ShortcutListTests asserts that
/// every gesture in MainWindow.axaml appears here, so the list cannot quietly
/// fall behind the application.
/// </summary>
public static class Shortcuts
{
    public static IReadOnlyList<ShortcutGroup> All { get; } =
    [
        new("Getting around",
        [
            new("Alt+←", "Back"),
            new("Alt+→", "Forward"),
            new("Alt+↑", "Up one folder"),
            new("Alt+Home", "Home folder"),
            new("Backspace", "Back"),
            new("F5", "Refresh"),
            new("Ctrl+L", "Type a path"),
            new("Alt+D", "Type a path"),
            new("Enter", "Open what is selected"),
            new("Page Up / Page Down", "A screenful at a time"),
            new("Mouse back / forward", "Back and forward, in the pane under the pointer"),
        ]),

        new("Tabs and panes",
        [
            new("Ctrl+T", "New tab"),
            new("Ctrl+W", "Close tab, or the window when it is the last one"),
            new("Ctrl+Shift+T", "Reopen the last closed tab"),
            new("Ctrl+Q", "Close the window"),
            new("Ctrl+1…9", "Jump to a tab"),
            new("Tab", "Move to the other pane, when split"),
            new("Ctrl+Tab", "Next tab"),
            new("Ctrl+Page Down", "Next tab"),
            new("Ctrl+Page Up", "Previous tab"),
            new("Ctrl+Shift+Tab", "Previous tab"),
            new("Middle click a tab", "Close it"),
            new("Double-click the tab strip", "New tab"),
            new("Middle click a folder", "Open it in a new tab"),
            // The sheet promised the middle button on folders and the sidebar's
            // own menu offered "Open in new tab" on every place — while the
            // gesture reached neither a place nor a crumb.
            new("Middle click a place or crumb", "Open it in a new tab"),
            new("Ctrl+click a place or crumb", "Open it in a new tab"),
            // **Explorer opens search on F3; Vaktari splits the window on it,
            // matching Dolphin.** Somebody arriving from Explorer presses it,
            // gets a second pane, and opens this sheet to find out what
            // happened — landing on this line, which answers only the question
            // they did not ask. The key they wanted is two headings down under
            // "Finding things", off the bottom of a 620px sheet. The Dolphin
            // meaning stays; the answer moves to the line being read at the
            // moment the question is asked.
            new("F3", "Split the window — search is Ctrl+F"),
            new("F11", "Details panel"),
            new("Ctrl+B", "Sidebar"),
            new("F9", "Sidebar"),
        ]),

        new("Finding things",
        [
            new("Ctrl+F", "Search"),
            new("Ctrl+E", "Search"),
            new("Ctrl+I", "Filter this listing"),
            // In the search box, where Enter had no effect at all: it
            // dead-ended, and a result could only be reached with the mouse.
            // Down is gone with the popup it moved into — the results are the
            // listing now, and every key that works in a listing works on them.
            new("Enter", "Go to the search results"),
            // And the same idea one box along: both keys somebody presses to
            // leave the filter did nothing, so the way out was Tab, F6 or the
            // mouse. Listed as its own row because it is a different box and a
            // different destination — the rows you have just narrowed.
            new("Enter / ↓", "From the filter, go to the rows"),
            new("Escape", "Clear the filter, and any pending cut"),
            new("Type any letters", "Jump to the first matching name"),
        ]),

        new("Working with files",
        [
            new("Ctrl+C", "Copy"),
            new("Ctrl+X", "Cut"),
            new("Ctrl+V", "Paste"),
            new("Ctrl+Z", "Undo"),
            new("Ctrl+Y", "Redo"),
            new("Ctrl+Shift+Z", "Redo"),
            new("F2", "Rename"),
            new("Shift+F2", "Rename in bulk"),
            new("Delete", "Move to the bin"),
            new("Shift+Delete", "Delete for good"),
            new("Ctrl+A", "Select everything"),
            new("Ctrl+Shift+A", "Invert the selection"),
            new("Ctrl+Shift+N", "New folder"),
            new("Ctrl+Shift+C", "Copy as path"),
            new("Alt+Enter", "Properties"),
            new("Space", "Quick preview"),
            new("F4", "Open a terminal here"),
        ]),

        new("Dragging",
        [
            new("Drag", "Move within a drive, copy between drives"),
            new("Ctrl+drag", "Copy — onto the same folder, duplicate"),
            new("Shift+drag", "Move"),
        ]),

        new("Looking at things",
        [
            new("F8", "Change the view"),
            // F8 cycles; these go straight to one. The numbers follow the
            // toolbar chip left to right, which is the order on screen.
            new("Ctrl+Shift+1", "List"),
            new("Ctrl+Shift+2", "Small grid"),
            new("Ctrl+Shift+3", "Large grid"),
            new("Ctrl+H", "Show hidden files"),
            new("Ctrl+D", "Pin this folder to places"),
            new("Ctrl + scroll", "Resize the pane under the pointer"),
            new("Ctrl+Shift + scroll", "Resize its icons only"),
            new("Ctrl + middle click", "Reset that pane's size"),
            new("Ctrl++ / Ctrl+-", "Zoom in and out"),
            new("Ctrl+0", "Reset the zoom"),
        ]),

        new("The application",
        [
            new("F1", "This list"),
            new("Ctrl+Shift+,", "Settings"),
        ]),
    ];
}
