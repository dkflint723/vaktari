using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What follows the pointer while files are being dragged.
///
/// **The destination half of a drag was answered and the source half was the
/// cursor.** A folder row under the pointer takes a ring and a place takes a
/// wash, so where a drop would land is said out loud — but nothing said what
/// was in the hand. Once the pointer has left the rows the drag started on,
/// which is every drag worth making (into the other pane, onto a place, two
/// folders down), the count and the name of what is being carried are off
/// screen, and letting go is the only way to find out whether it was the one
/// file or the eleven.
///
/// Avalonia has no drag image to hand it to: <c>DragDrop.DoDragDropAsync</c>
/// takes a trigger, a payload and a set of effects, and that is the whole
/// signature. So the ghost is a label this window draws on its own overlay and
/// moves from the drag-over handler.
///
/// **The handlers are driven, not read** — the same route
/// <see cref="SidebarPinDropTests"/> opened. A DragEventArgs has a public
/// constructor, the window's own StorageProvider fills a DataTransfer, and
/// raising the event runs the real handler, so these tests fail when the
/// feature is dead rather than when a word is missing from the source.
/// </summary>
public sealed class DragGhostTests : OwnedViewModels
{
    // ---- what it says --------------------------------------------------------

    private static string Under(string name) => Path.Combine(Path.GetTempPath(), name);

    /// <summary>
    /// The one is named. The whole complaint is not knowing WHAT is being
    /// dragged, and for a single file the name is the answer — "1 item" would
    /// be the count of a thing already obvious.
    /// </summary>
    [Fact]
    public void The_one_thing_dragged_is_named()
        => Assert.Equal("q4-forecast.xlsx",
                        Vaktari.Ui.Input.DragGhost.Label([Under("q4-forecast.xlsx")]));

    /// <summary>And the many are counted, the way the undo row already counts
    /// them — a list of eleven names beside the pointer is unreadable, and the
    /// number is the part that was missing.</summary>
    [Fact]
    public void The_many_are_counted()
        => Assert.Equal("3 items", Vaktari.Ui.Input.DragGhost.Label(
            [Under("a.txt"), Under("b.txt"), Under("c.txt")]));

    /// <summary>
    /// A drag that carries no paths says nothing, and the caller draws nothing
    /// rather than a label reading "0 items".
    ///
    /// **What reaches this branch is a drag Vaktari did not start.** The
    /// handler hands an empty list straight through for one of those rather
    /// than reading its payload at all — so the empty case is not an
    /// unusual-looking drag, it is every foreign drag, on every drag-over. The
    /// archive case this used to name is real but arrives somewhere else:
    /// DroppedFileReader.HasVirtualFiles documents it, and the drags this
    /// window starts are all built from a local path.
    /// </summary>
    [Fact]
    public void A_drag_carrying_no_paths_says_nothing()
        => Assert.Equal("", Vaktari.Ui.Input.DragGhost.Label([]));

    /// <summary>
    /// **The ghost said "Firefox.lnk" over a row reading "Firefox".** Every
    /// name cell in the window draws FileKind.DisplayName, which hides a
    /// Windows shortcut's extension; the label read the raw leaf name. The
    /// assertion is the agreement, not the string: what a listing decides to
    /// show is what a drag out of it has to say.
    /// </summary>
    [Fact]
    public void A_shortcut_is_named_the_way_its_row_names_it()
    {
        var path = Under("Firefox.lnk");

        Assert.Equal(
            Vaktari.Core.FileSystem.FileKind.DisplayName(
                new Vaktari.Core.FileSystem.FileEntry(
                    "Firefox.lnk", path, 0, default, Vaktari.Core.FileSystem.EntryFlags.None)),
            Vaktari.Ui.Input.DragGhost.Label([path]));

        // Said outright as well, because the line above would also hold if both
        // sides broke together.
        Assert.Equal("Firefox", Vaktari.Ui.Input.DragGhost.Label([path]));
    }

