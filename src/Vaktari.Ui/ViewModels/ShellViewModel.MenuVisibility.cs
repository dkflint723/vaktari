using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Which rows the context menu shows, straight off the preferences.
///
/// **Hiding a row is not removing a command.** Every one of these still exists
/// and keeps its key and its place in the command palette; the menu simply
/// stops listing it, which is how Dolphin's Services page works and the reason
/// these are bound with IsVisible rather than branching anywhere.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- context menu visibility ------------------------------------------
    //
    // Straight off the preferences, like the status bar above. Bound with
    // IsVisible on the MenuItems, which is how Dolphin's Services page works:
    // the commands all still exist and keep their shortcuts, the menu just
    // stops listing them.

    private static Core.Settings.ContextMenuSettings Menu => Settings.AppSettings.Current.ContextMenu;

    // The four that act on a selection carry the preference AND the selection.
    // One menu serves a row and the empty space below it, so without the second
    // half they were listed on an empty-space click and did nothing when picked.
    //
    // ShowAddToPlaces and ShowCopyLocation are deliberately NOT gated: their
    // commands retarget the current folder when nothing is selected, which is a
    // real answer rather than a silent no-op. See AddSelectionToPlaces below.
    public bool ShowCopyToInMenu => Menu.ShowCopyTo && ActiveTab?.CanActOnSelection == true;
    public bool ShowMoveToInMenu => Menu.ShowMoveTo && ActiveTab?.CanActOnSelection == true;
    public bool ShowSortByInMenu => Menu.ShowSortBy;

    public bool ShowDuplicateInMenu => Menu.ShowDuplicate && ActiveTab?.CanActOnSelection == true;
    /// <summary>
    /// Only for a FOLDER. OpenInNewTab opens a directory and quietly does
    /// nothing for anything else, so offering it on a text file was a row that
    /// could only disappoint — and in the bin it named a path the folder no
    /// longer occupies.
    /// </summary>
    public bool ShowOpenInNewTabInMenu =>
        Menu.ShowOpenInNewTab
        && ActiveTab is { HasAnyDirectorySelected: true, IsTrashListing: false };

    /// <summary>
    /// The same question of the same rows, asked of its OWN preference: the
    /// context-menu page carries one flag per entry, and one checkbox that
    /// silently removed two entries would be a page that no longer does what it
    /// says.
    /// </summary>
    public bool ShowOpenInNewWindowInMenu =>
        Menu.ShowOpenInNewWindow
        && ActiveTab is { HasAnyDirectorySelected: true, IsTrashListing: false };
    /// <summary>
    /// The files waiting to be moved by a paste, which every row binds to so a
    /// cut one can be greyed the way Explorer greys it.
    ///
    /// Mirrored from <see cref="CutMarks"/> rather than owned here: cut is
    /// raised by a pane, which has no reference to the shell, and the marks
    /// apply to every listing rather than to the one that was cut from.
    /// </summary>
    [ObservableProperty] private IReadOnlySet<string> _cutPaths = CutMarks.Paths;

    public bool ShowAddToPlacesInMenu => Menu.ShowAddToPlaces;

    /// <summary>
    /// The selection row appears only when a FOLDER is selected.
    ///
    /// **Two entries were doing one thing.** Splitting "Add to places" into a
    /// selection row and a current-folder row read well until you noticed that
    /// the selection command falls back to the current folder for anything that
    /// is not a directory — so with nothing selected, or a file selected, both
    /// rows pinned the same path under two different labels, and the one naming
    /// a selection was not acting on it.
    ///
    /// **And never in the bin**, where a deleted folder's path is the one it
    /// was deleted from — the same reason the two rows above carry the same
    /// gate, one heading up, and with the same consequence: a place naming a
    /// path the folder no longer occupies. The current-folder row is already
    /// out of the bin, because <c>IsRealFolder</c> is false there, so without
    /// this the only "Add to places" the bin offered was the one that could
    /// only get it wrong.
    /// </summary>
    public bool ShowAddSelectionToPlaces
        => Menu.ShowAddToPlaces
           && ActiveTab is { HasDirectorySelected: true, IsTrashListing: false };

    /// <summary>
    /// The current-folder row shows only when the selection row does not —
    /// they are one "Add to places" slot in the menu, and which command fills
    /// it depends on whether a folder is selected. Both visible at once was
    /// the old layout's homework: two adjacent rows whose difference the
    /// reader had to work out.
    /// </summary>
    public bool ShowAddCurrentToPlaces
        => Menu.ShowAddToPlaces
           && ActiveTab?.HasDirectorySelected != true
           && ActiveTab?.IsRealFolder == true;

    /// <summary>
    /// The same slot, in a search listing: the row reads "save this search"
    /// there rather than "add this folder", because a search is not a folder
    /// and a row that called it one would be the reader's homework again.
    /// Same command behind both — PinCurrent knows which it is standing in.
    /// </summary>
    public bool ShowSaveSearchToPlaces
        => Menu.ShowAddToPlaces
           && ActiveTab?.HasDirectorySelected != true
           && ActiveTab?.IsSearchListing == true;
    public bool ShowCopyLocationInMenu => Menu.ShowCopyLocation;

    /// <summary>
    /// Every selected path, the way Explorer's verb of the same name gives
    /// them: one per line, quoted on Windows.
    ///
    /// **It copied one.** Selecting five files and choosing "Copy as path" put
    /// a single path on the clipboard and said nothing about the other four —
    /// and the Windows verb that would have done it properly is filtered out of
    /// the hosted menu as a duplicate of this one.
    ///
    /// Quoted only on Windows, because that is where the shell's own version
    /// quotes and where a path with a space in it needs them to survive being
    /// pasted into a command line. A quoted path pasted back into the address
    /// bar is understood.
    ///
    /// Reuses CopyTextRequested, which already exists for share URLs and mount
    /// paths — the view owns the clipboard, so the shell asks rather than
    /// reaches.
    /// </summary>
    [RelayCommand]
    private void CopyLocation()
    {
        if (ActiveTab is not { } pane) return;

        var paths = pane.Selection.Count > 0
            ? pane.Selection.Select(e => e.FullPath).OfType<string>().ToList()
            : pane.SelectedEntry?.FullPath is { Length: > 0 } one ? [one]
            : new List<string> { pane.CurrentPath };

        paths = paths.Where(p => p.Length > 0).ToList();

        if (paths.Count == 0) return;

        var text = string.Join(
            Environment.NewLine,
            OperatingSystem.IsWindows() ? paths.Select(Quoted) : paths);

        CopyTextRequested?.Invoke(this, text);
    }

    private static string Quoted(string path) => QUOTE_CHAR + path + QUOTE_CHAR;

    private const string QUOTE_CHAR = "\"";

    /// <summary>
    /// Pins the selected folder rather than the current one, which is what a
    /// context menu on a row should mean. Falls back to the current folder when
    /// the click was on empty space.
    ///
    /// Through the same helper the gesture uses, so **the menu is not the one
    /// route that stays silent**: this was the other caller of the bare
    /// <c>Sidebar.PinAsync</c>, and it reported nothing for the same reason.
    ///
    /// **Not the selection in the bin, where a row's path is where the folder
    /// USED to be.** MEASURED: <c>RecentListing.GatherTrash</c> builds each row
    /// as <c>new FileEntry(name, item.OriginalPath, …)</c> and copies the
    /// deleted item's directory flag onto it, so a deleted folder there is a
    /// selected directory carrying a real-looking path that nothing occupies —
    /// and this pinned it, a bookmark that could never be opened. The bin is
    /// the only listing that hands out such a path: Recent's <c>Build</c>
    /// returns null where the path is neither a directory nor a file, and a
    /// search listing's folders are really there and are worth pinning, which
    /// is why the guard names the bin rather than asking whether the folder
    /// exists.
    /// </summary>
    [RelayCommand]
    private async Task AddSelectionToPlacesAsync()
    {
        var path = ActiveTab is { IsTrashListing: false, SelectedEntry: { IsDirectory: true } entry }
            ? entry.FullPath
            : ActiveTab?.CurrentPath;

        await PinOneAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Keeps the sidebar's highlight on the place the active pane is showing.
    /// The shell is the only thing that knows which pane that is, which is the
    /// same reason it owns the navigation callback.
    ///
    /// Whether hidden folders show goes first, and for the same reason: the
    /// folder tree follows the ACTIVE pane's answer, and a reveal into a dot
    /// folder made under the other answer stops one level short.
    /// </summary>
    public void SyncSidebarLocation()
    {
        Sidebar.FollowHidden(ActiveTab?.ShowHidden ?? false);
        Sidebar.SetCurrentPath(ActiveTab?.CurrentPath);
    }

    /// <summary>
    /// Re-writes every pane's metrics, for a change that came from outside the
    /// settings dialog.
    ///
    /// **The desktop's own text size is such a change**: it arrives on a theme
    /// palette rather than through Save, so none of the routes that already
    /// re-apply the metrics run. Narrower than
    /// <see cref="OnSettingsChanged"/> on purpose — nothing about a desktop
    /// scheme changes the sort order, the status bar or what the decorations
    /// say, and re-raising those would be this method claiming things it does
    /// not know.
    /// </summary>
    public void RefreshPaneScales()
    {
        foreach (var group in new[] { Left, Right })
            if (group is not null)
                foreach (var tab in group.Tabs)
                    tab.RefreshScale();

        // The flyout's size boxes read a pane's points and pixels THROUGH this
        // shell, so telling the panes is not telling the numbers beside them —
        // and this is exactly the change that moves the size a pane draws at
        // without touching the pane's own zoom.
        NotifyTargetSizes();
    }
}
