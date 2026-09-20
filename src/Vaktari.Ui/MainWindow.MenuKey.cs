using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Vaktari.Ui;

/// <summary>
/// What the Menu key opens, and where.
///
/// **One if/else, kept in one place.** The keyboard asks InAddressBar whether
/// the focus is on the crumb bar: if it is, the bar raises its own context
/// request; if it is not, the listing's menu opens at the focused row. Those
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
    /// Opens the listing's context menu from the keyboard, at the focused row.
    ///
    /// The menu hangs off the ItemsControl that holds the tabs, so it is found
    /// by walking up from the listing rather than from the row — the row's own
    /// template has no menu of its own.
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
    /// rather than in the markup: a RIGHT-CLICK must still open at the pointer,
    /// and the markup has one ContextMenu serving both routes.
    /// </summary>
    private void OpenListingMenu()
    {
        if (ActiveListing() is not { } list) return;

        for (var visual = (Visual?)list; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control { ContextMenu: { } menu } host) continue;

            // Under the focused row and left-aligned with it, which is where
            // Explorer puts the Menu key's menu. An empty listing has no row to
            // hang it on, so it goes in the middle of the list — still on the
            // thing the menu is about, which the pointer's last resting place
            // is not.
            var row = FocusedRow(list);

            menu.Placement = row is null
                ? PlacementMode.Center
                : PlacementMode.BottomEdgeAlignedLeft;

            menu.PlacementTarget = row ?? list;

            // **Open() takes the control the menu is ATTACHED to and refuses
            // any other**, so the row cannot be handed to it — the host is.
            // The anchor is PlacementTarget, set above, and it does reach the
            // popup: measured on a real Menu-key press in a headless
            // MainWindow, with the fourth row focused, the popup's own
            // PlacementTarget came back as that ListBoxItem, bounds 0,90 by
            // 1185x30 — the fourth 30px row.
            menu.Open(host);
            return;
        }
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
