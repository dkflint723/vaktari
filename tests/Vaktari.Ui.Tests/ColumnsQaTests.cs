using Avalonia;
using Avalonia.Controls;
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
/// QA of columns part two (qa/columns2, on 1e54c79): the states the
/// implementer's own tests did not reach. Each case says what it is after.
///
/// **Red on 4328b4c, each a QA finding rather than a broken test:**
/// - Right_then_Down_at_once_keeps_selection_and_keyboard_together: ↓ pressed
///   before the folder's read lands moves the selection, and the keyboard is
///   then put back on the folder's row, so the two are apart.
/// - A_click_on_a_heading_straight_after_a_drag_sorts: within the double-click
///   time of a grip press that then DRAGGED, a click where the edge was is
///   taken as the second half of a double-click and fits the column,
///   undoing the drag, instead of sorting.
/// </summary>
public sealed class ColumnsQaTests : ColumnWindow
{
    private readonly ITestOutputHelper _out;

    public ColumnsQaTests(ITestOutputHelper output) => _out = output;

    private static void Overflow(Window window, PaneViewModel pane, double name = 1400)
    {
        pane.ColumnWidths = pane.ColumnWidths with { Name = name, Span = 0 };
        Settle(window);

        Assert.True(pane.ColumnsOverflow, "the columns fit, so nothing here scrolls");
    }

    private static void Key1(Window window, Key key, PhysicalKey physical, RawInputModifiers mods = RawInputModifiers.None)
        => window.KeyPress(key, mods, physical, null);

    // ---- 1. X kept by every route that brings a row into view (M20) --------------

    /// <summary>
    /// **Every way the keyboard or the code brings a row into view keeps the
    /// horizontal offset.** The implementer's test only presses Down and
    /// selects from code; Page Down, End, Ctrl+End, ScrollIntoView of an
    /// unrealised row and a file arriving from outside are the others.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("down")]
    [InlineData("pagedown")]
    [InlineData("end")]
    [InlineData("ctrl-end")]
    [InlineData("code-last")]
    [InlineData("scrollintoview")]
    [InlineData("arrival")]
    public async Task X_is_kept_however_a_row_is_brought_into_view(string how)
    {
        Fill(120);

        var (window, _, pane) = await Open();

        Overflow(window, pane);

        var list = List(window, pane);
        var rows = Rows(window, pane);
        var first = pane.DetailsEntries.First(e => !e.IsDirectory);

        pane.SelectedEntry = first;
        Settle(window);
        list.ContainerFromItem(first)!.Focus(NavigationMethod.Directional);
        Settle(window);

        rows.Offset = new Vector(300, rows.Offset.Y);
        window.UpdateLayout();

        var y = rows.Offset.Y;

        switch (how)
        {
            case "down":
                for (var i = 0; i < 40; i++) { Key1(window, Key.Down, PhysicalKey.ArrowDown); Settle(window, 1); }
                break;
            case "pagedown":
                for (var i = 0; i < 3; i++) { Key1(window, Key.PageDown, PhysicalKey.PageDown); Settle(window, 1); }
                break;
            case "end":
                Key1(window, Key.End, PhysicalKey.End);
                break;
            case "ctrl-end":
                Key1(window, Key.End, PhysicalKey.End, RawInputModifiers.Control);
                break;
            case "code-last":
                pane.SelectedEntry = pane.DetailsEntries.Last();
                break;
            case "scrollintoview":
                list.ScrollIntoView(pane.DetailsEntries.Last());
                break;
            case "arrival":
                File.WriteAllText(Path.Combine(Root, "zzzz-arrived-last.txt"), "new");

                for (var i = 0; i < 300 && !pane.DetailsEntries.Any(e => e.Name == "zzzz-arrived-last.txt"); i++)
                {
                    Settle(window, 1);
                    await Task.Delay(10);
                }

                pane.SelectedEntry = pane.DetailsEntries.Single(e => e.Name == "zzzz-arrived-last.txt");
                break;
        }

        Settle(window);

        _out.WriteLine($"{how}: y {y} -> {rows.Offset.Y}, x {rows.Offset.X}, selected {pane.SelectedEntry?.Name}");

        Assert.True(rows.Offset.Y > y, $"{how} never scrolled down, so this measured nothing");
        Assert.Equal(300, rows.Offset.X);
        Assert.Equal(300, Headings(window, pane).Offset.X);
    }

