using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where the Menu key puts the menu.
///
/// **Pressing Menu in the listing raised an unhandled exception; no menu opened
/// at all.** The keyboard route called menu.Open(list) under a comment that
/// said the menu was thereby "placed on the list rather than at the pointer".
/// The listing's menu is not attached to the listing: it hangs off the
/// ItemsControl that holds the tabs, and the ListBox lives inside that
/// control's item template. ContextMenu.Open refuses any control but the one it
/// is attached to, so every press threw ArgumentException — "Cannot show
/// ContentMenu on a different control to the one it is attached to" — out of
/// OnWindowKeyDown, with nothing above it to catch it. Measured: restoring that
/// call reddens all four tests below with exactly that exception, so
/// <see cref="The_menu_key_opens_the_menu_at_the_focused_row"/> is first of all
/// the pin for "the Menu key does not throw".
///
/// The comment it was written under was wrong about the mechanism as well, in
/// the opposite direction. Open(control) DOES anchor the popup on the control
/// it is handed — measured on Avalonia 12.1, with PlacementTarget left null the
/// popup's own PlacementTarget comes back as that control. What it does not
/// touch is Placement, and a ContextMenu is born Pointer; the pointer wins over
/// the anchor. So had the call been legal, the menu would have opened wherever
/// the mouse was resting rather than on the list.
///
/// The fix sets Placement for the keystroke and puts it back when the menu
/// closes, rather than declaring it in the markup, because ONE ContextMenu
/// serves both routes: a right-click has a pointer and must still open there.
/// The restore is the half that is easy to leave out and impossible to see —
/// the menu would look right every time it was opened with the key, and every
/// right-click afterwards would open somewhere else.
/// </summary>
public sealed class ContextMenuPlacementTests : OwnedViewModels
{
    /// <summary>
    /// Pumps the dispatcher and lays the window out, the way the other window
    /// tests in this assembly do.
    /// </summary>
    private static async Task Layout(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The listing the window is showing, found the way the window's
    /// own ActiveListing finds it.</summary>
    private static ListBox Listing(MainWindow window, PaneViewModel pane)
        => window.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(list => list.IsVisible
                            && ReferenceEquals(list.DataContext, pane)
                            && list.SelectionMode.HasFlag(SelectionMode.Multiple));

    /// <summary>The menu that hangs off the tab strip above the listing, which
    /// is where the listing's menu actually lives.</summary>
    private static ContextMenu ListingMenu(ListBox list)
    {
        for (var visual = (Visual?)list; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { ContextMenu: { } menu }) return menu;

        throw new InvalidOperationException("the listing has no context menu above it");
    }

    private static string TempFolder(int files)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-menuplace-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        for (var i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(root, $"file-{i}.txt"), "x");

        return root;
    }

