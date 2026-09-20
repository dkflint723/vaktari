using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The edge-scroll timer has two callers, and only one of them owns a band.
///
/// **A file drag emptied the selection of whatever listing it rested over.**
/// The timer inside AutoScroll is armed by UpdateBand for a rubber band and by
/// DragScroll for a FILE drag — MainWindow.DragDrop.cs says as much in its own
/// comment, that it borrows the band's timer, and argues the two can never run
/// at once. True, and it was not the arming that was wrong: the TICK re-applied
/// the band unconditionally, against <c>_bandRect</c>, which only UpdateBand
/// ever writes and nothing ever resets.
///
/// So on the drag path it applied a rectangle from some earlier band, or, on a
/// window where none had been drawn, an empty one. An empty rect intersects no
/// row, ApplyBand wants nothing, and its last loop removes every row it did not
/// want. Rest a drag near a listing's top or bottom edge until it scrolls and
/// that listing's selection went. Dragging inside the window it is the source's
/// own rows that vanish, and OnDragOver reaches DragScroll with no
/// internal-drag gate, so a drag in from the desktop does it too.
///
/// Driven through ReapplyBandAfterScroll rather than by waiting on a 16ms
/// DispatcherTimer, because the rule is which caller owns the band — not
/// whether a timer fires. A test that waits for a tick is a test that fails on
/// a busy machine.
/// </summary>
public sealed class BandScrollTests : OwnedViewModels
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// **The precondition is asserted first**, so this cannot pass because
    /// nothing was selected to begin with — which is exactly the state the bug
    /// produced.
    /// </summary>
    [AvaloniaFact]
    public async Task A_scroll_with_no_band_in_progress_leaves_the_selection_alone()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-bandscroll-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(root, name), name);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            await shell.ActiveTab!.NavigateAsync(root);
            Settle();

            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Settle();

            var pane = shell.ActiveTab;

            Assert.Equal(3, pane.Entries.Count);

            var list = window.GetVisualDescendants().OfType<ListBox>()
                .First(l => l.IsVisible
                            && ReferenceEquals(l.DataContext, pane)
                            && l.SelectionMode.HasFlag(SelectionMode.Multiple));

            var rows = pane.Entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

            list.SelectedItems!.Add(rows[0]);
            list.SelectedItems!.Add(rows[1]);
            Settle();

            Assert.Equal(2, list.SelectedItems!.Count);

            // No band has ever been drawn in this window, which is the state a
            // file drag leaves: _bandKept null, _bandRect empty. This is the
            // call the scroll tick makes.
            window.ReapplyBandAfterScroll(list);
            Settle();

            Assert.Equal(2, list.SelectedItems!.Count);
            Assert.Contains(rows[0], list.SelectedItems!.Cast<object>());
            Assert.Contains(rows[1], list.SelectedItems!.Cast<object>());
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }
}