    // ---- 2. hidden heading guards (R9a / R9b) ------------------------------------

    /// <summary>
    /// **A tab whose widths change while its heading is hidden comes back
    /// drawn right in its first pass.** The two guards in OnHeadingLaidOut
    /// stop a hidden heading from computing from stale numbers; this is the
    /// state where the stale numbers differ from the right ones.
    /// </summary>
    [AvaloniaFact]
    public async Task A_listing_whose_widths_change_in_the_grid_comes_back_right()
    {
        Fill(40);

        var (window, _, pane) = await Open();

        Overflow(window, pane);
        Rows(window, pane).Offset = new Vector(200, 0);
        window.UpdateLayout();

        pane.View = Vaktari.Core.Session.ViewMode.Grid;
        Settle(window);

        window.Width = 1100;
        Settle(window);

        pane.ResetColumnWidthsCommand.Execute(null);
        Settle(window);

        _out.WriteLine($"in the grid: overflow {pane.ColumnsOverflow} row {pane.DetailsRowWidth} give {pane.NameGive}");

        pane.View = Vaktari.Core.Session.ViewMode.Details;
        Pump();
        window.UpdateLayout();

        _out.WriteLine($"first pass back: overflow {pane.ColumnsOverflow} row {pane.DetailsRowWidth} offset {Rows(window, pane).Offset} worst {Worst(window, pane)}");

        Settle(window);

        Assert.False(pane.ColumnsOverflow);
        Assert.Equal(0, Rows(window, pane).Offset.X);
        Assert.True(Worst(window, pane) < 0.6);
    }

    // ---- 3. the rename box's other keys ------------------------------------------

    /// <summary>
    /// **The rename box keeps the keyboard for every caret key**, not only ←
    /// and →. Up, Down, Page Down, Home and End each reach the ListBox when the
    /// box has nothing to do with them; this records which of them end the
    /// rename.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("details", Key.Right)]
    [InlineData("grid", Key.Right)]
    [InlineData("compact", Key.Right)]
    [InlineData("details", Key.Down)]
    [InlineData("details", Key.Up)]
    [InlineData("details", Key.PageDown)]
    [InlineData("details", Key.End)]
    public async Task The_rename_box_keeps_the_name_being_typed(string layout, Key key)
    {
        Fill(20);

        var (window, _, pane) = await Open();

        pane.View = layout switch
        {
            "grid" => Vaktari.Core.Session.ViewMode.Grid,
            "compact" => Vaktari.Core.Session.ViewMode.Compact,
            _ => Vaktari.Core.Session.ViewMode.Details,
        };
        Settle(window);

        var file = pane.Entries.Where(e => !e.IsDirectory).Skip(3).First();

        pane.SelectedEntry = file;
        Settle(window);

        ListBox? list = null;

        for (var i = 0; i < 200 && list?.ContainerFromItem(file) is null; i++)
        {
            Settle(window, 1);
            await Task.Delay(10);
            list = window.GetVisualDescendants().OfType<ListBox>()
                         .FirstOrDefault(l => ReferenceEquals(l.DataContext, pane) && l.IsEffectivelyVisible && l.GetVisualDescendants().OfType<ListBoxItem>().Any());
            list?.ScrollIntoView(file);
        }

        Assert.NotNull(list?.ContainerFromItem(file));

        list!.ContainerFromItem(file)!.Focus(NavigationMethod.Directional);
        Settle(window);

        pane.BeginRenameCommand.Execute(null);
        Settle(window);

        var box = window.GetVisualDescendants().OfType<TextBox>()
                        .Single(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);

        Assert.True(box.IsFocused);

        box.SelectionStart = box.SelectionEnd = box.CaretIndex = box.Text!.Length;

        var physical = key switch
        {
            Key.Right => PhysicalKey.ArrowRight,
            Key.Down => PhysicalKey.ArrowDown,
            Key.Up => PhysicalKey.ArrowUp,
            Key.PageDown => PhysicalKey.PageDown,
            _ => PhysicalKey.End,
        };

        Key1(window, key, physical);
        Settle(window);

        _out.WriteLine($"{layout} {key}: renaming '{pane.RenamingPath}', focused {window.FocusManager?.GetFocusedElement()?.GetType().Name}, selected {pane.SelectedEntry?.Name}");

        Assert.Equal(file.FullPath, pane.RenamingPath);
        Assert.True(box.IsFocused, $"{key} at the end of the name took the keyboard out of the rename box");
    }

