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
///
/// **One line is not a constant, and this used to be described as though the
/// whole sheet were.** Backspace is a preference — Back by default, up one
/// folder when the Navigation page says so — so the sheet is built per read
/// rather than once.
/// </summary>
public static class Shortcuts
{
    /// <summary>
    /// The sheet as it should read right now.
    ///
    /// **A property with an initializer would have printed one Backspace line
    /// for everybody**, and Backspace is the one key here whose meaning is a
    /// preference: it goes Back by default and up one folder when
    /// <c>NavigationSettings.BackspaceGoesUp</c> is set. A sheet that
    /// named the other behaviour would be teaching the wrong key at exactly
    /// the moment somebody opened it to find out what a key does — which is
    /// the failure this whole list exists to prevent.
    ///
    /// Rebuilt on each read rather than cached and invalidated: the reads are
    /// one per F1 press and a handful in the tests, and a cache with a
    /// subscription to <c>AppSettings.Changed</c> would be more machinery than
    /// the thing it speeds up.
    /// </summary>
    public static IReadOnlyList<ShortcutGroup> All
        => For(Settings.AppSettings.Current.Navigation.BackspaceGoesUp);

    /// <summary>
    /// The sheet under a stated setting, so a test can ask for both without
    /// touching the process-wide settings.
    /// </summary>
    public static IReadOnlyList<ShortcutGroup> For(bool backspaceGoesUp) =>
    [
        new("Getting around",
        [
            new("Alt+←", "Back"),
            new("Alt+→", "Forward"),
            new("Alt+↑", "Up one folder"),
            new("Alt+Home", "Home folder"),
            // Both halves named on one line, whichever way it is set: the
            // reader is here because they pressed a key and want to know what
            // it did, and the second clause is the only thing on the sheet
            // that tells them the answer is theirs to change.
            new("Backspace", backspaceGoesUp
                ? "Up one folder — Settings, Navigation makes it Back"
                : "Back — Settings, Navigation makes it up one folder"),
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
            new("Ctrl+N", "New window, on this folder"),
            new("Ctrl+W", "Close tab, or the window when it is the last one"),
            new("Ctrl+Shift+T", "Reopen the last closed tab"),
            new("Ctrl+Q", "Close the window"),
            new("Ctrl+1…9", "Jump to a tab"),
            new("Tab", "Move to the other pane, when split"),
            // Listed at last. It has been bound since there was any keyboard
            // route into the listing at all, and appeared nowhere — a key
            // nobody can find is a key nobody uses.
            new("F6", "Listing, address bar, sidebar — in turn"),
            // The other half of F6: it delivered you to a panel with no way to
            // move in it, so the key that got you there was the only one that
            // worked once you had arrived.
            // All four, not just the one. Home and End walk to the first and
            // last place and were printed nowhere -- and the drift test could
            // not have said so, because it read only the markup and every key
            // in the sidebar is answered in the code-behind.
            new("↑ / ↓ / Home / End", "Move in the sidebar, once F6 has taken you there"),
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
            // In the rename bar. Renaming a run of files cost three keystrokes
            // each — Enter, arrow, F2 — and the arrow was the worst of them: a
            // rename can re-sort the folder, so the row under the one just
            // finished is not the file that was under it a moment ago.
            new("Tab", "Keep the name and rename the next file"),
            new("Shift+Tab", "The one before"),
            new("Shift+F2", "Rename in bulk"),
            new("Delete", "Move to the bin"),
            new("Shift+Delete", "Delete for good"),
            new("Ctrl+A", "Select everything"),
            new("Ctrl+Shift+A", "Invert the selection"),
            new("Ctrl+Shift+N", "New folder"),
            new("Ctrl+Shift+C", "Copy as path"),
            new("Alt+Enter", "Properties"),
            // **The keyboard route to the right-click menu, printed nowhere.**
            // Both spellings have worked since the menu learnt to answer empty
            // space, and the sheet checked itself against the markup only --
            // where neither of these lives, because a menu that must open at
            // the focused row needs the row, not a command.
            new("Menu / Shift+F10", "The right-click menu, where the keyboard is"),
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
            // The only route to expandable folders that is not a triangle the
            // width of a row icon. In the list view alone, which is where the
            // triangles are — the other two layouts keep these keys for moving
            // sideways.
            //
            // **This line has no killing mutation in the direction that would
            // catch its absence**, and it was measured: deleting it left the
            // whole Vaktari.Ui.Tests project green, because
            // ShortcutListTests.Every_bound_key_is_in_the_list draws its cases
            // from `KeyBinding Gesture="..."` in the markup, and these two keys
            // are answered by case labels in the code-behind switch instead.
            // The other direction does hold: spell either arrow wrong here, or
            // take the case labels out of the switch, and
            // Every_listed_key_is_actually_bound goes red.
            new("→ / ←", "In the list, open a folder where it is and close it again"),
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
