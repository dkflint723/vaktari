using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging a column heading's edge with a real pointer, in a real window.
///
/// **The tests that shipped with the grips never moved a pointer.** They
/// raised the Thumb's DragDelta by hand with a vector of 40 and asserted the
/// stored number went up by 40 — which it did, while on screen two things the
/// maintainer then reported were wrong: "I tried resizing name, but I can't",
/// and "if I resize 'size' it actually resizes Name". Measured on 0.11.1 with
/// the presses and moves below: there was no grip on the name; every other
/// grip sat on an edge that did not move, because the name filled the row and
/// took up whatever a column gained, so the column grew leftwards and the name
/// shrank; and a 40-pixel drag in four moves widened the column by 100, since
/// the Thumb measures from the press point and an edge that stands still
/// reports the whole distance again on every move. And a drag in one half of
/// a split moved the other half's columns.
///
/// So everything here is measured where a person sees it: the heading's and a
/// row's column edges in the window's coordinates, read off the laid-out
/// grids, after a press, moves and a release at screen points. These touch no
/// API the fix added, so the same file runs against the code before it.
///
/// The interface text size is pinned AFTER the window is built, because
/// building one reads the desktop's (see InterfaceTextSizeTests), and both it
/// and the settings are put back afterwards.
/// </summary>
public sealed class ColumnDragPointerTests : OwnedViewModels
{
    private static readonly string[] Metadata = ["Type", "Size", "Modified", "Created"];

    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly double? _systemBefore = InterfaceText.SystemScale;
    private MainWindow? _window;
    private string? _root;

    public override void Dispose()
    {
        if (_window?.DataContext is ShellViewModel shell)
        {
            // Back to one half and the designed widths, so the session this
            // window flushes on close does not open the next test split.
            foreach (var group in new[] { shell.Right, shell.Left })
            {
                if (group is null) continue;

                shell.ActivateGroup(group);
                shell.ResetColumnWidthsCommand.Execute(null);
            }

            if (shell.IsSplit) shell.ToggleSplit();

            Pump();
        }

        _window?.Close();
        AppSettings.Apply(_settingsBefore);
        InterfaceText.SystemScale = _systemBefore;

        if (_root is not null)
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir is not worth failing over */ }

        base.Dispose();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    // ---- the window ----------------------------------------------------------

    /// <summary>
    /// A window wide enough for every column at 150% in either half of a
    /// split, with every pane on screen in details, all four columns ticked,
    /// at <paramref name="zoom"/>, at the designed widths, listing three files.
    /// </summary>
    private async Task<(MainWindow Window, ShellViewModel Shell)> Open(double zoom, bool split)
    {
        UseSearch(PaneViewModel.Search);

        _root = Directory.CreateTempSubdirectory("vaktari-coldrag-").FullName;

        foreach (var name in new[] { "alpha.txt", "beta.txt", "gamma.txt" })
            File.WriteAllText(Path.Combine(_root, name), name);

        var window = _window = new MainWindow { Width = 3000, Height = 900 };

        window.Show();
        Pump();

        InterfaceText.SystemScale = null;
        AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with
            {
                InterfaceTextScale = 0,
                Details = new DetailsViewSettings(),
            },
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        if (shell.IsSplit != split) shell.ToggleSplit();

        foreach (var group in new[] { shell.Right, shell.Left })
        {
            if (group?.ActiveTab is not { } pane) continue;

            shell.ActivateGroup(group);

            pane.View = Vaktari.Core.Session.ViewMode.Details;
            pane.ShowTypeColumn = true;
            pane.ShowCreatedColumn = true;
            pane.HideSizeColumn = false;
            pane.HideModifiedColumn = false;
            pane.FontScale = zoom;

            // Every input the test reads is pinned: the window restores the
            // session the last test in this class left.
            shell.ResetColumnWidthsCommand.Execute(null);

            await pane.NavigateAsync(_root);
            await pane.RefreshAsync();
        }

        shell.RefreshPaneScales();

        foreach (var pane in Panes(shell))
            await RowsOnScreen(window, pane);

        return (window, shell);
    }

