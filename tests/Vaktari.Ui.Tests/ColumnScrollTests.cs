using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The details columns scroll sideways once nothing else makes them fit**,
/// headings and rows as one (ColumnScroll). Before, a column dragged to the
/// pane's edge or a pane too narrow even with the name at its floor cut the
/// last columns off, with no way to see them but to narrow something.
///
/// Measured where a person sees it, in a real MainWindow: the rows' and the
/// headings' scrollers, the column edges in the window's pixels, and what the
/// keyboard, the wheel and navigation do to the offset.
/// </summary>
public sealed class ColumnScrollTests : ColumnWindow
{
    /// <summary>Gives the name a width of its own wider than the pane, so the
    /// columns overflow.</summary>
    private static void Overflow(Window window, PaneViewModel pane, double name = 1400)
    {
        pane.ColumnWidths = pane.ColumnWidths with { Name = name, Span = 0 };
        Settle(window);

        Assert.True(pane.ColumnsOverflow, "the columns fit, so nothing here scrolls");
    }

    /// <summary>
    /// **Only when they do not fit.** At the designed widths the rows have no
    /// sideways axis at all. A name that FILLS asks for nothing: a 300-character
    /// name is trimmed, as it always was, rather than measured at its whole
    /// width and scrolled to. Only a row still too wide with the name at its
    /// width — or at its floor — scrolls, and then the rows are exactly as wide
    /// as the columns, and the headings as wide as the rows.
    /// </summary>
    [AvaloniaFact]
    public async Task The_columns_scroll_only_when_they_do_not_fit()
    {
        Fill(20);
        File.WriteAllText(Path.Combine(Root, new string('w', 200) + ".txt"), "w");

        var (window, _, pane) = await Open();
        var rows = Rows(window, pane);

        Assert.False(pane.ColumnsOverflow);
        Assert.True(double.IsNaN(pane.DetailsRowWidth));
        Assert.Equal(ScrollBarVisibility.Disabled, rows.HorizontalScrollBarVisibility);
        Assert.Equal(rows.Viewport.Width, rows.Extent.Width, 0.5);

        Overflow(window, pane);

        Assert.Equal(ScrollBarVisibility.Auto, rows.HorizontalScrollBarVisibility);
        Assert.Equal(pane.DetailsRowWidth, rows.Extent.Width, 0.5);
        Assert.Equal(rows.Extent.Width, Headings(window, pane).Extent.Width);
        Assert.Equal(rows.Viewport.Width, Headings(window, pane).Viewport.Width);

        // And back: the axis off, and the offset with it.
        rows.Offset = new Vector(200, 0);
        window.UpdateLayout();

        pane.ResetColumnWidthsCommand.Execute(null);
        Settle(window);

        Assert.False(pane.ColumnsOverflow);
        Assert.Equal(0, rows.Offset.X);
        Assert.Equal(0, Headings(window, pane).Offset.X);
    }

    /// <summary>
    /// **A pane too narrow even for the name's floor scrolls rather than
    /// cutting the last columns off** — the third of the three stages after
    /// the room after the last column and the name giving way.
    /// </summary>
    [AvaloniaFact]
    public async Task A_narrower_pane_takes_the_room_then_the_name_then_scrolls()
    {
        Fill(10);

        var (window, _, pane) = await Open(width: 2400);

        pane.ColumnWidths = pane.ColumnWidths with { Name = 600, Span = VisibleRow(window, pane), Size = 400, Modified = 400 };
        Settle(window);

        var wide = Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0];

