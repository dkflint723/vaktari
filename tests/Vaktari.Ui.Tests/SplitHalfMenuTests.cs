using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Session;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The listing's two menus in the half of a split that is not active, and
/// the Shift that arms the administrator rows** — two routes of the two-menu
/// redesign that no test pressed for real.
///
/// A right-click in the other half has to do two things before its menu is of
/// any use: make that half the active one, because a row bound through the
/// shell (Add this folder to places, Copy across, Properties) acts on the
/// shell's active tab; and open THAT half's menu, about the row under the
/// pointer. Measured by removing the press's ActivateGroupAt: the right half's
/// menu still opened, over a left half that stayed active.
///
/// Shift on the right press is what the extended rows are armed by, and it is
/// recorded on the press because a menu opening carries no record of which keys
/// were down. Measured by arming on Alt instead: nothing else noticed.
/// </summary>
public sealed class SplitHalfMenuTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-splitmenus").FullName;

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

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
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
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

    /// <summary>Waits until the point about to be clicked hits what the test
    /// says is there, polled at that very point.</summary>
    private static async Task Hits(Window window, Point at, Func<Visual, bool> expected, string what)
    {
        for (var i = 0; i < 50; i++)
        {
            await Layout(window);

            if (window.InputHitTest(at) is Visual hit && expected(hit)) return;

            await Task.Delay(10);
        }

        Assert.Fail($"the point never hit {what}, so no click there means anything");
    }

    private static ListBox? Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
            .OfType<ListBox>()
            .SingleOrDefault(l => l.IsVisible && ReferenceEquals(l.DataContext, pane)
                                  && l.SelectionMode.HasFlag(SelectionMode.Multiple));

    private static Point At(Visual visual, double x, double y, Window window)
        => visual.TranslatePoint(new Point(x, y), window)
           ?? throw new InvalidOperationException("the control is not in the window");

    private static async Task RightClick(Window window, Point at, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(at, MouseButton.Right, modifiers);
        await Layout(window);
        window.MouseUp(at, MouseButton.Right, modifiers);
        await Layout(window);
    }

    private static void CloseMenus(Window window)
    {
        foreach (var menu in window.GetVisualDescendants().OfType<Control>()
                     .Select(c => c.ContextMenu).OfType<ContextMenu>().Distinct())
            if (menu.IsOpen) menu.Close();
    }

    /// <summary>
    /// A real window split in two, the right half showing this class's folder
    /// in Details and the LEFT half active. The split, the right half's path
    /// and the left half's path are put back before the close, which flushes
    /// the developer's own session.
    /// </summary>
    private async Task InASplit(Func<MainWindow, ShellViewModel, PaneGroupViewModel, Task> body)
    {
        UseSearch(PaneViewModel.Search);

        Directory.CreateDirectory(Path.Combine(_root, "adir"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");

        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var split = shell.IsSplit;
        var home = shell.Left.ActiveTab?.CurrentPath;
        string? rightWas = null;
        ViewMode? rightView = null;

        try
        {
            // Ensured rather than toggled: a session left split elsewhere is
            // restored by the constructor.
            if (!shell.IsSplit) shell.ToggleSplit();

            await Layout(window);

            var other = shell.Right!;
            var pane = other.ActiveTab!;

            rightWas = pane.CurrentPath;
            rightView = pane.View;

            pane.View = ViewMode.Details;
            await pane.NavigateAsync(_root);

            shell.ActivateGroup(shell.Left);

            await Until(window, () => Listing(window, pane) is { } list && list.ContainerFromIndex(2) is not null,
                        "the right half never listed the folder");

            Assert.NotSame(other, shell.ActiveGroup);

            await body(window, shell, other);
        }
        finally
        {
            CloseMenus(window);

            if (shell.Right?.ActiveTab is { } right)
            {
                if (rightView is { } view) right.View = view;
                if (!string.IsNullOrEmpty(rightWas)) await right.NavigateAsync(rightWas);
            }

            if (shell.IsSplit != split) shell.ToggleSplit();

            if (home is { } back && shell.ActiveTab is { } tab) await tab.NavigateAsync(back);

            Settle();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_right_click_on_a_row_in_the_other_half_opens_that_halfs_item_menu_and_makes_it_active()
        => await InASplit(async (window, shell, other) =>
        {
            var pane = other.ActiveTab!;
            var list = Listing(window, pane)!;
            var row = list.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(i => i.DataContext is Vaktari.Core.FileSystem.FileEntry { Name: "b.txt" });

            var at = At(row, 40, row.Bounds.Height / 2, window);

            await Hits(window, at, hit => hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) == row, "the row");

            await RightClick(window, at);

            Assert.Same(other, shell.ActiveGroup);
            Assert.Equal("b.txt", pane.SelectedEntry?.Name);
            Assert.True(ListingMenus.Above(list, ListingMenus.Item).IsOpen, "the other half's item menu did not open");
            Assert.False(ListingMenus.Above(list, ListingMenus.Background).IsOpen);

            var left = Listing(window, shell.Left.ActiveTab!);

            if (left is not null)
            {
                Assert.False(ListingMenus.Above(left, ListingMenus.Item).IsOpen, "the active half's menu opened instead");
                Assert.False(ListingMenus.Above(left, ListingMenus.Background).IsOpen, "the active half's menu opened instead");
            }
        });

    [AvaloniaFact]
    public async Task A_right_click_below_the_rows_of_the_other_half_opens_that_halfs_background_menu_and_makes_it_active()
        => await InASplit(async (window, shell, other) =>
        {
            var pane = other.ActiveTab!;
            var list = Listing(window, pane)!;
            var below = At(list, list.Bounds.Width / 2, list.Bounds.Height - 12, window);

            await Hits(window, below,
                       hit => hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null
                              && hit.FindAncestorOfType<ListBox>(includeSelf: true) == list,
                       "the other half's empty space");

            await RightClick(window, below);

            Assert.Same(other, shell.ActiveGroup);
            Assert.True(ListingMenus.Above(list, ListingMenus.Background).IsOpen, "the other half's background menu did not open");
            Assert.False(ListingMenus.Above(list, ListingMenus.Item).IsOpen);

            // A row bound through the shell now names this half's folder.
            Assert.Equal(pane.CurrentPath, shell.ActiveTab?.CurrentPath);
        });

    /// <summary>
    /// **A right-click on a group heading in the other half left that half
    /// inactive.** The press on a heading is claimed so it moves no selection,
    /// and the claim returned before the activation every other press gets —
    /// so the background menu opened in the right half while the shell's
    /// active tab, which its shell-bound rows act on, was the left half's.
    /// </summary>
    [AvaloniaFact]
    public async Task A_right_click_on_a_heading_in_the_other_half_makes_it_active()
        => await InASplit(async (window, shell, other) =>
        {
            var pane = other.ActiveTab!;

            pane.GroupBy = Vaktari.Core.FileSystem.GroupMode.Kind;

            try
            {
                await Until(window, () => Headings(window, pane).Any(), "the other half drew no group heading");

                var heading = Headings(window, pane).First();
                var at = At(heading, heading.Bounds.Width * 0.6, heading.Bounds.Height / 2, window);

                await Hits(window, at, hit => MainWindow.GroupHeadingAt(hit) is not null, "the heading");

                shell.ActivateGroup(shell.Left);
                Assert.NotSame(other, shell.ActiveGroup);

                await RightClick(window, at);

                Assert.Same(other, shell.ActiveGroup);
                Assert.True(ListingMenus.Above(Listing(window, pane)!, ListingMenus.Background).IsOpen,
                            "the heading opened no background menu");
            }
            finally
            {
                CloseMenus(window);
                pane.GroupBy = Vaktari.Core.FileSystem.GroupMode.None;
            }
        });

    private static IEnumerable<Control> Headings(Window window, PaneViewModel pane)
        => Listing(window, pane)!.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains(MainWindow.GroupHeadingClass) && c.IsVisible && c.Bounds.Height > 0);

    /// <summary>
    /// Shift on the right press arms the administrator rows; a plain right
    /// press disarms them. Asked of the pane under the pointer, which the
    /// press writes and the menu reads.
    /// </summary>
    [AvaloniaFact]
    public async Task Shift_on_the_right_press_arms_the_admin_rows_and_a_plain_press_does_not()
        => await InASplit(async (window, _, other) =>
        {
            var pane = other.ActiveTab!;
            var list = Listing(window, pane)!;
            var below = At(list, list.Bounds.Width / 2, list.Bounds.Height - 12, window);

            await Hits(window, below,
                       hit => hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null
                              && hit.FindAncestorOfType<ListBox>(includeSelf: true) == list,
                       "the empty space");

            await RightClick(window, below, RawInputModifiers.Shift);

            Assert.True(pane.AdminRequested, "Shift on the right press did not arm the administrator rows");

            CloseMenus(window);
            await Layout(window);

            await RightClick(window, below);

            Assert.False(pane.AdminRequested, "a plain right press left the administrator rows armed");
        });
}
