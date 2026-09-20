using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Vaktari.Ui;

/// <summary>
/// Moving the tab strip sideways when there are more tabs than fit.
///
/// The two chevrons, the nudge they make, the scroll state that decides
/// whether either is offered, and bringing a newly chosen tab into view. All
/// of it is reached from markup — four attributes and nothing else in C# calls
/// in — and all of its arithmetic is in the separate TabStripScroll class,
/// which has tests of its own.
///
/// One thing here is not tab scrolling and cannot leave: choosing a tab also
/// refreshes the window title, so OnTabStripSelectionChanged calls
/// RefreshTitle. Both halves are one markup-wired handler, and a test pins
/// that call to this method by name, so the title stays named here rather than
/// pretended away.
///
/// **The wheel over the strip is NOT here**, though it scrolls the same strip
/// and its comment is written about it. ScrolledSideways is a routing rule for
/// a wheel gesture: it hardcodes its own step, touches none of TabStripScroll,
/// and is reached only from the window's wheel handler. It travels with the
/// wheel, which is where the gesture is decided.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The tab ScrollViewer the pressed chevron flanks. By tree rather than by
    /// name, because the strip lives in a template stamped once per pane and a
    /// name would find only one of them.
    /// </summary>
    private static ScrollViewer? TabScrollerFor(object? sender)
    {
        for (var visual = sender as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is DockPanel dock)
                return dock.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        return null;
    }

    private void OnScrollTabsLeft(object? sender, RoutedEventArgs e) => NudgeTabs(sender, -1);

    private void OnScrollTabsRight(object? sender, RoutedEventArgs e) => NudgeTabs(sender, +1);

    private static void NudgeTabs(object? sender, int direction)
    {
        if (TabScrollerFor(sender) is not { } scroller) return;

        scroller.Offset = scroller.Offset.WithX(TabStripScroll.Toward(
            scroller.Offset.X, scroller.Viewport.Width, scroller.Extent.Width, direction));
    }

    /// <summary>
    /// Keeps the chevrons truthful. ScrollChanged fires for offset, extent and
    /// viewport alike, so opening a tab, closing one, resizing the window and
    /// scrolling all pass through here — there is no state to get stale.
    /// </summary>
    private void OnTabStripScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;

        DockPanel? dock = null;
        for (var visual = (Visual?)scroller; visual is not null; visual = visual.GetVisualParent())
            if (visual is DockPanel found) { dock = found; break; }

        if (dock is null) return;

        var overflows = TabStripScroll.Overflows(scroller.Extent.Width, scroller.Viewport.Width);

        foreach (var chevron in dock.GetVisualDescendants().OfType<RepeatButton>())
        {
            if (chevron.Classes.Contains("tab-nudge-left"))
            {
                chevron.IsVisible = overflows;
                chevron.IsEnabled = TabStripScroll.CanGoLeft(scroller.Offset.X);
            }
            else if (chevron.Classes.Contains("tab-nudge-right"))
            {
                chevron.IsVisible = overflows;
                chevron.IsEnabled = TabStripScroll.CanGoRight(
                    scroller.Offset.X, scroller.Viewport.Width, scroller.Extent.Width);
            }
        }
    }

    /// <summary>
    /// Scrolls the tab just selected into view. Ctrl+Tab can land on a tab the
    /// strip has scrolled past, and a selection you cannot see reads as the
    /// keystroke doing nothing. Posted, because at selection time the container
    /// may not have been arranged yet.
    /// </summary>
    private void OnTabStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Primitives.TabStrip strip
            || strip.SelectedItem is not { } selected) return;

        // Switching tabs changes the folder on screen as surely as navigating
        // does, and the title has to say so.
        RefreshTitle();

        Dispatcher.UIThread.Post(
            () => strip.ContainerFromItem(selected)?.BringIntoView(),
            DispatcherPriority.Loaded);
    }
}