    /// <summary>
    /// The same on the other platform's shortcut, where the divergence was
    /// total: the row reads "Konsole" and the file is called
    /// org.kde.konsole.desktop. Driven through FileKind's own launcher seam,
    /// which is what a Linux composition root fills in.
    /// </summary>
    [Fact]
    public void A_launcher_is_named_the_way_its_row_names_it()
    {
        Vaktari.Core.FileSystem.FileKind.LauncherName = _ => "Konsole";

        Assert.Equal("Konsole",
                     Vaktari.Ui.Input.DragGhost.Label([Under("org.kde.konsole.desktop")]));
    }

    /// <summary>
    /// A folder is not a shortcut however it is spelled, so one called
    /// "Notes.lnk" keeps every character — the row it came out of does, because
    /// FileKind reads that off the entry's flags rather than off its extension,
    /// and the label has to hand it the same answer.
    /// </summary>
    [Fact]
    public void A_folder_that_looks_like_a_shortcut_keeps_its_name()
    {
        Vaktari.Ui.Input.DragGhost.IsFolder = _ => true;

        Assert.Equal("Notes.lnk", Vaktari.Ui.Input.DragGhost.Label([Under("Notes.lnk")]));
    }

    /// <summary>
    /// **The disk is asked only when the answer could differ**, because asking
    /// is 28 µs and this runs on every drag-over — measured on this machine at
    /// 200,000 warm Directory.Exists calls: 28.3 µs for an existing file, 30.8
    /// for a folder, 18.5 for a path that is not there. On the thread that is
    /// following the pointer, and on a networked path, that is not a rounding
    /// error.
    ///
    /// Counted rather than timed: skipping a stat changes no answer, so nothing
    /// but the count can tell the guard from its absence.
    /// </summary>
    [Fact]
    public void An_ordinary_name_is_never_asked_of_the_disk()
    {
        var asked = 0;

        Vaktari.Ui.Input.DragGhost.IsFolder = _ => { asked++; return false; };

        Vaktari.Ui.Input.DragGhost.Label([Under("q4-forecast.xlsx")]);

        Assert.Equal(0, asked);

        // And the case that does need it still asks, or the line above would
        // hold just as well with the whole thing deleted.
        Vaktari.Ui.Input.DragGhost.Label([Under("Firefox.lnk")]);

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// **A name with newlines in it turned the label into a slab**, because the
    /// box is bounded in width and nothing bounded its height — and on Linux
    /// only "/" and NUL are illegal in a file name, so this is content whoever
    /// made the file chose. Measured through the window's own controls before
    /// the fix: 136 × 532 against a one-line 124 × 22, which Spot then parked
    /// at the top of the window instead of beside the pointer.
    ///
    /// Asserted on the string rather than on the drawn box because the drawn
    /// box cannot be reached from here: Win32 refuses to create a file whose
    /// name holds a control character, so this suite cannot put one through a
    /// real drag. <see cref="A_name_with_newlines_in_it_stays_one_line_high"/>
    /// takes the label the rest of the way, into the real controls.
    /// </summary>
    [Fact]
    public void A_name_with_newlines_in_it_is_flattened()
        => Assert.Equal("notesandmore.txt",
                        Vaktari.Ui.Input.DragGhost.Label([Under("notes\nand\rmore.txt")]));

    // ---- where it sits -------------------------------------------------------

    private static readonly Size Ghost = new(80, 22);

    /// <summary>
    /// Beside the pointer, never under it. A label centred on the pointer would
    /// cover the row being aimed at, which is the one thing a drag has to keep
    /// showing.
    /// </summary>
    [Fact]
    public void The_ghost_sits_off_the_pointer()
        => Assert.Equal(new Point(314, 214),
                        Vaktari.Ui.Input.DragGhost.Spot(new Point(300, 200), Ghost, new Size(1000, 800)));

    /// <summary>
    /// **Flipped at the right edge, not clamped to it.** Clamping puts the
    /// label's far edge on the window's, which walks the pointer inside the
    /// label — so the closer you get to the edge the more of the target you are
    /// aiming at is hidden. Flipping keeps the gap on the side there is room
    /// for, and the assertion is that one: the whole label is clear of the
    /// pointer.
    /// </summary>
    [Fact]
    public void At_the_right_edge_the_ghost_flips_to_the_other_side()
    {
        var spot = Vaktari.Ui.Input.DragGhost.Spot(new Point(980, 200), Ghost, new Size(1000, 800));

        Assert.Equal(886, spot.X);
        Assert.True(spot.X + Ghost.Width <= 980, "the ghost is drawn over the pointer");
    }

    /// <summary>The same at the bottom, where a label hanging below the pointer
    /// would otherwise be cut off by the window edge.</summary>
    [Fact]
    public void At_the_bottom_edge_the_ghost_flips_above_the_pointer()
    {
        var spot = Vaktari.Ui.Input.DragGhost.Spot(new Point(300, 790), Ghost, new Size(1000, 800));

        Assert.Equal(754, spot.Y);
        Assert.True(spot.Y + Ghost.Height <= 790, "the ghost is drawn over the pointer");
    }

    /// <summary>A window with nowhere to put the label on either side still has
    /// to put it somewhere inside itself: a negative position draws it off the
    /// glass, which is the same as not drawing it.</summary>
    [Fact]
    public void The_ghost_never_starts_outside_the_window()
    {
        var spot = Vaktari.Ui.Input.DragGhost.Spot(new Point(5, 5), new Size(200, 22), new Size(60, 40));

        Assert.True(spot.X >= 0 && spot.Y >= 0, $"the ghost is drawn off the window at {spot}");
    }

    /// <summary>
    /// A layer that has not been laid out reports no size, and clamping against
    /// no size makes every position an overflow — which would park the ghost in
    /// the top-left corner for the whole of that drag instead of following the
    /// pointer.
    /// </summary>
    [Fact]
    public void A_layer_with_no_size_yet_is_not_clamped_against()
        => Assert.Equal(new Point(314, 214),
                        Vaktari.Ui.Input.DragGhost.Spot(new Point(300, 200), Ghost, default));

    // ---- the real window, with a real drag -----------------------------------

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaktari-ghost-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>The window, shown and settled. Its stores go to this class's own
    /// directory, per TestState.</summary>
    private MainWindow Shown()
    {
        // A real window assigns the platform's own search backend to the pane's
        // static; borrowed and given back, so no later class runs a real
        // recursive walk of the machine.
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        window.Show();
        Pump();
        Pump();

        return window;
    }

    private static Border GhostOf(MainWindow window)
        => window.FindControl<Border>("DragGhostBox")
           ?? throw new InvalidOperationException("the window has no drag ghost in it");

    private static TextBlock GhostTextOf(MainWindow window)
        => window.FindControl<TextBlock>("DragGhostText")
           ?? throw new InvalidOperationException("the drag ghost has no label in it");

    private static Canvas LayerOf(MainWindow window)
        => window.FindControl<Canvas>("BandLayer")
           ?? throw new InvalidOperationException("the window has no overlay layer");

    /// <summary>
    /// A drag carrying these paths, built the way the window's own drag builds
    /// one — through the StorageProvider, which the headless top level answers
    /// with real Bcl storage items.
    /// </summary>
    private static async Task<DataTransfer> Carrying(TopLevel top, params string[] paths)
    {
        var data = new DataTransfer();

        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                : await top.StorageProvider.TryGetFileFromPathAsync(path);

            Assert.True(item is not null, "the drag could not be given " + path);

            data.Add(DataTransferItem.CreateFile(item!));
        }

        return data;
    }