    private static IEnumerable<PaneViewModel> Panes(ShellViewModel shell)
        => new[] { shell.Left.ActiveTab, shell.Right?.ActiveTab }.OfType<PaneViewModel>();

    private static ListBox List(Window window, PaneViewModel pane)
        => window.GetVisualDescendants().OfType<ListBox>()
                 .First(l => ReferenceEquals(l.DataContext, pane) && l.ItemsSource == pane.DetailsEntries);

    /// <summary>Waits on the subject: a realized row in this pane's list.</summary>
    private static async Task RowsOnScreen(Window window, PaneViewModel pane)
    {
        for (var i = 0; i < 400; i++)
        {
            Pump();
            window.UpdateLayout();

            if (List(window, pane).GetVisualDescendants().OfType<ListBoxItem>()
                    .Any(item => item.IsVisible && item.DataContext is FileEntry))
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"no row of {pane.CurrentPath} was realized within four seconds");
    }

    private static Thumb Grip(Window window, PaneViewModel pane, string column)
        => window.GetVisualDescendants().OfType<Thumb>()
                 .Single(t => (string?)t.Tag == column && ReferenceEquals(t.DataContext, pane));

    /// <summary>The heading's grid: the one the size grip is in.</summary>
    private static Grid Heading(Window window, PaneViewModel pane)
        => Assert.IsType<Grid>(Grip(window, pane, "Size").Parent);

    /// <summary>The first realized row's grid, kept in step with the heading's.</summary>
    private static Grid Row(Window window, PaneViewModel pane)
        => List(window, pane).GetVisualDescendants().OfType<ListBoxItem>()
               .First(item => item.IsVisible && item.DataContext is FileEntry)
               .GetVisualDescendants().OfType<Grid>()
               .First(g => g.Margin == new Thickness(12, 0, 18, 0));

    /// <summary>
    /// Where each column of a grid starts and ends, in the window's pixels:
    /// the grid's left edge and then the right edge of each column in turn.
    /// </summary>
    private static double[] Edges(Grid grid, Visual window)
    {
        var x = grid.TranslatePoint(default, window)!.Value.X;
        var edges = new List<double> { x };

        foreach (var column in grid.ColumnDefinitions)
            edges.Add(edges[^1] + column.ActualWidth);

        return [.. edges];
    }

    /// <summary>The cell a column's heading and grip sit in, in both grids.</summary>
    private static int Cell(string column) => column == "Name" ? 1 : Array.IndexOf(Metadata, column) + 3;

    private static double Width(double[] edges, int cell) => edges[cell + 1] - edges[cell];

    /// <summary>The cell after Created: the empty room once the name has a
    /// width of its own. Not a column anybody drags.</summary>
    private const int Trailing = 7;

    private static Point Centre(Visual control, Visual window)
    {
        var at = control.TranslatePoint(default, window)!.Value;

        return new Point(at.X + control.Bounds.Width / 2, at.Y + control.Bounds.Height / 2);
    }

    /// <summary>
    /// Presses on the edge of <paramref name="column"/>'s heading — at the
    /// boundary as drawn, which is the line a person aims for, not at wherever
    /// the grip happens to be — moves the pointer <paramref name="dx"/> to the
    /// right in four steps with the window laid out between them, and lets go.
    /// <paramref name="inside"/> presses that far to the left of the line
    /// instead, inside the column.
    /// </summary>
    private static void Drag(MainWindow window, PaneViewModel pane, string column, double dx, double inside = 0)
    {
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(column) + 1];
        var press = new Point(edge - inside, Centre(heading, window).Y);

        window.MouseMove(press);
        window.MouseDown(press, MouseButton.Left);
        Pump();

        for (var step = 1; step <= 4; step++)
        {
            window.MouseMove(press + new Point(dx * step / 4, 0));
            Pump();
            window.UpdateLayout();
        }

