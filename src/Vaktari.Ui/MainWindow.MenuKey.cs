using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Vaktari.Ui;

/// <summary>
/// What the Menu key opens, and where.
///
/// **One if/else, kept in one place.** The keyboard asks InAddressBar whether
/// the focus is on the crumb bar: if it is, the bar raises its own context
/// request; if it is not, one of the listing's two menus opens — the item
/// menu at the focused row when something is selected, the background menu in
/// the middle of the list when nothing is. Those
/// are the two arms of a single keystroke, and an earlier plan would have put
/// one of them in the keymap file and left the other forty lines up in the
/// window — which is the shape this whole item exists to undo.
///
/// FocusedRow is here because placement is the point: a menu raised by a key
/// has no pointer to appear under, so it appears at the row the keyboard is
/// on. It has no other caller.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The row the keyboard is on, or null when the listing has none.
    ///
    /// Focus first and selection second, because they come apart: Ctrl+arrow
    /// moves the focused row without selecting it, and the menu belongs where
    /// the person is looking rather than where the last selection was left.
    /// </summary>
    private static Control? FocusedRow(ListBox list)
        => Rows(list).FirstOrDefault(row => row.IsKeyboardFocusWithin)
           ?? (list.SelectedIndex >= 0 ? list.ContainerFromIndex(list.SelectedIndex) : null);

    /// <summary>
    /// Opens one of the listing's two context menus from the keyboard: the
    /// item menu at the focused row, or the background menu on the list.
    ///
    /// The item menu hangs off the ItemsControl that holds the tabs and the
    /// background menu off the Panel around it, so each is found by walking up
    /// from the listing rather than from the row — the row's own template has
    /// no menu of its own.
    ///
    /// **The Menu key did not open a menu in the wrong place — it threw.** This
    /// called menu.Open(list), under a comment claiming that placed the menu on
    /// the list rather than at the pointer, and Open refuses any control but
    /// the one the menu is attached to: the menu hangs off the tab strip's
    /// ItemsControl, the listing is a ListBox inside its item template, so
    /// every press raised ArgumentException — "Cannot show ContentMenu on a
    /// different control to the one it is attached to" — straight out of
    /// OnWindowKeyDown with nothing to catch it. Restoring that one call
    /// reddens all four tests in ContextMenuPlacementTests with that exception,
    /// which is what The_menu_key_opens_the_menu_at_the_focused_row now pins.
    ///
    /// The comment's claim was wrong the other way too. Open(control) does
    /// anchor the popup on the control it is handed — measured, with
    /// PlacementTarget null the popup's own PlacementTarget comes back as that
    /// control — but Placement stays whatever the menu holds, and a
    /// ContextMenu is born Pointer. So had the call been legal it would still
    /// have opened at the pointer, ignoring the anchor.
    ///
    /// Placement is set here and put back in <see cref="OnListingMenuClosed"/>,
    /// over in MainWindow.ListingMenu.cs, rather than in the markup: a
    /// RIGHT-CLICK must still open at the pointer, and each of the two menus
    /// serves both routes. This is the only line of the keyboard
    /// route that another file has to undo, which is why it is named on both
    /// sides.
    /// </summary>
    private void OpenListingMenu(Key key)
    {
        if (ActiveListing() is not { } list) return;

        // **The item menu with a selection, the background menu without one.**
        // A right-click decides by where it landed; a key has landed nowhere,
        // so the selection is the only thing it can go by — and it is what the
        // keyboard has been building. Explorer's Shift+F10 makes the same
        // choice.
        var items = list.DataContext is ViewModels.PaneViewModel { HasSelection: true };

        if (ListingMenuHost(list, items ? ItemMenuName : BackgroundMenuName) is not
            { ContextMenu: { } menu } host) return;

        // The item menu under the focused row and left-aligned with it, which
        // is where Explorer puts the Menu key's menu. The background menu is
        // about the listing rather than any row, so it goes in the middle of
        // the list — still on the thing the menu is about, which the
        // pointer's last resting place is not — and so does an item menu
        // whose row has scrolled out of the realized set.
        var row = items ? FocusedRow(list) : null;

        menu.Placement = row is null
            ? PlacementMode.Center
            : PlacementMode.BottomEdgeAlignedLeft;

        menu.PlacementTarget = row ?? list;

        // Prepared before it opens, as the right-click route is in Opening, so
        // no frame of it is drawn with the last opening's rows. See
        // PrepareEarly.
        PrepareEarly(menu, host.DataContext as ViewModels.PaneGroupViewModel);

        // **Open() takes the control the menu is ATTACHED to and refuses
        // any other**, so the row cannot be handed to it — the host is.
        // The anchor is PlacementTarget, set above, and it does reach the
        // popup: measured on a real Menu-key press in a headless
        // MainWindow, with the fourth row focused, the popup's own
        // PlacementTarget came back as that ListBoxItem, bounds 0,90 by
        // 1185x30 — the fourth 30px row.
        //
        // **And Open() does not raise Opening** — measured, a headless
        // ContextMenu opened this way raised it zero times. So the click
        // memory the right-click route clears in Opening is cleared here for
        // the keyboard's menu (see ForgetTheClick), and the preparing the
        // right-click route does in Opening is done above.
        ForgetTheClick();

        // Held here, as the menu opens, and nowhere else: a key held on its
        // way to the address bar's flyout, or to a listing that turned out to
        // have no menu, was never let go of, and the next Menu key that closed
        // a menu took two presses. See _menuKeyHeld.
        HoldMenuKey(key);
        menu.Open(host);
    }

    /// <summary>
    /// The key that opened a menu, while it is still down.
    ///
    /// **The Menu key opened the menu on its press and closed it on its
    /// release.** Measured headless on this tree and on 0.11.0: KeyPress(Apps)
    /// opened the listing's menu and the KeyRelease that follows every real
    /// press closed it again, so the key flashed a menu at best. The closing is
    /// Avalonia's own: ContextMenu.PopupKeyUp shuts an open menu on the Menu
    /// key's release, because Avalonia's route OPENS on that release and a
    /// second one means "put it away". This window opens on the press, where
    /// the rest of its keys are answered, so the release that follows reaches
    /// a menu that is already open — the menu has the keyboard by then, so the
    /// release is routed through the menu rather than the window, and it is
    /// swallowed there, by <see cref="OnMenuKeyUp"/>, before the popup's own
    /// handler hears it. Shift+F10 is held the same way, though
    /// PopupKeyUp was measured to close on the Menu key alone.
    /// </summary>
    private Key? _menuKeyHeld;

    /// <summary>Remembers the key that is opening a menu, so its release can
    /// be kept from reaching the menu. See <see cref="_menuKeyHeld"/>.</summary>
    private void HoldMenuKey(Key key) => _menuKeyHeld = key;

    /// <summary>
    /// Swallows the release of the key that just opened a menu — once. Any
    /// other key's release, and a later release of the same key, go through:
    /// pressing the Menu key again on an open menu still puts it away.
    ///
    /// On the menu, from the markup: the release starts at the menu or a row in
    /// it and bubbles up to the popup, whose own handler is the one that
    /// closes, so the menu is the one place that hears it first.
    /// </summary>
    private void OnMenuKeyUp(object? sender, KeyEventArgs e)
    {
        if (_menuKeyHeld is not { } held || e.Key != held) return;

        _menuKeyHeld = null;
        e.Handled = true;
    }

    /// <summary>The name the markup gives the item menu.</summary>
    private const string ItemMenuName = "ItemMenu";

    /// <summary>
    /// The control carrying the named listing menu, walked up to from the
    /// listing: the ItemsControl that holds the tabs for the item menu, and the
    /// Panel around it for the background's. Named rather than "the first menu
    /// above the list", which is how this found its menu while there was one.
    /// </summary>
    private static Control? ListingMenuHost(ListBox list, string menuName)
    {
        for (var visual = (Visual?)list; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { ContextMenu: { } menu } host && menu.Name == menuName)
                return host;

        return null;
    }

    /// <summary>
    /// The name the address bar's Panel carries in MainWindow.axaml.
    ///
    /// A name rather than a field: the bar lives inside the per-group
    /// DataTemplate, so there is one of it per split half and no generated
    /// field for either.
    /// </summary>
    private const string AddressBarName = "AddressBar";

    /// <summary>
    /// Whether the keyboard is somewhere on the address bar.
    ///
    /// Walked from the focused element UP, because what is focused is a crumb
    /// button — the bar's menu hangs on the Panel above the crumbs and the
    /// double-click target both, which is the only ancestor they share.
    /// </summary>
    private static bool InAddressBar(object? element)
    {
        for (var visual = element as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { Name: AddressBarName }) return true;

        return false;
    }
}
