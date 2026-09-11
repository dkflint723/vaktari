using Vaktari.Ui.Input;

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
/// **Two kinds of line.** A command's line prints whatever keys the keymap
/// gives that command, so a key somebody moves is printed where they moved it
/// and a command they cleared drops off the sheet; the words beside it are
/// written here, because the sheet says what a key does and the command's
/// own name only says what it is called. The rest are the keys no keymap
/// can move — Enter, Tab, the arrows, Backspace, the pointer gestures — and
/// are written out as they are.
///
/// **And a command somebody gives a key to appears here even if no line was
/// written for it**, under its own group, by its own name: "every key Vaktari
/// answers" is the promise, and Sort by size on Ctrl+Alt+S is one of them.
///
/// ShortcutListTests holds the fixed lines to the window's key handler in both
/// directions, so a fixed key cannot quietly stop being printed or start
/// being printed when nothing answers it.
/// </summary>
public static class Shortcuts
{
    /// <summary>
    /// The sheet as it should read right now: the keymap in force, and the
    /// Backspace preference in force.
    ///
    /// Rebuilt on each read rather than cached and invalidated: the reads are
    /// one per F1 press and a handful in the tests, and a cache with a
    /// subscription to <c>AppSettings.Changed</c> would be more machinery than
    /// the thing it speeds up.
    /// </summary>
    public static IReadOnlyList<ShortcutGroup> All
        => For(Settings.AppSettings.Current.Navigation.BackspaceGoesUp, Keymap.Current);

    /// <summary>The sheet under a stated Backspace setting and the shipped keys,
    /// so a test can ask for both without touching the process-wide settings.</summary>
    public static IReadOnlyList<ShortcutGroup> For(bool backspaceGoesUp) => For(backspaceGoesUp, Keymap.Default);

