using System.Windows.Input;
using Avalonia.Input;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui.Input;

/// <summary>
/// Where in the window's key handling a command is answered. A property of
/// the command rather than of its key, so a key somebody chooses is answered
/// exactly where the one it replaced was.
/// </summary>
public enum KeyTier
{
    /// <summary>
    /// Everywhere, ahead of whatever has focus. These become the window's own
    /// KeyBindings, which Avalonia checks on the way from the focused control
    /// up to the window BEFORE the key is routed at all — so they reach the
    /// window through an open rename box and a focused address bar alike. A
    /// focused box keeps the keys it edits with; see
    /// <see cref="KeyChords.IsTextEditing"/>.
    /// </summary>
    Anywhere,

    /// <summary>
    /// After the rename box has had its say and before a focused box owns the
    /// keyboard.
    ///
    /// **These were window KeyBindings and fired straight through an open
    /// rename box.** Ctrl+H flipped the hidden files in behind it, Ctrl+I
    /// opened the filter and pulled the caret away mid-name, Ctrl+F swapped
    /// the listing for a search, Ctrl+D pinned the folder and Ctrl+Shift+N made
    /// a folder the one-tenant rule then left unnamed. They are answered in the
    /// window's key handler, behind the rename guard — and above the text-box
    /// guard, because two of them are wanted from inside a box: Ctrl+F from
    /// the address bar goes to the search field, and Ctrl+I from the filter
    /// box puts the filter away.
    /// </summary>
    Guarded,

    /// <summary>
    /// Only while no box has the keyboard. The clipboard, undo, rename, delete
    /// and select keys are the keys a text cursor owns too: as window
    /// KeyBindings, Ctrl+V pasted FILES into the folder behind the address
    /// bar, Ctrl+Z reversed the last copy on disk instead of the last
    /// keystroke, and Space opened the preview mid-way through typing "new
    /// folder".
    /// </summary>
    Listing,
}

/// <summary>
/// What a command needs from the window that runs it. Most commands are the
/// shell's or the active pane's; these few are the window's own, because they
/// go through the listing control or through the prompt bar.
/// </summary>
public interface ICommandHost
{
    ShellViewModel Shell { get; }

    /// <summary>Whether a text box has the keyboard, which decides whether a
    /// command answering anywhere gives an editing key back to it.</summary>
    bool TypingInABox { get; }

    void SelectAll();
    void SelectNone();
    void InvertSelection();

    /// <summary>Deletes the selection for good — through the confirmation the
    /// setting asks for, and as a purge when the listing is the bin.</summary>
    void DeletePermanently();

    /// <summary>The next of the listing, the address bar and the sidebar.</summary>
    void NextRegion();
}

/// <summary>
/// One thing the window can be told to do: what it is called, where it is
/// answered, which keys it answers to until somebody says otherwise, and how
/// to run it.
///
/// **The palette, the keys and the key editor all run the same one.** Before
/// this the palette carried its own table, and its Delete for good went
/// straight to the pane's delete — the command whose own summary says the
/// view must confirm first — so the palette deleted for good without the
/// question Shift+Delete asks. A second route to a command is a second place
/// for its safety net to be missing.
/// </summary>
public sealed record AppCommand(
    string Id,
    string Name,
    string Group,
    KeyTier Tier,
    IReadOnlyList<string> Defaults)
{
    /// <summary>The shell or pane command this runs, for most commands.</summary>
    public Func<ShellViewModel, ICommand?>? Command { get; init; }

    public object? Parameter { get; init; }

    /// <summary>The window's own action, for the commands that are not a
    /// view model's. Used instead of <see cref="Command"/> when set.</summary>
    public Action<ICommandHost>? Window { get; init; }

    /// <summary>
    /// Acts on the listing's selection, and so is refused while the keyboard
    /// is in the sidebar: the selection it would act on is not what the
    /// keyboard is pointing at, and Delete is the one that costs files.
    /// </summary>
    public bool OnSelection { get; init; }

    /// <summary>Listed in the command palette. False only for the palette
    /// itself and for moving the keyboard between regions, which the palette
    /// taking the keyboard makes meaningless.</summary>
    public bool InPalette { get; init; } = true;

    private IReadOnlyList<KeyGesture>? _defaultKeys;

    /// <summary>The defaults as gestures. Written as text above so the table
    /// reads the way the sheet prints it.</summary>
    public IReadOnlyList<KeyGesture> DefaultKeys
        => _defaultKeys ??= Defaults.Select(KeyChords.Parse).OfType<KeyGesture>().ToList();

    /// <summary>Whether running it now would do anything.</summary>
    public bool CanRun(ICommandHost host)
        => Window is not null || Command?.Invoke(host.Shell)?.CanExecute(Parameter) == true;

    /// <summary>Runs it; false when there was nothing it could do.</summary>
    public bool Run(ICommandHost host)
    {
        if (Window is { } act)
        {
            act(host);
            return true;
        }

        if (Command?.Invoke(host.Shell) is not { } command || !command.CanExecute(Parameter)) return false;

        command.Execute(Parameter);
        return true;
    }
}

