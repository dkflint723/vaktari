using Avalonia.Controls;
using Avalonia.Interactivity;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The listing's right-click menu, from the moment it opens to the moment it
/// closes.
///
/// **One ContextMenu, declared once in markup, serves every right-click in a
/// listing and the Menu key as well.** That is what joins these: a menu built
/// once but opened thousands of times has to ask, at each opening, the
/// questions a binding cannot answer for it — and then put back whatever the
/// opening changed.
///
/// The questions come in three kinds, and each one is why a member here exists
/// rather than a binding in the markup. Per-machine and per-item at once: is
/// the Proton CLI installed, is this path inside the drive folder, has a link
/// already been made. Too expensive to pay for unasked: building the desktop's
/// own submenu gives every shell extension on the machine a turn, so it waits
/// for a hover instead of every right-click. And stale by the time anyone
/// looks: scripts and templates are re-read on each opening, because the menu
/// itself invites you to go and add one and then used to never notice what you
/// put there.
///
/// **Not because the menu has no DataContext.** It has one — OnListingMenuOpening
/// reads <c>menu.DataContext</c> as a PaneGroupViewModel and hands it to
/// PrepareListingMenu, where the whole Proton block depends on that being
/// there (the Menu key hands in its host's instead, because before Open the
/// menu's own is still null), PaneFromMenuItem says so in as many
/// words, and the markup declares the menu with an x:DataType and compiled
/// bindings against it. The claim is true of the PLACE row's menu, which is
/// declared inside a DataTemplate and now has its own file,
/// MainWindow.PlaceMenu.cs, where that sentence sits beside the menu it is
/// true of. Filing both menus together under one true-of-one sentence is the
/// mistake this file exists not to make.
///
/// Every member here is reached only from markup, except PaneFromMenuItem,
/// whose four references in the whole repository are the three Proton handlers
/// and its own declaration. It has to come along; left behind it would be a
/// member in MainWindow.axaml.cs that nothing in MainWindow.axaml.cs calls.
///
/// **Two things the closing puts back have their other end elsewhere**, and
/// they are named here because after this move nothing else names them.
///
/// The menu's Placement is set in MainWindow.MenuKey.cs, in OpenListingMenu,
/// for the Menu-key route only — and put back here, because the right-click
/// route must still open at the pointer and there is only the one menu to set
/// it on. That file's own summary points at OnListingMenuClosed by name and
/// should now be read as pointing at this file.
///
/// <c>AdminRequested</c> is cleared here and ARMED in OnPointerPressedAnywhere,
/// which stays in MainWindow.axaml.cs and cannot move — it is the shared press
/// dispatcher. The comment explaining why the Shift has to be caught at the
/// press, rather than when the menu builds, stays with the arm. Of the three
/// things the closing restores, only CloseShellMenu has its counterpart inside
/// this file.
///
/// One piece of debt carried unchanged, because a pure move may not edit what
/// it carries: OnListingMenuClosed's summary begins "Releases it", and the
/// "it" lost its antecedent long ago. The two were born thirteen lines apart
/// in bf9ce4b, where "it" was the shell menu OnShellMenuOpening had just
/// opened; everything now standing between them was inserted afterwards. The
/// summary is also short of what the member does, which is three things
/// rather than one. Keeping source order here preserves the fault rather than
/// repairing it — repairing it means reordering members, which is an edit to
/// the narrative and not a move.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Builds the desktop's own menu, at the moment its submenu opens.
    ///
    /// Not on a binding: building it gives every shell extension on the machine
    /// a turn, and no ordinary right-click should pay for something behind one
    /// more hover.
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

        if (sender is Control { DataContext: PaneGroupViewModel { ActiveTab: { } pane } })
            _ = pane.OpenShellMenuAsync();
    }

    /// <summary>
    /// The right-click route into <see cref="PrepareListingMenu"/>.
    ///
    /// **Only the right-click route.** Avalonia raises Opening on the way to a
    /// menu it opens itself, for a ContextRequested; ContextMenu.Open(control)
    /// does not raise it at all — measured on 12.1.2 in a headless probe, zero
    /// times. So the Menu key, which opens the menu with Open in
    /// OpenListingMenu, never came through here, and the keyboard's menu
    /// showed the scripts, templates, Undo label, Paste row and Proton rows as
    /// the previous right-click had left them: with no right-click yet, no
    /// Proton row at all.
    /// OpenListingMenu now calls PrepareListingMenu itself.
    /// </summary>
    private void OnListingMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu menu)
            PrepareListingMenu(menu, menu.DataContext as PaneGroupViewModel);
    }

    /// <summary>
    /// What every opening of the listing's menu asks before it is shown,
    /// whichever route opened it.
    ///
    /// The rows a binding keeps current are not the question. The ones here
    /// are read afresh because nothing tells the menu they changed: the
    /// scripts and templates folders, the engine's undo history and the
    /// clipboard. And the Proton entries, which are shown per item, decided
    /// here rather than bound, because the questions are per-item and
    /// per-machine at once: is the CLI installed, is the path inside the drive
    /// folder, and did Vaktari already make a link for it. Three hidden items
    /// cost nothing when the answer is no.
    ///
    /// **The group is handed in rather than read off the menu**, because on
    /// the keyboard route there is nothing on the menu to read yet. Measured
    /// in a headless MainWindow: before Open(host), menu.DataContext is null.
    /// The menu inherits the group from its host only once it opens, so a body
    /// that asked the menu for it did nothing at all when OpenListingMenu
    /// called it first, without a word said.
    /// </summary>
    private void PrepareListingMenu(ContextMenu menu, PaneGroupViewModel? group)
    {
        // **Re-read on every menu open, which is what their own comments always
        // claimed.** Both were called once, from the pane's constructor, so
        // adding a script or a template needed a restart to appear — while the
        // menu itself invites you to go and add one ("Add your own scripts"
        // opens the folder) and then never notices what you put there. Reading
        // two small directories is cheap next to building this menu at all.
        if (group is { ActiveTab: { } tab })
        {
            tab.RefreshScripts();
            tab.RefreshTemplates();

            // The Undo row names what it will take back, and the history is
            // the engine's — shared by every pane — so it is read when the
            // menu opens rather than tracked here.
            tab.RefreshUndoState();

            // Not awaited: a menu opens now. The Paste row shows what the last
            // answer was and corrects itself a round trip later. Deliberately
            // in THIS block, above the early return further down that has
            // silently swallowed work in this handler before.
            _ = tab.RefreshClipboardAsync();
        }

        // The Proton entries live inside the Share submenu now, so the walk
        // has to descend — and it has to see more than MenuItems, because the
        // rule between the two sharing methods is a Separator. The first
        // version walked OfType<MenuItem> only, could never find it, and the
        // early return below silently kept the whole submenu hidden: an eye
        // test on a machine WITH copyparty is what caught it.
        static T? Find<T>(IEnumerable<object?> items, string name) where T : Control
        {
            foreach (var item in items.OfType<Control>())
            {
                if (item is T match && match.Name == name) return match;
                if (item is MenuItem parent && Find<T>(parent.Items, name) is { } nested)
                    return nested;
            }

            return null;
        }

        if (Find<MenuItem>(menu.Items, "ShareMenu") is not { } shareMenu
            || Find<MenuItem>(menu.Items, "ProtonShareItem") is not { } share
            || Find<MenuItem>(menu.Items, "ProtonCopyLinkItem") is not { } copy
            || Find<MenuItem>(menu.Items, "ProtonUnshareItem") is not { } unshare
            || Find<MenuItem>(menu.Items, "ProtonInstallingItem") is not { } installing
            || Find<Separator>(menu.Items, "ShareMethodSeparator") is not { } separatorHost) return;

        var entry = group?.ActiveTab?.SelectedEntry;
        var path = entry?.FullPath;

        // Linkable is about WHERE the item is, not whether the tool exists —
        // the share click installs what is missing. The busy row takes the
        // share row's seat while that download runs.
        var linkable = path is not null && _shell.CanLinkShare(path);
        var existing = path is not null ? _shell.LinkFor(path) : null;
        var busy = path is not null && _shell.ShowDriveInstallBusy(path);

        share.IsVisible = linkable && existing is null && !busy;
        copy.IsVisible = linkable && existing is not null;
        unshare.IsVisible = linkable && existing is not null;
        installing.IsVisible = busy;

        // The submenu earns its place when EITHER way of sharing applies; the
        // rule between them only when both do.
        shareMenu.IsVisible = linkable || _shell.HasSharingEntry;
        separatorHost.IsVisible = linkable && _shell.HasSharingEntry;
    }

    private void OnProtonShareClicked(object? sender, RoutedEventArgs e)
    {
        if (PaneFromMenuItem(sender)?.SelectedEntry is { } entry)
            _ = _shell.CreateDriveLinkAsync(entry.FullPath);
    }

    private void OnProtonCopyLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (PaneFromMenuItem(sender)?.SelectedEntry is { } entry
            && _shell.LinkFor(entry.FullPath) is { } link)
            _shell.CopyDriveLinkCommand.Execute(link);
    }

    private void OnProtonUnshareClicked(object? sender, RoutedEventArgs e)
    {
        if (PaneFromMenuItem(sender)?.SelectedEntry is { } entry
            && _shell.LinkFor(entry.FullPath) is { } link)
            _shell.StopDriveLinkCommand.Execute(link);
    }

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
    private static ViewModels.PaneViewModel? PaneFromMenuItem(object? sender)
        => (sender as Control)?.DataContext is ViewModels.PaneGroupViewModel group
            ? group.ActiveTab
            : null;

    /// <summary>
    /// Puts back the three things opening the menu changed.
    ///
    /// The summary here read "Releases it when the menu closes" for a long
    /// while. The "it" was the desktop's shell menu, which OnShellMenuOpening
    /// had opened thirteen lines above when both were written; a hundred and
    /// more lines have since been inserted between them, and the member has
    /// grown two more jobs, so the sentence had lost both its antecedent and
    /// its count.
    ///
    /// **The shell menu.** Its ids are offsets into one live menu, so they are
    /// meaningless once it is gone — and each menu owns an STA thread, so
    /// never releasing would leak one per right-click. This is the only one of
    /// the three whose other half is in this file.
    ///
    /// **The placement**, set by the Menu-key route in MainWindow.MenuKey.cs
    /// and put back here because the same ContextMenu serves the pointer.
    ///
    /// **The elevation flag**, armed on the right-button press in
    /// MainWindow.axaml.cs and cleared here, so a Shift held a minute ago
    /// cannot still be offering elevation on the next ordinary right-click.
    /// </summary>
    private void OnListingMenuClosed(object? sender, RoutedEventArgs e)
    {
        // **Placement belongs to the menu, and ONE menu serves both routes.**
        // OpenListingMenu sets BottomEdgeAlignedLeft for the Menu key; left
        // set, it outlives the keystroke, and the next right-click anywhere in
        // the listing opens with it. Measured, driving a real right-button
        // press at a row in a headless MainWindow with that placement left
        // behind: the popup came up BottomEdgeAlignedLeft anchored on a 30px
        // panel inside the tab strip — not at the cursor, and not on any row.
        // Every right-click after a keyboard one would have put the menu up
        // there under the tabs until the next keyboard one.
        //
        // The same measurement is why the target is cleared rather than
        // restored to something: the right-click route never reads
        // PlacementTarget at all — it re-anchors on the attached control's own
        // panel, with the menu's target still pointing at the keyboard's row —
        // and the Menu-key route always assigns it before opening. So nulling
        // it changes no placement; it drops the menu's reference to a
        // ListBoxItem the virtualizing panel is free to recycle.
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
