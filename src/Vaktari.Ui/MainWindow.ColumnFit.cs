using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// A double-click on a heading's edge: the column to its left fitted to its
/// widest entry, the way Explorer's is. The measuring is ColumnFitter's,
/// reached through the tab (PaneViewModel.FitColumn), which is the same road
/// *Size all columns to fit* takes from the menus and the palette.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// **The second press of a double-click has already started a drag** by
    /// the time this runs — the Thumb raises DragStarted on every press, and
    /// Avalonia raises DoubleTapped on the second one after it — so the drag
    /// is cancelled here before anything else. Left running, a wobble between
    /// the second press and its release would be added to the fitted width,
    /// and the name would stay held where the drag froze it.
    ///
    /// Nothing else on the heading needs stopping: the press never reached a
    /// sort button, because the grip took it, and the window's own
    /// double-click acts on rows and the tab strip only. Marked handled for
    /// tidiness; no test can tell.
    /// </summary>
    private void OnColumnGripDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_columnDrag is { } drag) drag.Pane.IsResizingColumns = false;

        _columnDrag = null;
        _columnDragMoved = false;

        if (sender is not Thumb { DataContext: PaneViewModel pane, Tag: string tag }) return;

        e.Handled = true;
        pane.FitColumn(Enum.Parse<DetailsColumn>(tag));
    }
}
