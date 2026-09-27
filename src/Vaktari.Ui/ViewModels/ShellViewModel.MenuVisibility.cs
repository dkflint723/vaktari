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

    // The rows that act on a selection carry the preference AND what the rows
    // are. They live on the ITEM menu only, which opens on a selection, so the
    // second half is no longer "is anything selected" but "can these rows be
    // sent anywhere" — not the bin's, and not This PC's volumes. See
    // PaneViewModel.CanMoveSelection.
    public bool ShowCopyToInMenu => Menu.ShowCopyTo && ActiveTab?.CanMoveSelection == true;
    public bool ShowMoveToInMenu => Menu.ShowMoveTo && ActiveTab?.CanMoveSelection == true;

    /// <summary>
    /// The half of View that used to be Arrange: Sort by, Group by and Columns.
    ///
    /// **The preference's reach changed when View and Arrange merged**, and its
    /// meaning did not: it still hides how the listing is ordered, and leaves
    /// alone how it is drawn. The layouts and Show hidden files are the other
    /// half of View and were never something the settings page offered to
    /// hide — the left half of a split has no other pointer route to them.
    /// </summary>
    public bool ShowSortByInMenu => Menu.ShowSortBy;

    /// <summary>
    /// Group by and Columns, which only Details can draw — see the comment on
    /// Group by in MainWindow.axaml — and which the preference above hides
    /// along with Sort by.
    ///
    /// One property for the pair because they are one rule, and a MultiBinding
    /// on each would be two places for it to drift.
    /// </summary>
    public bool ShowDetailsArrangeInMenu => Menu.ShowSortBy && ActiveTab?.IsDetailsView == true;

    /// <summary>
    /// Duplicate, where it would duplicate.
    ///
    /// **A search, Recent, This PC and the scan listings drew it and the
    /// command refused**: DuplicateSelected writes the copy into the folder on
    /// screen, and RefusedVirtualDestination turns away every listing that is
    /// not one, with "this listing is a view, not a folder" (This PC, whose
    /// rows are drives, with the drive sentence — see RefusedOnVolumes). The
    /// row asked CanActOnSelection, which excludes only the bin.
    /// </summary>
    public bool ShowDuplicateInMenu
        => Menu.ShowDuplicate && ActiveTab is { HasSelection: true, IsRealFolder: true };

    /// <summary>
    /// The background menu's rarely wanted folder tools — space usage,
    /// duplicate files, and comparing the two sides — which is somewhere to be
    /// only where one of them applies: a real folder for the first two, a split
    /// for the rest.
    /// </summary>
    public bool ShowAnalyseInMenu => ActiveTab?.IsRealFolder == true || IsSplit;

    /// <summary>
    /// "Select what differs" and "Copy what is newer or missing", which act
    /// on a comparison — so only where one can exist: a split whose two sides
    /// are both folders.
    ///
    /// **Both were offered wherever the window was split**, and in a search,
    /// the bin, Recent or This PC on either side the copy refused ("cannot
    /// copy across: one side is a view, not a folder") and the select found
    /// no marks to select. That is the structural half of WhyNotComparable;
    /// the other half — a side still loading, or one that could not be read —
    /// passes, and the command says so, because a row that came and went with
    /// a listing's progress would move under the pointer. "Compare the two
    /// sides" stays with the split alone: it is a switch that outlives the
    /// listing, and hiding it in a view would take away the way to turn it
    /// off.
    /// </summary>
    public bool CanActAcrossSides
        => IsSplit && ActiveTab?.IsRealFolder == true && OtherGroup?.ActiveTab?.IsRealFolder == true;

    /// <summary>
    /// Every gate in this file, raised at once. Called by the listing menus as
    /// they are prepared — see PaneViewModel.NotifyMenuGates for why that is
    /// the moment that matters. <see cref="NotifySelectionMenu"/> raises most
    /// of the same gates on its own list, for the selection and the active tab
    /// changing; it does not call this.
    /// </summary>
    public void NotifyMenuGates()
    {
        OnPropertyChanged(nameof(ShowCopyToInMenu));
        OnPropertyChanged(nameof(ShowMoveToInMenu));
        OnPropertyChanged(nameof(ShowSortByInMenu));
        OnPropertyChanged(nameof(ShowDetailsArrangeInMenu));
        OnPropertyChanged(nameof(ShowDuplicateInMenu));
        OnPropertyChanged(nameof(ShowAnalyseInMenu));
        OnPropertyChanged(nameof(CanActAcrossSides));
        OnPropertyChanged(nameof(ShowOpenInNewTabInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewWindowInMenu));
        OnPropertyChanged(nameof(ShowAddSelectionToPlaces));
        OnPropertyChanged(nameof(ShowAddCurrentToPlaces));
        OnPropertyChanged(nameof(ShowSaveSearchToPlaces));
        OnPropertyChanged(nameof(ShowCopyLocationInMenu));
        OnPropertyChanged(nameof(CanShowProperties));
        OnPropertyChanged(nameof(TransferTargets));
    }
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
    /// "Add this folder to places", on the background menu.
    ///
    /// **It used to hide whenever a folder was selected**, because the two
    /// rows shared one slot in the one menu and the selection decided which
    /// command filled it. They are in different menus now — the selected
    /// folder's row on the item menu, this one on the background's — and a
    /// right-click on empty space keeps the selection, so asking about it here
    /// would take the row away from a background menu opened beside a selected
    /// folder, which is exactly when somebody wants the folder they are in.
    /// </summary>
    public bool ShowAddCurrentToPlaces
        => Menu.ShowAddToPlaces && ActiveTab?.IsRealFolder == true;

    /// <summary>
    /// The same slot, in a search listing: the row reads "save this search"
    /// there rather than "add this folder", because a search is not a folder
    /// and a row that called it one would be the reader's homework again.
    /// Same command behind both — PinCurrent knows which it is standing in.
    /// Not about the selection, for the reason given on the row above.
    /// </summary>
    public bool ShowSaveSearchToPlaces
        => Menu.ShowAddToPlaces && ActiveTab?.IsSearchListing == true;
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
