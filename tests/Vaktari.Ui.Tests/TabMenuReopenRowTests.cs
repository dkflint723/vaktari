using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Reopen closed tab" on a REAL tab menu, in a real split window (fix-12
/// verification).
///
/// ListingMenuGatesTests pins the gate on the view model and runs the command
/// with a tab handed in; neither reaches the markup, so a row whose
/// <c>IsVisible</c> or <c>CommandParameter</c> stopped binding went unseen —
/// and a dead binding here is fail-open: the row shows, and the command gets
/// null, which is the ACTIVE side (see ReachUpBindingTests). These open the
/// tab's own context menu and assert in the two directions a dead binding
/// cannot satisfy: hidden where nothing was closed, and the tab itself as the
/// parameter, by reference. And the key, which hands in no tab, still acts on
/// the active side.
///
/// Every window here starts with one side and leaves with one, as
/// CompareSidesTests explains.
/// </summary>
public sealed class TabMenuReopenRowTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-reopenrow").FullName;
    private MainWindow? _window;

    public override void Dispose()
    {
        if (_window is { } window)
        {
            if (window.Shell.IsSplit) window.Shell.ToggleSplit();

            var services = window.Services;

            window.Close();

            // Close() only starts the teardown that writes session.json; wait
            // for it, so the next window in the run does not restore this one.
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (services.Windows.Count > 0 && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
        }

        AppSettings.Apply(_settingsBefore);

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }

        base.Dispose();
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>A real window split in two, each side with one tab it has
    /// just closed when <paramref name="closeOnLeft"/> / <paramref name="closeOnRight"/>.</summary>
    private ShellViewModel Split(bool closeOnLeft, bool closeOnRight)
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 800 };
        _window = window;

        window.Show();
        Pump();

        var shell = window.Shell;

        if (shell.IsSplit)
        {
            shell.ToggleSplit();
            Pump();
        }

        shell.ToggleSplit();
        Pump();

        Assert.True(shell.IsSplit);

        foreach (var (group, close) in new[] { (shell.Left, closeOnLeft), (shell.Right!, closeOnRight) })
        {
            if (!close) continue;

            var extra = group.AddTab(_root);
            Pump();
            group.CloseTab(extra);
            Pump();
        }

        return shell;
    }

    /// <summary>The tab strip's own panel for <paramref name="tab"/> — the one
    /// whose menu carries the reopening row.</summary>
    private DockPanel TabPanel(PaneViewModel tab)
        => _window!.GetVisualDescendants()
                   .OfType<DockPanel>()
                   .Single(d => ReferenceEquals(d.DataContext, tab)
                                && d.ContextMenu is { } m
                                && m.Items.OfType<MenuItem>().Any(i => MenuLabels.Plain(i.Header as string) == "Reopen closed tab"));

    /// <summary>The tab's menu, open, with its reopening row and the separator
    /// drawn above it.</summary>
    private (ContextMenu Menu, MenuItem Row, Separator Rule) OpenTabMenu(PaneViewModel tab)
    {
        var panel = TabPanel(tab);
        var menu = panel.ContextMenu!;

        menu.Open(panel);
        Pump();

        Assert.True(menu.IsOpen, "the tab's menu did not open");

        var items = menu.Items.Cast<object>().ToList();
        var row = items.OfType<MenuItem>().Single(i => MenuLabels.Plain(i.Header as string) == "Reopen closed tab");

        return (menu, row, Assert.IsType<Separator>(items[items.IndexOf(row) - 1]));
    }

    private static void Close(ContextMenu menu)
    {
        menu.Close();
        Pump();
    }

    /// <summary>
    /// Only the right side has closed a tab. On the LEFT tab's menu the row
    /// and its rule are hidden; on the RIGHT tab's menu they show, the row is
    /// the shell's own command and its parameter the tab — so choosing it,
    /// with the LEFT side active, puts the tab back on the right.
    /// </summary>
    [AvaloniaFact]
    public void The_row_is_hidden_where_nothing_was_closed_and_reopens_on_its_own_side()
    {
        var shell = Split(closeOnLeft: false, closeOnRight: true);
        var left = shell.Left.ActiveTab!;
        var right = shell.Right!.ActiveTab!;

        shell.ActivateGroup(shell.Left);
        Pump();

        var (leftMenu, leftRow, leftRule) = OpenTabMenu(left);

        try
        {
            Assert.False(leftRow.IsVisible,
                "the reopening row shows on a side that has closed nothing — its IsVisible binding "
                + "is gone or no longer resolves, and a failed binding leaves it at true");
            Assert.False(leftRule.IsVisible, "the rule above a hidden row is drawn with nothing under it");
        }
        finally
        {
            Close(leftMenu);
        }

        var leftCount = shell.Left.Tabs.Count;
        var rightCount = shell.Right.Tabs.Count;

        var (rightMenu, rightRow, rightRule) = OpenTabMenu(right);

        try
        {
            Assert.True(rightRow.IsVisible);
            Assert.True(rightRule.IsVisible);
            Assert.Same(shell.ReopenClosedTabCommand, rightRow.Command);
            Assert.Same(right, rightRow.CommandParameter);

            rightRow.Command!.Execute(rightRow.CommandParameter);
            Pump();
        }
        finally
        {
            Close(rightMenu);
        }

        Assert.Equal(leftCount, shell.Left.Tabs.Count);
        Assert.Equal(rightCount + 1, shell.Right.Tabs.Count);
    }

    /// <summary>
    /// The mirror: both sides have closed a tab, the RIGHT side is active, and
    /// the row is chosen on a LEFT tab — the left side gets its tab back.
    /// </summary>
    [AvaloniaFact]
    public void The_row_on_a_left_tab_reopens_on_the_left_while_the_right_is_active()
    {
        var shell = Split(closeOnLeft: true, closeOnRight: true);
        var left = shell.Left.ActiveTab!;

        shell.ActivateGroup(shell.Right!);
        Pump();

        var leftCount = shell.Left.Tabs.Count;
        var rightCount = shell.Right!.Tabs.Count;

        var (menu, row, _) = OpenTabMenu(left);

        try
        {
            Assert.True(row.IsVisible);
            Assert.Same(left, row.CommandParameter);

            row.Command!.Execute(row.CommandParameter);
            Pump();
        }
        finally
        {
            Close(menu);
        }

        Assert.Equal(leftCount + 1, shell.Left.Tabs.Count);
        Assert.Equal(rightCount, shell.Right.Tabs.Count);
    }

    /// <summary>
    /// **The key is unchanged**: Ctrl+Shift+T hands in no tab and reopens on
    /// the ACTIVE side, whichever that is.
    /// </summary>
    [AvaloniaFact]
    public void The_key_reopens_on_the_active_side()
    {
        var shell = Split(closeOnLeft: true, closeOnRight: true);

        var leftCount = shell.Left.Tabs.Count;
        var rightCount = shell.Right!.Tabs.Count;

        shell.ActivateGroup(shell.Right);
        Pump();

        _window!.KeyPress(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.T, null);
        Pump();

        Assert.Equal(leftCount, shell.Left.Tabs.Count);
        Assert.Equal(rightCount + 1, shell.Right.Tabs.Count);

        shell.ActivateGroup(shell.Left);
        Pump();

        _window!.KeyPress(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.T, null);
        Pump();

        Assert.Equal(leftCount + 1, shell.Left.Tabs.Count);
        Assert.Equal(rightCount + 1, shell.Right.Tabs.Count);
    }
}