    /// <summary>
    /// Runs a body against a real window showing a real folder, and puts the
    /// tab back where it was — this window flushes the session it was built
    /// from when it closes.
    /// </summary>
    private async Task InAWindow(int files, Func<MainWindow, PaneViewModel, Task> body)
    {
        UseSearch(PaneViewModel.Search);

        var root = TempFolder(files);
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var was = shell.ActiveTab?.CurrentPath;

        try
        {
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            await Layout(window);

            await body(window, pane);
        }
        finally
        {
            if (was is { } back && shell.ActiveTab is { } tab)
            {
                await tab.NavigateAsync(back);
                Dispatcher.UIThread.RunJobs();
            }

            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>Presses the key that asks for the menu.</summary>
    private static void PressMenuKey(Window window)
        => window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);

    // ---- the row ------------------------------------------------------------

    /// <summary>
    /// The finding itself: the menu opens under the row the keyboard is on.
    ///
    /// BottomEdgeAlignedLeft anchored on the ListBoxItem, which is where
    /// Explorer draws it — under the row, left-aligned with it — and what makes
    /// the answer to "which file is this menu about" the row it is touching.
    ///
    /// **Focus and selection are pulled apart here on purpose.** Ctrl+arrow
    /// moves the focused row without selecting it, so a menu placed on the
    /// SELECTED row would appear somewhere the person is not looking — and a
    /// test that focused the row it had just selected would be satisfied by
    /// either rule.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_key_opens_the_menu_at_the_focused_row()
        => await InAWindow(6, async (window, pane) =>
        {
            var list = Listing(window, pane);

            Assert.True(list.ItemCount >= 4, "the listing did not load, so this proves nothing");

            // Selected here, focused three rows down.
            list.SelectedIndex = 0;

            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(3));

            row.Focus();

            await Layout(window);

            var menu = ListingMenu(list);

            PressMenuKey(window);
            await Layout(window);

            Assert.True(menu.IsOpen, "the Menu key did not open the listing's menu");

            // The two halves of "at the row": the anchor is the row the
            // keyboard is on, and the menu hangs off its bottom edge rather
            // than following a pointer that is nowhere near it.
            Assert.Same(row, menu.PlacementTarget);
            Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Placement);

            menu.Close();
            await Layout(window);
        });

    /// <summary>
    /// And the other way round. A listing can be selected without any row
    /// holding the keyboard — click a row, press F6 twice, come back — and the
    /// selected row is then the only row the menu could sensibly be about.
    /// </summary>
    [AvaloniaFact]
    public async Task A_selection_the_keyboard_is_not_sitting_on_still_gets_the_menu()
        => await InAWindow(6, async (window, pane) =>
        {
            var list = Listing(window, pane);

            Assert.True(list.ItemCount >= 3, "the listing did not load, so this proves nothing");

            // The LIST has the keyboard, not a row inside it.
            list.Focus();
            list.SelectedIndex = 2;

            await Layout(window);

            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(2));

            Assert.False(row.IsKeyboardFocusWithin,
                         "the row took focus, so this proves nothing about the fallback");

            var menu = ListingMenu(list);

            PressMenuKey(window);
            await Layout(window);

            Assert.Same(row, menu.PlacementTarget);
            Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Placement);

            menu.Close();
            await Layout(window);
        });

    /// <summary>
    /// And the restore, which is the half a person would never see going wrong.
    ///
    /// **Left set, BottomEdgeAlignedLeft outlives the keystroke and pins the
    /// next right-click under the tab strip.** Placement is a property of the
    /// menu and one menu serves both routes. Measured, driving a real
    /// right-button press at row 0 of a headless MainWindow with the keyboard's
    /// placement left behind: the popup opened BottomEdgeAlignedLeft anchored
    /// on a 30px-tall panel inside the tab strip — not at the cursor, and not
    /// on any row. Not under the row the keyboard had, either: the right-click
    /// route never reads the menu's PlacementTarget, it re-anchors on the
    /// attached control's own panel, so only Placement carries over. Every
    /// right-click after a keyboard one would have gone up there until the next
    /// keyboard one.
    ///
    /// Which is also why the assertion on PlacementTarget below is about
    /// housekeeping rather than about placement: nulling it moves nothing, it
    /// drops the menu's reference to a ListBoxItem the virtualizing panel is
    /// free to recycle.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_the_menu_gives_the_pointer_its_placement_back()
        => await InAWindow(6, async (window, pane) =>
        {
            var list = Listing(window, pane);
            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));

            list.SelectedIndex = 1;
            row.Focus();

            await Layout(window);

            var menu = ListingMenu(list);

            PressMenuKey(window);
            await Layout(window);

            Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Placement);

            menu.Close();
            await Layout(window);

            // Avalonia's own default for a ContextMenu, which is what a
            // right-click wants: the menu appears under the cursor that asked
            // for it.
            Assert.Equal(PlacementMode.Pointer, menu.Placement);
            Assert.Null(menu.PlacementTarget);
        });

    /// <summary>
    /// The restore runs before the close handler can return early, and this is
    /// a source rule because nothing else here can be.
    ///
    /// **The block under it returns when the closing menu's DataContext has no
    /// ActiveTab, and what the restore puts back belongs to the MENU rather
    /// than to any pane.** Measured: moving the two lines below that return
    /// leaves all four tests above green, because every window this suite can
    /// build has a tab and never takes the early exit. So the ordering is real
    /// and invisible to them, which is what a rule of its own is for.
    /// </summary>
    [Fact]
    public void The_placement_is_put_back_before_the_handler_can_return_early()
    {
        var body = RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                                   "private void OnListingMenuClosed(");

        var restore = body.IndexOf("menu.Placement = PlacementMode.Pointer;",
                                   StringComparison.Ordinal);

        var bail = body.IndexOf("            return;", StringComparison.Ordinal);

        Assert.True(restore >= 0, "OnListingMenuClosed no longer puts the placement back");
        Assert.True(bail >= 0, "OnListingMenuClosed no longer has the early return this is about");

        Assert.True(restore < bail,
                    "the placement is put back after the handler can already have returned");
    }

    /// <summary>
    /// An empty folder has no row to hang the menu on, and the pointer is still
    /// no answer. The menu goes in the middle of the listing — on the thing it
    /// is about — rather than at wherever the mouse was left.
    /// </summary>
    [AvaloniaFact]
    public async Task An_empty_listing_puts_the_menu_in_the_middle_of_itself()
        => await InAWindow(0, async (window, pane) =>
        {
            var list = Listing(window, pane);

            Assert.Equal(0, list.ItemCount);

            list.Focus();
            await Layout(window);

            var menu = ListingMenu(list);

            PressMenuKey(window);
            await Layout(window);

            Assert.True(menu.IsOpen, "the Menu key did not open the listing's menu");

            Assert.Same(list, menu.PlacementTarget);
            Assert.Equal(PlacementMode.Center, menu.Placement);

            menu.Close();
            await Layout(window);
        });
}