        // Room left after the last column: the name keeps its width.
        window.Width = 2000;
        Settle(window);
        Assert.Equal(wide, Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0], 0.75);
        Assert.False(pane.ColumnsOverflow);

        // Narrower: the name gives way, and nothing scrolls yet.
        window.Width = 1500;
        Settle(window);
        Assert.True(pane.NameGive > 0, "the name did not give way");
        Assert.False(pane.ColumnsOverflow, "it scrolled before the name had given way");

        // Narrower than the columns with the name at its floor: they scroll.
        window.Width = 900;
        Settle(window);
        Assert.True(pane.ColumnsOverflow, "the last columns are cut off rather than scrolled to");
        Assert.Equal(PaneScale.NameMin * pane.TextScale, Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0], 0.75);
        Assert.Equal(0, Worst(window, pane), 0.6);
    }

    /// <summary>
    /// **Resizing the window settles.** The give and the row's width are
    /// written as the heading is laid out and read by the next pass, so a
    /// change can take one more pass to land — but the third pass must change
    /// nothing the second did not, at 115% device scaling (where the round-2
    /// and round-3 jitter was seen), and the overflow must turn on once and off
    /// once across a sweep, never back and forth.
    /// </summary>
    [AvaloniaFact]
    public async Task Resizing_the_window_settles()
    {
        Fill(10);

        var (window, _, pane) = await Open(scaling: 1.15, width: 1300);

        pane.ColumnWidths = pane.ColumnWidths with { Name = 400, Span = 1270, Size = 300 };
        Settle(window);

        var flips = 0;
        var last = pane.ColumnsOverflow;

        void Step(double width)
        {
            window.Width = width;

            for (var pass = 0; pass < 2; pass++)
            {
                Pump();
                window.UpdateLayout();
            }

            var second = (pane.NameGive, pane.DetailsRowWidth, pane.ColumnsOverflow, Rows(window, pane).Offset);

            Pump();
            window.UpdateLayout();

            var third = (pane.NameGive, pane.DetailsRowWidth, pane.ColumnsOverflow, Rows(window, pane).Offset);

            Assert.True(second.Equals(third), $"at {width}: pass 2 {second} and pass 3 {third}");

            for (var more = 0; more < 3; more++)
            {
                Pump();
                window.UpdateLayout();

                Assert.Equal(third.ColumnsOverflow, pane.ColumnsOverflow);
            }

            if (pane.ColumnsOverflow != last) flips++;
            last = pane.ColumnsOverflow;

            Assert.True(Worst(window, pane) < 0.6, $"at {width}: rows and headings apart by {Worst(window, pane)}");
        }

        for (var width = 1300.0; width >= 1000; width -= 1) Step(width);
        for (var width = 1000.0; width <= 1300; width += 1) Step(width);

        Assert.Equal(2, flips);
    }

    /// <summary>
    /// **Back to the left in another folder; kept in this one.** Another
    /// folder's rows are other rows, read by their names first. A refresh, a
    /// sort and a folder opened in place are the same folder, and a watcher
    /// must never jerk the view sideways under somebody reading a column.
    /// </summary>
    [AvaloniaFact]
    public async Task Navigating_resets_the_offset_and_refreshing_keeps_it()
    {
        Fill(30);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var rows = Rows(window, pane);

        rows.Offset = new Vector(300, 0);
        window.UpdateLayout();

        await pane.RefreshAsync();
        Settle(window);
        Assert.Equal(300, rows.Offset.X);

        pane.SortByCommand.Execute("size");
        Settle(window);
        Assert.Equal(300, rows.Offset.X);
        pane.GroupBy = GroupMode.Kind;
        Settle(window);
        Assert.Equal(300, rows.Offset.X);

        pane.GroupBy = GroupMode.None;
        Settle(window);

        await pane.ToggleExpandAsync(pane.DetailsEntries.First(e => e.IsDirectory));
        Settle(window);
        Assert.Equal(300, rows.Offset.X);

        await pane.NavigateAsync(Path.Combine(Root, "folder0"));
        Settle(window);
        Assert.Equal(0, rows.Offset.X);
        Assert.Equal(0, Headings(window, pane).Offset.X);
    }

    /// <summary>
    /// **Each half of a split, and each tab, keeps its own offset.** Every
    /// pane has its own heading and its own list, paired with each other and
    /// with nothing else.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_tab_and_each_half_keeps_its_own_offset()
    {
        Fill(30);

        var (window, shell, left) = await Open(split: true);
        var right = shell.Right!.ActiveTab!;

        Overflow(window, left);
        Overflow(window, right);

        Rows(window, left).Offset = new Vector(250, 0);
        window.UpdateLayout();

        Assert.Equal(250, Headings(window, left).Offset.X);
        Assert.Equal(0, Rows(window, right).Offset.X);
        Assert.Equal(0, Headings(window, right).Offset.X);

        shell.ActivateGroup(shell.Left);
        shell.Left.NewTabHereCommand.Execute(null);
        Settle(window);

        shell.Left.ActiveTab = left;
        Settle(window);

        Assert.Equal(250, Rows(window, left).Offset.X);
        Assert.Equal(250, Headings(window, left).Offset.X);
    }

    /// <summary>
    /// **A row brought into view keeps the horizontal offset.** Rows are as
    /// wide as the columns, and the scroller brings a rectangle wider than
    /// itself into view by lining up its left edge: arrowing down past the
    /// bottom and a selection made from code both threw the view back to the
    /// left (measured: 300 to 0).
    /// </summary>
    [AvaloniaFact]
    public async Task Keyboard_selection_keeps_the_horizontal_offset()
    {
        Fill(80);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var list = List(window, pane);
        var rows = Rows(window, pane);

        // The last row on screen, focused, with the view 300 to the right.
        var bottom = RowGrids(window, pane)
            .Where(r => r.Grid.TranslatePoint(default, rows)!.Value.Y + r.Grid.Bounds.Height <= rows.Viewport.Height)
            .OrderBy(r => r.Grid.TranslatePoint(default, rows)!.Value.Y)
            .Last().Entry;

        pane.SelectedEntry = bottom;
        Settle(window);
        list.ContainerFromItem(bottom)!.Focus();
        Settle(window);

        rows.Offset = new Vector(300, rows.Offset.Y);
        window.UpdateLayout();

        for (var step = 0; step < 3; step++)
        {
            var y = rows.Offset.Y;

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Settle(window);

            Assert.Equal(300, rows.Offset.X);

            if (step == 2) Assert.True(rows.Offset.Y > y || pane.SelectedEntry == pane.DetailsEntries.Last(),
                                       $"Down never went past the bottom, so this measured nothing: y {y} -> {rows.Offset.Y}, selected {pane.SelectedEntry?.Name} from {bottom.Name}, focus {window.FocusManager?.GetFocusedElement()?.GetType().Name}, extent {rows.Extent} viewport {rows.Viewport}");
        }

        pane.SelectedEntry = pane.DetailsEntries.Last();
        Settle(window);

        Assert.Equal(300, rows.Offset.X);
        Assert.Equal(300, Headings(window, pane).Offset.X);
    }

    /// <summary>
    /// **Type-ahead brings the name it found back on screen** — typing a name
    /// is looking for names — and leaves the offset alone when the name's
    /// start is already showing.
    /// </summary>
    [AvaloniaFact]
    public async Task Type_ahead_brings_the_name_back_when_it_is_scrolled_out()
    {
        Fill(80);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var list = List(window, pane);
        var rows = Rows(window, pane);

        list.ContainerFromItem(pane.DetailsEntries.First())!.Focus();
        Settle(window);

        rows.Offset = new Vector(600, 0);
        window.UpdateLayout();

        window.KeyTextInput("file-07");
        Settle(window);

        Assert.StartsWith("file-07", pane.SelectedEntry?.Name);
        Assert.Equal(0, rows.Offset.X);

        await Task.Delay(1100);

        rows.Offset = new Vector(10, 0);
        window.UpdateLayout();

        window.KeyTextInput("file-05");
        Settle(window);

        Assert.StartsWith("file-05", pane.SelectedEntry?.Name);
        Assert.Equal(10, rows.Offset.X);
    }

    /// <summary>
    /// **F2 still brings the rename box into view sideways**: only a request
    /// whose target is a ROW is kept from moving the view left or right.
    /// </summary>
    [AvaloniaFact]
    public async Task F2_brings_the_rename_box_into_view()
    {
        Fill(20);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var rows = Rows(window, pane);

        pane.SelectedEntry = pane.DetailsEntries.First(e => !e.IsDirectory);
        Settle(window);

        rows.Offset = new Vector(rows.Extent.Width, 0);
        window.UpdateLayout();

        Assert.True(rows.Offset.X > 500, "not scrolled far enough to hide the name");

        pane.BeginRenameCommand.Execute(null);
        Settle(window);

        var box = window.GetVisualDescendants().OfType<TextBox>()
                        .Single(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);

        Assert.True(box.IsFocused);

        var left = box.TranslatePoint(default, rows)!.Value.X;

        Assert.True(left >= -0.5 && left < rows.Viewport.Width, $"the rename box is at {left}, off screen");

    }

    /// <summary>
    /// **Shift with the wheel and a touchpad's sideways swipe scroll the
    /// headings and the rows together**, over either of them.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("rows", "shift")]
    [InlineData("rows", "swipe")]
    [InlineData("headings", "shift")]
    [InlineData("headings", "swipe")]
    public async Task Shift_wheel_and_a_swipe_scroll_rows_and_headings_together(string over, string how)
    {
        Fill(30);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var rows = Rows(window, pane);

        rows.Offset = new Vector(200, 0);
        window.UpdateLayout();

        Visual target = over == "rows" ? List(window, pane) : Band(window, pane);
        var at = target.TranslatePoint(new Point(400, target.Bounds.Height / 2), window)!.Value;

        window.MouseMove(at);
        window.MouseWheel(at,
                          how == "shift" ? new Vector(0, -1) : new Vector(-1, 0),
                          how == "shift" ? RawInputModifiers.Shift : RawInputModifiers.None);
        Settle(window);

        Assert.True(rows.Offset.X > 200, $"{how} over the {over} did not scroll the rows right ({rows.Offset.X})");
        Assert.Equal(rows.Offset.X, Headings(window, pane).Offset.X);
        Assert.True(Worst(window, pane) < 0.6);
    }

    /// <summary>
    /// **A plain wheel over the headings does not scroll sideways.** Their
    /// scroller is exactly the shape the tab strip's sideways rule takes the
    /// wheel for, and it turned a wheel over the listing into a 64-pixel jump
    /// sideways (measured).
    /// </summary>
    [AvaloniaFact]
    public async Task A_plain_wheel_over_the_headings_does_not_scroll_sideways()
    {
        Fill(30);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var rows = Rows(window, pane);

        rows.Offset = new Vector(200, 0);
        window.UpdateLayout();

        var band = Band(window, pane);
        var at = band.TranslatePoint(new Point(400, band.Bounds.Height / 2), window)!.Value;

        window.MouseMove(at);
        window.MouseWheel(at, new Vector(0, -1));
        Settle(window);

        Assert.Equal(200, rows.Offset.X);
        Assert.Equal(200, Headings(window, pane).Offset.X);
    }

    /// <summary>
    /// **A sort heading reached with the keyboard while it is off the edge
    /// brings the rows with it.** The heading's scroller brings the button into
    /// view itself, and the rows follow — or the headings would scroll away
    /// from their columns.
    /// </summary>
    [AvaloniaFact]
    public async Task Focusing_an_off_screen_heading_scrolls_the_rows_with_it()
    {
        Fill(30);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        // From a row, Shift+Tab goes to the list and then to the last sort
        // heading, which is the one off the right edge.
        List(window, pane).ContainerFromItem(pane.DetailsEntries.First())!.Focus();
        Settle(window);

        for (var press = 0; press < 4 && window.FocusManager?.GetFocusedElement() is not Button; press++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
            Settle(window);
        }

        var focused = Assert.IsType<Button>(window.FocusManager?.GetFocusedElement());

        Assert.Equal("created", focused.CommandParameter as string);
        Assert.True(Headings(window, pane).Offset.X > 0, "the heading did not bring the button into view");
        Assert.Equal(Headings(window, pane).Offset.X, Rows(window, pane).Offset.X);
        Assert.True(Worst(window, pane) < 0.6);
    }

    /// <summary>
    /// **The selection bar and the group headings stay at the left of what is
    /// on screen**, exactly on its edge — moved by the rows' offset as the
    /// scroller draws it, rounded to the device pixel, at 115% where an
    /// unrounded 0.45 sat the 3-pixel bar 0.42 off the edge.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.15)]
    public async Task The_selection_bar_and_group_heading_stay_at_the_left(double scaling)
    {
        Fill(30);

        var (window, _, pane) = await Open(scaling: scaling);

        pane.GroupBy = GroupMode.Kind;
        Settle(window);
        Overflow(window, pane);

        var list = List(window, pane);
        var rows = Rows(window, pane);
        var first = RowGrids(window, pane).First().Entry;

        pane.SelectedEntry = first;
        Settle(window);

        foreach (var x in new[] { 0.45, 123.4, 300 })
        {
            rows.Offset = new Vector(x, 0);
            window.UpdateLayout();

            var edge = rows.TranslatePoint(default, window)!.Value.X;
            var item = list.ContainerFromItem(first)!;

            var bar = item.GetVisualDescendants().OfType<Border>()
                          .Single(b => b.Width == 3 && b.IsVisible);
            var barAt = bar.TranslatePoint(default, window)!.Value.X;

            Assert.True(Math.Abs(barAt - edge) < 0.01, $"at {x}: the bar is at {barAt}, the pane's edge at {edge}");

            var heading = list.GetVisualDescendants().OfType<Button>()
                              .First(b => b.Classes.Contains("groupheading") && b.IsVisible);
            var headingAt = heading.TranslatePoint(default, window)!.Value.X;

            Assert.True(Math.Abs(headingAt - (edge + heading.Bounds.X)) < 0.01,
                        $"at {x}: the group heading is at {headingAt}, the pane's edge at {edge}; transform {(heading.RenderTransform as Avalonia.Media.TranslateTransform)?.X} {heading.RenderTransform?.GetType().Name} pinned {ColumnScroll.GetPinned(list)} bounds {heading.Bounds}");
        }
    }

    /// <summary>
    /// **A heading nobody can see says nothing about overflowing.** A tab that
    /// has never been shown in the List layout has a viewport of nothing, and
    /// read as a row the columns overflow entirely.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tab_never_shown_in_details_does_not_overflow()
    {
        Fill(5);

        var (window, shell, pane) = await Open();

        pane.View = Vaktari.Core.Session.ViewMode.Grid;
        Settle(window);

        shell.Left.NewTabHereCommand.Execute(null);
        Settle(window);

        // The new tab takes this one's layout, the grid, so its heading has
        // never been laid out with a width.
        var other = shell.Left.ActiveTab!;

        Assert.NotSame(pane, other);
        Assert.False(other.IsDetailsView, "the new tab is in the List layout, so this measures nothing");

        Assert.False(other.ColumnsOverflow);
        Assert.True(double.IsNaN(other.DetailsRowWidth));
        Assert.False(pane.ColumnsOverflow);
    }

    /// <summary>
    /// **A closed window's pair lets go.** The pair subscribes to its pane;
    /// a subscription that outlived the window would keep its controls alive
    /// and run a fit against controls nobody can see.
    /// </summary>
    [AvaloniaFact]
    public async Task A_closed_window_no_longer_answers_its_tabs()
    {
        Fill(5);

        var (window, _, pane) = await Open();

        var closed = 0;

        window.Closed += (_, _) => closed++;
        window.Close();

        // The window closes after its own closing work, a few jobs later.
        for (var i = 0; i < 100 && closed == 0; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.Equal(1, closed);

        var before = pane.ColumnWidths;

        pane.FitColumn(DetailsColumn.Size);

        Assert.Equal(before, pane.ColumnWidths);
    }

    /// <summary>
    /// **A drag in a scrolled pane leaves the name its width.** The row a
    /// drag records is the row on screen, not the heading grid, which is as
    /// wide as the columns once they scroll: recorded from that, it was wider
    /// than the row after every drag, and the name gave way the moment the
    /// button came up — "resizing Size resizes Name" again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_in_a_scrolled_pane_leaves_the_name_its_width()
    {
        Fill(20);

        var (window, _, pane) = await Open();

        Overflow(window, pane, name: 900);

        var rows = Rows(window, pane);

        rows.Offset = new Vector(100, 0);
        window.UpdateLayout();

        var name = Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0];

        // The name's own edge: taking it chooses the name's width again, in
        // the row the drag records.
        Drag(window, pane, DetailsColumn.Name, 40);

        Assert.Equal(VisibleRow(window, pane) / pane.TextScale, pane.ColumnWidths.Span, 0.5);
        Assert.Equal(0, pane.NameGive);
        Assert.Equal(name + 40, Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0], 0.5);
    }
    /// <summary>
    /// **Narrowing a column with the view scrolled to the far right follows
    /// the pointer.** The row is narrower by as much as the column, and a
    /// scroller at its far end pulls back by as much as its content shrinks —
    /// so every column would slide right under a still pointer and the edge
    /// being dragged would stay put. The row's width is held while the button
    /// is down; on release the offset settles once.
    /// </summary>
    [AvaloniaFact]
    public async Task Narrowing_a_column_scrolled_to_the_far_right_follows_the_pointer()
    {
        Fill(20);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        pane.ColumnWidths = pane.ColumnWidths with { Size = 300 };
        Settle(window);

        var rows = Rows(window, pane);

        rows.Offset = new Vector(rows.Extent.Width, 0);
        window.UpdateLayout();

        var edge = Edges(Heading(window, pane), window)[Cell(DetailsColumn.Size)];
        var at = Drag(window, pane, DetailsColumn.Size, -40, release: false);

        Assert.Equal(edge - 40, Edges(Heading(window, pane), window)[Cell(DetailsColumn.Size)], 0.5);

        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Settle(window);

        Assert.False(pane.IsResizingColumns);
        Assert.Equal(rows.Extent.Width - rows.Viewport.Width, rows.Offset.X, 0.5);
        Assert.Equal(rows.Offset.X, Headings(window, pane).Offset.X);
        Assert.True(Worst(window, pane) < 0.6);
    }

    /// <summary>
    /// **A pane that is already scrolling, made narrower, still takes the room
    /// it lost from the name** — the give is worked out against the row on
    /// screen. Against the heading grid's own width, which is the columns'
    /// once they scroll, a narrower pane read as no narrower at all, and the
    /// name kept its width while the scroll grew instead.
    /// </summary>
    [AvaloniaFact]
    public async Task A_scrolled_pane_made_narrower_still_takes_the_room_from_the_name()
    {
        Fill(10);

        var (window, _, pane) = await Open(width: 2000);

        pane.ColumnWidths = pane.ColumnWidths with { Name = 600, Size = 600, Modified = 600, Span = VisibleRow(window, pane) };
        Settle(window);

        Assert.True(pane.ColumnsOverflow, "the columns fit, so nothing scrolls");
        Assert.Equal(0, pane.NameGive);

        var name = Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0];

        window.Width = 1900;
        Settle(window);

        Assert.Equal(name - 100, Edges(Heading(window, pane), window)[1] - Edges(Heading(window, pane), window)[0], 0.75);
        Assert.True(pane.ColumnsOverflow);
        Assert.True(Worst(window, pane) < 0.6);
    }

    /// <summary>
    /// **Type-ahead counts the indent of a nested row**: the start of a name
    /// inside an opened folder is further right by its indent, so a view
    /// scrolled past the first column but not past that start already shows
    /// the name, and is left where it is.
    /// </summary>
    [AvaloniaFact]
    public async Task Type_ahead_counts_the_indent_of_a_nested_name()
    {
        Fill(10);
        File.WriteAllText(Path.Combine(Root, "folder0", "nested-target.txt"), "n");

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        await pane.ToggleExpandAsync(pane.DetailsEntries.Single(e => e.Name == "folder0"));
        Settle(window);

        var nested = pane.DetailsEntries.Single(e => e.Name == "nested-target.txt");
        var indent = pane.Indents[nested.FullPath];
        var heading = Heading(window, pane);
        var firstColumn = heading.Margin.Left + heading.ColumnDefinitions[0].ActualWidth;

        Assert.True(indent > 4, "the row is not indented, so this measures nothing");

        List(window, pane).ContainerFromItem(pane.DetailsEntries.First())!.Focus();
        Settle(window);

        var rows = Rows(window, pane);
        var between = Math.Round(firstColumn + indent / 2);

        rows.Offset = new Vector(between, 0);
        window.UpdateLayout();

        window.KeyTextInput("nested-t");
        Settle(window);

        Assert.Equal("nested-target.txt", pane.SelectedEntry?.Name);
        Assert.Equal(between, rows.Offset.X);
    }
}