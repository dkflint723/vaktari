using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// How wide a tab's details columns are, from the number on the tab to the
/// pixels on screen and into the session. The pointer itself is
/// ColumnDragPointerTests; this is everything the drag leaves behind.
///
/// **The widths were one preference for every pane, and that was a bug.** A
/// drag in the left half of a split moved the right half's columns, because
/// both drew from the same four numbers in the settings. They are the tab's
/// now: drawn through the tab's own resources, saved and restored with the
/// tab, carried to a tab opened from it, and reset one tab at a time. The four
/// numbers in the settings survive only as what a tab with nothing to copy
/// starts from, which is how an upgrading install keeps what it had dragged.
///
/// Both statics the metric pipeline reads are put back afterwards, as
/// InterfaceTextSizeTests does and for its reason: this assembly runs its
/// classes in sequence, and a leak here lands on somebody else's assertion.
/// </summary>
public sealed class ColumnWidthTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly double? _systemBefore = InterfaceText.SystemScale;
    private MainWindow? _window;

    /// <summary>Stated, not inherited: the published value is this machine's
    /// own desktop text size, which is 125% on the desktop these were written
    /// on and 100% on the runner.</summary>
    public ColumnWidthTests() => InterfaceText.SystemScale = null;

    public override void Dispose()
    {
        _window?.Close();
        AppSettings.Apply(_settingsBefore);
        InterfaceText.SystemScale = _systemBefore;

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Configure(DetailsViewSettings details, double interfaceScale = 0)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with
            {
                InterfaceTextScale = interfaceScale,
                Details = details,
            },
        });

    private static double Metric(string key, ColumnWidths widths, double fontScale = 1.0)
        => PaneScale.Compute(fontScale, 1.0, widths).Single(m => m.Key == key).Value;

    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "vaktari-widths-" + name);

    // ---- the width a column is drawn at -----------------------------------------

    /// <summary>
    /// **Zero is "as designed", not a width.** A session written before tabs
    /// had widths, or a key nobody wrote, arrives as zero — which has to draw
    /// as the width the column always had rather than as the narrowest a
    /// column may be. For the name, as designed is "fills", which the grids
    /// read from a zero.
    /// </summary>
    [AvaloniaFact]
    public void A_column_nobody_has_dragged_is_drawn_at_its_designed_width()
    {
        Configure(new DetailsViewSettings());

        Assert.Equal(110, Metric("ColType", ColumnWidths.Designed));
        Assert.Equal(100, Metric("ColSize", ColumnWidths.Designed));
        Assert.Equal(150, Metric("ColModified", ColumnWidths.Designed));
        Assert.Equal(150, Metric("ColCreated", ColumnWidths.Designed));
        Assert.Equal(0, Metric("ColName", ColumnWidths.Designed));

        Assert.All(Enum.GetValues<DetailsColumn>(),
                   column => Assert.Equal(PaneScale.DesignedWidth(column),
                                          PaneScale.ColumnWidth(ColumnWidths.Designed, column)));
    }

    /// <summary>
    /// A chosen width rides the same scale the designed one did — the
    /// interface size and the pane's own zoom — or a column dragged to 200 at
    /// 100% would be a column dragged to 200 at every size, which is the fault
    /// the metric pipeline exists to prevent. The name's too, once it has
    /// one. And only the column that was dragged: its neighbour stays
    /// designed.
    /// </summary>
    [AvaloniaFact]
    public void A_chosen_width_is_drawn_through_the_same_scale_as_a_designed_one()
    {
        Configure(new DetailsViewSettings(), interfaceScale: 1.5);

        var widths = new ColumnWidths { Modified = 200, Name = 300 };

        Assert.Equal(300, Metric("ColModified", widths));
        Assert.Equal(600, Metric("ColModified", widths, fontScale: 2.0));
        Assert.Equal(450, Metric("ColName", widths));

        Assert.Equal(225, Metric("ColCreated", widths));
    }

    /// <summary>
    /// A session can be edited by hand, and a column 2 pixels wide is a
    /// column that has vanished with its heading still ticked — held to the
    /// range on the way in, the way InterfaceText holds the text size.
    /// </summary>
    [AvaloniaFact]
    public void A_width_written_by_hand_is_held_to_the_range()
    {
        Configure(new DetailsViewSettings());

        var widths = new ColumnWidths { Type = 2, Size = 4000, Name = 5 };

        Assert.Equal(PaneScale.ColumnMin, Metric("ColType", widths));
        Assert.Equal(PaneScale.ColumnMax, Metric("ColSize", widths));
        Assert.Equal(PaneScale.NameMin, Metric("ColName", widths));
    }

    /// <summary>
    /// The tab holds its widths to the same range as they are set, so the
    /// session never stores a width the drawing would then refuse. The STORED
    /// number is what is asserted: reading clamps too, so a drag that stored
    /// −900 would still draw something and the fault would be in the file
    /// alone.
    /// </summary>
    [AvaloniaFact]
    public void A_width_is_held_to_the_range_as_it_is_set()
    {
        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Temp("range"));

        var pane = shell.ActiveTab!;

        pane.SetColumnWidth(DetailsColumn.Size, -1000);
        Assert.Equal(PaneScale.ColumnMin, pane.ColumnWidths.Size);

        pane.SetColumnWidth(DetailsColumn.Size, 5000);
        Assert.Equal(PaneScale.ColumnMax, pane.ColumnWidths.Size);

        pane.SetColumnWidth(DetailsColumn.Name, 3);
        Assert.Equal(PaneScale.NameMin, pane.ColumnWidths.Name);
    }

    // ---- one tab's widths, and nobody else's -------------------------------------

    /// <summary>
    /// **A tab's widths reach that tab's resources and no other's.** Two
    /// panes, each with the control PaneScale writes its metrics into, as the
    /// two halves of a split have: widening a column in one moves its own
    /// dictionary — which is where its heading and its rows resolve their
    /// widths — and leaves the other's at what it was. The window-level half of
    /// this, with a pointer, is in ColumnDragPointerTests.
    /// </summary>
    [AvaloniaFact]
    public void A_tabs_width_reaches_its_own_resources_and_no_others()
    {
        Configure(new DetailsViewSettings());

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Temp("left"));
        shell.ToggleSplit();

        var left = shell.Left.ActiveTab!;
        var right = shell.Right!.ActiveTab!;
        var leftControl = new Panel();
        var rightControl = new Panel();

        PaneScale.SetPane(leftControl, left);
        PaneScale.SetPane(rightControl, right);

        Assert.Equal(150.0, leftControl.Resources["ColModified"]);
        Assert.Equal(150.0, rightControl.Resources["ColModified"]);

        left.SetColumnWidth(DetailsColumn.Modified, 200);
        left.SetColumnWidth(DetailsColumn.Name, 300);

        Assert.Equal(200.0, leftControl.Resources["ColModified"]);
        Assert.Equal(300.0, leftControl.Resources["ColName"]);

        Assert.Equal(150.0, rightControl.Resources["ColModified"]);
        Assert.Equal(0.0, rightControl.Resources["ColName"]);
        Assert.Equal(ColumnWidths.Designed, right.ColumnWidths);

        // And a zoom re-applies every metric from the tab's own widths, not
        // from the ones a new tab starts at.
        left.FontScale = 2.0;

        Assert.Equal(400.0, leftControl.Resources["ColModified"]);
        Assert.Equal(600.0, leftControl.Resources["ColName"]);
    }

    /// <summary>
    /// The way back from a drag that went too far: every column of THIS tab
    /// to its designed width and the name to filling — from the heading's own
    /// menu, which runs on the tab it was opened over, and from the shell's,
    /// which runs on the active tab. The other half of the split keeps its
    /// widths, and so do the starting widths in the settings: a reset is as
    /// local as the drag it undoes.
    /// </summary>
    [AvaloniaFact]
    public void Reset_puts_back_this_tabs_columns_alone()
    {
        var starting = new DetailsViewSettings { TypeColumn = 300 };

        Configure(starting);

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Temp("reset"));
        shell.ToggleSplit();

        var left = shell.Left.ActiveTab!;
        var right = shell.Right!.ActiveTab!;

        left.SetColumnWidth(DetailsColumn.Size, 250);
        left.SetColumnWidth(DetailsColumn.Name, 400);
        right.SetColumnWidth(DetailsColumn.Size, 70);

        left.ResetColumnWidthsCommand.Execute(null);

        Assert.Equal(ColumnWidths.Designed, left.ColumnWidths);
        Assert.True(left.NameFills);
        Assert.Equal(70, right.ColumnWidths.Size);

        shell.ActivateGroup(shell.Right!);
        shell.ResetColumnWidthsCommand.Execute(null);

        Assert.Equal(ColumnWidths.Designed, right.ColumnWidths);
        Assert.Equal(starting, AppSettings.Current.Views.Details);
    }

    // ---- the session -------------------------------------------------------------

    /// <summary>
    /// **The widths are saved with the session as they change, and come back
    /// with it, per tab.** Two tabs on one side and one on the other, all
    /// three different: the session the shell hands its store holds each
    /// tab's own, and a shell started from that session puts each back on its
    /// own tab. Read from the state handed to the store, not from the file:
    /// the file's reader answers from a backup when it cannot read, which a
    /// test must never assert through.
    /// </summary>
    [AvaloniaFact]
    public void Each_tabs_widths_go_into_the_session_and_come_back_from_it()
    {
        Configure(new DetailsViewSettings());

        var store = new LastSession();
        var shell = Own(new ShellViewModel(new InertFileSystem(), store: store));

        shell.Start(null, Temp("one"));

        var first = shell.ActiveTab!;
        var second = shell.Left.AddTab(Temp("two"));

        shell.ToggleSplit();

        var other = shell.Right!.ActiveTab!;

        first.SetColumnWidth(DetailsColumn.Name, 320);
        second.SetColumnWidth(DetailsColumn.Size, 180);
        other.SetColumnWidth(DetailsColumn.Created, 90);

        var saved = Assert.Single(Assert.IsType<SessionState>(store.Last).Windows);

        Assert.Equal(new ColumnWidths { Name = 320 }, saved.Panes[0].Tabs[0].Widths);
        Assert.Equal(new ColumnWidths { Size = 180 }, saved.Panes[0].Tabs[1].Widths);
        Assert.Equal(new ColumnWidths { Created = 90 }, saved.Panes[1].Tabs[0].Widths);

        var restored = Own(new ShellViewModel(new InertFileSystem()));

        restored.Start(store.Last);

        Assert.Equal(320, restored.Left.Tabs[0].ColumnWidths.Name);
        Assert.Equal(180, restored.Left.Tabs[1].ColumnWidths.Size);
        Assert.Equal(90, restored.Right!.Tabs[0].ColumnWidths.Created);
        Assert.Equal(ColumnWidths.Designed with { Size = 180 }, restored.Left.Tabs[1].ColumnWidths);
    }

    /// <summary>
    /// **An upgrade keeps what was dragged.** A settings.json from when the
    /// widths were one preference carries them, and a session from then has
    /// no widths on its tabs at all: those tabs come back at the settings'
    /// widths rather than at the designed ones, and so does a tab with nothing
    /// to copy. A tab that does carry widths takes its own.
    /// </summary>
    [AvaloniaFact]
    public void A_session_from_before_tabs_had_widths_keeps_the_ones_the_settings_carried()
    {
        Configure(new DetailsViewSettings { TypeColumn = 200, ModifiedColumn = 90 });

        var session = new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes =
                    [
                        new PaneState
                        {
                            Tabs =
                            [
                                new TabState { Path = Temp("old") },
                                new TabState { Path = Temp("new"), Widths = new ColumnWidths { Size = 70 } },
                            ],
                        },
                    ],
                },
            ],
        };

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(session);

        Assert.Equal(new ColumnWidths { Type = 200, Modified = 90 }, shell.Left.Tabs[0].ColumnWidths);
        Assert.Equal(new ColumnWidths { Size = 70 }, shell.Left.Tabs[1].ColumnWidths);

        var fresh = Own(new ShellViewModel(new InertFileSystem()));

        fresh.Start(null, Temp("fresh"));

        Assert.Equal(new ColumnWidths { Type = 200, Modified = 90 }, fresh.ActiveTab!.ColumnWidths);
    }

    /// <summary>
    /// The file's half of the two above: a tab's widths are written into
    /// session.json and read back from it, and a tab written before they
    /// existed reads back as NULL — not as the designed widths — which is
    /// the one value the restore takes as "use what the settings carried".
    /// </summary>
    [AvaloniaFact]
    public void The_session_file_carries_a_tabs_widths_and_an_old_tab_reads_as_none()
    {
        var written = new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes = [new PaneState { Tabs = [new TabState { Path = "/a", Widths = new ColumnWidths { Name = 310, Created = 95 } }] }],
                },
            ],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(written, SessionJsonContext.Default.SessionState);
        var read = System.Text.Json.JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState)!;

        Assert.Equal(new ColumnWidths { Name = 310, Created = 95 }, read.Windows[0].Panes[0].Tabs[0].Widths);

        var old = System.Text.Json.JsonSerializer.Deserialize(
            """{"version":13,"windows":[{"panes":[{"tabs":[{"path":"/old","showType":true}]}]}]}""",
            SessionJsonContext.Default.SessionState)!;

        Assert.Null(old.Windows[0].Panes[0].Tabs[0].Widths);
    }

    // ---- where a new tab starts ----------------------------------------------------

    /// <summary>
    /// A tab opened from another starts with its widths, the way it starts
    /// with its column ticks, its sort and its zoom — Ctrl+T, and a window
    /// opened from this one. Copied, not shared: widening the new one leaves
    /// the one it came from alone.
    /// </summary>
    [AvaloniaFact]
    public void A_tab_or_window_opened_from_a_tab_starts_with_its_widths()
    {
        Configure(new DetailsViewSettings());

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Temp("from"));

        var from = shell.ActiveTab!;

        from.SetColumnWidth(DetailsColumn.Name, 280);
        from.SetColumnWidth(DetailsColumn.Type, 160);

        shell.NewTabCommand.Execute(null);

        var tab = shell.ActiveTab!;

        Assert.NotSame(from, tab);
        Assert.Equal(from.ColumnWidths, tab.ColumnWidths);

        tab.SetColumnWidth(DetailsColumn.Type, 60);

        Assert.Equal(160, from.ColumnWidths.Type);

        var window = Own(new ShellViewModel(new InertFileSystem())
        {
            LikeTab = from,
        });

        window.Start(null, Temp("window"));

        Assert.Equal(new ColumnWidths { Name = 280, Type = 160 }, window.ActiveTab!.ColumnWidths);
    }

    /// <summary>
    /// **The other half of a split starts at the starting widths, not at this
    /// half's**, because it starts from nothing else of this half's either —
    /// not its layout, its sort, its zoom or its column ticks — and because a
    /// name given its width in a pane twice as wide would push the dates off
    /// the edge of the half it lands in. A half reopened after being closed
    /// comes back with its own.
    /// </summary>
    [AvaloniaFact]
    public void The_other_half_of_a_split_starts_at_the_starting_widths_and_comes_back_with_its_own()
    {
        Configure(new DetailsViewSettings { SizeColumn = 120 });

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Temp("split"));
        shell.ActiveTab!.SetColumnWidth(DetailsColumn.Name, 500);

        shell.ToggleSplit();

        var right = shell.Right!.ActiveTab!;

        Assert.Equal(new ColumnWidths { Size = 120 }, right.ColumnWidths);

        right.SetColumnWidth(DetailsColumn.Modified, 210);

        shell.ToggleSplit();
        shell.ToggleSplit();

        Assert.Equal(new ColumnWidths { Size = 120, Modified = 210 }, shell.Right!.ActiveTab!.ColumnWidths);
    }

    /// <summary>
    /// A tab closed by accident and put back with Ctrl+Shift+T comes back at
    /// the widths it had, with the rest of its view.
    /// </summary>
    [AvaloniaFact]
    public void A_reopened_tab_comes_back_with_its_widths()
    {
        Configure(new DetailsViewSettings());

        var shell = Own(new ShellViewModel(new InertFileSystem()));

        shell.Start(null, Temp("keep"));

        var closing = shell.Left.AddTab(Temp("closing"));

        closing.SetColumnWidth(DetailsColumn.Created, 222);
        shell.CloseTabCommand.Execute(closing);

        Assert.DoesNotContain(closing, shell.Left.Tabs);

        shell.Left.ReopenClosedTab();

        Assert.Equal(222, shell.Left.ActiveTab!.ColumnWidths.Created);
    }

    // ---- the window ----------------------------------------------------------------

    /// <summary>
    /// **A grip hides WITH its heading, and that is not tidiness.** The cell
    /// is Auto, so a grip left visible in a column that is off keeps the cell
    /// five pixels wide with nothing in it, and the rows — whose cell really
    /// is empty — draw five pixels left of the headings from there on.
    ///
    /// The column's state is SET rather than read: the window is built from
    /// the session on disk, and a session whose last tab had the column on
    /// would read the first assertion backwards.
    /// </summary>
    [AvaloniaFact]
    public void A_grip_hides_with_its_column()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow { Width = 1800, Height = 800 };

        window.Show();
        Pump();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        pane.ShowCreatedColumn = false;
        Pump();
        window.UpdateLayout();

        var grip = Grip(window, pane, DetailsColumn.Created);
        var header = Assert.IsType<Grid>(grip.Parent);

        Assert.Equal(0, header.ColumnDefinitions[6].ActualWidth);

        pane.ShowCreatedColumn = true;
        Pump();
        window.UpdateLayout();

        Assert.True(header.ColumnDefinitions[6].ActualWidth >= PaneScale.ColumnMin,
                    $"with the column on its cell was {header.ColumnDefinitions[6].ActualWidth} wide");
    }

    /// <summary>
    /// **What a pointer drag leaves is the tab's, and it is what the session
    /// is handed.** A real press, moves and release on the size grip, in a
    /// pane at 150%: after the release the tab holds the width the pointer
    /// left it at (in pixels at 100%, so 60 pixels on screen are 40), the name
    /// holds the width it was drawn at when the drag began, and the window's
    /// session — what the store writes and the next start restores, as the
    /// session tests above follow through — says the same. No settings file is
    /// written at all: the drag is not a preference any more.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pointer_drag_stays_after_release_and_is_what_the_session_holds()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow { Width = 2400, Height = 800 };

        window.Show();
        Pump();

        InterfaceText.SystemScale = null;
        Configure(new DetailsViewSettings());

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Details;
        pane.HideSizeColumn = false;
        pane.ResetColumnWidthsCommand.Execute(null);
        pane.FontScale = 1.5;
        shell.RefreshPaneScales();
        Pump();
        window.UpdateLayout();

        var grip = Grip(window, pane, DetailsColumn.Size);
        var heading = Assert.IsType<Grid>(grip.Parent);
        var drawnName = heading.ColumnDefinitions[1].ActualWidth;
        var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        var settingsBefore = window.Services.SettingsStore.Load().Views.Details;

        window.MouseDown(at, MouseButton.Left);
        Pump();

        for (var step = 1; step <= 3; step++)
        {
            window.MouseMove(at + new Point(20 * step, 0));
            Pump();
        }

        window.MouseUp(at + new Point(60, 0), MouseButton.Left);
        Pump();

        Assert.Equal(140, pane.ColumnWidths.Size, 0.1);
        Assert.Equal(drawnName / 1.5, pane.ColumnWidths.Name, 0.1);

        var tab = shell.ToWindowSession().Panes[0].Tabs[shell.Left.Tabs.IndexOf(pane)];

        Assert.Equal(pane.ColumnWidths, tab.Widths);

        await window.Services.SettingsStore.Writes.Idle;

        Assert.Equal(settingsBefore, window.Services.SettingsStore.Load().Views.Details);

        pane.ResetColumnWidthsCommand.Execute(null);
    }

    /// <summary>
    /// **A click on an edge that goes nowhere changes nothing**, the name's
    /// filling included: the name takes a width of its own when an edge is
    /// moved, not when one is pressed, so a stray click does not quietly stop
    /// the name following the window as it is resized.
    /// </summary>
    [AvaloniaFact]
    public void A_click_on_an_edge_leaves_the_name_filling()
    {
        UseSearch(PaneViewModel.Search);

        var window = _window = new MainWindow { Width = 2400, Height = 800 };

        window.Show();
        Pump();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        pane.View = ViewMode.Details;
        pane.HideSizeColumn = false;
        pane.ResetColumnWidthsCommand.Execute(null);
        Pump();
        window.UpdateLayout();

        var grip = Grip(window, pane, DetailsColumn.Size);
        var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;

        window.MouseDown(at, MouseButton.Left);
        Pump();
        window.MouseUp(at, MouseButton.Left);
        Pump();

        Assert.True(pane.NameFills, $"a click with no move gave the name {pane.ColumnWidths.Name}");
        Assert.Equal(ColumnWidths.Designed, pane.ColumnWidths);
    }

    /// <summary>
    /// **A row nested under a folder opened in place keeps its cells under
    /// the headings once the name has a width.** The indent is docked into
    /// the first column, which the name used to absorb by filling; with a
    /// width of its own the name is drawn the indent narrower on that row, so
    /// its edge and every cell after it land where the heading's do.
    /// </summary>
    [AvaloniaFact]
    public async Task A_nested_row_lines_up_with_the_headings_when_the_name_has_a_width()
    {
        UseSearch(PaneViewModel.Search);

        var root = Directory.CreateTempSubdirectory("vaktari-widths-nest-").FullName;

        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "child.txt"), "child");

        try
        {
            var window = _window = new MainWindow { Width = 1800, Height = 800 };

            window.Show();
            Pump();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            pane.View = ViewMode.Details;
            pane.ShowTypeColumn = true;

            await pane.NavigateAsync(root);
            await pane.RefreshAsync();

            var sub = pane.Entries.Single(e => e.Name == "sub");

            await pane.ToggleExpandAsync(sub);

            pane.SetColumnWidth(DetailsColumn.Name, 300);

            var child = Path.Combine(root, "sub", "child.txt");
            Grid? row = null;

            for (var i = 0; i < 400 && row is null; i++)
            {
                Pump();
                window.UpdateLayout();

                row = window.GetVisualDescendants().OfType<ListBoxItem>()
                    .Where(item => item.IsVisible && item.DataContext is FileEntry { FullPath: var p } && p == child)
                    .Select(item => item.GetVisualDescendants().OfType<Grid>()
                                        .First(g => g.Margin == new global::Avalonia.Thickness(12, 0, 18, 0)))
                    .FirstOrDefault();

                if (row is null) await Task.Delay(10);
            }

            Assert.NotNull(row);
            Assert.True(pane.Indents.ContainsKey(child), "the child row is not indented, so this measures nothing");

            var heading = Assert.IsType<Grid>(Grip(window, pane, DetailsColumn.Size).Parent);

            Assert.Equal(Right(heading, 1, window), Right(row!, 1, window), 0.5);
            Assert.Equal(Right(heading, 4, window), Right(row!, 4, window), 0.5);
        }
        finally
        {
            if (_window?.DataContext is ShellViewModel shell) shell.ActiveTab?.ResetColumnWidthsCommand.Execute(null);

            _window?.Close();
            _window = null;

            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a temp dir is not worth failing over */ }
        }
    }

    private static double Right(Grid grid, int column, global::Avalonia.Visual window)
        => grid.TranslatePoint(default, window)!.Value.X
           + grid.ColumnDefinitions.Take(column + 1).Sum(c => c.ActualWidth);

    // ---- the markup ----------------------------------------------------------------

    /// <summary>
    /// The markup half of the above, for all five: each grip sits in its
    /// column's cell, hides on exactly the heading's condition (the name's
    /// never hides), names a column the tab knows, says what it is to a screen
    /// reader, and is wired to the three handlers. The shape they share —
    /// width, the straddle, the cursor, the bare template — is one Style, so
    /// it cannot drift between them. And they come after every heading in the
    /// grid, which is what puts each on top of the heading it straddles.
    /// </summary>
    [AvaloniaFact]
    public void Each_grip_sits_in_its_columns_cell_and_hides_with_it()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var grips = doc.Descendants(Avalonia + "Thumb")
                       .Where(t => (string?)t.Attribute("Classes") == "columnGrip")
                       .ToList();

        Assert.Equal(
            Enum.GetValues<DetailsColumn>().Order(),
            grips.Select(g => Enum.Parse<DetailsColumn>((string)g.Attribute("Tag")!)).Order());

        var grid = grips[0].Parent!;

        Assert.All(grips, grip => Assert.Same(grid, grip.Parent));

        var lastHeading = grid.Elements().Last(e => e.Name != Avalonia + "Thumb");

        Assert.All(grips, grip => Assert.True(grip.IsAfter(lastHeading), $"the {grip.Attribute("Tag")} grip is under a heading"));

        foreach (var grip in grips)
        {
            var cell = (string?)grip.Attribute("Grid.Column");

            if ((string?)grip.Attribute("Tag") == "Name")
            {
                Assert.Equal("1", cell);
                Assert.Null(grip.Attribute("IsVisible"));
            }
            else
            {
                var heading = grid.Elements(Avalonia + "Button")
                                  .Single(b => (string?)b.Attribute("Grid.Column") == cell);

                Assert.NotNull((string?)heading.Attribute("IsVisible"));
                Assert.Equal((string?)heading.Attribute("IsVisible"), (string?)grip.Attribute("IsVisible"));
            }

            Assert.StartsWith("Resize the ", (string?)grip.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
            Assert.Equal("OnColumnGripDragStarted", (string?)grip.Attribute("DragStarted"));
            Assert.Equal("OnColumnGripDragDelta", (string?)grip.Attribute("DragDelta"));
            Assert.Equal("OnColumnGripDragCompleted", (string?)grip.Attribute("DragCompleted"));
            Assert.Equal("OnColumnGripDoubleTapped", (string?)grip.Attribute("DoubleTapped"));
        }

        var style = doc.Descendants(Avalonia + "Style")
                       .Single(s => (string?)s.Attribute("Selector") == "Thumb.columnGrip");

        var setters = style.Elements(Avalonia + "Setter").ToList();

        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "Cursor"
                                      && (string?)s.Attribute("Value") == "SizeWestEast");
        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "HorizontalAlignment"
                                      && (string?)s.Attribute("Value") == "Right");
        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "Template");
    }

    /// <summary>
    /// The way back is on both menus that choose the columns — the one on
    /// the headings and the one a keyboard opens, since the grips are a
    /// pointer's — and each binds to a tab, like the column ticks beside it:
    /// the heading's to the pane under it, the listing menu's to the active
    /// tab. Neither reaches out through the window any more, which is what
    /// the widths being one preference for every pane used to need.
    /// </summary>
    [AvaloniaFact]
    public void Both_column_menus_offer_the_way_back()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var commands = doc.Descendants(Avalonia + "MenuItem")
                          .Where(m => (string?)m.Attribute("Header") == "Reset column _widths")
                          .Select(m => (string?)m.Attribute("Command"))
                          .Order(StringComparer.Ordinal)
                          .ToList();

        Assert.Equal(
            [
                "{Binding ActiveTab.ResetColumnWidthsCommand}",
                "{Binding ResetColumnWidthsCommand}",
            ],
            commands);
    }

    /// <summary>
    /// *Size all columns to fit* is on both menus too, bound the same way —
    /// the heading's to the pane under it, the listing menu's to the active
    /// tab — and sits above the way back in each.
    /// </summary>
    [AvaloniaFact]
    public void Both_column_menus_offer_the_fit()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var rows = doc.Descendants(Avalonia + "MenuItem")
                      .Where(m => (string?)m.Attribute("Header") == "Size all columns to _fit")
                      .ToList();

        Assert.Equal(
            [
                "{Binding ActiveTab.SizeAllColumnsToFitCommand}",
                "{Binding SizeAllColumnsToFitCommand}",
            ],
            rows.Select(m => (string?)m.Attribute("Command")).Order(StringComparer.Ordinal).ToList());

        Assert.All(rows, row => Assert.Equal("Reset column _widths",
            (string?)row.ElementsAfterSelf(Avalonia + "MenuItem").First().Attribute("Header")));
    }

    // ---- helpers ----------------------------------------------------------------

    private static Thumb Grip(Window window, PaneViewModel pane, DetailsColumn column)
        => window.GetVisualDescendants()
                 .OfType<Thumb>()
                 .Single(t => (string?)t.Tag == column.ToString() && ReferenceEquals(t.DataContext, pane));

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>A store that keeps the last session it was handed.</summary>
    private sealed class LastSession : ISessionStore
    {
        public SessionState? Last { get; private set; }

        public SessionState? Load() => Last;
        public void NotifyChanged(SessionState state) => Last = state;
        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class InertFileSystem : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
