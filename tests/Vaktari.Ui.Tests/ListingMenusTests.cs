using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The listing's two right-click menus, on a real window.
///
/// **One menu served a row and the empty space below it**, and so carried
/// everything either could want: twenty-eight rows and six rules for a plain
/// file on Windows, 904 pixels, with ten folder rows on every file's menu. It
/// is two menus now — the item menu for a click on a row or a tile, the
/// background menu for a click off every row — and this file is where that is
/// driven by real right-clicks rather than read out of the markup: which menu a
/// click reaches is Avalonia's routing plus one Opening handler, and nothing
/// about the markup can say whether the two agree.
///
/// Then what each menu shows in each kind of listing, because the other half
/// of the redesign was hiding every row whose command refuses where the menu
/// opened, and a row is only hidden if its binding resolves — a dead binding
/// leaves IsVisible at true and SHOWS the row (ReachUpBindingTests has the
/// measurement). So every assertion that a row is absent is made on a menu
/// that is open, in the direction a dead binding cannot satisfy.
///
/// And the rules, which are decided as each menu opens now rather than gated
/// one by one in the markup: every listing below is also walked for a rule
/// that is first, last, or next to another.
/// </summary>
public sealed class ListingMenusTests(ITestOutputHelper output) : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private readonly ITrashMaintenance? _trashBefore = PaneViewModel.Trash;
    private readonly IPlacesProvider? _placesBefore = PaneViewModel.Places;
    private readonly IRecentStore? _recentsBefore = PaneViewModel.Recents;
    private readonly IShellMenuProvider? _shellMenuBefore = PaneViewModel.ShellMenu;
    private readonly IFolderViewStore? _viewsBefore = PaneViewModel.FolderViews;

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-listingmenus").FullName;

    public override void Dispose()
    {
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        PaneViewModel.Trash = _trashBefore;
        PaneViewModel.Places = _placesBefore;
        PaneViewModel.Recents = _recentsBefore;
        PaneViewModel.ShellMenu = _shellMenuBefore;
        PaneViewModel.FolderViews = _viewsBefore;

        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    // ---- the window ---------------------------------------------------------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>The same pump ContextMenuPlacementTests uses, for the reason
    /// given there.</summary>
    private static async Task Layout(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Settle();
            await Task.Yield();
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Settle();
    }

    private static async Task Until(Window window, Func<bool> done, string what)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            await Layout(window);
            await Task.Delay(10);
        }

        Assert.True(done(), what);
    }

    /// <summary>
    /// A real window on a folder holding two files and a subfolder, in
    /// Details, with the context-menu preferences at their shipped defaults —
    /// set AFTER the window is built, whose constructor applies the settings on
    /// disk over anything set before it. The tab's path and layout are put
    /// back before the close, which flushes the developer's own session.
    /// </summary>
    private async Task InAWindow(Func<MainWindow, ShellViewModel, PaneViewModel, Task> body)
    {
        Directory.CreateDirectory(Path.Combine(_root, "adir"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "other.txt"), "y");

        var window = new MainWindow();

        window.Show();
        Settle();

        // After the window, which assigns the platform's own providers.
        UseSearch(null);
        PaneViewModel.FolderViews = null;

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore with { ContextMenu = new ContextMenuSettings() });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.OnSettingsChanged();

        var pane = shell.ActiveTab!;
        var was = pane.CurrentPath;
        var wasView = pane.View;

        try
        {
            pane.View = ViewMode.Details;

            await pane.NavigateAsync(_root);
            await Until(window, () => Row(window, pane, "notes.txt") is not null,
                        "the folder's rows never reached the screen");

            await body(window, shell, pane);
        }
        finally
        {
            foreach (var menu in window.GetVisualDescendants().OfType<Control>()
                                       .Select(c => c.ContextMenu).OfType<ContextMenu>())
                menu.Close();

            pane.View = wasView;

            if (!string.IsNullOrEmpty(was))
            {
                await pane.NavigateAsync(was);
                Settle();
            }

            window.Close();
        }
    }

    private static ListBox Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(list => list.IsVisible
                            && ReferenceEquals(list.DataContext, pane)
                            && list.SelectionMode.HasFlag(SelectionMode.Multiple));

    private static ListBoxItem? Row(Window window, PaneViewModel pane, string name)
    {
        window.UpdateLayout();

        return Listing(window, pane).GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(r => r.DataContext is FileEntry entry && entry.Name == name
                                 && r.Bounds.Width > 0);
    }

    private static Point At(Visual visual, double x, double y, Window window)
        => visual.TranslatePoint(new Point(x, y), window)
           ?? throw new InvalidOperationException("the point is not on the window");

    /// <summary>
    /// Waits until hit testing sees the layout, judged by a point on the
    /// row's name hitting the row.
    ///
    /// **InputHitTest reads the last RENDERED scene, not the layout** — and so
    /// does a headless mouse press. Measured: with the window laid out at 1400
    /// wide and nothing rendered since, every point past x=1020 hit nothing,
    /// and every point on a row hit the ScrollContentPresenter behind it. A
    /// render tick brings the scene up to the layout; one was enough run alone
    /// and not in a batch, so this ticks until the scene agrees.
    /// </summary>
    private static async Task Rendered(Window window, ListBoxItem row)
    {
        var probe = At(row, 40, row.Bounds.Height / 2, window);

        for (var i = 0; i < 50; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Layout(window);

            if ((window.InputHitTest(probe) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) == row)
                return;

            await Task.Delay(10);
        }

        Assert.Fail("hit testing never caught up with the layout, so no click here means anything");
    }

    private static async Task RightClick(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        await Layout(window);
    }

    private static ContextMenu Item(Window window, PaneViewModel pane)
        => ListingMenus.Above(Listing(window, pane), ListingMenus.Item);

    private static ContextMenu Background(Window window, PaneViewModel pane)
        => ListingMenus.Above(Listing(window, pane), ListingMenus.Background);

    /// <summary>Opens a menu the way a test can — Open(), which raises Opened
    /// and so the preparing and the tidying — and lays the popup out.</summary>
    private static async Task<List<string>> Open(Window window, ContextMenu menu)
    {
        menu.Open();
        await Layout(window);

        Assert.True(menu.IsOpen, $"the {menu.Name} did not open, so this proves nothing");

        return ListingMenus.Read(menu);
    }

    private static async Task Close(Window window, ContextMenu menu)
    {
        menu.Close();
        await Layout(window);
    }

    private void Print(string what, IEnumerable<string> rows)
        => output.WriteLine($"{what}: {string.Join(" | ", rows)}");

    // ---- which menu a right-click reaches -----------------------------------

    /// <summary>
    /// **On a row, the item menu — and the row is what it is about.** The
    /// right-click selects the row it lands on (Avalonia's own ListBox does,
    /// RightClickSelectionTests keeps it honest), the item menu opens, and the
    /// background menu stays shut.
    /// </summary>
    [AvaloniaFact]
    public async Task A_right_click_on_a_row_opens_the_item_menu()
        => await InAWindow(async (window, _, pane) =>
        {
            var row = Row(window, pane, "notes.txt")!;

            await Rendered(window, row);
            await RightClick(window, At(row, 40, row.Bounds.Height / 2, window));

            Assert.True(Item(window, pane).IsOpen, "a right-click on a row did not open the item menu");
            Assert.False(Background(window, pane).IsOpen, "the background menu opened on a row");
            Assert.Equal("notes.txt", pane.SelectedEntry?.Name);
        });

    /// <summary>
    /// **Anywhere on a full-width details row counts as the row**, the blank
    /// half included: the row's own background is what is there, and it is
    /// where people aim for a context menu. The point is chosen so that the hit
    /// element is not text, an icon or a glyph — asserted, or a point that
    /// happened to land on a size would prove nothing about the blank half.
    /// And the row was not selected before, so the click is what picked it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_right_click_on_the_blank_half_of_a_details_row_opens_the_item_menu()
        => await InAWindow(async (window, _, pane) =>
        {
            var list = Listing(window, pane);

            list.SelectedItems?.Clear();
            pane.SelectedEntry = null;
            await Layout(window);

            var row = Row(window, pane, "other.txt")!;

            Point? blank = null;
            var tried = new List<string>();

            // Across the LISTING's width rather than the row's: a details row
            // can be wider than the viewport, and a point past the viewport's
            // edge hits nothing at all.
            var middle = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), list)!.Value.Y;

            await Rendered(window, row);

            for (var across = 0.3; across < 0.98 && blank is null; across += 0.02)
            {
                var at = At(list, list.Bounds.Width * across, middle, window);
                var hit = window.InputHitTest(at) as Visual;

                tried.Add($"{across:0.00}:{hit?.GetType().Name ?? "null"}"
                          + $"/{(hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true) == row ? "row" : "other")}");

                if (hit is not null
                    && hit is not (TextBlock or Image or Avalonia.Controls.Shapes.Path)
                    && hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) == row)
                    blank = at;
            }

            Assert.True(blank is not null,
                        "no blank point on the row, so this proves nothing: " + string.Join(" ", tried));

            await RightClick(window, blank!.Value);

            Assert.True(Item(window, pane).IsOpen, "the blank half of a row opened no item menu");
            Assert.False(Background(window, pane).IsOpen, "the blank half of a row counted as empty space");
            Assert.Equal("other.txt", pane.SelectedEntry?.Name);
        });

    /// <summary>
    /// **Off every row, the background menu — and the selection stays.** The
    /// maintainer chose this over Explorer's deselect: FocusListIfEmptySpace
    /// runs for the left button only, because clearing on a right press once
    /// collapsed a five-file selection before the menu opened. So the
    /// background menu opens with a row still selected, which is the reason
    /// no row on it reads the selection.
    /// </summary>
    [AvaloniaFact]
    public async Task A_right_click_below_the_rows_opens_the_background_menu_and_keeps_the_selection()
        => await InAWindow(async (window, _, pane) =>
        {
            var list = Listing(window, pane);
            var notes = Row(window, pane, "notes.txt")!;

            list.SelectedItem = notes.DataContext;
            await Layout(window);

            var below = At(list, list.Bounds.Width / 2, list.Bounds.Height - 12, window);

            // Off every row, asserted on the rendered scene — see Rendered.
            await Rendered(window, notes);

            var hit = window.InputHitTest(below) as Visual;

            Assert.NotNull(hit);
            Assert.Null(hit.FindAncestorOfType<ListBoxItem>(includeSelf: true));

            await RightClick(window, below);

            Assert.True(Background(window, pane).IsOpen, "empty space opened no background menu");
            Assert.False(Item(window, pane).IsOpen, "empty space opened the item menu");
            Assert.Equal("notes.txt", pane.SelectedEntry?.Name);
            Assert.Single(pane.Selection);
        });

    /// <summary>
    /// The same two answers in the grid, where the space between and below the
    /// tiles is the listing's own and a tile is the row.
    /// </summary>
    [AvaloniaFact]
    public async Task In_the_grid_a_tile_gets_the_item_menu_and_the_space_around_it_the_background()
        => await InAWindow(async (window, _, pane) =>
        {
            pane.View = ViewMode.Grid;

            await Until(window, () => Row(window, pane, "notes.txt") is { Bounds.Width: < 600 },
                        "the grid never drew its tiles");

            var tile = Row(window, pane, "notes.txt")!;

            await Rendered(window, tile);
            await RightClick(window, At(tile, tile.Bounds.Width / 2, tile.Bounds.Height / 2, window));

            Assert.True(Item(window, pane).IsOpen, "a tile opened no item menu");
            Assert.False(Background(window, pane).IsOpen);

            await Close(window, Item(window, pane));

            var list = Listing(window, pane);
            var below = At(list, list.Bounds.Width / 2, list.Bounds.Height - 12, window);

            await Rendered(window, tile);

            var hit = window.InputHitTest(below) as Visual;

            Assert.NotNull(hit);
            Assert.Null(hit.FindAncestorOfType<ListBoxItem>(includeSelf: true));

            await RightClick(window, below);

            Assert.True(Background(window, pane).IsOpen, "the space below the tiles opened no background menu");
            Assert.False(Item(window, pane).IsOpen);
        });

    // ---- the Windows menu follows the menu ----------------------------------

    /// <summary>
    /// **The hosted menu asks about what the MENU is about**: the background
    /// menu's row asks the shell for the folder's own background menu even
    /// with a file selected, and the item menu's asks for the selection's. It
    /// used to decide by the selection alone, and a right-click on empty space
    /// keeps the selection. Driven through the rows' own SubmenuOpened on a
    /// real window, so the name that tells them apart is under test as well.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_menus_windows_row_asks_the_shell_about_its_own_subject()
        => await InAWindow(async (window, _, pane) =>
        {
            var shell = new AskedShell();

            PaneViewModel.ShellMenu = shell;

            var list = Listing(window, pane);
            var notes = Row(window, pane, "notes.txt")!;

            list.SelectedItem = notes.DataContext;
            await Layout(window);

            var background = Background(window, pane);
            await Open(window, background);

            var folderRow = background.Items.OfType<MenuItem>().Single(i => i.Name == "BackgroundShellMenu");
            folderRow.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, folderRow));
            await Until(window, () => shell.Asked.Count == 1, "the background row asked the shell nothing");

            Assert.Equal((true, _root), (shell.Asked[0].Background, shell.Asked[0].Paths.Single()));

            await Close(window, background);

            var item = Item(window, pane);
            await Open(window, item);

            var itemRow = item.Items.OfType<MenuItem>().Single(i => i.Name == "ShellMenu");
            itemRow.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, itemRow));
            await Until(window, () => shell.Asked.Count == 2, "the item row asked the shell nothing");

            Assert.Equal((false, Path.Combine(_root, "notes.txt")),
                         (shell.Asked[1].Background, shell.Asked[1].Paths.Single()));
        });

    // ---- undo and redo -----------------------------------------------------

    /// <summary>
    /// **Undo and Redo are on the background menu only when they would do
    /// something.** They were always shown and greyed; the maintainer overruled
    /// that when the menu was split. Driven through the pane's own flags on an
    /// open menu, in both directions, so a dead binding — which shows the row —
    /// cannot pass the hidden half.
    /// </summary>
    [AvaloniaFact]
    public async Task Undo_and_redo_are_there_only_when_there_is_something_to_take_back()
        => await InAWindow(async (window, _, pane) =>
        {
            var menu = Background(window, pane);
            await Open(window, menu);

            MenuItem Named(string command)
                => menu.Items.OfType<MenuItem>().Single(i => i.Command == command switch
                {
                    "undo" => pane.UndoCommand,
                    _ => pane.RedoCommand,
                });

            pane.CanUndo = false;
            pane.CanRedo = false;
            Settle();

            Assert.False(Named("undo").IsVisible, "Undo was offered with nothing to take back");
            Assert.False(Named("redo").IsVisible, "Redo was offered with nothing to put back");

            pane.CanUndo = true;
            Settle();

            Assert.True(Named("undo").IsVisible, "Undo was hidden with something to take back");
            Assert.False(Named("redo").IsVisible);

            pane.CanRedo = true;
            Settle();

            Assert.True(Named("redo").IsVisible, "Redo was hidden with something to put back");

            // And neither is on the item menu at all.
            Assert.DoesNotContain(Item(window, pane).Items.OfType<MenuItem>(),
                                  i => i.Command == pane.UndoCommand || i.Command == pane.RedoCommand);
        });

    // ---- what each menu shows, listing by listing ----------------------------

    /// <summary>
    /// A file in an ordinary folder: its verbs, and none of the folder's.
    /// </summary>
    [AvaloniaFact]
    public async Task A_files_item_menu_is_about_the_file()
        => await InAWindow(async (window, shell, pane) =>
        {
            Listing(window, pane).SelectedItem = Row(window, pane, "notes.txt")!.DataContext;
            await Layout(window);

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, file", rows);

            foreach (var expected in new[] { "Open", "Cut", "Copy", "Copy as path", "Copy to", "Move to",
                                             "Rename", shell.BinMoveLabel, "Compress to ZIP",
                                             "Duplicate", "Properties" })
                Assert.Contains(expected, rows);

            // The folder's rows live on the background menu now.
            foreach (var absent in new[] { "View", "Arrange", "Select", "Select all", "Paste", "New",
                                           "Refresh", "Open terminal here", "Analyse",
                                           "Add this folder to places", "Compare" })
                Assert.DoesNotContain(absent, rows);

            // A file is not a folder: nothing to open in a tab, nothing to pin,
            // and no network share of the folder around it.
            foreach (var absent in new[] { "Open in new tab", "Open in new window", "Add to places", "Share" })
                Assert.DoesNotContain(absent, rows);

            // Open is the first thing, and the Windows menu the last.
            Assert.Equal("Open", rows[0]);

            if (pane.HasShellMenu) Assert.Equal("Windows menu", rows[^1]);

            Assert.Empty(ListingMenus.StrayRules(menu, "item, file"));
        });

    /// <summary>
    /// A folder: the file's verbs plus the ones only a folder answers — open
    /// in a tab or a window, pin it, share it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folders_item_menu_adds_what_only_a_folder_answers()
        => await InAWindow(async (window, shell, pane) =>
        {
            Listing(window, pane).SelectedItem = Row(window, pane, "adir")!.DataContext;
            await Layout(window);

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, folder", rows);

            foreach (var expected in new[] { "Open", "Open in new tab", "Open in new window", "Cut", "Copy",
                                             "Rename", "Add to places", "Properties" })
                Assert.Contains(expected, rows);

            // Sharing the selected folder, which the platform has a backend for.
            if (shell.HasSharingEntry) Assert.Contains("Share", rows);

            Assert.Empty(ListingMenus.StrayRules(menu, "item, folder"));
        });

    /// <summary>
    /// Several files: Rename in bulk joins Rename, and no rule strays.
    /// </summary>
    [AvaloniaFact]
    public async Task A_multiple_selection_offers_the_bulk_rename()
        => await InAWindow(async (window, _, pane) =>
        {
            var list = Listing(window, pane);

            list.SelectedItems!.Add(Row(window, pane, "notes.txt")!.DataContext);
            list.SelectedItems!.Add(Row(window, pane, "other.txt")!.DataContext);
            await Layout(window);

            Assert.Equal(2, pane.Selection.Count);

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, two files", rows);

            Assert.Contains("Rename in bulk…", rows);
            Assert.Empty(ListingMenus.StrayRules(menu, "item, two files"));
        });

    /// <summary>
    /// Empty space in an ordinary folder: the folder's menu, and nothing that
    /// reads the selection — which is still there.
    /// </summary>
    [AvaloniaFact]
    public async Task The_background_menu_is_about_the_folder()
        => await InAWindow(async (window, shell, pane) =>
        {
            Listing(window, pane).SelectedItem = Row(window, pane, "notes.txt")!.DataContext;
            await Layout(window);

            var menu = Background(window, pane);
            var rows = await Open(window, menu);

            Print("background, folder", rows);

            foreach (var expected in new[] { "View", "Select all", "Paste", "New", "Refresh",
                                             "Open terminal here", "Analyse",
                                             "Add this folder to places", "Properties" })
                Assert.Contains(expected, rows);

            // Scripts, where there is somewhere for scripts to live.
            if (window.Services.Platform.Scripts is not null) Assert.Contains("Scripts", rows);

            foreach (var absent in new[] { "Open", "Cut", "Copy", "Copy as path", "Rename",
                                           shell.BinMoveLabel, "Add your own scripts", "Arrange" })
                Assert.DoesNotContain(absent, rows);

            Assert.Equal("View", rows[0]);

            if (pane.HasBackgroundShellMenu) Assert.Equal("Windows menu", rows[^1]);

            Assert.Empty(ListingMenus.StrayRules(menu, "background, folder"));
        });

    /// <summary>
    /// **The bin's item menu offered Open with, Copy and Scripts, and all of
    /// them acted on the path a file USED to have.** Open with's command
    /// refused ("already in the bin"); Copy and Scripts refused nothing, and
    /// put the old path on the clipboard or handed it to a script. What is
    /// left is the purge and Copy as path — the row's original path is what
    /// the bin's own Path column shows.
    /// </summary>
    [AvaloniaFact]
    public async Task The_bins_item_menu_is_the_purge_and_nothing_that_refuses()
        => await InAWindow(async (window, _, pane) =>
        {
            PaneViewModel.Trash = new Bin("gone.txt", "also.txt");

            await pane.NavigateAsync(VirtualPaths.Trash);
            await Until(window, () => Row(window, pane, "gone.txt") is not null, "the bin never listed");

            Listing(window, pane).SelectedItem = Row(window, pane, "gone.txt")!.DataContext;
            await Layout(window);

            // What would have filled Open with, had the launcher answered.
            pane.OpenWithOptions.Add(new LaunchOption("Notepad", "notepad.exe", null));

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, bin", rows);

            Assert.Equal(["Copy as path", "—", "Delete permanently"], rows);
            Assert.Empty(ListingMenus.StrayRules(menu, "item, bin"));

            await Close(window, menu);

            var background = Background(window, pane);
            var folderRows = await Open(window, background);

            Print("background, bin", folderRows);

            foreach (var absent in new[] { "Paste", "New", "Properties", "Scripts", "Share",
                                           "Open terminal here", "Windows menu", "Analyse" })
                Assert.DoesNotContain(absent, folderRows);

            Assert.Empty(ListingMenus.StrayRules(background, "background, bin"));
        });

    /// <summary>
    /// **A drive in This PC offered every verb a folder row gets.** Its rows
    /// are volumes: the item menu keeps what reads a drive's path — Open, Copy,
    /// Copy as path, Properties — and drops what would move, rename, bin or
    /// duplicate it, each of which refuses or is wrong for a volume (see
    /// PaneViewModel.CanMoveSelection). The background has no folder behind
    /// it, so no Paste, New, Properties or folder Windows menu either.
    /// </summary>
    [AvaloniaFact]
    public async Task This_pcs_menus_do_not_offer_to_move_a_drive()
        => await InAWindow(async (window, shell, pane) =>
        {
            PaneViewModel.Places = new Drives(new Place
            {
                Id = "dev:test",
                Label = "Test drive",
                Path = _root,
                Kind = PlaceKind.Device,
                Icon = "drive-harddisk",
            });

            await pane.NavigateAsync(VirtualPaths.Computer);
            await Until(window, () => pane.Entries.Count == 1 && Listing(window, pane).ContainerFromIndex(0) is not null,
                        "This PC never listed the drive");

            Listing(window, pane).SelectedIndex = 0;
            await Layout(window);

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, This PC", rows);

            foreach (var expected in new[] { "Open", "Copy", "Copy as path", "Properties" })
                Assert.Contains(expected, rows);

            foreach (var absent in new[] { "Cut", "Rename", "Rename in bulk…", shell.BinMoveLabel,
                                           "Duplicate", "Copy to", "Move to", "Create shortcut",
                                           "Compress to ZIP" })
                Assert.DoesNotContain(absent, rows);

            Assert.Empty(ListingMenus.StrayRules(menu, "item, This PC"));

            await Close(window, menu);

            var background = Background(window, pane);
            var folderRows = await Open(window, background);

            Print("background, This PC", folderRows);

            foreach (var absent in new[] { "Paste", "New", "Properties", "Windows menu", "Scripts",
                                           "Add this folder to places", "Share" })
                Assert.DoesNotContain(absent, folderRows);

            Assert.Empty(ListingMenus.StrayRules(background, "background, This PC"));
        });

    /// <summary>
    /// A search: rows from everywhere, so the item menu gains Open file
    /// location and loses what writes into the folder on screen — Duplicate,
    /// Create shortcut, the archive pair — which a search is not. The
    /// background saves the search and offers nothing that would write into it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_offers_where_things_are_and_nothing_that_writes_into_it()
        => await InAWindow(async (window, _, pane) =>
        {
            var hit = new FileEntry("notes.txt", Path.Combine(_root, "notes.txt"), 1,
                                    DateTimeOffset.Now, EntryFlags.None);

            UseSearch(new Finds(hit));

            await pane.NavigateAsync(VirtualPaths.Search("notes", _root, scoped: false));
            await Until(window, () => pane.Entries.Count == 1 && Listing(window, pane).ContainerFromIndex(0) is not null,
                        "the search never listed its hit");

            Listing(window, pane).SelectedIndex = 0;
            await Layout(window);

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, search", rows);

            Assert.Contains("Open file location", rows);
            Assert.Contains("Rename", rows);

            foreach (var absent in new[] { "Duplicate", "Create shortcut", "Compress to ZIP", "Forget (keeps the file)" })
                Assert.DoesNotContain(absent, rows);

            Assert.Empty(ListingMenus.StrayRules(menu, "item, search"));

            await Close(window, menu);

            var background = Background(window, pane);
            var folderRows = await Open(window, background);

            Print("background, search", folderRows);

            Assert.Contains("Save this search to places", folderRows);

            foreach (var absent in new[] { "Paste", "New", "Properties", "Windows menu", "Scripts",
                                           "Open terminal here", "Share", "Add this folder to places" })
                Assert.DoesNotContain(absent, folderRows);

            Assert.Empty(ListingMenus.StrayRules(background, "background, search"));
        });

    /// <summary>
    /// Recent files: Open file location and Forget, beside Open where they
    /// belong now rather than near the bottom.
    /// </summary>
    [AvaloniaFact]
    public async Task Recents_item_menu_puts_where_and_forget_beside_open()
        => await InAWindow(async (window, _, pane) =>
        {
            PaneViewModel.Recents = new Remembered(Path.Combine(_root, "notes.txt"));

            await pane.NavigateAsync(VirtualPaths.Files);
            await Until(window, () => pane.Entries.Count == 1 && Listing(window, pane).ContainerFromIndex(0) is not null,
                        "Recent never listed its file");

            Listing(window, pane).SelectedIndex = 0;
            await Layout(window);

            var menu = Item(window, pane);
            var rows = await Open(window, menu);

            Print("item, recent", rows);

            var open = rows.IndexOf("Open");
            var cut = rows.IndexOf("Cut");

            Assert.True(open >= 0 && cut > open, "Open and Cut are not both on the menu in order");
            Assert.InRange(rows.IndexOf("Open file location"), open + 1, cut - 1);
            Assert.InRange(rows.IndexOf("Forget (keeps the file)"), open + 1, cut - 1);

            Assert.Empty(ListingMenus.StrayRules(menu, "item, recent"));
        });

    // ---- View, merged -------------------------------------------------------

    /// <summary>
    /// **The sort half of View hides with its preference, and takes its rule
    /// with it.** The rule above Sort by has nothing under it with the
    /// preference off, and Avalonia collapses no rule: the opening's tidying is
    /// what keeps it off the bottom of the submenu.
    /// </summary>
    [AvaloniaFact]
    public async Task With_the_sort_preference_off_view_ends_at_hidden_files()
        => await InAWindow(async (window, shell, pane) =>
        {
            var menu = Background(window, pane);
            var view = ListingMenus.Row(menu, "View");

            await Open(window, menu);

            Assert.Equal(["List", "Small grid", "Large grid", "—", "Show hidden files", "—",
                          "Sort by", "Group by", "Columns"],
                         ListingMenus.Read(view));

            await Close(window, menu);

            Vaktari.Ui.Settings.AppSettings.Apply(Vaktari.Ui.Settings.AppSettings.Current with
            {
                ContextMenu = new ContextMenuSettings { ShowSortBy = false },
            });
            shell.OnSettingsChanged();

            await Open(window, menu);

            Assert.Equal(["List", "Small grid", "Large grid", "—", "Show hidden files"],
                         ListingMenus.Read(view));
        });

    // ---- scripts --------------------------------------------------------------

    /// <summary>
    /// **The background menu's Scripts ends in the row that opens their
    /// folder**, drawn with its own words through the submenu's DataTemplates —
    /// a record with no template would draw as its type name. Opened for real,
    /// so the containers exist to be read.
    /// </summary>
    [AvaloniaFact]
    public async Task The_folders_scripts_submenu_ends_in_open_scripts_folder()
        => await InAWindow(async (window, _, pane) =>
        {
            var menu = Background(window, pane);
            await Open(window, menu);

            var scripts = menu.Items.OfType<MenuItem>().Single(i => i.Name == "FolderScriptsMenu");

            Assert.True(scripts.IsVisible, "Scripts is not on the background menu in a real folder");
            Assert.Same(ScriptsFolderRow.Instance, scripts.Items.Cast<object>().Last());

            scripts.Open();
            await Layout(window);

            var last = scripts.ContainerFromIndex(scripts.ItemCount - 1);

            Assert.NotNull(last);
            Assert.Contains(last!.GetVisualDescendants().OfType<TextBlock>(),
                            t => t.Text == "Open scripts folder");

            // And the command is the one that tells the two kinds of row apart.
            Assert.Same(pane.RunScriptHereCommand, ((MenuItem)last).Command);
        });

    // ---- doubles --------------------------------------------------------------

    /// <summary>Records which of the shell's two menus was asked for, and
    /// about what.</summary>
    private sealed class AskedShell : IShellMenuProvider
    {
        public List<(bool Background, IReadOnlyList<string> Paths)> Asked { get; } = [];

        public Task<IShellMenu?> BuildAsync(IReadOnlyList<string> paths)
        {
            Asked.Add((false, paths));
            return Task.FromResult<IShellMenu?>(null);
        }

        public Task<IShellMenu?> BuildBackgroundAsync(string folder)
        {
            Asked.Add((true, [folder]));
            return Task.FromResult<IShellMenu?>(null);
        }
    }

    /// <summary>A bin holding the names it was built with.</summary>
    private sealed class Bin(params string[] names) : ITrashMaintenance
    {
        public IReadOnlyList<TrashedItem> List() =>
            [.. names.Select(n => new TrashedItem(
                n, Path.Combine(Path.GetTempPath(), "vaktari-gone", n), "payload/" + n,
                DateTimeOffset.UnixEpoch, 1, false))];

        public void Delete(string trashName) { }
        public string Restore(string trashName) => trashName;

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    /// <summary>The places behind This PC.</summary>
    private sealed class Drives(params Place[] places) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public string? NameFor(string path) => null;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([new("DEVICES", places)]);

        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    /// <summary>A search backend that finds what it was built with.</summary>
    private sealed class Finds(params FileEntry[] hits) : ISearchProvider
    {
        public string BackendName => "test";
        public bool SupportsContentSearch => false;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            foreach (var hit in hits) yield return hit;
        }
    }

    /// <summary>A recent store holding one file.</summary>
    private sealed class Remembered(string file) : IRecentStore
    {
        public void Record(string path, RecentKind kind) { }

        public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count)
            => kind == RecentKind.File ? [new RecentEntry(file, RecentKind.File, DateTimeOffset.Now)] : [];

        public void Forget(string path) { }
        public int Count => 1;
        public int ForgetAll() => 0;

        public event EventHandler? Changed { add { } remove { } }
    }
}
