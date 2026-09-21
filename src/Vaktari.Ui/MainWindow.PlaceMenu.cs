using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The sidebar place row's right-click menu.
///
/// Two handlers, and neither is called from any C# in the repository — the
/// markup binds one to the row's Button and the other to the ContextMenu
/// inside the row's DataTemplate. That is the whole file.
///
/// **This is the menu the DataContext claim is actually true of.** A
/// ContextMenu is its own popup root; this one is declared inside a
/// per-row DataTemplate, and asking its sender for a DataContext returned null
/// every single time, which cancelled the menu for every row — the pinned ones
/// and the ejectable drives alike. So the sidebar menu had never opened for
/// anything. PlacementTarget is no help either; it is still null when Opening
/// fires. The answer is the pair: ContextRequested fires on the Button, where
/// the DataContext is real, and hands the row over before Opening asks for it.
///
/// The listing's menu is NOT like this and its own file says so at length: it
/// is declared with an x:DataType against the pane group and its opening
/// handler reads menu.DataContext successfully. The two menus were filed apart
/// for that reason, and this file exists so the true-of-one sentence sits
/// beside the one it is true of.
///
/// What Opening decides is only whether there is a place behind the menu at
/// all. Each entry decides its own visibility in the markup, which is why the
/// menu has grown from one row to nine without this pair changing.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Keeps a sidebar place from opening a menu with nothing in it.
    ///
    /// **Avalonia opens a ContextMenu whether or not any child is visible**,
    /// and cancelling here is the only hook that stops the popup rather than
    /// its contents. So the question is whether there is a real place behind
    /// the menu at all — which is all this asks, and all it has asked since
    /// the menu stopped being about one entry.
    ///
    /// **It used to be per-ENTRY, and that is the history worth keeping.** The
    /// menu began with a single row, "Remove from places", which means nothing
    /// on the places the user did not put there — Home, Documents, the drives,
    /// the shares — so every one of those popped a 2px sliver of menu
    /// background at the cursor, which on a fresh install with no pins is every
    /// row in the sidebar. Eject arrived next and broke the fix: the rule then
    /// cancelled on any row that was not user-pinned, which is every DRIVE row,
    /// precisely the ones eject exists for. It failed silently, because a
    /// cancelled ContextMenu is not an error — it is a menu that never appears.
    ///
    /// The menu now carries nine entries and the gate is per-ROW rather than
    /// per-entry, which is why the seven added since needed nothing here. Each
    /// decides its own visibility in the markup. A new entry only has to come
    /// back to this method if it must appear on a row with no path — and then
    /// the question this asks is the one that has to change.
    /// </summary>
    private void OnPlaceMenuOpening(object? sender, CancelEventArgs e)
    {
        // **A ContextMenu is its own popup root and inherits no DataContext.**
        // This asked `sender` for one, got null every single time, and so
        // cancelled the menu for EVERY row — the pinned ones and the ejectable
        // drives it was written to allow included. The sidebar menu had never
        // opened for anything, and had it opened, CanEject, IsUserPinned and
        // every CommandParameter inside it would have bound against nothing.
        //
        // PlacementTarget is no help either: it is still null when Opening
        // fires. OnPlaceContextRequested below hands the row over first, from
        // the button, where the DataContext is real.
        if (sender is ContextMenu { DataContext: PlaceItemViewModel row }
            && row.Path.Length > 0) return;

        e.Cancel = true;
    }

    /// <summary>
    /// Gives a sidebar row's menu the row, before it opens.
    ///
    /// The button is the only place the DataContext is real — the menu is a
    /// separate popup root and inherits nothing — and ContextRequested is the
    /// one event raised on the button while there is still time to act.
    /// </summary>
    private void OnPlaceContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: PlaceItemViewModel row, ContextMenu: { } menu })
            menu.DataContext = row;
    }
}
