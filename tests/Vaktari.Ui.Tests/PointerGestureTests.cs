using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a press means, and the five ways it used to mean the wrong thing.
///
/// Every one of these is a gesture a Windows or Dolphin user makes in the first
/// ten minutes, and four of the five moved or destroyed files:
///
///  - Ctrl+click to extend a selection LAUNCHED the file it landed on.
///  - Right-clicking the blank half of a selected row cleared the selection
///    before the menu opened, so the Delete you then chose took one file.
///  - Pressing the scrollbar cleared the selection and dragged a rubber band.
///  - A twitch while pressing the column heading or the tab strip started
///    dragging the selection, and a drop moved real files.
///  - The row you double-clicked stayed remembered as "clicked once" forever,
///    so a single click on it later re-opened it.
///
/// The empty-space tests build a real, shown ListBox and pull the scrollbar out
/// of the theme's own template, because that walk up the visual tree IS the
/// fault — a hand-made stand-in would have proved nothing about where the
/// scrollbar actually sits. The two-click tests model the tap state machine
/// instead: it is one field and three branches, and synthesising taps would
/// test Avalonia's gesture recognizer rather than the rule.
/// </summary>
public sealed class PointerGestureTests
{
    // ---- the empty-space decision (ranks 2 and 3) ---------------------------
    //
    // ListForEmptySpace walks the VISUAL tree, so these build a real one: a
    // window with a real ListBox, shown and laid out so the theme supplies the
    // scrollbar the fault was actually about.

    private static ListBox? EmptySpaceFor(object? source)
        => (ListBox?)typeof(MainWindow)
            .GetMethod("ListForEmptySpace", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source]);

    private static (Window Window, ListBox List) Shown()
    {
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = Enumerable.Range(0, 200).Select(i => "row " + i).ToList(),
        };

        var window = new Window { Content = list, Width = 300, Height = 120 };

        window.Show();
        window.Measure(new Avalonia.Size(300, 120));
        window.Arrange(new Avalonia.Rect(0, 0, 300, 120));
        Dispatcher.UIThread.RunJobs();

        return (window, list);
    }

    /// <summary>
    /// **The scrollbar lives inside the list**, so walking up from it reached
    /// the ListBox and called it empty space: pressing the scrollbar cleared
    /// the selection, and dragging the thumb drew a rubber band down the side
    /// of the listing while the view scrolled under it.
    /// </summary>
    [AvaloniaFact]
    public void The_scrollbar_is_not_empty_space()
    {
        var (window, list) = Shown();

        var bar = list.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault();

        Assert.True(bar is not null, "the list grew no scrollbar, so this proves nothing");
        Assert.Null(EmptySpaceFor(bar));

        window.Close();
    }

    /// <summary>The thumb you actually grab, which is a child of the bar.</summary>
    [AvaloniaFact]
    public void Neither_is_the_thumb_you_drag()
    {
        var (window, list) = Shown();

        var thumb = list.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();

        Assert.True(thumb is not null, "no thumb to test");
        Assert.Null(EmptySpaceFor(thumb));

        window.Close();
    }

    /// <summary>The list itself still is, or clicking below the rows would stop
    /// meaning "never mind".</summary>
    [AvaloniaFact]
    public void The_list_itself_is_still_empty_space()
    {
        var (window, list) = Shown();

        Assert.NotNull(EmptySpaceFor(list));

        window.Close();
    }

    /// <summary>And a row's own label is still not: grabbing it means "take
    /// this file", so a band must not start there.</summary>
    [AvaloniaFact]
    public void A_row_label_is_still_not_empty_space()
    {
        var (window, list) = Shown();

        var label = list.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>())
            .FirstOrDefault();

        Assert.True(label is not null, "no row label to test");
        Assert.Null(EmptySpaceFor(label));

        window.Close();
    }

    // ---- the two-click open (ranks 1 and 7) ---------------------------------
    //
    // OnTapped's state is one field, so these drive it the way the handler
    // does: remember a row, then ask what the next tap should do.

    private sealed class Taps
    {
        private string? _last;

        /// <summary>Returns true when this tap opens the row.</summary>
        public bool Tap(string path, KeyModifiers modifiers)
        {
            // The shape of the fixed OnTapped, in order.
            if (modifiers is not KeyModifiers.None)
            {
                _last = null;
                return false;
            }

            if (_last == path)
            {
                _last = null;
                return true;
            }

            _last = path;
            return false;
        }

        /// <summary>What TryOpen now does when a row is opened by any route.</summary>
        public void Opened() => _last = null;
    }

    [Fact]
    public void Two_plain_clicks_on_one_row_open_it()
    {
        var taps = new Taps();

        Assert.False(taps.Tap("a.txt", KeyModifiers.None));
        Assert.True(taps.Tap("a.txt", KeyModifiers.None));
    }

    /// <summary>
    /// **Ctrl+click is a selection gesture.** Two of them on the same row used
    /// to open it, so building a selection launched whatever it passed over.
    /// </summary>
    [Fact]
    public void Ctrl_clicking_a_row_twice_never_opens_it()
    {
        var taps = new Taps();

        Assert.False(taps.Tap("a.txt", KeyModifiers.Control));
        Assert.False(taps.Tap("a.txt", KeyModifiers.Control));
        Assert.False(taps.Tap("a.txt", KeyModifiers.Control));
    }

    [Fact]
    public void Shift_clicking_a_row_twice_never_opens_it()
    {
        var taps = new Taps();

        Assert.False(taps.Tap("a.txt", KeyModifiers.Shift));
        Assert.False(taps.Tap("a.txt", KeyModifiers.Shift));
    }

    /// <summary>
    /// And a modified click cannot supply half of a pair: click once plainly,
    /// Ctrl+click to add something, then click the first row again — that is
    /// two plain clicks separated by a selection gesture, not a double-click.
    /// </summary>
    [Fact]
    public void A_modified_click_in_between_does_not_complete_a_pair()
    {
        var taps = new Taps();

        Assert.False(taps.Tap("a.txt", KeyModifiers.None));
        Assert.False(taps.Tap("b.txt", KeyModifiers.Control));
        Assert.False(taps.Tap("a.txt", KeyModifiers.None));
    }

    /// <summary>
    /// **The row that was opened is forgotten.** There is no time limit on the
    /// pair, so a folder opened by double-click stayed remembered as "clicked
    /// once" — go Back, click it once to rename it, and it opened again.
    /// </summary>
    [Fact]
    public void A_row_that_was_opened_does_not_reopen_on_one_later_click()
    {
        var taps = new Taps();

        taps.Tap("folder", KeyModifiers.None);
        Assert.True(taps.Tap("folder", KeyModifiers.None));

        taps.Opened();

        Assert.False(taps.Tap("folder", KeyModifiers.None));
    }

    /// <summary>Clicking a different row still resets, which is what keeps the
    /// pair from spanning the whole listing.</summary>
    [Fact]
    public void Clicking_another_row_resets_the_pair()
    {
        var taps = new Taps();

        Assert.False(taps.Tap("a.txt", KeyModifiers.None));
        Assert.False(taps.Tap("b.txt", KeyModifiers.None));
        Assert.False(taps.Tap("a.txt", KeyModifiers.None));
    }
}
