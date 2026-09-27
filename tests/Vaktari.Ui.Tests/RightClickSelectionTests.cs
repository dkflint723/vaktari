using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Does a right-click select the row it lands on?**
///
/// The item menu rests on this. It opens for a right-click on a row and acts
/// on the selection, and its rows are gated on what is selected — so if a
/// right-click on an unselected file did not select it first, the menu would
/// open about the PREVIOUS selection, or with Open, Copy and Cut hidden, at
/// exactly the moment somebody is asking for them. (It used to be one menu for
/// a row and the empty space below it, and this was what told the two apart;
/// where the click landed does that now — ListingMenusTests.)
///
/// Asserted rather than assumed. This is Avalonia's own behaviour, not
/// something this application writes, so it is precisely the kind of thing that
/// is true until a framework upgrade quietly makes it false.
/// </summary>
public sealed class RightClickSelectionTests
{
    /// <summary>
    /// **No Dispatcher.RunJobs here, deliberately.** Pumping the dispatcher from
    /// inside a test is re-entrant: every headless test in this assembly runs on
    /// one UI thread, xunit schedules classes in parallel, and a pump lets
    /// another test's body run in the middle of this one. ThemeApplierTests
    /// asserts on Application.Current.RequestedThemeVariant — application-wide
    /// state — so an interleaved test there reads a variant a different test
    /// set. The suite failed twice that way, both times unreproducible
    /// afterwards, which is what a race looks like from the outside.
    ///
    /// Measure and Arrange are enough: they are synchronous, and a container
    /// only needs a laid-out parent to have a position worth clicking.
    /// </summary>
    private static (Window Window, ListBox List) Build()
    {
        var list = new ListBox
        {
            ItemsSource = new[] { "one", "two", "three" },
            Width = 200,
            Height = 300,
        };

        var window = new Window { Content = list, Width = 300, Height = 400 };
        window.Show();

        // Layout has to have run before an item has a position to click.
        window.Measure(new Size(300, 400));
        window.Arrange(new Rect(0, 0, 300, 400));

        return (window, list);
    }

    private static Point CentreOf(Control container, Visual relativeTo) =>
        container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), relativeTo)
        ?? new Point(0, 0);

    [AvaloniaFact]
    public void A_right_click_selects_the_row_under_the_pointer()
    {
        var (window, list) = Build();

        var container = (Control)list.ContainerFromIndex(1)!;
        var point = CentreOf(container, window);

        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);

        Assert.Equal("two", list.SelectedItem);

        window.Close();
    }

    /// <summary>
    /// And the other half: a right-click on empty space below the rows must NOT
    /// invent a selection — it opens the folder's menu, and a row picked up on
    /// the way would be a selection nobody made.
    /// </summary>
    [AvaloniaFact]
    public void A_right_click_on_empty_space_selects_nothing()
    {
        var (window, list) = Build();

        window.MouseDown(new Point(100, 280), MouseButton.Right);
        window.MouseUp(new Point(100, 280), MouseButton.Right);

        Assert.Null(list.SelectedItem);

        // Closed rather than left standing: this application is shared with
        // every other headless test in the assembly.
        window.Close();
    }
}
