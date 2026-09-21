using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Whether the menu rows that reach up to the window actually resolve.
///
/// **This suite could not see a dead reach-up binding, and these two tests are
/// the shapes that can.** A ContextMenu is its own popup root, so a row that
/// needs something off the shell reaches for it through
/// <c>$parent[Window]</c>; MainWindow.axaml carries over a hundred of those.
/// Until now every assertion about them compared the markup's TEXT — the
/// binding expression as a string — which is satisfied whether or not the
/// binding resolves at runtime.
///
/// Three things make that gap invisible rather than merely untested:
///
/// The headless host in TestApp is built without LogToTrace, and nothing in
/// tests/ turns logging on. Under test an unresolved binding is not thrown,
/// not logged and not counted.
///
/// A failed binding leaves the property at its DEFAULT, and IsVisible defaults
/// true. So the failure mode is fail-OPEN: a gate that should hide a row shows
/// it instead. Every existing assertion about these rows is in the direction a
/// fail-open binding satisfies. CutFadeBindingTests wrote the same observation
/// down for Opacity — "nothing would be logged where anybody would see it" —
/// and it is the reason that file exists.
///
/// And six of the flags these bindings gate — ShowCopyToInMenu,
/// ShowMoveToInMenu, ShowSortByInMenu, ShowDuplicateInMenu,
/// ShowCopyLocationInMenu, CanInstallSharing — were named nowhere in this test
/// tree at all while being live in the application.
///
/// **So both tests below are written in the only two shapes a fail-open
/// binding cannot satisfy**: asserting a gated row is HIDDEN, and asserting a
/// bound Command is the shell's own object by reference. A broken gate shows
/// the row; a broken Command binding leaves it null. Neither can pass by
/// accident.
///
/// These are worth having whether or not MainWindow.axaml is ever broken up.
/// The comment under that file's Window tag explains why it was left whole,
/// and names this shape as the thing to build first if that is reopened.
/// </summary>
public sealed class ReachUpBindingTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;
    private MainWindow? _window;

    public override void Dispose()
    {
        // A headless MainWindow flushes the developer's own session.json when
        // it closes, and a window left open is torn down later on whatever
        // thread xunit is on — see OwnedViewModels.
        _window?.Close();

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A gate that says "hide this" hides it.
    ///
    /// Arrange is gated on ShowSortByInMenu, which is <c>ContextMenu.ShowSortBy</c>
    /// and nothing else — no selection state, no platform, so it can be driven
    /// to false and stay there. With the preference off the row must be
    /// invisible. If the reach-up stops resolving, IsVisible falls back to its
    /// default of true and this fails; there is no third outcome, which is the
    /// whole point of asserting in this direction.
    ///
    /// The setting is applied AFTER the window is built, because MainWindow's
    /// constructor applies the settings it loads from disk and would overwrite
    /// a value set before it.
    /// </summary>
    [AvaloniaFact]
    public void A_menu_row_gated_off_is_actually_hidden()
    {
        var (window, shell, pane) = Open();

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore with
        {
            ContextMenu = _settingsBefore.ContextMenu with { ShowSortBy = false },
        });

        // The gate is computed, not stored, so the shell has to be told the
        // answer changed before the binding can carry it.
        shell.OnSettingsChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ShowSortByInMenu,
                     "the preference did not reach the shell, so the row below proves nothing");

        var menu = OpenListingMenu(window, pane);

        try
        {
            var arrange = Row(menu, "Arrange");

            Assert.False(arrange.IsVisible,
                         "the row is showing with its preference off — either the gate is not "
                         + "bound, or the reach-up through $parent[Window] no longer resolves. "
                         + "A failed binding leaves IsVisible at its default of true and says "
                         + "nothing anywhere.");
        }
        finally
        {
            menu.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A Command that reaches up arrives as the shell's own command object.
    ///
    /// Asserted by REFERENCE rather than by "not null and does something":
    /// identity is what distinguishes a binding that resolved from one that
    /// found some other object with a command of the same name. A broken
    /// reach-up leaves Command null, which fails here too.
    ///
    /// Open in new tab carries a gate of its own (ShowOpenInNewTabInMenu,
    /// which also wants a directory selected), and that is deliberately not
    /// what is under test here: a Command binding resolves whether or not the
    /// row is showing, so the row is looked up regardless of IsVisible and
    /// only its Command is asserted. The gate is the other test's subject.
    /// </summary>
    [AvaloniaFact]
    public void A_menu_row_reaching_up_for_a_command_gets_the_shell_s_own()
    {
        var (window, shell, pane) = Open();

        var menu = OpenListingMenu(window, pane);

        try
        {
            var row = Row(menu, "Open in new tab");

            Assert.NotNull(row.Command);

            Assert.Same(shell.OpenInNewTabCommand, row.Command);
        }
        finally
        {
            menu.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A shown, laid-out window with its pane ready.</summary>
    private (MainWindow Window, ShellViewModel Shell, PaneViewModel Pane) Open()
    {
        // The constructor assigns the platform's real search backend to
        // PaneViewModel's static; borrowing it gives it back on Dispose.
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();
        _window = window;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        return (window, shell, shell.ActiveTab!);
    }

    /// <summary>
    /// Opens the listing's own menu with Shift+F10, the way ColumnChooserTests
    /// does — including sending the keyboard away and back first, because where
    /// focus sits decides which arm of OnWindowKeyDown answers that key, and
    /// the window parks it in the listing by itself at Background priority.
    /// </summary>
    private static ContextMenu OpenListingMenu(MainWindow window, PaneViewModel pane)
    {
        var list = Listing(window, pane);
        var menu = MenuAbove(list);

        var sidebar = window.FindControl<Border>("SidebarPanel");

        Assert.NotNull(sidebar);

        SidebarReady(window);

        var place = sidebar!.GetVisualDescendants()
                            .OfType<Button>()
                            .First(b => b.IsVisible && b is not ToggleButton);

        place.Focus();
        Dispatcher.UIThread.RunJobs();

        list.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(list.IsFocused, "the listing never took the keyboard back");

        window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(menu.IsOpen, "the key did not open the listing's menu");

        return menu;
    }

    private static ListBox Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
                 .OfType<ListBox>()
                 .Single(l => l.IsVisible
                              && ReferenceEquals(l.DataContext, pane)
                              && l.SelectionMode.HasFlag(SelectionMode.Multiple));

    private static ContextMenu MenuAbove(Visual from)
    {
        for (Visual? visual = from; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { ContextMenu: { } menu })
                return menu;

        throw new InvalidOperationException("nothing above the listing carries a context menu");
    }

    /// <summary>
    /// Read through MenuLabels because these rows carry access-key markers —
    /// "Arran_ge", "Open in new _tab" — and which letter each takes is
    /// ContextMenuKeysTests' business, not this file's.
    ///
    /// Searched with IsVisible ignored, unlike the walk in ColumnChooserTests:
    /// the first test here is about a row that should NOT be visible, so a
    /// lookup that skipped hidden rows could never find its subject and would
    /// throw instead of asserting.
    /// </summary>
    private static MenuItem Row(ItemsControl menu, string header)
        => menu.Items.OfType<MenuItem>()
               .Single(i => MenuLabels.Plain(i.Header as string) == header);
}
