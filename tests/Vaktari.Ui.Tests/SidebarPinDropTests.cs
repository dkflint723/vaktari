using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Sharing;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dropping a folder on the sidebar itself.
///
/// **Only the place ROW took a drop, and the sidebar is mostly not rows.** The
/// blank strip under the sections, the gaps between them and every section
/// heading refused — nothing under the pointer there was a target, so
/// OnDragOver answered None and the drop was never delivered. The gesture both
/// references offer for adding a place, dragging a folder onto the navigation
/// pane, therefore did not exist; the only ways in were Ctrl+D and the menu,
/// and both of those pin the folder you are already standing in, so a folder
/// you could see in the listing had to be opened before it could be pinned.
///
/// A place is a folder, so a file dropped there is counted and left alone
/// rather than being turned into a pin of the folder it lives in.
///
/// **The handlers are driven, not read.** An earlier draft of these tests
/// scanned the text of OnDragOver and OnDrop, on the belief that the headless
/// platform delivers no drag and DragEventArgs cannot be built without one.
/// That was false and measured to be false here: DragEventArgs has a public
/// constructor, a DataTransfer can carry a folder the window's own
/// StorageProvider hands back, and raising the event on the control the blank
/// strip hit-tests to runs the real handler end to end. The scans passed with
/// the feature dead — the offered paths replaced by an empty list left every
/// word they looked for in place — so they are gone.
/// </summary>
public sealed class SidebarPinDropTests : OwnedViewModels
{
    // ---- what the pointer is over -------------------------------------------