        window.MouseUp(press + new Point(dx, 0), MouseButton.Left);
        Pump();
        window.UpdateLayout();
    }

    private static string Describe(IInputElement? hit)
    {
        for (var visual = hit as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Thumb thumb) return $"the {thumb.Tag} grip";
            if (visual is Button button) return $"the {button.CommandParameter} heading";
        }

        return hit?.GetType().Name ?? "nothing";
    }

    // ---- the drag ------------------------------------------------------------

    /// <summary>
    /// **The edge a person grabs is the edge that moves, by as much as the
    /// pointer did, and nothing else changes size.** For every column,
    /// the name's included, at 100% and 150%: the column is 40 pixels wider,
    /// its right edge is 40 pixels further right, every other column is the
    /// width it was, the columns before it have not moved, and a row's cells
    /// sit exactly under the headings — during the drag's last step and after
    /// the release.
    ///
    /// Red before the fix on both reports: there was no grip on the name to
    /// press, and for the other four the right edge stayed put while the name
    /// lost 100 pixels.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Name", 1.0)]
    [InlineData("Type", 1.0)]
    [InlineData("Size", 1.0)]
    [InlineData("Modified", 1.0)]
    [InlineData("Created", 1.0)]
    [InlineData("Name", 1.5)]
    [InlineData("Type", 1.5)]
    [InlineData("Size", 1.5)]
    [InlineData("Modified", 1.5)]
    [InlineData("Created", 1.5)]
    public async Task Dragging_an_edge_widens_that_column_alone_and_moves_the_rest_along(string column, double zoom)
    {
        var (window, shell) = await Open(zoom, split: false);
        var pane = shell.ActiveTab!;
        var cell = Cell(column);

        // No edge goes past the pane's (Name_cannot_be_dragged_past_the_
        // panes_edge, An_edge_dragged_far_stops_at_the_panes_edge), and a
        // name that fills leaves no room to widen into — so make some first.
        Drag(window, pane, "Name", -60);

        var before = Edges(Heading(window, pane), window);

        Assert.Equal(before, Edges(Row(window, pane), window), Close);

        // Two pixels inside the column, where the grips before the fix were
        // too: what is measured here is what the drag DID, and the line
        // itself is Either_side_of_a_headings_edge_is_its_grip.
        Drag(window, pane, column, 40, inside: 2);

        var after = Edges(Heading(window, pane), window);

        // "If I resize size it actually resizes Name" first, so that is what
        // a failure says when it is that.
        if (column != "Name")
            Assert.True(Math.Abs(Width(before, 1) - Width(after, 1)) < 0.5,
                $"dragging {column} by 40 changed the name from {Width(before, 1)} to {Width(after, 1)}");

        Assert.True(Math.Abs(before[cell + 1] + 40 - after[cell + 1]) < 0.5,
            $"dragging {column} by 40 moved its right edge from {before[cell + 1]} to {after[cell + 1]}");

        Assert.Equal(Width(before, cell) + 40, Width(after, cell), 0.5);

        // Every column but the trailing room after the last, which is what
        // gives or takes the difference.
        for (var other = 0; other < Trailing; other++)
        {
            if (other == cell) continue;

            Assert.True(Math.Abs(Width(before, other) - Width(after, other)) < 0.5,
                $"dragging {column} changed column {other} from {Width(before, other)} to {Width(after, other)}");
        }

        for (var edge = 0; edge <= cell; edge++)
            Assert.Equal(before[edge], after[edge], 0.5);

        Assert.Equal(after, Edges(Row(window, pane), window), Close);
    }

    /// <summary>
    /// The way back: the same edge dragged left narrows the same column, and
    /// the rows follow it there too.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Name", 1.0)]
    [InlineData("Size", 1.5)]
    public async Task Dragging_an_edge_back_narrows_that_column(string column, double zoom)
    {
        var (window, shell) = await Open(zoom, split: false);
        var pane = shell.ActiveTab!;
        var cell = Cell(column);

        var before = Edges(Heading(window, pane), window);

        Drag(window, pane, column, -30);

        var after = Edges(Heading(window, pane), window);

        Assert.Equal(Width(before, cell) - 30, Width(after, cell), 0.5);
        Assert.Equal(before[1] - before[0], after[1] - after[0], 0.5);
        Assert.Equal(after, Edges(Row(window, pane), window), Close);
    }

    /// <summary>
    /// **The line between two headings belongs to the grip**, either side of
    /// it: a pointer aimed at the edge lands a few pixels left or right about
    /// equally, and a landing on the next heading sorts the listing instead of
    /// taking the edge. Measured before the fix: three pixels right of the
    /// size column's edge was the modified heading, and there was nothing at
    /// all to take on the name's.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Name")]
    [InlineData("Type")]
    [InlineData("Size")]
    [InlineData("Modified")]
    [InlineData("Created")]
    public async Task Either_side_of_a_headings_edge_is_its_grip(string column)
    {
        var (window, shell) = await Open(1.0, split: false);
        var pane = shell.ActiveTab!;
        var heading = Heading(window, pane);
        var edge = Edges(heading, window)[Cell(column) + 1];
        var y = Centre(heading, window).Y;

        foreach (var dx in new[] { -3.0, 0, 3 })
            Assert.Equal($"the {column} grip", Describe(window.InputHitTest(new Point(edge + dx, y))));

        Assert.Equal(StandardCursorType.SizeWestEast.ToString(),
                     Grip(window, pane, column).Cursor?.ToString());

        // And the line that says the edge has been found shows only while
        // the pointer is on it.
        var line = Grip(window, pane, column).GetVisualDescendants().OfType<Border>()
                       .Single(b => b.Classes.Contains("edge"));

        window.MouseMove(new Point(edge - 40, y));
        Pump();
        Assert.Equal(0, line.Opacity);

        window.MouseMove(new Point(edge + 2, y));
        Pump();
        Assert.Equal(1, line.Opacity);
    }

    /// <summary>
    /// **A drag is not a click on the heading beside it.** The grip straddles
    /// the edge and lies over five pixels of the next heading, so the press
    /// goes to the grip; the sort and its direction are what they were after
    /// the release, for every edge.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_never_sorts()
    {
        var (window, shell) = await Open(1.0, split: false);
        var pane = shell.ActiveTab!;

        var sort = (pane.Sort, pane.SortDescending);

        foreach (var column in new[] { "Name", "Type", "Size", "Modified", "Created" })
        {
            Drag(window, pane, column, 12);
            Drag(window, pane, column, -12);
        }

        Assert.Equal(sort, (pane.Sort, pane.SortDescending));
    }

    /// <summary>
    /// **A drag in one half of a split leaves the other half exactly as it
    /// was** — its headings and its rows. Before the fix the widths were one
    /// preference for every pane, so the size column dragged on the left grew
    /// on the right too, and took the right side's name with it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Name")]
    [InlineData("Size")]
    [InlineData("Created")]
    public async Task A_drag_in_one_half_of_a_split_leaves_the_other_alone(string column)
    {
        var (window, shell) = await Open(1.0, split: true);
        var left = shell.Left.ActiveTab!;
        var right = shell.Right!.ActiveTab!;

        shell.ActivateGroup(shell.Left);

        var leftBefore = Edges(Heading(window, left), window);
        var rightBefore = Edges(Heading(window, right), window);
        var rightRowBefore = Edges(Row(window, right), window);

        Assert.True(Grip(window, right, column).IsVisible, $"the right half is not showing {column}");

        // Narrower for the name and the last column, whose edges have no room
        // to widen into in a full row.
        var dx = column is "Name" or "Created" ? -40 : 40;

        Drag(window, left, column, dx);

        Assert.Equal(Width(leftBefore, Cell(column)) + dx,
                     Width(Edges(Heading(window, left), window), Cell(column)), 0.5);

        Assert.Equal(rightBefore, Edges(Heading(window, right), window), Close);
        Assert.Equal(rightRowBefore, Edges(Row(window, right), window), Close);
    }

    // ---- a name with a width of its own, in a narrower pane --------------------

    /// <summary>
    /// The right edge of the heading band as drawn, in the window's pixels:
    /// what is right of it is cut off.
    /// </summary>
    private static double BandRight(Window window, PaneViewModel pane)
    {
        var band = Assert.IsType<Border>(Heading(window, pane).Parent);

        return band.TranslatePoint(new Point(band.Bounds.Width, 0), window)!.Value.X;
    }

    /// <summary>Every visible grip of the pane lies inside its heading band,
    /// where a pointer can reach it, and a row lines up with the headings.</summary>
    private static void EveryGripOnScreen(Window window, PaneViewModel pane, string when)
    {
        var right = BandRight(window, pane);

        foreach (var column in new[] { "Name", "Type", "Size", "Modified", "Created" })
        {
            var grip = Grip(window, pane, column);

            if (!grip.IsVisible) continue;

            var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, 0), window)!.Value.X;

            Assert.True(at < right, $"{when}: the {column} grip is at {at}, past the band's edge at {right}");
        }

        Assert.Equal(Edges(Heading(window, pane), window), Edges(Row(window, pane), window), Close);
    }

    /// <summary>Only the pane under test: the other half of a split may come
    /// back from the session in another layout.</summary>
    private static async Task Settle(MainWindow window, ShellViewModel shell)
    {
        await RowsOnScreen(window, shell.Left.ActiveTab!);

        for (var i = 0; i < 5; i++)
        {
            Pump();
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// **A name given its width in a wide pane gives way in a narrower one,
    /// and gets it back when the pane is wide again.** The maintainer's own
    /// order: drag the name's edge in one pane, then F3. Before, the left half
    /// showed the name and nothing else — every later column and every grip
    /// past the edge, with no way back but Reset (measured by QA with a real
    /// mouse, and headless: a 900-pixel name in a 1000-pixel window, no grip on
    /// screen). Now each column's grip is on screen in the half, the rows line
    /// up, the width somebody chose is untouched, and closing the split draws
    /// the name at it again. The same for a window made narrower, and for a
    /// zoom in.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("split", 1.0)]
    [InlineData("split", 1.25)]
    [InlineData("narrower", 1.0)]
    [InlineData("narrower", 1.25)]
    [InlineData("zoom", 1.0)]
    public async Task A_named_width_gives_way_to_a_narrower_pane_and_comes_back(string how, double zoom)
    {
        var (window, shell) = await Open(zoom, split: false);
        var pane = shell.ActiveTab!;

        Drag(window, pane, "Name", -20);

        var chosen = pane.ColumnWidths.Name;
        var wide = Width(Edges(Heading(window, pane), window), 1);

        Assert.True(chosen > 1500, $"the name was given only {chosen}");

        switch (how)
        {
            case "split": shell.ToggleSplit(); shell.ActivateGroup(shell.Left); break;
            case "narrower": window.Width = 1200; break;
            default: pane.FontScale = 2.0; break;
        }

        await Settle(window, shell);

        Assert.True(Width(Edges(Heading(window, pane), window), 1) < wide - 100, "the name did not give way");
        NameGivesOnlyWhatIsNeeded(window, pane, how);
        EveryGripOnScreen(window, pane, how);
        Assert.Equal(chosen, pane.ColumnWidths.Name);

        switch (how)
        {
            case "split": shell.ToggleSplit(); break;
            case "narrower": window.Width = 3000; break;
            default: pane.FontScale = zoom; break;
        }

        await Settle(window, shell);

        Assert.Equal(wide, Width(Edges(Heading(window, pane), window), 1), 0.5);
        Assert.Equal(Edges(Heading(window, pane), window), Edges(Row(window, pane), window), Close);
    }

    /// <summary>
    /// The name gives way no further than its floor, 80 pixels at 100%: in a
    /// pane too narrow even for that, the columns after it are what is cut
    /// off, not the name.
    /// </summary>
    [AvaloniaFact]
    public async Task A_name_gives_way_no_further_than_its_floor()
    {
        var (window, shell) = await Open(1.0, split: false);
        var pane = shell.ActiveTab!;

        // The narrow-pane rule drops columns before the name runs out of room
        // at their designed widths, so a column is widened first: this is a
        // pane narrower than the columns after the name.
        Drag(window, pane, "Name", -400);
        Drag(window, pane, "Size", 350);

        window.Width = 1100;
        await Settle(window, shell);

        NameGivesOnlyWhatIsNeeded(window, pane, "a 1100-pixel window");
        Assert.Equal(PaneScale.NameMin, Width(Edges(Heading(window, pane), window), 1), 0.5);
        Assert.Equal(Edges(Heading(window, pane), window), Edges(Row(window, pane), window), Close);
    }

    /// <summary>
    /// **The name's edge, dragged while the name is giving way, stays under
    /// the pointer** — narrower by as much as the pointer went, then wider by
    /// as much, the rows with it — and what is kept is the width it was let
    /// go at: taking the name's own edge chooses its width again, in this
    /// pane. (QA measured the round-2 build keeping the old width less the
    /// drag, which came back 579 pixels wide after a drag that had left it at
    /// 425.)
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_a_name_that_is_giving_way_follows_the_pointer()
    {
        var (window, shell) = await Open(1.0, split: false);
        var pane = shell.ActiveTab!;

        Drag(window, pane, "Name", -20);

        shell.ToggleSplit();
        shell.ActivateGroup(shell.Left);
        await Settle(window, shell);

        var before = Edges(Heading(window, pane), window);

        Drag(window, pane, "Name", -50);

        var narrower = Edges(Heading(window, pane), window);

        // What is kept is the width the edge was let go at: dragging the
        // name's own edge chooses its width again, here.
        Assert.Equal(before[2] - 50, narrower[2], 0.5);
        Assert.Equal(Width(before, 1) - 50, pane.ColumnWidths.Name, 0.5);

        Drag(window, pane, "Name", 30);

        var back = Edges(Heading(window, pane), window);

        Assert.Equal(narrower[2] + 30, back[2], 0.5);
        Assert.Equal(back, Edges(Row(window, pane), window), Close);
        EveryGripOnScreen(window, pane, "after the drags");
    }

    /// <summary>
    /// **The name's edge stops at the pane's.** Dragged past it, the columns
    /// after the name went off the side, which is how a name came to hide
    /// every grip; now the edge goes no further than the room after the last
    /// column, and the last grip stays on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task Name_cannot_be_dragged_past_the_panes_edge()
    {
        var (window, shell) = await Open(1.0, split: false);
        var pane = shell.ActiveTab!;

        Drag(window, pane, "Name", -30);

        var before = Edges(Heading(window, pane), window);

        Drag(window, pane, "Name", 200);

        var after = Edges(Heading(window, pane), window);

        Assert.Equal(before[2] + 30, after[2], 1.0);
        EveryGripOnScreen(window, pane, "after dragging past the edge");
    }

    /// <summary>
    /// **A drag that comes back to exactly where it was pressed puts the
    /// column back.** The handler skipped any move at the press point so that
    /// a click would change nothing, and that skipped this one too: QA dragged
    /// Size 20 narrower and back, released, and Size stayed 20 narrower.
    /// </summary>
    [AvaloniaFact]
    public async Task Coming_back_to_the_press_point_puts_the_width_back()
    {
        var (window, shell) = await Open(1.0, split: false);
        var pane = shell.ActiveTab!;
        var heading = Heading(window, pane);
        var before = Edges(heading, window);
        var press = new Point(before[Cell("Size") + 1], Centre(heading, window).Y);

        window.MouseMove(press);
        window.MouseDown(press, MouseButton.Left);
        Pump();

        foreach (var dx in new[] { -10.0, -20, 0 })
        {
            window.MouseMove(press + new Point(dx, 0));
            Pump();
            window.UpdateLayout();
        }

        window.MouseUp(press, MouseButton.Left);
        Pump();
        window.UpdateLayout();

        Assert.Equal(Width(before, Cell("Size")), Width(Edges(Heading(window, pane), window), Cell("Size")), 0.5);
    }

    /// <summary>
    /// The name is drawn at exactly what the row leaves it — the row less the
    /// other columns — held between its floor and the width chosen for it:
    /// no narrower, so there is no empty room after the last column while it
    /// is below its width, and no wider, so nothing runs past the edge that
    /// an 80-pixel name could have made room for. QA's round-2 split left 170
    /// pixels empty after Size with the name at its floor.
    /// </summary>
    private static void NameGivesOnlyWhatIsNeeded(Window window, PaneViewModel pane, string when)
    {
        var heading = Heading(window, pane);
        var edges = Edges(heading, window);
        var others = Enumerable.Range(0, Trailing).Where(c => c != 1).Sum(c => Width(edges, c));
        var scale = pane.TextScale;
        var expected = Math.Clamp(heading.Bounds.Width - others, PaneScale.NameMin * scale, pane.ColumnWidths.Name * scale);

        Assert.True(Math.Abs(Width(edges, 1) - expected) < 0.75,
            $"{when}: the name is {Width(edges, 1)} where the row leaves it {heading.Bounds.Width - others} (chosen {pane.ColumnWidths.Name * scale})");

        if (Width(edges, 1) < pane.ColumnWidths.Name * scale - 0.75)
            Assert.True(Width(edges, Trailing) < 0.75 || Width(edges, 1) <= PaneScale.NameMin * scale + 0.75,
                $"{when}: {Width(edges, Trailing)} pixels left empty after the last column while the name is below its width");
    }

    /// <summary>
    /// **A narrower window first uses up the room after the last column, and
    /// only then the name.** QA measured the round-2 build squeezing the name
    /// from the first pixel the window lost, with room still empty after
    /// Modified. With 200 pixels of room after the last column: a window 120
    /// narrower leaves the name alone, and one 400 narrower takes 200 from it
    /// — the rows with it, at 100% and 125%.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public async Task A_narrower_window_uses_the_empty_room_before_the_name(double zoom)
    {
        var (window, shell) = await Open(zoom, split: false);
        var pane = shell.ActiveTab!;

        Drag(window, pane, "Name", -200);

        var name = Width(Edges(Heading(window, pane), window), 1);
        var room = Width(Edges(Heading(window, pane), window), Trailing);

        Assert.Equal(200, room, 1.0);

        window.Width = 3000 - 120;
        await Settle(window, shell);

        Assert.Equal(name, Width(Edges(Heading(window, pane), window), 1), 0.75);
        NameGivesOnlyWhatIsNeeded(window, pane, "120 narrower");
        Assert.Equal(Edges(Heading(window, pane), window), Edges(Row(window, pane), window), Close);

        window.Width = 3000 - 400;
        await Settle(window, shell);

        Assert.Equal(name - 200, Width(Edges(Heading(window, pane), window), 1), 0.75);
        NameGivesOnlyWhatIsNeeded(window, pane, "400 narrower");
        EveryGripOnScreen(window, pane, "400 narrower");
    }

    /// <summary>
    /// **No column's edge goes past the pane's.** QA dragged Size 330 pixels
    /// wider in a full row and its edge, and its grip, landed 190 pixels off
    /// the side — easiest to do by dragging on into the other half of a
    /// split. Every column's edge now stops at the pane's right edge, and its
    /// grip stays where a pointer can take it back.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Type")]
    [InlineData("Size")]
    [InlineData("Modified")]
    [InlineData("Created")]
    public async Task An_edge_dragged_far_stops_at_the_panes_edge(string column)
    {
        var (window, shell) = await Open(1.0, split: true);
        var pane = shell.Left.ActiveTab!;

        shell.ActivateGroup(shell.Left);

        var heading = Heading(window, pane);
        var right = heading.TranslatePoint(new Point(heading.Bounds.Width, 0), window)!.Value.X;

        Drag(window, pane, column, 700);

        var after = Edges(Heading(window, pane), window);

        Assert.Equal(right, after[Cell(column) + 1], 1.0);

        var grip = Grip(window, pane, column);
        var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, 0), window)!.Value.X;

        Assert.True(at < BandRight(window, pane), $"the {column} grip is at {at}, off the band");
    }
    private static readonly IEqualityComparer<double> Close = new Within(0.5);

    private sealed class Within(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