    /// <summary>
    /// Marks the drag in flight as one this APPLICATION started, which is the
    /// only state a real BeginDragAsync leaves behind that a test cannot reach:
    /// it blocks on the platform's own drag loop.
    ///
    /// Takes no window, and that is the point of it — the flag belongs to the
    /// application because the ghost has to survive the crossing from one
    /// Vaktari window to another. A static, so <see cref="Dispose"/> puts it
    /// back.
    /// </summary>
    private static void OurOwnDrag(bool ours)
        => InFlight.SetValue(null, ours);

    private static readonly FieldInfo InFlight = typeof(MainWindow)
        .GetField("_dragBegunInThisApplication", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "MainWindow no longer records that a drag started in this application");

    /// <summary>
    /// **Three statics are borrowed here and all three are given back.** The
    /// in-flight flag would otherwise leave every later class in this assembly
    /// drawing a ghost over a drag nobody started; FileKind's launcher seam
    /// would rename every .desktop file in the run; and the folder seam would
    /// answer for a disk that was never asked.
    /// </summary>
    private readonly Func<string, string?>? _launcherBefore
        = Vaktari.Core.FileSystem.FileKind.LauncherName;

    private readonly Func<string, bool>? _isFolderBefore = Vaktari.Ui.Input.DragGhost.IsFolder;