/// <summary>
/// Every command the window answers, in the order the key editor lists them.
///
/// **Ids are what settings.json stores**, so they are names rather than
/// indices and never change: renaming one would drop the key somebody chose
/// for it. The group is the F1 sheet's heading, which is where a command
/// somebody gives a key to appears.
/// </summary>
public static class Commands
{
    private const string Around = "Getting around";
    private const string Tabs = "Tabs and panes";
    private const string Finding = "Finding things";
    private const string Files = "Working with files";
    private const string Looking = "Looking at things";
    private const string App = "The application";

    private static Func<ShellViewModel, ICommand?> Pane(Func<PaneViewModel, ICommand?> pick)
        => shell => shell.ActiveTab is { } pane ? pick(pane) : null;

    public static IReadOnlyList<AppCommand> All { get; } =
    [
        // ---- getting around --------------------------------------------------
        new("GoBack", "Back", Around, KeyTier.Anywhere, ["Alt+Left"]) { Command = Pane(p => p.GoBackCommand) },
        new("GoForward", "Forward", Around, KeyTier.Anywhere, ["Alt+Right"]) { Command = Pane(p => p.GoForwardCommand) },
        new("GoUp", "Up one folder", Around, KeyTier.Anywhere, ["Alt+Up"]) { Command = Pane(p => p.GoUpCommand) },
        new("GoHome", "Home folder", Around, KeyTier.Anywhere, ["Alt+Home"]) { Command = Pane(p => p.GoHomeCommand) },
        new("Refresh", "Refresh", Around, KeyTier.Anywhere, ["F5"]) { Command = Pane(p => p.RefreshCommand) },

        // Both habits: Ctrl+L is the browsers', Alt+D is Explorer's and the
        // browsers' other spelling.
        new("EditPath", "Type a path", Around, KeyTier.Anywhere, ["Ctrl+L", "Alt+D"]) { Command = Pane(p => p.BeginEditPathCommand) },

        // ---- tabs and panes --------------------------------------------------
        new("NewTab", "New tab", Tabs, KeyTier.Anywhere, ["Ctrl+T"]) { Command = s => s.NewTabCommand },
        new("NewWindow", "New window", Tabs, KeyTier.Anywhere, ["Ctrl+N"]) { Command = s => s.NewWindowCommand },
        new("CloseTab", "Close tab", Tabs, KeyTier.Anywhere, ["Ctrl+W"]) { Command = s => s.CloseTabCommand },
        new("ReopenClosedTab", "Reopen closed tab", Tabs, KeyTier.Anywhere, ["Ctrl+Shift+T"]) { Command = s => s.ReopenClosedTabCommand },
        new("DuplicateTab", "Duplicate tab", Tabs, KeyTier.Anywhere, []) { Command = s => s.DuplicateTabCommand },
        new("Quit", "Close the window", Tabs, KeyTier.Anywhere, ["Ctrl+Q"]) { Command = s => s.QuitCommand },

        // Ctrl+Page is the pair somebody who lives in a browser reaches for;
        // the listing's ScrollViewer claims plain Page and not a modified one.
        new("NextTab", "Next tab", Tabs, KeyTier.Anywhere, ["Ctrl+Tab", "Ctrl+PageDown"]) { Command = s => s.NextTabCommand },
        new("PreviousTab", "Previous tab", Tabs, KeyTier.Anywhere, ["Ctrl+Shift+Tab", "Ctrl+PageUp"]) { Command = s => s.PreviousTabCommand },

        // Guarded, and first of the guarded: leaving a box is most of what this
        // key is for, and the rename box is the one box it must not leave.
        new("NextRegion", "Move between the listing, address bar and sidebar", Tabs, KeyTier.Guarded, ["F6"])
        {
            Window = host => host.NextRegion(),
            InPalette = false,
        },
        new("ToggleSplit", "Split the window", Tabs, KeyTier.Anywhere, ["F3"]) { Command = s => s.ToggleSplitCommand },
        new("ToggleInfo", "Details panel", Tabs, KeyTier.Anywhere, ["F11"]) { Command = s => s.ToggleInfoCommand },
        new("CompareSides", "Compare the two sides", Tabs, KeyTier.Anywhere, []) { Command = s => s.ToggleCompareCommand },

        // F9 is Dolphin's own key for this panel.
        new("Sidebar", "Sidebar", Tabs, KeyTier.Anywhere, ["Ctrl+B", "F9"]) { Command = s => s.Sidebar.CycleRailCommand },

        // ---- finding things --------------------------------------------------
        // Explorer binds search to Ctrl+E and everything else to Ctrl+F: two
        // spellings of one habit, so one command.
        new("Search", "Search", Finding, KeyTier.Guarded, ["Ctrl+F", "Ctrl+E"]) { Command = Pane(p => p.BeginSearchCommand) },
        new("ToggleFilter", "Filter this listing", Finding, KeyTier.Guarded, ["Ctrl+I"]) { Command = Pane(p => p.ToggleFilterCommand) },

        // ---- working with files ----------------------------------------------
        new("Copy", "Copy", Files, KeyTier.Listing, ["Ctrl+C"]) { Command = Pane(p => p.CopySelectionToClipboardCommand), OnSelection = true },
        new("Cut", "Cut", Files, KeyTier.Listing, ["Ctrl+X"]) { Command = Pane(p => p.CutSelectionToClipboardCommand), OnSelection = true },
        new("Paste", "Paste", Files, KeyTier.Listing, ["Ctrl+V"]) { Command = Pane(p => p.PasteCommand) },
        new("Undo", "Undo", Files, KeyTier.Listing, ["Ctrl+Z"]) { Command = Pane(p => p.UndoCommand) },
        new("Redo", "Redo", Files, KeyTier.Listing, ["Ctrl+Y", "Ctrl+Shift+Z"]) { Command = Pane(p => p.RedoCommand) },
        new("Rename", "Rename", Files, KeyTier.Listing, ["F2"]) { Command = Pane(p => p.BeginRenameCommand), OnSelection = true },
        new("BatchRename", "Rename in bulk", Files, KeyTier.Listing, ["Shift+F2"]) { Command = s => s.BatchRenameCommand, OnSelection = true },

        // Through the shell's request rather than the pane's command, so the
        // "ask before moving files to the bin" setting is honoured wherever
        // the command comes from — the key, the menu or the palette.
        new("Trash", "Move to the bin", Files, KeyTier.Listing, ["Delete"]) { Command = s => s.TrashSelectionCommand, OnSelection = true },
        new("DeletePermanently", "Delete for good", Files, KeyTier.Listing, ["Shift+Delete"])
        {
            Window = host => host.DeletePermanently(),
            OnSelection = true,
        },
        new("SelectAll", "Select everything", Files, KeyTier.Listing, ["Ctrl+A"]) { Window = host => host.SelectAll(), OnSelection = true },
        new("SelectNone", "Select nothing", Files, KeyTier.Listing, []) { Window = host => host.SelectNone() },
        new("InvertSelection", "Invert the selection", Files, KeyTier.Listing, ["Ctrl+Shift+A"]) { Window = host => host.InvertSelection(), OnSelection = true },
        new("SelectDifferences", "Select what differs from the other side", Files, KeyTier.Listing, []) { Command = Pane(p => p.SelectDifferencesCommand) },
        new("CopyAcross", "Copy what is newer or missing here to the other side", Files, KeyTier.Listing, []) { Command = s => s.RequestCopyAcrossCommand },
        new("NewFolder", "New folder", Files, KeyTier.Guarded, ["Ctrl+Shift+N"]) { Command = Pane(p => p.NewFolderCommand) },
        new("NewFile", "New file", Files, KeyTier.Listing, []) { Command = Pane(p => p.NewFileCommand) },
        new("Duplicate", "Duplicate", Files, KeyTier.Listing, []) { Command = Pane(p => p.DuplicateSelectedCommand), OnSelection = true },
        new("CreateShortcut", "Create shortcut", Files, KeyTier.Listing, []) { Command = Pane(p => p.CreateShortcutCommand), OnSelection = true },
        new("Compress", "Compress to ZIP", Files, KeyTier.Listing, []) { Command = Pane(p => p.CompressSelectionCommand), OnSelection = true },
        new("Extract", "Extract all", Files, KeyTier.Listing, []) { Command = Pane(p => p.ExtractSelectionCommand), OnSelection = true },
        new("CopyLocation", "Copy as path", Files, KeyTier.Anywhere, ["Ctrl+Shift+C"]) { Command = s => s.CopyLocationCommand },

        // Through the shell, which refuses the bin and Recent: their rows name
        // where a file USED to be.
        new("Properties", "Properties", Files, KeyTier.Listing, ["Alt+Enter"]) { Command = s => s.ShowPropertiesCommand, OnSelection = true },
        // Not refused from the sidebar: it shows the selection and changes
        // nothing, and Space on a place row is the row's own key — the button
        // claims it before the window hears it. SidebarWalkTests pins this.
        new("Preview", "Quick preview", Files, KeyTier.Listing, ["Space"]) { Command = Pane(p => p.TogglePreviewCommand) },
        new("OpenTerminalHere", "Open a terminal here", Files, KeyTier.Anywhere, ["F4"]) { Command = Pane(p => p.OpenTerminalHereCommand) },
        new("PinCurrent", "Add this folder to places", Files, KeyTier.Guarded, ["Ctrl+D"]) { Command = s => s.PinCurrentCommand },
        new("EmptyTrash", "Empty the bin", Files, KeyTier.Listing, []) { Command = s => s.EmptyTrashCommand },

        // ---- looking at things -----------------------------------------------
        new("ToggleView", "Change the view", Looking, KeyTier.Anywhere, ["F8"]) { Command = Pane(p => p.ToggleViewCommand) },

        // The numbers follow the toolbar chip left to right, which is the
        // order on screen; Explorer numbers its views on Ctrl+Shift too.
        new("ShowAsDetails", "List", Looking, KeyTier.Anywhere, ["Ctrl+Shift+1"]) { Command = Pane(p => p.ShowAsDetailsCommand) },
        new("ShowAsCompact", "Small grid", Looking, KeyTier.Anywhere, ["Ctrl+Shift+2"]) { Command = Pane(p => p.ShowAsCompactCommand) },
        new("ShowAsGrid", "Large grid", Looking, KeyTier.Anywhere, ["Ctrl+Shift+3"]) { Command = Pane(p => p.ShowAsGridCommand) },
        new("ToggleHidden", "Show hidden files", Looking, KeyTier.Guarded, ["Ctrl+H"]) { Command = Pane(p => p.ToggleHiddenCommand) },
        new("SortByName", "Sort by name", Looking, KeyTier.Listing, []) { Command = Pane(p => p.SortByNameCommand) },
        new("SortBySize", "Sort by size", Looking, KeyTier.Listing, []) { Command = Pane(p => p.SortBySizeCommand) },
        new("SortByModified", "Sort by date modified", Looking, KeyTier.Listing, []) { Command = Pane(p => p.SortByModifiedCommand) },
        new("SortByCreated", "Sort by date created", Looking, KeyTier.Listing, []) { Command = Pane(p => p.SortByCreatedCommand) },
        new("SortByType", "Sort by type", Looking, KeyTier.Listing, []) { Command = Pane(p => p.SortByCommand), Parameter = "kind" },
        new("GroupByNone", "Group by nothing", Looking, KeyTier.Listing, []) { Command = Pane(p => p.GroupByNoneCommand) },
        new("GroupByName", "Group by name", Looking, KeyTier.Listing, []) { Command = Pane(p => p.GroupByNameCommand) },
        new("GroupBySize", "Group by size", Looking, KeyTier.Listing, []) { Command = Pane(p => p.GroupBySizeCommand) },
        new("GroupByModified", "Group by date modified", Looking, KeyTier.Listing, []) { Command = Pane(p => p.GroupByModifiedCommand) },
        new("GroupByType", "Group by type", Looking, KeyTier.Listing, []) { Command = Pane(p => p.GroupByKindCommand) },

        // Keyless, like the sorting and grouping rows above it: this is a way
        // of looking at the folder you are in, reached from the listing's menu
        // or by name from the palette.
        new("ShowSpaceUsage", "Show space usage", Looking, KeyTier.Listing, []) { Command = Pane(p => p.ShowSpaceUsageCommand) },

        // The pad's plus and minus answer these too — Avalonia folds them onto
        // the top row's when it matches — but not the pad's nought, which is
        // why reset carries both.
        new("ZoomIn", "Zoom in", Looking, KeyTier.Anywhere, ["Ctrl++"]) { Command = s => s.ZoomInCommand },
        new("ZoomOut", "Zoom out", Looking, KeyTier.Anywhere, ["Ctrl+-"]) { Command = s => s.ZoomOutCommand },
        new("ZoomReset", "Reset the zoom", Looking, KeyTier.Anywhere, ["Ctrl+0", "Ctrl+NumPad0"]) { Command = s => s.ZoomResetCommand },
        new("UseThisViewEverywhere", "Use this view for all folders", Looking, KeyTier.Listing, []) { Command = s => s.UseThisViewEverywhereCommand },
        new("ResetColumnWidths", "Reset column widths", Looking, KeyTier.Listing, []) { Command = s => s.ResetColumnWidthsCommand },

        // ---- the application -------------------------------------------------
        new("ShowShortcuts", "Keyboard shortcuts", App, KeyTier.Anywhere, ["F1"]) { Command = s => s.ShowShortcutsCommand },

        // The gesture VS Code and the Windows Files app use for the same box.
        new("ShowPalette", "Command palette", App, KeyTier.Anywhere, ["Ctrl+Shift+P"])
        {
            Command = s => s.ShowPaletteCommand,
            InPalette = false,
        },

        // KDE's standard "Configure" gesture, so it is where a Plasma user reaches.
        new("OpenSettings", "Settings", App, KeyTier.Anywhere, ["Ctrl+Shift+,"]) { Command = s => s.OpenSettingsCommand },
        new("ShowTour", "Take the tour", App, KeyTier.Anywhere, []) { Command = s => s.ShowTourCommand },
    ];

    private static readonly Dictionary<string, AppCommand> ById =
        All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    /// <summary>The command with this id, or null for an id no command has —
    /// one written by a newer Vaktari, or typed by hand.</summary>
    public static AppCommand? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>The command with this id, for code that names one it knows
    /// exists. Throws on a typo rather than hinting nothing.</summary>
    public static AppCommand Get(string id)
        => Find(id) ?? throw new ArgumentException($"no command is called {id}", nameof(id));
}
