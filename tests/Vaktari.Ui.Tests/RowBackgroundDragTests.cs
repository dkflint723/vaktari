using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What pressing a details row's background means.
///
/// **Building a selection and then reaching for it destroyed the selection.**
/// The gaps around the Size and Date text are row background, and those gaps
/// are most of the width of both columns because the text is short — so
/// pressing one started a rubber band and cleared everything. Explorer drags
/// the whole row.
///
/// The rule now depends on whether the row is already picked out, which is what
/// makes two readings of the same pixel unambiguous: on something selected it
/// means "take these", and on something not it means "start again here".
/// </summary>
public sealed class RowBackgroundDragTests
{
    /// <summary>
    /// A details-shaped list: rows that span its whole width, each holding a
    /// short label with background either side of it.
    /// </summary>
    private static (ListBox List, Window Window) Build()
    {
        var list = new ListBox
        {
            Width = 400,
            ItemsSource = new[] { "a.txt", "b.txt", "c.txt" },
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<string>(
                (_, _) => new Border
                {
                    Background = Avalonia.Media.Brushes.Transparent,
                    Child = new TextBlock { Width = 40 },
                },
                supportsRecycling: true),
        };

        var window = new Window { Content = list, Width = 400, Height = 300 };

        window.Show();
        window.UpdateLayout();

        return (list, window);
    }

    private static Control Row(ListBox list, int index)
        => (Control)list.ContainerFromIndex(index)!;

    /// <summary>The whole finding: a press on a selected row's background is a
    /// drag, not a band.</summary>
    [AvaloniaFact]
    public void A_selected_rows_background_does_not_start_a_band()
    {
        var (list, window) = Build();

        try
        {
            list.SelectedIndex = 0;
            window.UpdateLayout();

            var background = Row(list, 0).GetVisualDescendants().OfType<Border>().First();

            Assert.Null(MainWindow.ListForEmptySpace(background));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **And an unselected one still does**, which is what keeps rubber-band
    /// selection reachable: a details listing that fills the window leaves no
    /// empty space beside its rows, so their background is the only place left
    /// to start one.
    /// </summary>
    [AvaloniaFact]
    public void An_unselected_rows_background_still_starts_one()
    {
        var (list, window) = Build();

        try
        {
            list.SelectedIndex = 0;
            window.UpdateLayout();

            var background = Row(list, 2).GetVisualDescendants().OfType<Border>().First();

            Assert.Same(list, MainWindow.ListForEmptySpace(background));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The name and the icon were never band-able and still are not — grabbing
    /// one of those has always meant "take this file", selected or otherwise.
    /// </summary>
    [AvaloniaFact]
    public void The_name_is_a_drag_whether_or_not_the_row_is_picked()
    {
        var (list, window) = Build();

        try
        {
            list.SelectedIndex = 0;
            window.UpdateLayout();

            foreach (var index in new[] { 0, 2 })
            {
                var name = Row(list, index).GetVisualDescendants().OfType<TextBlock>().First();

                Assert.Null(MainWindow.ListForEmptySpace(name));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Empty space below the rows is a band, as it always was.</summary>
    [AvaloniaFact]
    public void The_space_below_the_rows_is_still_a_band()
    {
        var (list, window) = Build();

        try
        {
            list.SelectedIndex = 0;
            window.UpdateLayout();

            Assert.Same(list, MainWindow.ListForEmptySpace(list));
        }
        finally
        {
            window.Close();
        }
    }
}