    public override void Dispose()
    {
        InFlight.SetValue(null, false);
        Vaktari.Core.FileSystem.FileKind.LauncherName = _launcherBefore;
        Vaktari.Ui.Input.DragGhost.IsFolder = _isFolderBefore;

        base.Dispose();
    }

    private static void Raise(
        MainWindow window, RoutedEvent<DragEventArgs> which, DataTransfer data, Point point)
        => window.RaiseEvent(new DragEventArgs(which, data, window, point, KeyModifiers.None));

    /// <summary>Where the pointer is, in the overlay's own coordinates — which
    /// is what the handler positions the ghost against.</summary>
    private static Point OnTheLayer(MainWindow window, Point point)
    {
        var origin = LayerOf(window).TranslatePoint(default, window);

        Assert.True(origin is not null, "the overlay is not laid out");

        return new Point(point.X - origin!.Value.X, point.Y - origin.Value.Y);
    }

    /// <summary>
    /// The count, from the real handler. This is the whole finding: three files
    /// leave the rows they were selected in and the drag can still say there
    /// are three of them.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_of_three_files_says_three_items()
    {
        var dir = Temp();
        var files = new[] { "a.txt", "b.txt", "c.txt" }
            .Select(name => Path.Combine(dir, name)).ToArray();

        foreach (var file in files) File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, files), new Point(400, 300));

            Assert.True(GhostOf(window).IsVisible, "a drag over the window draws nothing");
            Assert.Equal("3 items", GhostTextOf(window).Text);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>And one file is named rather than counted.</summary>
    [AvaloniaFact]
    public async Task A_drag_of_one_file_names_it()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "q4-forecast.xlsx");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, file), new Point(400, 300));

            Assert.Equal("q4-forecast.xlsx", GhostTextOf(window).Text);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Nothing on the glass until something is dragged. The handler decides
    /// what to draw on every drag-over, so the only state it never speaks for
    /// is the one before the first one — and a ghost that ships visible is an
    /// empty bordered box sitting over the listing from the moment the window
    /// opens.
    /// </summary>
    [AvaloniaFact]
    public void A_window_that_has_seen_no_drag_shows_no_ghost()
    {
        var window = Shown();

        try
        {
            Assert.False(GhostOf(window).IsVisible, "the ghost is on screen before any drag");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And a real shortcut through the real handler, because the label rule and
    /// the handler are two places to get this wrong. Measured before the fix,
    /// this drew "Firefox.lnk" while the row it came from read "Firefox".
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_of_a_shortcut_says_what_its_row_says()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "Firefox.lnk");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, file), new Point(400, 300));

            Assert.Equal("Firefox", GhostTextOf(window).Text);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The label rule taken the rest of the way, into the controls that draw
    /// it: a forty-line name asked the box for 136 × 532 against a one-line
    /// 124 × 22, and <c>Spot</c> put that at (614, 0) in a 1200 × 1000 window —
    /// a column down the whole of it rather than a label beside the pointer.
    ///
    /// The text is set on the real DragGhostText rather than raised as a drag,
    /// because Win32 refuses to create a file whose name holds a control
    /// character and this suite is Windows-only. What is measured is still the
    /// window's own box, at the window's own font.
    /// </summary>
    [AvaloniaFact]
    public void A_name_with_newlines_in_it_stays_one_line_high()
    {
        var window = Shown();

        try
        {
            var ghost = GhostOf(window);
            var text = GhostTextOf(window);

            var many = string.Join("\n", Enumerable.Range(0, 40).Select(i => "line" + i)) + ".txt";

            text.Text = Vaktari.Ui.Input.DragGhost.Label([Under("plain.txt")]);
            ghost.IsVisible = true;
            ghost.InvalidateMeasure();
            ghost.Measure(Size.Infinity);

            var one = ghost.DesiredSize.Height;

            Assert.True(one > 0, "the ghost was never given a size to draw");

            text.Text = Vaktari.Ui.Input.DragGhost.Label([Under(many)]);
            ghost.InvalidateMeasure();
            ghost.Measure(Size.Infinity);

            Assert.Equal(one, ghost.DesiredSize.Height, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **It has to MOVE**, which is the difference between a drag image and a
    /// notice. Two drag-overs at different points, and the label is beside the
    /// pointer in both — a ghost drawn once where the drag began would satisfy
    /// the visibility check above and be useless.
    /// </summary>
    [AvaloniaFact]
    public async Task The_ghost_follows_the_pointer()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var data = await Carrying(window, file);
            var ghost = GhostOf(window);

            var first = new Point(300, 200);
            var second = new Point(700, 500);

            Raise(window, DragDrop.DragOverEvent, data, first);

            Assert.Equal(OnTheLayer(window, first).X + Vaktari.Ui.Input.DragGhost.Gap,
                         Canvas.GetLeft(ghost));
            Assert.Equal(OnTheLayer(window, first).Y + Vaktari.Ui.Input.DragGhost.Gap,
                         Canvas.GetTop(ghost));

            Raise(window, DragDrop.DragOverEvent, data, second);

            Assert.Equal(OnTheLayer(window, second).X + Vaktari.Ui.Input.DragGhost.Gap,
                         Canvas.GetLeft(ghost));
            Assert.Equal(OnTheLayer(window, second).Y + Vaktari.Ui.Input.DragGhost.Gap,
                         Canvas.GetTop(ghost));
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The flip, measured on the drawn label rather than on the number handed
    /// to the placement — <see cref="Border.Bounds"/> is what the layout pass
    /// actually gave it, so a placement made against a width of zero is caught
    /// here and nowhere else.
    ///
    /// That is not a hypothetical: a control that is not visible measures to an
    /// empty size, so measuring the ghost before showing it put the first ghost
    /// of every drag under the pointer at the right-hand edge, and every later
    /// one — measured while visible — clear of it.
    /// </summary>
    [AvaloniaFact]
    public async Task At_the_windows_right_edge_the_ghost_keeps_clear_of_the_pointer()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var ghost = GhostOf(window);
            var edge = new Point(LayerOf(window).Bounds.Width - 6, 400);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, file), edge);

            Pump();

            Assert.True(ghost.Bounds.Width > 0, "the ghost was never given a size to draw");

            // The drawn label's far edge, one gap clear of the pointer — which
            // is the same statement as "the whole of it is off the pointer",
            // said in a way that a placement made against a width of zero
            // cannot satisfy.
            Assert.Equal(OnTheLayer(window, edge).X - Vaktari.Ui.Input.DragGhost.Gap,
                         Canvas.GetLeft(ghost) + ghost.Bounds.Width, 3);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **Against the width of the label it is drawing NOW.** The placement
    /// happens inside the drag-over handler, before any layout pass has been
    /// near the new text, and a Border whose own measure is still marked valid
    /// hands back the size it had for the last label — so a drag that changed
    /// what it says at the right-hand edge was placed against the previous
    /// width and drew somewhere else entirely.
    ///
    /// Two labels of different widths at one point at the edge, and the far
    /// edge lands one gap clear of the pointer both times.
    /// </summary>
    [AvaloniaFact]
    public async Task The_ghost_is_placed_against_the_label_it_draws_now()
    {
        var dir = Temp();

        var wide = Path.Combine(dir, "quarterly-forecast-final.xlsx");
        File.WriteAllText(wide, "x");

        var narrow = new[] { "a.txt", "b.txt", "c.txt" }
            .Select(name => Path.Combine(dir, name)).ToArray();

        foreach (var file in narrow) File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var ghost = GhostOf(window);
            var edge = new Point(LayerOf(window).Bounds.Width - 6, 400);
            var want = OnTheLayer(window, edge).X - Vaktari.Ui.Input.DragGhost.Gap;

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, wide), edge);
            Pump();

            var first = ghost.Bounds.Width;

            Assert.Equal(want, Canvas.GetLeft(ghost) + first, 3);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, narrow), edge);
            Pump();

            // A guard on the case, not on the code: two labels of the same
            // width would make the assertion below true whatever the placement
            // read.
            Assert.True(ghost.Bounds.Width < first,
                        "both labels are the same width, so this proves nothing");

            Assert.Equal(want, Canvas.GetLeft(ghost) + ghost.Bounds.Width, 3);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A drag from another application arrives with whatever that application
    /// drew for it, and a label of ours underneath would be Vaktari narrating
    /// somebody else's gesture. The finding is about the drags Vaktari starts,
    /// which are the ones the toolkit leaves bare.
    ///
    /// **Arranged and then flipped, because the ghost ships invisible.** Asked
    /// only for "not visible" this passed on a Border that had never been
    /// shown — measured: with the ShowDragGhost call deleted from OnDragOver
    /// outright, ten of this class's tests went red and this was one of the ten
    /// that stayed green. So the same data is put through the same point twice,
    /// once each side of the flag, and the assertion is the CHANGE.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_from_another_application_draws_no_ghost()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            var data = await Carrying(window, file);
            var point = new Point(400, 300);

            OurOwnDrag(true);

            Raise(window, DragDrop.DragOverEvent, data, point);

            Assert.True(GhostOf(window).IsVisible,
                        "this drag draws no ghost even as one of ours, so refusing it proves nothing");

            OurOwnDrag(false);

            Raise(window, DragDrop.DragOverEvent, data, point);

            Assert.False(GhostOf(window).IsVisible,
                         "a drag from elsewhere is labelled twice, once by each application");
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **A drag between two Vaktari windows lost its label at the boundary.**
    /// The flag saying "we started this" belonged to a window rather than to
    /// the application, so the receiving window called the drag foreign and
    /// drew nothing — while the source window had already put its own away on
    /// the leave. Measured, before the fix: second window ghost visible=False,
    /// label empty, source window ghost visible=False. Nothing on screen for
    /// the whole crossing, which is the gesture this feature is about.
    ///
    /// Two real windows, and the drag is raised on the one that did NOT start
    /// it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_into_a_second_vaktari_window_keeps_its_label()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var source = Shown();
        var second = Shown();

        try
        {
            OurOwnDrag(true);

            Raise(second, DragDrop.DragOverEvent, await Carrying(second, file), new Point(400, 300));

            Assert.True(GhostOf(second).IsVisible,
                        "the second window draws nothing for a drag this application started");

            Assert.Equal("a.txt", GhostTextOf(second).Text);
        }
        finally
        {
            second.Close();
            source.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **The ghost is the one thing in this window that floats over the
    /// listing, and it borrowed the toolbar chip's wash.** With
    /// Views.FollowDesktopColours on, ChipBackground is alpha 26 of 255 — 10%
    /// — so the label was drawn onto row text with nothing behind it. Measured
    /// on the resolved brush rather than read off the markup, because that is
    /// where the wash is made: the same key is opaque on the default path and
    /// a wash on the desktop path, and only one of those was ever a problem.
    ///
    /// The palette below is Breeze-shaped and its exact colours do not matter;
    /// what matters is that it is a desktop palette at all, which is what puts
    /// ThemeApplier down the branch that derives a chip wash.
    /// </summary>
    [AvaloniaFact]
    public void The_ghost_has_something_solid_behind_it()
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        var window = Shown();

        try
        {
            Vaktari.Ui.Settings.AppSettings.Apply(before with
            {
                Views = before.Views with { FollowDesktopColours = true },
            });

            ThemeApplier.Apply(window, new Vaktari.Core.ThemePalette
            {
                IsDark = true,
                Colours = new Dictionary<string, string>
                {
                    [Vaktari.Core.ThemeRole.WindowBackground] = "#2A2E32",
                    [Vaktari.Core.ThemeRole.WindowText] = "#FCFCFC",
                    [Vaktari.Core.ThemeRole.ViewBackground] = "#1B1E20",
                    [Vaktari.Core.ThemeRole.ViewAlternate] = "#222528",
                    [Vaktari.Core.ThemeRole.ViewText] = "#FCFCFC",
                    [Vaktari.Core.ThemeRole.ViewDimText] = "#A0A0A0",
                    [Vaktari.Core.ThemeRole.SelectionBackground] = "#3DAEE9",
                    [Vaktari.Core.ThemeRole.SelectionText] = "#FCFCFC",
                    [Vaktari.Core.ThemeRole.Accent] = "#3DAEE9",
                },
            });

            var ghost = GhostOf(window);
            var ground = Assert.IsAssignableFrom<ISolidColorBrush>(ghost.Background);

            Assert.Equal(255, ground.Color.A);

            // And nothing dilutes it afterwards: opacity applies to the whole
            // subtree, so a translucent border is a translucent label too.
            Assert.Equal(1, ghost.Opacity);
        }
        finally
        {
            // The theme is applied to Application.Current.Resources, so it
            // outlives this window and this class. Put the settings back first,
            // then re-apply with no palette, which is exactly what a window
            // built on the default path does.
            Vaktari.Ui.Settings.AppSettings.Apply(before);
            ThemeApplier.Apply(window, null);

            window.Close();
        }
    }

    /// <summary>
    /// A drag that has nothing to say takes the label away rather than leaving
    /// the last one standing. The handler decides what to draw on every single
    /// drag-over, so every route out of it has to leave the screen agreeing
    /// with that decision — including the route that draws nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_that_says_nothing_takes_the_ghost_away()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var point = new Point(400, 300);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, file), point);

            Assert.True(GhostOf(window).IsVisible, "there was no ghost to take away");

            Raise(window, DragDrop.DragOverEvent, new DataTransfer(), point);

            Assert.False(GhostOf(window).IsVisible,
                         "the ghost still names files that are no longer in the drag");
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The gesture is over, so the label goes. One left on the glass
    /// after the drop would say a drag is still in flight.</summary>
    [AvaloniaFact]
    public async Task The_drop_takes_the_ghost_away()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var data = await Carrying(window, file);
            var point = new Point(400, 300);

            Raise(window, DragDrop.DragOverEvent, data, point);

            Assert.True(GhostOf(window).IsVisible, "there was no ghost to take away");

            Raise(window, DragDrop.DropEvent, data, point);

            Assert.False(GhostOf(window).IsVisible, "the ghost outlived the drop");
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// And a drag that leaves the window takes it away too: past the edge the
    /// gesture belongs to whatever is under the pointer there, which draws its
    /// own feedback or none.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_leaving_the_window_takes_the_ghost_away()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var data = await Carrying(window, file);
            var point = new Point(400, 300);

            Raise(window, DragDrop.DragOverEvent, data, point);

            Assert.True(GhostOf(window).IsVisible, "there was no ghost to take away");

            Raise(window, DragDrop.DragLeaveEvent, data, point);

            Assert.False(GhostOf(window).IsVisible, "the ghost outlived the drag");
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **The ghost travels with the pointer, so a hit-testable one would sit
    /// between the drag and every target it crosses** — the ring would stop
    /// following the rows and the drop would be offered to the label rather
    /// than to the folder underneath it. It rides the selection band's overlay,
    /// which is not hit-testable, and the assertion is that chain: from the
    /// drawn label up to the window, something says no.
    ///
    /// A live <c>InputHitTest</c> would be the better statement and was tried
    /// first. It is not usable here: hit testing answers from the compositor's
    /// scene, which a headless window updates on a render rather than on a
    /// layout pass, so with the overlay deliberately made hit-testable the hit
    /// still came back as a control from the frame before — a test that passes
    /// under its own mutation.
    /// </summary>
    [AvaloniaFact]
    public async Task The_ghost_cannot_swallow_the_drag_that_draws_it()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            var ghost = GhostOf(window);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, file), new Point(400, 300));

            Pump();

            // Or the statement below is about a label nobody can see, which
            // nothing can swallow either.
            Assert.True(ghost.IsVisible && ghost.Bounds.Width > 0,
                        "there is no ghost on screen to speak of");

            Assert.Contains(
                ghost.GetSelfAndVisualAncestors().OfType<InputElement>(),
                link => !link.IsHitTestVisible);

            Assert.Contains(ghost.GetVisualAncestors(), up => ReferenceEquals(up, LayerOf(window)));
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// What the layout drew, rather than what was handed to it: a trimmed
    /// line's runs are the head, the ellipsis and whatever tail survived, so
    /// reading them back is reading the drawn text. Same route as
    /// <see cref="NameEllipsisTests"/>.
    /// </summary>
    private static string Drawn(TextBlock block)
    {
        var text = "";

        foreach (var line in block.TextLayout.TextLines)
            foreach (var run in line.TextRuns)
                text += run.Text.ToString();

        return text;
    }

    /// <summary>
    /// A name too long for the label is cut, or the ghost grows wider than the
    /// window it is meant to float over — and the cut keeps the extension, the
    /// same rule every listing row follows, because a ghost reading
    /// "quarterly-forecast-…" no longer says what is being dragged, which is
    /// its whole job.
    /// </summary>
    [AvaloniaFact]
    public async Task A_name_too_long_for_the_ghost_keeps_its_extension()
    {
        var dir = Temp();
        var name = "quarterly-forecast-final-revision-with-the-numbers-from-accounts.xlsx";
        var file = Path.Combine(dir, name);
        File.WriteAllText(file, "x");

        var window = Shown();

        try
        {
            OurOwnDrag(true);

            Raise(window, DragDrop.DragOverEvent, await Carrying(window, file), new Point(300, 300));

            Pump();

            var drawn = Drawn(GhostTextOf(window));

            Assert.True(drawn.Length < name.Length,
                        $"the ghost drew the whole name, so it is as wide as the name is: {drawn}");

            Assert.EndsWith(".xlsx", drawn, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The drop and the leave both put the ghost away, and neither is
    /// guaranteed: a drag released over another application, or abandoned with
    /// Escape, ends in the drag call's own finally and nowhere else.
    ///
    /// Read from the source rather than driven, because the only way to reach
    /// that finally is to run the platform's drag loop, which a headless test
    /// has no way to end. Weaker than the tests above and named as such: it
    /// says the call is there, not that it fires.
    /// </summary>
    [Fact]
    public void The_end_of_a_drag_puts_the_ghost_away()
        => Assert.Contains(
            "HideDragGhost();",
            RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                            "private async Task BeginDragAsync("),
            StringComparison.Ordinal);

    /// <summary>
    /// The flag every window reads is raised and lowered by the one window that
    /// starts the drag — raised, or a Vaktari drag is a foreign drag to every
    /// window including its own; lowered, or the NEXT drag through this
    /// application, whoever started it, is labelled as ours.
    ///
    /// Read from the source, for the same reason as the test above and with the
    /// same weakness: BeginDragAsync blocks on the platform's drag loop, which
    /// a headless test cannot start or end, so every other test here reaches
    /// the flag by reflection instead. It says the two lines are there, not
    /// that they run.
    /// </summary>
    [Fact]
    public void A_drag_that_starts_here_is_flagged_for_every_window()
    {
        var body = RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                                   "private async Task BeginDragAsync(");

        Assert.Contains("_dragBegunInThisApplication = true;", body, StringComparison.Ordinal);
        Assert.Contains("_dragBegunInThisApplication = false;", body, StringComparison.Ordinal);
    }
}