    public static IReadOnlyList<ShortcutGroup> For(bool backspaceGoesUp, Keymap keys)
    {
        var mentioned = new HashSet<string>(StringComparer.Ordinal);

        // A command's line, or nothing when the command has no key.
        Shortcut? Bound(string id, string does)
        {
            mentioned.Add(id);

            return keys.Readable(id) is { Length: > 0 } printed ? new Shortcut(printed, does) : null;
        }

        // **A line that answers another program's habit.** Somebody who
        // pressed a key expecting what Explorer or Dolphin does with it, got
        // something else, and opened this sheet to find out what happened
        // lands on the line for the key they pressed — so that line names the
        // key that does what they wanted. Said only while the command still
        // holds the key the habit reaches for: moved elsewhere, there is no
        // collision left to explain.
        Shortcut? Habit(string id, string does, string habitKey, string wanted, string saying)
        {
            if (Bound(id, does) is not { } line) return null;

            var holdsIt = keys.KeysOf(id).Any(k => KeyChords.Readable(k) == habitKey);
            var answer = keys.KeysOf(wanted).FirstOrDefault();

            return holdsIt && answer is not null
                ? line with { Does = $"{does} — {saying} {KeyChords.Readable(answer)}" }
                : line;
        }

        var groups = new List<(string Name, List<Shortcut?> Lines)>
        {
            ("Getting around",
            [
                Bound("GoBack", "Back"),
                Bound("GoForward", "Forward"),
                Bound("GoUp", "Up one folder"),
                Bound("GoHome", "Home folder"),
                // Both halves named on one line, whichever way it is set: the
                // reader is here because they pressed a key and want to know
                // what it did, and the second clause is the only thing on the
                // sheet that tells them the answer is theirs to change.
                new("Backspace", backspaceGoesUp
                    ? "Up one folder — Settings, Navigation makes it Back"
                    : "Back — Settings, Navigation makes it up one folder"),
                Bound("Refresh", "Refresh"),
                Bound("EditPath", "Type a path"),
                new("Enter", "Open what is selected"),
                new("Page Up / Page Down", "A screenful at a time"),
                new("Mouse back / forward", "Back and forward, in the pane under the pointer"),
            ]),

            ("Tabs and panes",
            [
                Bound("NewTab", "New tab"),
                Bound("NewWindow", "New window, on this folder"),
                Bound("CloseTab", "Close tab, or the window when it is the last one"),
                Bound("ReopenClosedTab", "Reopen the last closed tab"),
                Bound("Quit", "Close the window"),
                new("Ctrl+1…9", "Jump to a tab"),
                new("Tab", "Move to the other pane, when split"),
                // Listed at last. It has been bound since there was any keyboard
                // route into the listing at all, and appeared nowhere — a key
                // nobody can find is a key nobody uses.
                Bound("NextRegion", "Listing, address bar, sidebar — in turn"),
                // All four, not just the one. Home and End walk to the first and
                // last place, and are answered in the window's key handler
                // rather than by any command.
                new("↑ / ↓ / Home / End", "Move in the sidebar, once the keyboard is in it"),
                Bound("NextTab", "Next tab"),
                Bound("PreviousTab", "Previous tab"),
                new("Middle click a tab", "Close it"),
                new("Double-click the tab strip", "New tab"),
                new("Middle click a folder", "Open it in a new tab"),
                // The sheet promised the middle button on folders and the
                // sidebar's own menu offered "Open in new tab" on every place —
                // while the gesture reached neither a place nor a crumb.
                new("Middle click a place or crumb", "Open it in a new tab"),
                new("Ctrl+click a place or crumb", "Open it in a new tab"),
                // **Explorer opens search on F3; Vaktari splits the window on
                // it, matching Dolphin.** Somebody arriving from Explorer presses
                // it, gets a second pane, and opens this sheet to find out what
                // happened.
                Habit("ToggleSplit", "Split the window", "F3", "Search", "search is"),
                Bound("ToggleInfo", "Details panel"),
                // **Dolphin adds a place on Ctrl+B; Vaktari folds the sidebar
                // on it.** It is also Firefox's bookmarks sidebar, and what this
                // window has always answered; the answer moves to the line
                // being read at the moment the question is asked. F9 is
                // Dolphin's OWN key for this panel, and asks nothing.
                Habit("Sidebar", "Sidebar", "Ctrl+B", "PinCurrent", "adding a place is"),
            ]),

            ("Finding things",
            [
                Bound("Search", "Search"),
                Bound("ToggleFilter", "Filter this listing"),
                // In the search box, where Enter had no effect at all: it
                // dead-ended, and a result could only be reached with the mouse.
                new("Enter", "Go to the search results"),
                // Both keys somebody presses to leave the filter did nothing, so
                // the way out was Tab, F6 or the mouse.
                new("Enter / ↓", "From the filter, go to the rows"),
                new("Escape", "Clear the filter, and any pending cut"),
                new("Type any letters", "Jump to the first matching name"),
            ]),

            ("Working with files",
            [
                Bound("Copy", "Copy"),
                Bound("Cut", "Cut"),
                Bound("Paste", "Paste"),
                Bound("Undo", "Undo"),
                Bound("Redo", "Redo"),
                Bound("Rename", "Rename"),
                // In the rename box. Renaming a run of files cost three
                // keystrokes each — Enter, arrow, F2 — and the arrow was the
                // worst of them: a rename can re-sort the folder.
                new("Tab", "Keep the name and rename the next file"),
                new("Shift+Tab", "The one before"),
                Bound("BatchRename", "Rename in bulk"),
                Bound("Trash", "Move to the bin"),
                Bound("DeletePermanently", "Delete for good"),
                Bound("SelectAll", "Select everything"),
                Bound("InvertSelection", "Invert the selection"),
                Bound("NewFolder", "New folder"),
                Bound("CopyLocation", "Copy as path"),
                Bound("Properties", "Properties"),
                // **The keyboard route to the right-click menu.** A menu that
                // must open at the focused row needs the row, not a command.
                new("Menu / Shift+F10", "The right-click menu, where the keyboard is"),
                Bound("Preview", "Quick preview"),
                Bound("OpenTerminalHere", "Open a terminal here"),
            ]),

            ("Dragging",
            [
                new("Drag", "Move within a drive, copy between drives"),
                new("Ctrl+drag", "Copy — onto the same folder, duplicate"),
                new("Shift+drag", "Move"),
                // Both spellings, because Alt+drag was the one that did not
                // work — printed on one line the way "Menu / Shift+F10" is: two
                // habits for one verb, not two verbs.
                new("Alt+drag / Ctrl+Shift+drag", "Create a shortcut"),
            ]),

            ("Looking at things",
            [
                Bound("ToggleView", "Change the view"),
                Bound("ShowAsDetails", "List"),
                Bound("ShowAsCompact", "Small grid"),
                Bound("ShowAsGrid", "Large grid"),
                Bound("ToggleHidden", "Show hidden files"),
                // The only route to expandable folders that is not a triangle
                // the width of a row icon, in the list view alone — the other
                // two layouts keep these keys for moving sideways.
                new("→ / ←", "In the list, open a folder where it is and close it again"),
                // **Explorer deletes on Ctrl+D; Vaktari pins on it.** Somebody
                // arriving from Explorer presses it over a file expecting the
                // bin, gets a place instead, and comes here to find out why.
                Habit("PinCurrent", "Pin this folder to places", "Ctrl+D", "Trash", "the bin is"),
                new("Ctrl + scroll", "Resize the pane under the pointer"),
                new("Ctrl+Shift + scroll", "Resize its icons only"),
                new("Ctrl + middle click", "Reset that pane's size"),
                Bound("ZoomIn", "Zoom in"),
                Bound("ZoomOut", "Zoom out"),
                Bound("ZoomReset", "Reset the zoom"),
            ]),

            ("The application",
            [
                Bound("ShowShortcuts", "This list"),
                Bound("ShowPalette", "Any command, by name"),
                Bound("OpenSettings", "Settings"),
            ]),
        };

        // Every command with a key that no line above mentions, under its own
        // heading and by its own name — see the class summary.
        foreach (var command in Commands.All)
        {
            if (mentioned.Contains(command.Id)) continue;
            if (keys.Readable(command.Id) is not { Length: > 0 } printed) continue;

            var home = groups.FindIndex(g => g.Name == command.Group);

            if (home < 0)
            {
                groups.Add((command.Group, []));
                home = groups.Count - 1;
            }

            groups[home].Lines.Add(new Shortcut(printed, command.Name));
        }

        return groups
            .Select(g => new ShortcutGroup(g.Name, g.Lines.OfType<Shortcut>().ToList()))
            .Where(g => g.Keys.Count > 0)
            .ToList();
    }
}