    private static object TargetAt(object? source)
        => typeof(MainWindow)
            .GetMethod("TargetAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source])!;

    private static T? Read<T>(object spot, string name)
        => (T?)spot.GetType().GetProperty(name)!.GetValue(spot);

    private static PlaceItemViewModel Place(
        string path, PlaceKind kind = PlaceKind.UserFolder, bool available = true)
        => new(new Place
        {
            Id = "row:" + path,
            Label = "a row",
            Path = path,
            Kind = kind,
            Icon = "folder",
            IsAvailable = available,
        });

    /// <summary>
    /// The panel with the given chain hung inside it, innermost last — which is
    /// the arrangement the walk actually meets. Every link but the last is a
    /// Border, because a Border adds its child to the visual AND logical tree
    /// the moment it is set, so the walk has parents to climb and DataContext
    /// inherits down them exactly as it does under a real row. A
    /// ContentControl's content is not in the visual tree until the control is
    /// templated, so the last link is the only place a Button can go here — and
    /// a Button with something inside it is what the real-window tests below
    /// cover.
    /// </summary>
    /// <returns>The innermost control, which is what the pointer is on.</returns>
    private static Control InThePanel(params Control[] chain)
    {
        Control here = new Border { Name = "SidebarPanel" };

        foreach (var link in chain)
        {
            if (here is not Border border)
                throw new InvalidOperationException(
                    $"nothing can be hung inside a {here.GetType().Name} without templating it");

            border.Child = link;
            here = link;
        }

        return here;
    }

    /// <summary>
    /// The fault itself. <c>Exists</c> is what OnDragOver refuses on, so the
    /// panel's own ground has to satisfy it or the drop never arrives.
    /// </summary>
    [AvaloniaFact]
    public void The_panels_own_ground_is_somewhere_a_drag_can_land()
    {
        var spot = TargetAt(InThePanel());

        Assert.True(Read<bool>(spot, "IsSidebar"), "the sidebar's own ground was not recognised");
        Assert.True(Read<bool>(spot, "Exists"),
            "OnDragOver refuses when Exists is false, so the drop never arrives");
    }

    /// <summary>
    /// A section heading is the other half of the finding: it carries the
    /// GROUP, not a place, and used to refuse.
    ///
    /// It is also the one button in the panel that is part of the ground, which
    /// is what its style class says — every other button here is a row or an
    /// action with a destination of its own.
    /// </summary>
    [AvaloniaFact]
    public void A_section_heading_is_the_panels_ground_too()
    {
        var heading = InThePanel(
            new Border { DataContext = new PlaceGroupViewModel(new PlaceGroup("PLACES", [])) },
            new ToggleButton { Classes = { "section" } });

        var spot = TargetAt(heading);

        Assert.True(Read<bool>(spot, "IsSidebar"));
    }

    /// <summary>
    /// Not vacuous: the panel is the FALLBACK, so a place row inside it still
    /// answers for itself and the files still go into that folder.
    /// </summary>
    [AvaloniaFact]
    public void A_place_row_still_takes_the_drop_into_itself()
    {
        var folder = Path.GetTempPath();

        var label = InThePanel(new Border { DataContext = Place(folder) }, new Border());

        var spot = TargetAt(label);

        Assert.False(Read<bool>(spot, "IsSidebar"),
            "dropping on Downloads would pin it instead of filing into it");
        Assert.Equal(folder, Read<string>(spot, "Place"));
    }

    /// <summary>
    /// The walk stops at ANY place row, not only at one that could take the
    /// drop. A share that is not mounted refuses a drop and says so on its own
    /// row; turning that refusal into a pin of something else answers a
    /// question nobody asked.
    /// </summary>
    [AvaloniaFact]
    public void An_unmounted_share_pins_nothing()
    {
        var label = InThePanel(
            new Border { DataContext = Place(Path.GetTempPath(), PlaceKind.Network, available: false) },
            new Border());

        var spot = TargetAt(label);

        Assert.False(Read<bool>(spot, "IsSidebar"));
        Assert.Null(Read<string>(spot, "Place"));
        Assert.False(Read<bool>(spot, "Exists"), "the row would take a drop it cannot honour");
    }

    /// <summary>The bin keeps its verb. It is a place row, so the walk stops on
    /// it before the panel is reached.</summary>
    [AvaloniaFact]
    public void The_bin_row_still_trashes_rather_than_pins()
    {
        var label = InThePanel(
            new Border { DataContext = Place(VirtualPaths.Trash, PlaceKind.Virtual) },
            new Border());

        var spot = TargetAt(label);

        Assert.True(Read<bool>(spot, "IsBin"));
        Assert.False(Read<bool>(spot, "IsSidebar"));
    }

    /// <summary>
    /// **A row that is not a place row still keeps its own answer.** The first
    /// draft made the panel a blanket fallback, and a real-window dump then
    /// showed the remote mounts, the discovered servers, Scan, Share, "Connect
    /// to a server…" and both Recent rows all answering as the panel's ground —
    /// so a folder released on a remote mount, a row aimed at BECAUSE it looks
    /// like somewhere a folder goes, was pinned instead of refused. Every one
    /// of those is a Button, and only a section heading is a button that is
    /// also the ground.
    /// </summary>
    [AvaloniaFact]
    public void A_row_that_is_a_button_is_not_the_panels_ground()
    {
        var spot = TargetAt(InThePanel(new Border(), new Button()));

        Assert.False(Read<bool>(spot, "IsSidebar"));
        Assert.False(Read<bool>(spot, "Exists"), "the row would answer a question nobody asked");
    }

    /// <summary>
    /// The rows that are NOT buttons: a served share and a shared link are
    /// Borders carrying their own model, so the button rule above cannot see
    /// them and they are named instead.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_shared_row_is_not_the_panels_ground(bool link)
    {
        object model = link
            ? new DriveLink("/local", "/remote", "https://example.invalid/x")
            : new ShareSession
            {
                Path = "/local", Url = "http://example.invalid", Port = 1, Writable = false,
                Handle = new object(),
            };

        var spot = TargetAt(InThePanel(new Border { DataContext = model }, new Border()));

        Assert.False(Read<bool>(spot, "IsSidebar"));
        Assert.False(Read<bool>(spot, "Exists"));
    }

    /// <summary>
    /// **This PC is drawn inside the Home row's template**, so it inherits
    /// Home's place as its DataContext — its label and icon included — and the
    /// walk read a drop on it as a drop on Home, which would silently file the
    /// folder into the home directory. It names nowhere instead, and so takes
    /// no drop at all.
    /// </summary>
    [AvaloniaFact]
    public void This_pc_names_nowhere_and_takes_no_drop()
    {
        var home = Place(Path.GetTempPath());

        var label = InThePanel(
            new Border { DataContext = home },
            new Border { Name = "ComputerRow" },
            new Border());

        // The whole difficulty in one assertion: the thing under the pointer
        // says it is Home, and only an ancestor says otherwise.
        Assert.Same(home, label.DataContext);

        var spot = TargetAt(label);

        Assert.Null(Read<string>(spot, "Place"));
        Assert.False(Read<bool>(spot, "Exists"),
            "a folder released on This PC would go into the home folder");
    }

    /// <summary>Outside the panel nothing changes: the sidebar rule must not
    /// answer for a drag over the listing.</summary>
    [AvaloniaFact]
    public void Nothing_outside_the_panel_is_the_sidebar()
    {
        var spot = TargetAt(new Border { Child = new Border() }.Child);

        Assert.False(Read<bool>(spot, "IsSidebar"));
        Assert.False(Read<bool>(spot, "Exists"));
    }

    // ---- what may be pinned --------------------------------------------------

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaktari-pin-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaFact]
    public void A_folder_is_what_gets_pinned()
    {
        var dir = Temp();

        try
        {
            var plan = PinnableDrop.For([dir]);

            Assert.Equal([dir], plan.Folders);
            Assert.Equal(0, plan.Files);
            Assert.True(plan.Any);
            Assert.Equal("pinned 1 folder(s) to places", plan.Report(already: 0));

            // A shortcut, not a copy: nothing is duplicated and nothing leaves
            // where it was.
            Assert.Equal(DragDropEffects.Link, plan.Effect);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **The half the finding asked about by name.** A file is not a place, and
    /// pinning the folder it lives in would be a different thing from the one
    /// that was dragged.
    /// </summary>
    [AvaloniaFact]
    public void A_file_is_never_pinned_and_never_stands_for_its_folder()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "notes.txt");

        try
        {
            File.WriteAllText(file, "x");

            var plan = PinnableDrop.For([file]);

            Assert.Empty(plan.Folders);
            Assert.Equal(1, plan.Files);
            Assert.False(plan.Any);
            Assert.Equal("only a folder can be a place", plan.Report(already: 0));

            // Refused by the cursor, on the way in. The toolkit delivers a drop
            // only where the drag-over said yes, so this is what keeps a file
            // from being swallowed by a release that then does nothing.
            Assert.Equal(DragDropEffects.None, plan.Effect);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A mixed drag pins the folders and says what it left alone,
    /// rather than pinning some of it in silence.</summary>
    [AvaloniaFact]
    public void A_mixed_drop_says_what_it_left_alone()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "notes.txt");

        try
        {
            File.WriteAllText(file, "x");

            var plan = PinnableDrop.For([dir, file]);

            Assert.Equal([dir], plan.Folders);
            Assert.Equal(1, plan.Files);
            Assert.Equal(
                "pinned 1 folder(s) to places — 1 file(s) cannot be a place",
                plan.Report(already: 0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **The report said "pinned" for folders that were not pinned.** A folder
    /// the panel already shows is left alone, and the line has to say that
    /// rather than claim a row that is not there.
    /// </summary>
    [AvaloniaFact]
    public void What_was_already_there_is_not_counted_as_pinned()
    {
        var first = Temp();
        var second = Temp();

        try
        {
            Assert.Equal(
                "1 folder(s) already in places",
                PinnableDrop.For([first]).Report(already: 1));

            Assert.Equal(
                "pinned 1 folder(s) to places — 1 already there",
                PinnableDrop.For([first, second]).Report(already: 1));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    /// <summary>A path that has gone away by the time the drop lands is not a
    /// folder, and must not become a place that opens nothing.</summary>
    [AvaloniaFact]
    public void A_path_that_is_no_longer_there_is_not_pinned()
    {
        var gone = Path.Combine(Path.GetTempPath(), "vaktari-gone-" + Guid.NewGuid().ToString("N")[..12]);

        var plan = PinnableDrop.For([gone]);

        Assert.Empty(plan.Folders);
        Assert.Equal(1, plan.Files);
    }

    // ---- what the drop then does --------------------------------------------

    /// <summary>The whole point: the folder reaches the provider that stores
    /// places.</summary>
    [AvaloniaFact]
    public async Task The_dropped_folder_becomes_a_place()
    {
        var dir = Temp();
        var places = new Recorder();
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        try
        {
            shell.Start(null, Path.GetTempPath());

            await shell.PinDroppedAsync([dir]);

            Assert.Equal([dir], places.Pinned);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Every folder in the drop, not just the first.</summary>
    [AvaloniaFact]
    public async Task Two_folders_become_two_places()
    {
        var first = Temp();
        var second = Temp();
        var places = new Recorder();
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        try
        {
            shell.Start(null, Path.GetTempPath());

            await shell.PinDroppedAsync([first, second]);

            Assert.Equal([first, second], places.Pinned);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    /// <summary>A file dropped on the panel pins nothing at all — not its own
    /// path, and not the folder it lives in.</summary>
    [AvaloniaFact]
    public async Task A_dropped_file_pins_nothing()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "notes.txt");
        var places = new Recorder();
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        try
        {
            File.WriteAllText(file, "x");

            shell.Start(null, Path.GetTempPath());

            await shell.PinDroppedAsync([file]);

            Assert.Empty(places.Pinned);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>And it says so, in the pane that is on screen — a drop that
    /// reports nothing is the silence this finding was about.</summary>
    [AvaloniaFact]
    public async Task It_says_what_it_did()
    {
        var dir = Temp();
        var shell = Own(new ShellViewModel(new Inert(), places: new Recorder()));

        try
        {
            shell.Start(null, Path.GetTempPath());

            await shell.PinDroppedAsync([dir]);

            Assert.Equal("pinned 1 folder(s) to places", shell.ActiveTab!.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **A folder the panel already shows was written to places.json and never
    /// drawn, and the drop said it had been pinned.** The provider drops a pin
    /// whose path is already one of the built-in places while building the list
    /// it renders — so the likeliest drop of all, one of your own well-known
    /// folders, claimed a row that does not exist and that no "Remove from
    /// places" can reach. Every repeat of the gesture appended another entry.
    ///
    /// So the rendered rows are what "already" is asked of, and the pin is not
    /// written at all.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_the_panel_already_shows_is_not_pinned_again()
    {
        var dir = Temp();
        var places = new Recorder(dir);
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        try
        {
            shell.Start(null, Path.GetTempPath());

            await shell.Sidebar.ReloadAsync();

            await shell.PinDroppedAsync([dir]);

            Assert.Empty(places.Pinned);
            Assert.Equal("1 folder(s) already in places", shell.ActiveTab!.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the real window, with a real drag -----------------------------------

    /// <summary>
    /// A drop carrying these paths, built the way the window's own drag builds
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

            Assert.True(item is not null, "the drop could not be given " + path);

            data.Add(DataTransferItem.CreateFile(item!));
        }

        return data;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>Pumps until the condition holds, or gives up. Pinning writes a
    /// file and the panel is rebuilt from the provider's own event, so the row
    /// does not appear on the statement after the drop.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Pump();
            Thread.Sleep(10);
        }

        Pump();
    }

    /// <summary>
    /// The window, shown and settled, with the sidebar panel found.
    ///
    /// The state every store in it writes goes to this test class's own
    /// directory, per TestState — including places.json, so a pin made here
    /// never touches the places of whoever runs the suite.
    /// </summary>
    private MainWindow Shown(out Border panel)
    {
        // A real window assigns the platform's own search backend to the pane's
        // static; borrowed and given back, so no later class runs a real
        // recursive walk of the machine.
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        window.Show();
        Pump();
        Pump();

        panel = window.FindControl<Border>("SidebarPanel")
                ?? throw new InvalidOperationException("the sidebar panel is not in the window");

        return window;
    }

    /// <summary>The middle of the blank strip below the last section.</summary>
    private static Point BlankStrip(MainWindow window, Border panel)
    {
        var top = panel.TranslatePoint(default, window);

        Assert.True(top is not null, "the panel is not laid out, so there is nothing to hit");

        // The bottom edge of the lowest thing the sections drew.
        var lowest = panel.GetVisualDescendants().OfType<Control>()
                          .Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0)
                          .Select(c => c.TranslatePoint(new Point(0, c.Bounds.Height), window))
                          .OfType<Point>()
                          .Where(p => p.Y < top!.Value.Y + panel.Bounds.Height)
                          .Max(p => p.Y);

        Assert.True(lowest + 8 < top!.Value.Y + panel.Bounds.Height,
            "the sections fill the panel in this window, so there is no blank strip to test");

        return new Point(top.Value.X + panel.Bounds.Width / 2, lowest + 8);
    }

    private static Control HitAt(MainWindow window, Point point)
    {
        var hit = window.InputHitTest(point);

        Assert.True(hit is Control, "the blank strip is not hit-testable, so no drag reaches it");

        return (Control)hit!;
    }

    private static DragEventArgs Raise(
        Control hit, RoutedEvent<DragEventArgs> which, DataTransfer data, Point point)
    {
        var e = new DragEventArgs(which, data, hit, point, KeyModifiers.None);

        hit.RaiseEvent(e);

        return e;
    }

    private static ShellViewModel ShellOf(MainWindow window)
        => (ShellViewModel)typeof(MainWindow)
            .GetProperty("Shell", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    private static IEnumerable<PlaceItemViewModel> RowsOf(MainWindow window)
        => ShellOf(window).Sidebar.Groups.SelectMany(g => g.Places);

    /// <summary>
    /// **The blank strip has to be armed for a drop, or none of the rest of
    /// this can happen** — the toolkit delivers a drag only where AllowDrop is
    /// set. Measured on the real templated tree rather than reasoned about:
    /// the point below the last section hit-tests to the ScrollViewer's own
    /// content presenter, three levels under the Border the attribute is on,
    /// and it reports AllowDrop true because the property inherits.
    /// </summary>
    [AvaloniaFact]
    public void The_blank_strip_under_the_sections_can_take_a_drop()
    {
        var window = Shown(out var panel);

        try
        {
            var hit = HitAt(window, BlankStrip(window, panel));

            Assert.True(DragDrop.GetAllowDrop(hit),
                "the panel is not a drop target, so a drag over it is never offered");

            Assert.True(Read<bool>(TargetAt(hit), "IsSidebar"),
                "a drop on the strip under the sections is still refused");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The This PC row in the real window, which is the only place that says
    /// the name survives being written inside a DataTemplate — <c>x:Name</c>
    /// there goes into the template's own name scope, and the walk reads
    /// <c>Control.Name</c>. Every hand-built tree above would pass with the
    /// attribute deleted from the markup.
    /// </summary>
    [AvaloniaFact]
    public void The_real_this_pc_row_names_nowhere()
    {
        var window = Shown(out var panel);

        try
        {
            var computer = panel.GetVisualDescendants().OfType<Control>()
                                .FirstOrDefault(c => c.Name == "ComputerRow");

            Assert.True(computer is not null,
                "no control in the sidebar is named ComputerRow, so a folder dropped "
                + "on This PC would be filed into the home folder");

            // Something INSIDE the row, the way a pointer lands on the label
            // rather than on the button's own rectangle — and the level at
            // which the inherited DataContext says "Home".
            var inside = computer!.GetVisualDescendants().OfType<Control>().LastOrDefault()
                         ?? computer;

            Assert.IsType<PlaceItemViewModel>(inside.DataContext);

            var spot = TargetAt(inside);

            Assert.Null(Read<string>(spot, "Place"));
            Assert.False(Read<bool>(spot, "Exists"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The cursor, from the real handler.** A folder dragged over the strip
    /// is offered as a place — Link, because a place is a pointer at a folder
    /// and nothing is duplicated or moved.
    ///
    /// This is what a source scan of the branch could not say: with the offered
    /// paths replaced by an empty list the branch still contained every word
    /// such a scan looks for, and the answer here silently became None, which
    /// is the toolkit never delivering the drop at all.
    ///
    /// Link is also what says the branch runs BEFORE the destination rules.
    /// Measured by letting a drag over the strip fall through to them: they
    /// answer Copy, with a destination of "".
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_dragged_over_the_panel_is_offered_as_a_place()
    {
        var dir = Temp();
        var window = Shown(out var panel);

        try
        {
            var point = BlankStrip(window, panel);
            var hit = HitAt(window, point);

            var e = Raise(hit, DragDrop.DragOverEvent, await Carrying(window, dir), point);

            Assert.Equal(DragDropEffects.Link, e.DragEffects);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>And a drag carrying only files is turned away on the way in,
    /// while it can still be steered somewhere that wants it.</summary>
    [AvaloniaFact]
    public async Task A_drag_of_files_alone_is_refused_over_the_panel()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "notes.txt");
        File.WriteAllText(file, "x");

        var window = Shown(out var panel);

        try
        {
            var point = BlankStrip(window, panel);
            var hit = HitAt(window, point);

            var e = Raise(hit, DragDrop.DragOverEvent, await Carrying(window, file), point);

            Assert.Equal(DragDropEffects.None, e.DragEffects);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A pointer that came off the listing left a ring on a pane and on the row
    /// it was last over. The panel is not where those files are going, so the
    /// ring has to let go — the same clearing the bin's branch does.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_over_the_panel_lets_go_of_the_listings_ring()
    {
        var dir = Temp();
        var window = Shown(out var panel);

        try
        {
            var shell = ShellOf(window);
            var tab = shell.ActiveTab!;
            var row = RowsOf(window).First();

            tab.IsDropTarget = true;
            row.IsDropTarget = true;

            var point = BlankStrip(window, panel);

            Raise(HitAt(window, point), DragDrop.DragOverEvent, await Carrying(window, dir), point);

            Assert.False(tab.IsDropTarget, "the pane is still ringed as the destination");
            Assert.False(row.IsDropTarget, "a place row is still ringed as the destination");
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// And the edge-scroll a drag near the bottom of the listing started keeps
    /// running otherwise: the scroll is armed above these branches and stopped
    /// inside each of them, so a drag that left the listing for the panel would
    /// scroll it on underneath.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_over_the_panel_stops_the_edge_scroll()
    {
        var dir = Temp();
        var window = Shown(out var panel);

        var armed = typeof(MainWindow)
            .GetField("_bandList", BindingFlags.NonPublic | BindingFlags.Instance)!;

        try
        {
            armed.SetValue(window, new ListBox());

            var point = BlankStrip(window, panel);

            Raise(HitAt(window, point), DragDrop.DragOverEvent, await Carrying(window, dir), point);

            Assert.Null(armed.GetValue(window));
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **The drop, end to end.** The folder reaches the provider, a row for it
    /// appears in the panel, and the active tab says what happened.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_dropped_on_the_panel_becomes_a_place()
    {
        var dir = Temp();
        var window = Shown(out var panel);

        try
        {
            var point = BlankStrip(window, panel);

            ShellOf(window).ActiveTab!.Status = "";

            Raise(HitAt(window, point), DragDrop.DropEvent, await Carrying(window, dir), point);

            PumpUntil(() => RowsOf(window).Any(p => PathRules.Same(p.Path, dir)));

            Assert.Contains(RowsOf(window), p => PathRules.Same(p.Path, dir));
            Assert.Equal("pinned 1 folder(s) to places", ShellOf(window).ActiveTab!.Status);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **A drive root dropped on the panel became a second row for the same
    /// drive**, labelled with the raw path because a root has no leaf name, and
    /// sitting beside the device row that was already there. The devices are
    /// added to the list after the pins and are not deduped against them at
    /// all, so nothing downstream caught it.
    ///
    /// The drive is read out of the panel rather than named, so this is the
    /// machine's own first device row whatever it is called.
    /// </summary>
    [AvaloniaFact]
    public async Task A_place_the_panel_already_shows_does_not_become_a_second_row()
    {
        var window = Shown(out var panel);

        try
        {
            var before = RowsOf(window).Select(p => p.Path).ToList();

            var already = RowsOf(window)
                .First(p => p.Path.Length > 0 && Directory.Exists(p.Path)).Path;

            // Emptied first, so waiting for a line to appear cannot be answered
            // by one the window put there while it was opening.
            ShellOf(window).ActiveTab!.Status = "";

            var point = BlankStrip(window, panel);

            Raise(HitAt(window, point), DragDrop.DropEvent, await Carrying(window, already), point);

            PumpUntil(() => ShellOf(window).ActiveTab!.Status.Length > 0);

            Assert.Equal("1 folder(s) already in places", ShellOf(window).ActiveTab!.Status);
            Assert.Equal(before, RowsOf(window).Select(p => p.Path).ToList());
        }
        finally
        {
            window.Close();
        }
    }

    // ---- fakes ---------------------------------------------------------------

    /// <summary>Places that remember what was pinned, and show whatever they
    /// were seeded with.</summary>
    private sealed class Recorder(params string[] shown) : IPlacesProvider
    {
        public List<string> Pinned { get; } = [];

        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
                shown.Length == 0
                    ? []
                    : [new PlaceGroup("places", [.. shown.Select(path => new Place
                    {
                        Id = "row:" + path, Label = "a row", Path = path,
                        Kind = PlaceKind.UserFolder, Icon = "folder",
                    })])]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
        {
            Pinned.Add(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