    // ---- 4. alignment matrix ------------------------------------------------------

    /// <summary>
    /// **Headings and rows together at the far right, while a column is dragged
    /// there, and while the window is resized there**, at three device scalings
    /// and three zooms, measured after ONE layout pass each time.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 1.25)]
    [InlineData(1.15, 1.0)]
    [InlineData(1.15, 1.15)]
    [InlineData(1.15, 1.25)]
    [InlineData(1.25, 1.0)]
    [InlineData(1.25, 1.25)]
    public async Task Rows_and_headings_stay_together_at_the_far_right(double scaling, double zoom)
    {
        Fill(60);

        var (window, _, pane) = await Open(zoom: zoom, scaling: scaling);

        Overflow(window, pane, name: 1300.3);

        var rows = Rows(window, pane);
        var heads = Headings(window, pane);
        var worst = 0.0;
        var mismatch = 0;

        void Check(string when)
        {
            var gap = Worst(window, pane);
            worst = Math.Max(worst, gap);

            if (rows.Offset.X != heads.Offset.X || rows.Extent.Width != heads.Extent.Width
                || rows.Viewport.Width != heads.Viewport.Width)
            {
                mismatch++;
                _out.WriteLine($"{when}: offsets {rows.Offset.X}/{heads.Offset.X} extents {rows.Extent.Width}/{heads.Extent.Width} viewports {rows.Viewport.Width}/{heads.Viewport.Width}");
            }

            if (gap >= 0.6) _out.WriteLine($"{when}: gap {gap}");
        }

        rows.Offset = new Vector(1e6, 0);
        window.UpdateLayout();
        Check("far right");

        // A drag at the far right, one pass per move, narrower then wider.
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(DetailsColumn.Size)];
        var press = new Point(edge, heading.TranslatePoint(new Point(0, heading.Bounds.Height / 2), window)!.Value.Y);

        window.MouseMove(press);
        window.MouseDown(press, MouseButton.Left);
        Pump();

        foreach (var dx in new[] { -7.0, -19, -33, -21, 4, 26, 55, 80 })
        {
            window.MouseMove(press + new Point(dx, 0));
            Pump();
            window.UpdateLayout();
            Check($"drag {dx}");
        }

        window.MouseUp(press + new Point(80, 0), MouseButton.Left);
        Pump();
        window.UpdateLayout();
        Check("release");
        Settle(window);
        Check("released");

        // A resize at the far right, one pass per step.
        rows.Offset = new Vector(1e6, 0);
        window.UpdateLayout();

        for (var width = 1600.0; width >= 1150; width -= 7.3)
        {
            window.Width = width;
            Pump();
            window.UpdateLayout();
            Check($"resize {width}");
        }

        for (var width = 1150.0; width <= 1700; width += 9.1)
        {
            window.Width = width;
            Pump();
            window.UpdateLayout();
            Check($"resize {width}");
        }

        Settle(window);
        Check("settled");

