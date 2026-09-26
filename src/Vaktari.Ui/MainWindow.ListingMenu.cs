using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The listing's two right-click menus, from the moment one is asked for to
/// the moment it closes.
///
/// **There were not two: one ContextMenu served every right-click in a
/// listing, on a row or on nothing, and the Menu key as well.** So it carried
/// everything either could want — twenty-eight rows and six rules for a plain
/// file on Windows, 904 pixels tall — and ten of those rows were about the
/// FOLDER and sat on every file's menu. They could not be gated away, because
/// a right-click on empty space deliberately keeps the selection (see
/// FocusListIfEmptySpace): "nothing is selected" never meant "this click was
/// on nothing", so the one test that could have separated the two menus'
/// rows did not exist.
///
/// **Two ContextMenus now, rather than one menu with a mode flag its gates
/// read**, and the choice was made on four things:
///
/// The markup. With one menu, every one of the forty-odd rows would have
/// carried its own gate AND the mode — a MultiBinding apiece, or a second
/// property per gate on two view models. With two, each row sits in the menu
/// it belongs to and keeps the gate it had.
///
/// Each pane-half's menu. Both are declared in the pane group's template, like
/// the one menu was, so each half of a split has its own pair and nothing here
/// has to ask which half it is.
///
/// The Windows menu. Its item-or-background question has to follow the MENU,
/// and a menu with a mode would have had to carry the mode to the hover that
/// builds it. Two menus have two rows with two names, and the name is the
/// answer — see <see cref="OnShellMenuOpening"/>.
///
/// Placement and the tests. Each menu keeps its own Placement, so the Menu
/// key setting one cannot leak into the other, and a test names the menu it
/// means instead of setting a mode first.
///
/// **How a click reaches the right one is Avalonia's routing, used as it
/// stands.** The item menu hangs on the ItemsControl that holds the tabs and
/// the background menu on the Panel around it. A right-click raises
/// ContextRequested from whatever it hit, bubbling up; the ItemsControl's menu
/// is asked first, and <see cref="OnItemMenuOpening"/> cancels it when the
/// click was off every row. Measured on 12.1.2 in a headless probe: a
/// cancelled Opening leaves the request unhandled, it carries on up, and the
/// Panel's menu opens at the pointer the way any right-click's does. Where the
/// click landed is recorded on the way DOWN, by
/// <see cref="OnContextRequestedTunnel"/>, because the Opening event carries no
/// source.
///
/// **What every opening asks, whichever menu and whichever route**, is in
/// <see cref="OnListingMenuOpened"/>: the scripts, templates, undo history,
/// clipboard and Proton rows re-read, every gate re-raised, and then the rules
/// tidied to what is drawn. Opened rather than Opening because Opened is the
/// one event every route raises — measured, ContextMenu.Open(control) raises
/// no Opening at all, which is how the keyboard's menu once showed whatever
/// the last right-click had left.
///
/// **Two things the closing puts back have their other end elsewhere.** The
/// Placement is set in MainWindow.MenuKey.cs, in OpenListingMenu, for the Menu
/// key only, and put back here because a right-click must still open at the
/// pointer. <c>AdminRequested</c> is armed in OnPointerPressedAnywhere, in
/// MainWindow.axaml.cs — the shared press dispatcher, where the Shift has to be
/// caught because nothing later can see it — and cleared here.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Whether the last context request in this window came from a row — the
    /// question <see cref="OnItemMenuOpening"/> answers with.
    /// </summary>
    private bool _contextOnRow;

    /// <summary>
    /// Records whether a context request came from a row, on its way down.
    ///
    /// **A row is anything EntryAt finds a FileEntry above**, which is the
    /// whole of a row: the blank half of a full-width details row included,
    /// because the row's own background is what is there — and a right-click
    /// there on an unselected row selects it, as on its name. A group heading
    /// is drawn inside a row and is not one (EntryAt says why), nor is the
    /// space between tiles, below the last row, or a band above the listing.
    ///
    /// EntryAt rather than the band's ListForEmptySpace, which reads the same
    /// pixels differently — it calls the blank half of an unselected
    /// full-width row empty space, so a band can start there. Measured with
    /// that walk swapped in: every right-click test stays green, because the
    /// press has already selected the row by the time the request arrives and
    /// the walk answers "not empty" for a selected row. The two part on a
    /// group heading, which the band's walk refuses to call empty space and
    /// EntryAt refuses to call a row; a heading is not an item, so it gets the
    /// background menu.
    ///
    /// Tunnelled, so it runs before any menu is asked; it records and never
    /// handles, so the right-drag suppression beside it in the constructor
    /// still decides whether a menu opens at all.
    /// </summary>
    private void OnContextRequestedTunnel(object? sender, ContextRequestedEventArgs e)
        => _contextOnRow = EntryAt(e.Source) is not null;

    /// <summary>
    /// The item menu's half of the routing: it steps aside for a click that
    /// landed off every row, and the request goes on up to the background
    /// menu.
    ///
    /// **Only the right-click route comes through here.** Avalonia raises
    /// Opening on the way to a menu it opens itself, for a ContextRequested;
    /// ContextMenu.Open(control) does not raise it at all — measured on 12.1.2
    /// in a headless probe, zero times — and the Menu key chooses its menu
    /// itself, in OpenListingMenu.
    /// </summary>
    private void OnItemMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_contextOnRow)
        {
            e.Cancel = true;
            return;
        }

        // The menu's Open does not pass through TryOpen, so the row it opens
        // would otherwise stay "clicked once" and open again on a single later
        // click. The keyboard's route forgets it in OpenListingMenu. See
        // ForgetTheClick.
        ForgetTheClick();
    }

    /// <summary>The background menu's right-click route: the same forgetting
    /// the item menu does, for the same reason.</summary>
    private void OnBackgroundMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ForgetTheClick();

    /// <summary>
    /// What every opening of either listing menu asks before it is seen, and
    /// the rules it draws afterwards.
    ///
    /// Opened rather than Opening because every route raises it: the Menu key
    /// opens with Open(control), which raises no Opening — measured on 12.1.2 —
    /// and a test opening a menu directly raises none either. Opened comes
    /// after the bindings have resolved — measured: a row whose gate is false
    /// already reads IsVisible false here, nested rows included — and before
    /// the first frame, so nothing below is ever drawn stale.
    /// </summary>
    private void OnListingMenuOpened(object? sender, RoutedEventArgs e)
    {
        // Only the menu's own opening: a submenu opening does not raise this,
        // but a handler that assumed so would tidy the wrong list if one did.
        if (!ReferenceEquals(e.Source, sender) || sender is not ContextMenu menu) return;

        PrepareListingMenu(menu, menu.DataContext as PaneGroupViewModel);

        TidyRules(menu.Items);
    }

    /// <summary>
    /// What an opening re-reads, because nothing tells the menu it changed.
    ///
    /// The scripts and templates folders, the engine's undo history and the
    /// clipboard; every gate the two menus added, re-raised so the one moment
    /// a stale answer would be seen is a moment it cannot be stale; and the
    /// Share rows, decided here rather than bound because the questions are
    /// per item and per machine at once — is the Proton CLI installed, is THIS
    /// path inside the drive folder, has a link already been made, is it a
    /// folder a network share would serve.
    /// </summary>
    private void PrepareListingMenu(ContextMenu menu, PaneGroupViewModel? group)
    {
        // **Re-read on every menu open, which is what their own comments always
        // claimed.** Both were called once, from the pane's constructor, so
        // adding a script or a template needed a restart to appear — while the
        // background menu invites you to go and add one ("Open scripts folder")
        // and then used to never notice what you put there. Reading two small
        // directories is cheap next to building a menu at all.
        if (group is not { ActiveTab: { } tab }) return;

        tab.RefreshScripts();
        tab.RefreshScriptRows();
        tab.RefreshTemplates();

        // The Undo row names what it will take back, and shows only when there
        // is something — and the history is the engine's, shared by every
        // pane, so it is read when the menu opens rather than tracked here.
        tab.RefreshUndoState();

        // Not awaited: a menu opens now. The Paste row shows what the last
        // answer was and corrects itself a round trip later.
        _ = tab.RefreshClipboardAsync();

        tab.NotifyMenuGates();
        _shell.NotifyMenuGates();

        var background = menu.Name == BackgroundMenuName;

        PrepareShare(menu, tab, background);
    }

    /// <summary>
    /// Which of a Share submenu's rows apply, for the one path the menu is
    /// about: the selected item on the item menu, the folder on screen on the
    /// background's.
    ///
    /// **A file's menu shared its parent folder.** The network rows serve a
    /// folder, and on a file they served the one around it — so they show for
    /// a folder only now, and the folder being looked at is shared from the
    /// background menu. Not a drive root either: the share refuses one
    /// outright. See ShellViewModel.CanNetworkShare.
    ///
    /// The walk has to descend and has to see more than MenuItems: the rows
    /// live inside the Share submenu. The first version of it walked
    /// OfType&lt;MenuItem&gt; only, could never find the rule it then looked
    /// for, and returned early — an eye test on a machine WITH copyparty is
    /// what caught it. There is no early return now: every row is found by the
    /// name the markup gives it, and a missing one throws in the tests rather
    /// than silently keeping a submenu hidden.
    /// </summary>
    private void PrepareShare(ContextMenu menu, PaneViewModel tab, bool background)
    {
        var prefix = background ? "Folder" : "";

        MenuItem Row(string name) => Find(menu.Items, name)
            ?? throw new InvalidOperationException($"the listing menu has no {name}");

        var shareMenu = Row(background ? "FolderShareMenu" : "ShareMenu");

        // The path the menu is about, and never a binned row's: that names
        // where an item USED to be.
        var path = background
            ? (tab.IsRealFolder ? tab.CurrentPath : null)
            : (tab.IsTrashListing ? null : tab.SelectedEntry?.FullPath);

        // Linkable is about WHERE the item is, not whether the tool exists —
        // the share click installs what is missing. The busy row takes the
        // share row's seat while that download runs.
        var linkable = path is not null && _shell.CanLinkShare(path);
        var existing = linkable ? _shell.LinkFor(path!) : null;
        var busy = linkable && _shell.ShowDriveInstallBusy(path!);

        Row(prefix + "ProtonShareItem").IsVisible = linkable && existing is null && !busy;
        Row(prefix + "ProtonCopyLinkItem").IsVisible = existing is not null;
        Row(prefix + "ProtonUnshareItem").IsVisible = existing is not null;
        Row(prefix + "ProtonInstallingItem").IsVisible = busy;

        // A folder a network share would serve.
        var folder = background
            ? path is not null && _shell.CanNetworkShare(path)
            : tab.SelectedEntry is { IsDirectory: true } && _shell.CanNetworkShare(path);

        // Three states that cover each other: ready, missing, installing.
        Row(prefix + "ShareRow").IsVisible = folder && _shell.CanShare;
        Row(prefix + "ShareWritableRow").IsVisible = folder && _shell.CanShare;
        Row(prefix + "ShareInstallRow").IsVisible = folder && _shell.CanInstallSharing;
        Row(prefix + "ShareInstallingRow").IsVisible = folder && _shell.IsInstalling;

        shareMenu.IsVisible = linkable || folder;
    }

    /// <summary>The name the markup gives the background menu, which is how an
    /// opening knows which of the two it is.</summary>
    private const string BackgroundMenuName = "BackgroundMenu";

    /// <summary>A row by name, however deep in the submenus it sits.</summary>
    private static MenuItem? Find(IEnumerable<object?> items, string name)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Name == name) return item;
            if (Find(item.Items, name) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>
    /// Shows a rule only where it separates two things that are drawn.
    ///
    /// **Avalonia draws every separator it is given and collapses none**, so
    /// the one listing menu gated each rule on the block it introduced, copied
    /// by hand from the rows under it — and got it wrong often enough that
    /// three comments in the old markup recorded a stray rule measured in one
    /// listing or another. With the rows split across two menus a block's gate
    /// became the OR of up to seven rows' gates on two view models. This reads
    /// what is actually drawn instead: a rule with no visible row since the
    /// last one, or none after it, is hidden, which covers a leading rule, a
    /// trailing rule and two rules meeting in one pass.
    ///
    /// Into static submenus as well, where the same thing happens on a smaller
    /// scale — View's rule above Sort by has nothing under it with the sort
    /// preference off. Not into a submenu built from an ItemsSource: those
    /// rules are the shell's, or the scripts list's, and are placed by whoever
    /// built the list.
    /// </summary>
    internal static void TidyRules(IEnumerable<object?> items)
    {
        Separator? pending = null;
        var rowSinceRule = false;

        foreach (var item in items.OfType<Control>())
        {
            if (item is Separator rule)
            {
                rule.IsVisible = false;

                if (rowSinceRule)
                {
                    pending = rule;
                    rowSinceRule = false;
                }

                continue;
            }

            if (!item.IsVisible) continue;

            if (pending is not null)
            {
                pending.IsVisible = true;
                pending = null;
            }

            rowSinceRule = true;

            if (item is MenuItem { ItemsSource: null } parent) TidyRules(parent.Items);
        }
    }

    /// <summary>
    /// Builds the desktop's own menu, at the moment its submenu opens.
    ///
    /// Not on a binding: building it gives every shell extension on the machine
    /// a turn, and no ordinary right-click should pay for something behind one
    /// more hover.
    ///
    /// **Which of the shell's two menus is decided by which row this is**, not
    /// by the selection: the background menu's row asks for the folder's own
    /// menu even with files selected, because a right-click on empty space
    /// keeps them selected and the row is about the folder.
    /// </summary>
    private void OnShellMenuOpening(object? sender, RoutedEventArgs e)
    {
        // **SubmenuOpened BUBBLES, and the shell's menu nests.** Hovering
        // 7-Zip's own submenu raised this event again on the way up, and
        // handling it rebuilt the whole menu — whose first act is to clear the
        // collection the open popup was being drawn from. The submenu appeared
        // and vanished in the same instant, every time, for every extension
        // that cascades: Send to, VLC, Restore previous versions.
        //
        // Only this item's own opening counts.
        if (!ReferenceEquals(e.Source, sender)) return;

        if (sender is Control { DataContext: PaneGroupViewModel { ActiveTab: { } pane } } row)
            _ = pane.OpenShellMenuAsync(background: row.Name == "BackgroundShellMenu");
    }

    private void OnProtonShareClicked(object? sender, RoutedEventArgs e)
        => ShareViaProton(sender, SelectedPath(sender));

    private void OnProtonCopyLinkClicked(object? sender, RoutedEventArgs e)
        => CopyProtonLink(SelectedPath(sender));

    private void OnProtonUnshareClicked(object? sender, RoutedEventArgs e)
        => StopProtonLink(SelectedPath(sender));

    private void OnFolderProtonShareClicked(object? sender, RoutedEventArgs e)
        => ShareViaProton(sender, FolderPath(sender));

    private void OnFolderProtonCopyLinkClicked(object? sender, RoutedEventArgs e)
        => CopyProtonLink(FolderPath(sender));

    private void OnFolderProtonUnshareClicked(object? sender, RoutedEventArgs e)
        => StopProtonLink(FolderPath(sender));

    private void ShareViaProton(object? sender, string? path)
    {
        if (path is not null && PaneFromMenuItem(sender) is not null)
            _ = _shell.CreateDriveLinkAsync(path);
    }

    private void CopyProtonLink(string? path)
    {
        if (path is not null && _shell.LinkFor(path) is { } link)
            _shell.CopyDriveLinkCommand.Execute(link);
    }

    private void StopProtonLink(string? path)
    {
        if (path is not null && _shell.LinkFor(path) is { } link)
            _shell.StopDriveLinkCommand.Execute(link);
    }

    /// <summary>The item menu's Proton rows act on the selected item.</summary>
    private static string? SelectedPath(object? sender)
        => PaneFromMenuItem(sender)?.SelectedEntry?.FullPath;

    /// <summary>The background menu's act on the folder on screen, whatever is
    /// selected.</summary>
    private static string? FolderPath(object? sender)
        => PaneFromMenuItem(sender) is { IsRealFolder: true } pane ? pane.CurrentPath : null;

    /// <summary>
    /// The pane whose menu the clicked item belongs to — read from the item's
    /// own DataContext, which it inherits from the ContextMenu.
    ///
    /// **Not through Parent**, which this used to do: when the Proton rows
    /// moved inside the Share submenu, their Parent became that submenu rather
    /// than the ContextMenu, the cast answered null, and every click on them
    /// did nothing without a word said. Inheritance does not care how deep the
    /// row sits.
    /// </summary>
    private static PaneViewModel? PaneFromMenuItem(object? sender)
        => (sender as Control)?.DataContext is PaneGroupViewModel group
            ? group.ActiveTab
            : null;

    /// <summary>
    /// Puts back the three things opening a listing menu changed. Either menu:
    /// both name this handler.
    ///
    /// **The shell menu.** Its ids are offsets into one live menu, so they are
    /// meaningless once it is gone — and each menu owns an STA thread, so
    /// never releasing would leak one per right-click.
    ///
    /// **The placement**, set by the Menu-key route in MainWindow.MenuKey.cs
    /// and put back here because the same menu serves the pointer.
    ///
    /// **The elevation flag**, armed on the right-button press in
    /// MainWindow.axaml.cs and cleared here, so a Shift held a minute ago
    /// cannot still be offering elevation on the next ordinary right-click.
    /// </summary>
    private void OnListingMenuClosed(object? sender, RoutedEventArgs e)
    {
        // **Placement belongs to the menu, and each menu serves both routes.**
        // OpenListingMenu sets BottomEdgeAlignedLeft or Center for the Menu
        // key; left set, it outlives the keystroke, and the next right-click
        // opens with it. Measured, driving a real right-button press at a row
        // in a headless MainWindow with that placement left behind: the popup
        // came up BottomEdgeAlignedLeft anchored on a 30px panel inside the tab
        // strip — not at the cursor, and not on any row.
        //
        // The target is cleared rather than restored to something: the
        // right-click route never reads PlacementTarget — it re-anchors on the
        // attached control's own panel — and the Menu-key route always assigns
        // it before opening. So nulling it changes no placement; it drops the
        // menu's reference to a ListBoxItem the virtualizing panel is free to
        // recycle.
        //
        // Ahead of the pane block below, which returns early: what is being put
        // back is a property of the menu, and it has to be put back whether or
        // not the menu turned out to have a pane behind it.
        if (sender is ContextMenu menu)
        {
            menu.Placement = PlacementMode.Pointer;
            menu.PlacementTarget = null;
        }

        if (sender is not Control { DataContext: PaneGroupViewModel { ActiveTab: { } pane } })
            return;

        pane.CloseShellMenu();

        // Disarmed with the menu. Left set, the next ordinary right-click would
        // still be offering elevation because of a Shift held a minute ago.
        pane.AdminRequested = false;
    }
}
