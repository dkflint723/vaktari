using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Tabs and windows: making them, closing them, and getting back the one
/// closed by accident.
///
/// **A tab is cheap and a window is not**, which is the whole difference here.
/// Opening a tab is bookkeeping on a group that already exists; opening a
/// window asks the application for another shell, another sidebar and another
/// session to write — so the two routes do not share an implementation, and
/// the menu rows that look alike are not.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- tabs ----------------------------------------------------------

    [RelayCommand]
    private void NewTab()
        // Carrying the current view: hidden files, layout, sort, grouping and
        // zoom. A new tab that resets all five is a new tab you have to set up.
        => ActiveGroup.AddTab(
            ActiveTab?.CurrentPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            like: ActiveTab);

    [RelayCommand]
    private void OpenInNewTab(FileEntry? entry)
    {
        // Nullable because the CommandParameter binds to the selected entry and
        // one menu serves both a row and the empty space below it — on an
        // empty-space click it resolves to null. FileEntry is a struct, so a
        // RelayCommand<FileEntry> could not accept that and threw
        // ArgumentException from the menu rather than doing nothing; taking
        // FileEntry? is what lets the command be handed the empty case at all.
        //
        // **Five folders selected opened one tab.** The parameter is a single
        // row — ActiveTab.SelectedEntry — so the entry that says "Open in new
        // tab" quietly dropped every folder but that one, with nothing said
        // about the rest. The same shape as Enter opening one of five files,
        // and EntriesToActOn is the answer already written for it: the whole
        // selection when there is one, the focused row when there is not.
        IReadOnlyList<FileEntry> chosen = ActiveTab?.EntriesToActOn() ?? [];

        // The parameter is the fallback, not the source. It is all a caller
        // outside the listing hands over, and when it IS the focused row of a
        // real selection the list above already holds it.
        if (chosen.Count == 0 && entry is { } handed) chosen = [handed];

        // Folders only, as this verb has always been — the mirror of
        // OpenSelectedAsync, which launches the files and leaves the folders
        // alone because there is no navigating into five at once.
        var folders = chosen.Where(e => e.IsDirectory).ToList();

        if (folders.Count == 0) return;

        // The same bound Enter obeys, for the same reason it exists there:
        // Ctrl+A in a folder of four hundred subfolders is four hundred tabs,
        // and it says so rather than doing nothing.
        if (folders.Count > PaneViewModel.OpenLimit)
        {
            if (ActiveTab is { } pane)
                pane.Status = $"that would open {folders.Count} tabs at once — select fewer";

            return;
        }

        // In the BACKGROUND, which is what the phrase means: you ask for a new
        // tab rather than opening the folder precisely so you can carry on
        // where you are. It used to jump to the new one.
        foreach (var folder in folders)
            ActiveGroup.AddTab(folder.FullPath, like: ActiveTab, activate: false);
    }

    // ---- windows -----------------------------------------------------------

    /// <summary>Raised so the window can build a peer; a view model has no
    /// business constructing one. The folder is where it should open.</summary>
    public event EventHandler<string?>? NewWindowRequested;

    /// <summary>
    /// Ctrl+N: a second window on the folder you are already in.
    ///
    /// Not home, and not the Startup preference's folder. That preference
    /// answers "where does a LAUNCH begin", and a window opened from another
    /// one is not a launch — the reason to want a second window is almost
    /// always to keep two views of work you are in the middle of, so a window
    /// that arrives elsewhere has to be navigated before it is any use. It is
    /// also what Explorer's Ctrl+N does.
    /// </summary>
    [RelayCommand]
    private void NewWindow() => NewWindowRequested?.Invoke(this, ActiveTab?.CurrentPath);

    /// <summary>
    /// "Open in new window", on the whole selection.
    ///
    /// Five folders selected opens FIVE windows, not one. Acting on the single
    /// row a context menu hands over is the documented fault in OpenInNewTab's
    /// own comment — "Five folders selected opened one tab… quietly dropped
    /// every folder but that one" — and it is not being reintroduced one entry
    /// below it. A window is heavier than a tab, so the limit that already
    /// exists matters more here rather than less, and it is reused rather than
    /// re-invented.
    /// </summary>
    [RelayCommand]
    private void OpenInNewWindow(FileEntry? entry)
    {
        IReadOnlyList<FileEntry> chosen = ActiveTab?.EntriesToActOn() ?? [];

        if (chosen.Count == 0 && entry is { } handed) chosen = [handed];

        // Folders only, as this verb has always been in its tab form — the
        // mirror of OpenSelectedAsync, which launches the files and leaves the
        // folders alone because there is no navigating into five at once.
        var folders = chosen.Where(e => e.IsDirectory).ToList();

        if (folders.Count == 0) return;

        if (folders.Count > PaneViewModel.OpenLimit)
        {
            if (ActiveTab is { } pane)
                pane.Status = $"that would open {folders.Count} windows at once — select fewer";

            return;
        }

        foreach (var folder in folders) NewWindowRequested?.Invoke(this, folder.FullPath);
    }

    /// <summary>
    /// The sidebar's twin of the row above it.
    ///
    /// The same guard as OpenPlaceInNewTab, and deliberately not HasRealPath:
    /// the tab row carries no IsVisible at all, because both references put
    /// "open in new tab" on every node of the navigation pane — and a bin you
    /// can open in a tab is a bin you can open in a window.
    /// </summary>
    [RelayCommand]
    private void OpenPlaceInNewWindow(PlaceItemViewModel? place)
    {
        if (place is { Path.Length: > 0 }) NewWindowRequested?.Invoke(this, place.Path);
    }

    // ---- the tab strip's own menu ------------------------------------------
    //
    // A tab had no right-click menu at all: no Duplicate, no Close others, no
    // Close to the right. With a dozen open, closing them one at a time is the
    // only route, and both references offer all three.

    [RelayCommand]
    private void DuplicateTab(PaneViewModel? pane)
    {
        var from = pane ?? ActiveTab;

        if (from is not null) ActiveGroup.AddTab(from.CurrentPath, like: from);
    }

    [RelayCommand]
    private void CloseOtherTabs(PaneViewModel? pane) => ActiveGroup.CloseOtherTabs(pane);

    [RelayCommand]
    private void CloseTabsToTheRight(PaneViewModel? pane) => ActiveGroup.CloseTabsToTheRight(pane);

    /// <summary>
    /// Ctrl+Shift+T. Closing a tab used to throw its whole state away — where
    /// it was, its history, its view — so a tab closed by accident was gone.
    /// </summary>
    [RelayCommand]
    private void ReopenClosedTab() => ActiveGroup.ReopenClosedTab();

    /// <summary>
    /// Opens a folder by path — used when the desktop hands one over, either on
    /// the command line or from a later launch forwarded to this instance.
    ///
    /// Reuses the current tab when it is already showing that folder, so
    /// repeatedly opening the same place from elsewhere does not stack up
    /// identical tabs.
    /// </summary>
    /// <summary>
    /// Opens a folder in a tab BEHIND the current one, which is what the middle
    /// button means everywhere it works.
    ///
    /// **Separate from the overload below on purpose.** That one is the
    /// desktop's handover — a second launch forwarded into this window — where
    /// reusing an existing tab and jumping to it is right, because somebody
    /// asked the desktop to show them that folder now. A middle click is the
    /// opposite intention: it says "keep this open too, I am not finished
    /// here", and answering it by moving you somewhere else, or by silently
    /// doing nothing because a tab is already there, is the gesture failing.
    /// </summary>
    public void OpenBehind(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        ActiveGroup.AddTab(path, like: ActiveTab, activate: false);
    }

    public void OpenInNewTab(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // PathRules.Same, like the two other places in this file that ask the
        // same question. An ordinal compare opens a second tab on the same
        // folder for a difference of case or a trailing separator - and its
        // doc comment names duplicate-tab detection as the reason it exists.
        var existing = ActiveGroup.Tabs.FirstOrDefault(
            t => Core.FileSystem.PathRules.Same(t.CurrentPath, path));

        if (existing is not null)
        {
            ActiveGroup.ActiveTab = existing;
            return;
        }

        ActiveGroup.AddTab(path);
    }

    /// <summary>
    /// Shows each item where it lives, with it highlighted — what another
    /// application means by "open containing folder".
    ///
    /// **Not OpenInNewTab, and the difference is the point of the feature.**
    /// That opens the folder and selects nothing, which in a Downloads folder of
    /// four hundred files does not answer "which one did I just save".
    ///
    /// **Grouped by folder rather than one tab per item.** "Show these four
    /// downloads" is one place with four things lit, not four tabs on the same
    /// folder — and OpenInNewTab's own duplicate-tab rule says the same about
    /// opening a folder twice.
    /// </summary>
    public async Task ShowAsync(IReadOnlyList<string> paths)
    {
        var comparer = StringComparer.FromComparison(Core.FileSystem.PathRules.Comparison);
        var groups = new Dictionary<string, List<string>>(comparer);

        // Insertion order kept separately: a Dictionary has none, and the first
        // folder named should be the one left in front.
        var order = new List<string>();

        foreach (var path in paths)
        {
            // **The PARENT, and this one line is the whole of ShowItems.** An
            // item another application asks us to show is selected where it
            // lives; navigating INTO it is what the search reveal does, and
            // doing that here puts you inside the very folder you were being
            // shown.
            var folder = Core.FileSystem.PathRules.Parent(path);

            // A filesystem root has no parent to be shown in. Nothing sensible
            // to do — opening the root itself would answer a question that was
            // not asked, and that question is ShowFolders.
            if (string.IsNullOrEmpty(folder)) continue;

            var key = Core.FileSystem.PathRules.Normalise(folder);

            if (!groups.TryGetValue(key, out var items))
            {
                groups[key] = items = [];
                order.Add(key);
            }

            items.Add(path);
        }

        PaneViewModel? first = null;

        foreach (var folder in order)
        {
            // PathRules.Same rather than an ordinal compare, for the reason
            // OpenInNewTab gives just above: a trailing separator or a
            // difference of case is the same folder and a different string.
            var pane = ActiveGroup.Tabs.FirstOrDefault(
                           t => Core.FileSystem.PathRules.Same(t.CurrentPath, folder))
                       ?? ActiveGroup.AddTab(folder, like: ActiveTab);

            first ??= pane;

            // AddTab has already started a navigation to this same folder, and
            // ShowAsync starts another. That is deliberate rather than
            // overlooked: the load cancels whatever is in flight before it
            // begins — its comment says that is not an optimisation — so the
            // second either finds the first finished and short-circuits, or
            // supersedes it.
            await pane.ShowAsync(folder, groups[folder]).ConfigureAwait(true);
        }

        // **Once, after the loop, and from the FIRST folder.** Setting it
        // inside the loop leaves the last one in front, which is the opposite
        // of what the order list above exists to preserve.
        if (first is not null) ActiveGroup.ActiveTab = first;
    }

    [RelayCommand]
    private void CloseTab(PaneViewModel? pane)
    {
        // Closing the last tab of the right side collapses the split rather
        // than refusing, which is what the user actually means.
        if (Right is not null && ActiveGroup.Tabs.Count <= 1 &&
            (pane is null || ActiveGroup.Tabs.Contains(pane)))
        {
            if (ReferenceEquals(ActiveGroup, Right)) { ToggleSplit(); return; }

            // Closing the last left tab: promote the right side to be the only one.
            var survivor = Right;
            Left.DisposeAll();
            Left = survivor;
            Right = null;
            ActiveGroup = Left;
            OnPropertyChanged(nameof(Left));
            return;
        }

        // **The last tab closes the window**, which is what Ctrl+W does in
        // Explorer and in every browser. It used to do nothing at all: the
        // group refuses to leave a side with no tabs, so with one tab and no
        // split both Ctrl+W and the tab's × were drawn, clickable, tooltipped
        // and inert — and there was no Ctrl+Q either, so the keyboard could not
        // close the window at all.
        if (Right is null && ActiveGroup.Tabs.Count <= 1
            && (pane is null || ActiveGroup.Tabs.Contains(pane)))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ActiveGroup.CloseTab(pane);
    }

    /// <summary>
    /// Asks the window to close. Raised rather than acted on: the shell owns no
    /// window, and the session is saved by whoever does.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>Ctrl+Q, which had no binding anywhere.</summary>
    [RelayCommand]
    private void Quit() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand] private void NextTab() => ActiveGroup.Cycle(1);
    [RelayCommand] private void PreviousTab() => ActiveGroup.Cycle(-1);

    public void SelectTabByIndex(int index) => ActiveGroup.SelectTabByIndex(index);

    public void ActivateGroup(PaneGroupViewModel group)
    {
        if (!ReferenceEquals(ActiveGroup, group)) ActiveGroup = group;
    }
}