        _out.WriteLine($"scaling {scaling} zoom {zoom}: worst {worst:N3}, mismatches {mismatch}");

        Assert.True(worst < 0.6, $"rows and headings apart by {worst}");
        Assert.Equal(0, mismatch);
    }

    /// <summary>
    /// **No flicker at the threshold.** Window widths stepped by a quarter
    /// pixel across the point where the columns start to overflow; at each,
    /// eight passes with nothing changing must not flip the overflow back and
    /// forth.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.15, 1.0)]
    [InlineData(1.15, 1.25)]
    [InlineData(1.25, 1.15)]
    public async Task The_overflow_does_not_flicker_at_its_threshold(double scaling, double zoom)
    {
        Fill(30);

        var (window, _, pane) = await Open(zoom: zoom, scaling: scaling, width: 1300);

        pane.ColumnWidths = pane.ColumnWidths with { Name = 400, Span = 1270, Size = 300 };
        Settle(window);

        // Find the threshold coarsely, then walk it finely.
        var width = 1300.0;

        while (!pane.ColumnsOverflow && width > 500)
        {
            width -= 5;
            window.Width = width;
            Settle(window, 3);
        }

        Assert.True(pane.ColumnsOverflow, "never overflowed");

        var oscillations = 0;

        for (var w = width + 6; w >= width - 2; w -= 0.25)
        {
            window.Width = w;
            Settle(window, 2);

            var states = new List<(bool, double, double)>();

            for (var pass = 0; pass < 8; pass++)
            {
                Pump();
                window.UpdateLayout();
                states.Add((pane.ColumnsOverflow, pane.NameGive, pane.DetailsRowWidth));
            }

            if (states.Distinct().Count() > 1)
            {
                oscillations++;
                _out.WriteLine($"at {w}: {string.Join(" | ", states)}");
            }

            Assert.True(Worst(window, pane) < 0.6, $"at {w}: apart by {Worst(window, pane)}");
        }

        Assert.Equal(0, oscillations);
    }

    /// <summary>
    /// **The vertical scroll bar appearing changes nothing sideways**: a
    /// listing that grows past the bottom while scrolled to the far right.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.15)]
    public async Task A_vertical_scroll_bar_appearing_moves_nothing_sideways(double scaling)
    {
        Fill(3);

        var (window, _, pane) = await Open(scaling: scaling);

        Overflow(window, pane);

        var rows = Rows(window, pane);

        rows.Offset = new Vector(1e6, 0);
        window.UpdateLayout();

        var before = (rows.Offset.X, rows.Viewport.Width, rows.Extent.Width);

        Assert.True(rows.Extent.Height <= rows.Viewport.Height, "already scrolls down");

        for (var i = 0; i < 80; i++) File.WriteAllText(Path.Combine(Root, $"late-{i:000}.txt"), "x");

        for (var i = 0; i < 300 && pane.DetailsEntries.Count() < 80; i++)
        {
            Settle(window, 1);
            await Task.Delay(10);
        }

        Settle(window);

        Assert.True(rows.Extent.Height > rows.Viewport.Height, "the listing did not grow past the bottom");
        Assert.Equal(before, (rows.Offset.X, rows.Viewport.Width, rows.Extent.Width));
        Assert.Equal(rows.Viewport.Width, Headings(window, pane).Viewport.Width);
        Assert.Equal(rows.Offset.X, Headings(window, pane).Offset.X);
        Assert.True(Worst(window, pane) < 0.6);
    }

    // ---- 5. the background finish ---------------------------------------------------

    /// <summary>
    /// **What happens after a fit is not undone by its own background
    /// finish.** A listing longer than the UI thread's share leaves the rest
    /// to a task that widens the column later. Reset column widths, or a drag,
    /// made before it lands must stay made.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("reset")]
    [InlineData("drag")]
    [InlineData("navigate")]
    public async Task A_later_choice_is_not_undone_by_the_background_finish(string then)
    {
        Fill(40);

        // The widest name, far below the rows on screen.
        File.WriteAllText(Path.Combine(Root, "zzz-" + new string('W', 120) + ".txt"), "w");

        var (window, _, pane) = await Open();

        ColumnFitter.SyncRows = 5;

        pane.SizeAllColumnsToFitCommand.Execute(null);
        var background = ColumnFitter.LastBackground;

        Assert.NotNull(background);

        switch (then)
        {
            case "reset":
                pane.ResetColumnWidthsCommand.Execute(null);
                break;
            case "drag":
                pane.SetColumnWidth(DetailsColumn.Name, 300);
                break;
            default:
                await pane.NavigateAsync(Path.Combine(Root, "folder0"));
                break;
        }

        var chosen = pane.ColumnWidths;

        try { await background!; } catch (OperationCanceledException) { }

        for (var i = 0; i < 20; i++) { Settle(window, 1); await Task.Delay(5); }

        _out.WriteLine($"{then}: chosen {chosen} now {pane.ColumnWidths}");

        Assert.Equal(chosen, pane.ColumnWidths);
    }
    // ---- 6. → then ← with nothing in between ----------------------------------------

    /// <summary>
    /// **→ then ← on the same folder row, with nothing done in between.** The
    /// implementer's test focuses the row again before ←; a person does not.
    /// Seen in the real window: after → opened the folder, ← moved the
    /// keyboard to the sidebar instead of shutting the folder.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task Right_then_Left_on_a_folder_row_opens_and_shuts_it(int files)
    {
        Fill(files);

        var (window, _, pane) = await Open();
        var list = List(window, pane);
        var folder = pane.DetailsEntries.Single(e => e.Name == "folder0");

        pane.SelectedEntry = folder;
        Settle(window);
        list.ContainerFromItem(folder)!.Focus(NavigationMethod.Directional);
        Settle(window);

        Key1(window, Key.Right, PhysicalKey.ArrowRight);

        for (var i = 0; i < 200 && !pane.IsExpanded(folder.FullPath); i++) { Settle(window, 1); await Task.Delay(5); }
        Settle(window);

        var focus = window.FocusManager?.GetFocusedElement();
        _out.WriteLine($"after →: expanded {pane.IsExpanded(folder.FullPath)}, focus {focus?.GetType().Name} on {(focus as Control)?.DataContext}");

        Assert.True(pane.IsExpanded(folder.FullPath));

        Key1(window, Key.Left, PhysicalKey.ArrowLeft);

        for (var i = 0; i < 200 && pane.IsExpanded(folder.FullPath); i++) { Settle(window, 1); await Task.Delay(5); }

        focus = window.FocusManager?.GetFocusedElement();
        _out.WriteLine($"after ←: expanded {pane.IsExpanded(folder.FullPath)}, focus {focus?.GetType().Name} on {(focus as Control)?.DataContext}");

        Assert.False(pane.IsExpanded(folder.FullPath), "← straight after → did not shut the folder");
    }

    /// <summary>
    /// **The state where the row rewrite matters: a FILLING name in a pane that
    /// overflows anyway.** A row brought into view before it is realised is
    /// measured at an infinite width, and a filling name then asks only for its
    /// text — so the row is narrower than the scrolled-to span, and the
    /// scroller pulls X back to show its left edge. With the name pinned, rows
    /// are always as wide as the columns, which is why no mutation of the
    /// rewrite reddened the implementer's tests.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("end")]
    [InlineData("down")]
    [InlineData("code-last")]
    public async Task X_is_kept_in_a_filling_tab_that_overflows(string how)
    {
        Fill(120);

        var (window, _, pane) = await Open();

        pane.ColumnWidths = pane.ColumnWidths with { Name = 0, Span = 0, Type = 600, Size = 600, Modified = 600, Created = 600 };
        Settle(window);

        Assert.True(pane.NameFills);
        Assert.True(pane.ColumnsOverflow, "a filling tab with these columns does not overflow");

        var list = List(window, pane);
        var rows = Rows(window, pane);
        var first = pane.DetailsEntries.First(e => !e.IsDirectory);

        pane.SelectedEntry = first;
        Settle(window);
        list.ContainerFromItem(first)!.Focus(NavigationMethod.Directional);
        Settle(window);

        rows.Offset = new Vector(900, rows.Offset.Y);
        window.UpdateLayout();

        switch (how)
        {
            case "end": Key1(window, Key.End, PhysicalKey.End); break;
            case "down":
                for (var i = 0; i < 40; i++) { Key1(window, Key.Down, PhysicalKey.ArrowDown); Settle(window, 1); }
                break;
            default: pane.SelectedEntry = pane.DetailsEntries.Last(); break;
        }

        Settle(window);

        _out.WriteLine($"{how}: x {rows.Offset.X} y {rows.Offset.Y}");

        Assert.True(rows.Offset.Y > 0, "nothing scrolled down");
        Assert.Equal(900, rows.Offset.X);
        Assert.Equal(900, Headings(window, pane).Offset.X);
    }
    /// <summary>
    /// **A double-click on an edge never sorts.** Seen in the real window: a
    /// double-click on the name's edge, with the name pinned narrower than its
    /// widest entry, left the listing sorted by Size — the column the grip
    /// half covers. Pinned narrow first, as a drag leaves it; then the
    /// double-click; the pointer pumped between every message, as a real queue
    /// would.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(DetailsColumn.Name, 0)]
    [InlineData(DetailsColumn.Name, 1)]
    [InlineData(DetailsColumn.Size, 0)]
    [InlineData(DetailsColumn.Size, 1)]
    public async Task A_double_click_on_a_pinned_edge_never_sorts(DetailsColumn column, double slip)
    {
        Fill(20);
        File.WriteAllText(Path.Combine(Root, "the-longest-name-in-this-folder-" + new string('q', 120) + ".txt"), "l");

        var (window, _, pane) = await Open(width: 1163);

        pane.ColumnWidths = pane.ColumnWidths with { Name = 380, Span = VisibleRow(window, pane) / pane.TextScale };
        Settle(window);

        var sort = (pane.Sort, pane.SortDescending);
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(column)];
        var y = heading.TranslatePoint(new Point(0, heading.Bounds.Height / 2), window)!.Value.Y;

        void Step(Action act) { act(); Settle(window, 2); }

        Step(() => window.MouseMove(new Point(edge, y)));
        Step(() => window.MouseDown(new Point(edge, y), MouseButton.Left));
        Step(() => window.MouseUp(new Point(edge, y), MouseButton.Left));
        Step(() => window.MouseMove(new Point(edge + slip, y)));
        Step(() => window.MouseDown(new Point(edge + slip, y), MouseButton.Left));
        Step(() => window.MouseUp(new Point(edge + slip, y), MouseButton.Left));

        _out.WriteLine($"{column} slip {slip}: sort {sort} -> {(pane.Sort, pane.SortDescending)}; widths {pane.ColumnWidths}");

        Assert.Equal(sort, (pane.Sort, pane.SortDescending));
        Assert.True(PaneScale.ColumnWidth(pane.ColumnWidths, column) > (column == DetailsColumn.Name ? 380 : 0));
    }
    /// <summary>
    /// **A zoom while scrolled keeps rows and headings together**, pass by
    /// pass: the column widths, the row's width and the offset all change at
    /// once.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.15)]
    [InlineData(1.25)]
    public async Task A_zoom_while_scrolled_keeps_rows_and_headings_together(double scaling)
    {
        Fill(40);

        var (window, shell, pane) = await Open(scaling: scaling);

        Overflow(window, pane);

        var rows = Rows(window, pane);
        var worst = 0.0;
        var mismatch = 0;

        foreach (var zoom in new[] { 1.25, 0.9, 1.5, 1.0 })
        {
            rows.Offset = new Vector(1e6, 0);
            window.UpdateLayout();

            pane.FontScale = zoom;
            shell.RefreshPaneScales();

            for (var pass = 0; pass < 4; pass++)
            {
                Pump();
                window.UpdateLayout();
                worst = Math.Max(worst, Worst(window, pane));

                if (rows.Offset.X != Headings(window, pane).Offset.X || rows.Extent.Width != Headings(window, pane).Extent.Width)
                    mismatch++;
            }
        }

        _out.WriteLine($"scaling {scaling}: worst {worst:N3} mismatches {mismatch}");

        Assert.True(worst < 0.6, $"apart by {worst}");
        Assert.Equal(0, mismatch);
    }
    // ---- 8. the fixes' edges (round 2) ------------------------------------------

    /// <summary>
    /// **→ and then ↓ at once**, before the folder's read has landed: the
    /// keyboard handed back to the folder's row must not leave the selection
    /// and the focused row apart.
    /// </summary>
    [AvaloniaFact]
    public async Task Right_then_Down_at_once_keeps_selection_and_keyboard_together()
    {
        Fill(10);

        var (window, _, pane) = await Open();
        var list = List(window, pane);
        var folder = pane.DetailsEntries.Single(e => e.Name == "folder0");

        pane.SelectedEntry = folder;
        Settle(window);
        list.ContainerFromItem(folder)!.Focus(NavigationMethod.Directional);
        Settle(window);

        Key1(window, Key.Right, PhysicalKey.ArrowRight);
        Key1(window, Key.Down, PhysicalKey.ArrowDown);

        for (var i = 0; i < 100; i++) { Settle(window, 1); await Task.Delay(5); }

        var focused = window.FocusManager?.GetFocusedElement() as ListBoxItem;

        _out.WriteLine($"selected {pane.SelectedEntry?.Name}, keyboard on {(focused?.DataContext as FileEntry?)?.Name}, expanded {pane.IsExpanded(folder.FullPath)}");

        Assert.NotNull(focused);
        Assert.Equal(pane.SelectedEntry, focused!.DataContext);
    }

    /// <summary>
    /// **A deliberate click on a heading straight after a drag sorts.** The
    /// double-click claim takes a heading press within the double-click time
    /// and distance of the grip's last press; after a real drag the grip has
    /// moved away from that press, so a quick click where the edge used to
    /// be lands on a heading, and must sort rather than fit.
    /// </summary>
    [AvaloniaFact]
    public async Task A_click_on_a_heading_straight_after_a_drag_sorts()
    {
        Fill(10);

        var (window, _, pane) = await Open();
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(DetailsColumn.Size)];
        var y = heading.TranslatePoint(new Point(0, heading.Bounds.Height / 2), window)!.Value.Y;
        var press = new Point(edge, y);

        // A quick drag of Size's edge 40 to the right, then a click where it was.
        window.MouseMove(press);
        window.MouseDown(press, MouseButton.Left);
        window.MouseMove(press + new Point(20, 0));
        window.MouseMove(press + new Point(40, 0));
        window.MouseUp(press + new Point(40, 0), MouseButton.Left);
        Pump();
        window.UpdateLayout();

        var dragged = PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size);
        var under = window.InputHitTest(press) as Visual;
        var button = under as Button ?? under?.FindAncestorOfType<Button>();

        _out.WriteLine($"after the drag Size is {dragged}; under the old edge: {button?.CommandParameter}");

        Assert.Equal("size", button?.CommandParameter as string);

        window.MouseDown(press, MouseButton.Left);
        window.MouseUp(press, MouseButton.Left);
        Settle(window);

        _out.WriteLine($"sort {pane.Sort} desc {pane.SortDescending}; Size {PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size)}");

        Assert.Equal(dragged, PaneScale.ColumnWidth(pane.ColumnWidths, DetailsColumn.Size));
        Assert.True(pane.IsSortedBySize, $"sorted by {pane.Sort}");
    }
}
