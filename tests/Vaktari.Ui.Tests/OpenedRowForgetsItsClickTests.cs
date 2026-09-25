using Avalonia;
using Avalonia.Controls;
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
/// A row opened by the keyboard or the menu is not "clicked once" any more.
///
/// **One click re-opened a row last opened with Enter or the menu.** The
/// two-click open remembers the row clicked once, with no time limit, and
/// only TryOpen forgot it — the pointer's own route. Enter opens through
/// pane.OpenSelectedAsync and the menu's Open through OpenSelectedCommand,
/// and neither passes there. So: click a folder, press Enter, go Back, and a
/// single click on that folder — to rename it, to drag it — went into it
/// again. On a program it launched it a second time.
///
/// Driven with real presses on a real window, because what is under test is
/// four members agreeing on one field: OnTapped, OnWindowKeyDown,
/// OnListingMenuOpening and OpenListingMenu. The model in PointerGestureTests
/// restates the rule and cannot see a route that forgets to apply it.
/// </summary>
public sealed class OpenedRowForgetsItsClickTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private MainWindow? _window;
    private string? _root;

    public override void Dispose()
    {
        // Closing flushes the session; TestState points it at this run's own
        // directory. A window left open is torn down later on whatever thread
        // xunit is on.
        if (_window?.DataContext is ShellViewModel shell && shell.ActiveTab is { } tab)
            tab.View = ViewMode.Details;

        _window?.Close();

        AppSettings.Apply(_settingsBefore);

        if (_root is not null)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            Settle();
            await Task.Delay(10);
        }

        Settle();

        Assert.True(done(), what);
    }

    /// <summary>
    /// Pumps for a while whatever happens — for the assertion that something
    /// did NOT happen, where there is no signal to wait for.
    /// </summary>
    private static async Task Drain()
    {
        for (var i = 0; i < 60; i++)
        {
            Settle();
            await Task.Delay(5);
        }

        Settle();
    }

    /// <summary>
    /// A real window on a folder holding one subfolder to open — a folder
    /// because opening one navigates, which a test can see and undo, where a
    /// file would be handed to the desktop — with the two-click rule pinned
    /// AFTER construction, because the constructor applies the settings on
    /// disk over anything set before it.
    /// </summary>
    private async Task<PaneViewModel> BuildAsync()
    {
        UseSearch(PaneViewModel.Search);

        _root = Path.Combine(
            Path.GetTempPath(), "vaktari-forgetclick-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(_root, "adir"));
        File.WriteAllText(Path.Combine(_root, "note.txt"), "x");

        var window = _window = new MainWindow();

        window.Show();
        Settle();

        AppSettings.Apply(_settingsBefore with
        {
            Navigation = _settingsBefore.Navigation with { OpenItemsWith = ActivationClick.Double },
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        // Details, explicitly: the view a new tab opens in is remembered, and
        // the clicks below aim at a row's width.
        pane.View = ViewMode.Details;

        await pane.NavigateAsync(_root);
        await Until(() => Row(window, pane, "adir") is not null, "the folder's rows never reached the screen");

        Assert.Equal(_root, pane.CurrentPath);

        return pane;
    }

    private static ListBox? Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(list => list.IsVisible
                                    && ReferenceEquals(list.DataContext, pane)
                                    && list.SelectionMode.HasFlag(SelectionMode.Multiple));

    private static ListBoxItem? Row(Window window, PaneViewModel pane, string name)
    {
        window.UpdateLayout();

        return Listing(window, pane)?.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(r => r.DataContext is FileEntry entry && entry.Name == name
                                 && r.Bounds.Width > 0);
    }

    /// <summary>
    /// One plain click, at a fraction of the way across the row. Not the
    /// middle: the name is there, and far enough from the expand triangle at
    /// the left edge. And the two clicks a test makes land at DIFFERENT
    /// fractions, so they can never be read as one double-click whatever the
    /// headless clock says — a pair that formed would open the row by the
    /// other route and prove nothing about this one.
    /// </summary>
    private static void ClickOnce(Window window, ListBoxItem row, double across)
    {
        var at = row.TranslatePoint(new Point(row.Bounds.Width * across, row.Bounds.Height / 2), window);

        Assert.NotNull(at);

        window.MouseDown(at!.Value, MouseButton.Left);
        window.MouseUp(at.Value, MouseButton.Left);
        Settle();
    }

    /// <summary>Back out of the folder the test opened, and the row it was
    /// opened from on screen again.</summary>
    private async Task<ListBoxItem> BackAsync(PaneViewModel pane)
    {
        await pane.GoBackAsync();
        await Until(() => pane.CurrentPath == _root && Row(_window!, pane, "adir") is not null,
                    "Back did not return to the folder with its rows");

        return Row(_window!, pane, "adir")!;
    }

    /// <summary>
    /// The finding as reported: click, Enter, Back, one click. The single
    /// click is the one that must not open anything.
    /// </summary>
    [AvaloniaFact]
    public async Task One_click_does_not_reopen_a_row_opened_with_enter()
    {
        var pane = await BuildAsync();
        var window = _window!;
        var inner = Path.Combine(_root!, "adir");

        var first = Row(window, pane, "adir")!;

        ClickOnce(window, first, 0.6);

        Assert.Equal("adir", pane.SelectedEntry?.Name);
        Assert.Equal(_root, pane.CurrentPath);

        // **The keyboard on the LIST, not on the row.** Avalonia 12.1.2's
        // ListBoxItem answers Enter itself (it selects), so an Enter pressed
        // with a row focused never bubbles to OnWindowKeyDown at all —
        // measured, handled at the ListBoxItem's own bubble step. The list
        // holds the keyboard after the window moves it there, as it does when
        // Tab switches the sides of a split, and from there Enter opens.
        Listing(window, pane)!.Focus();
        Settle();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => pane.CurrentPath == inner, "Enter did not open the folder");

        var row = await BackAsync(pane);

        ClickOnce(window, row, 0.85);
        await Drain();

        Assert.Equal(_root, pane.CurrentPath);
    }

    /// <summary>
    /// And the menu: click, open the listing's menu, Open, Back, one click.
    /// The menu's Open row is its command; what forgets is the menu opening.
    ///
    /// **Both ways the menu opens**, because they do not meet: a right-click
    /// raises the menu's Opening, and the Menu key opens it through
    /// ContextMenu.Open, which raises no Opening at all.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_click_does_not_reopen_a_row_opened_from_the_menu(bool byKey)
    {
        var pane = await BuildAsync();
        var window = _window!;
        var inner = Path.Combine(_root!, "adir");

        var first = Row(window, pane, "adir")!;

        ClickOnce(window, first, 0.6);

        Assert.Equal("adir", pane.SelectedEntry?.Name);

        first.Focus();
        Settle();

        if (byKey)
        {
            window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
        }
        else
        {
            var at = first.TranslatePoint(new Point(first.Bounds.Width * 0.6, first.Bounds.Height / 2), window)!.Value;

            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
        }

        Settle();

        var menu = first.GetVisualAncestors().OfType<Control>()
            .Select(c => c.ContextMenu)
            .FirstOrDefault(m => m is not null);

        Assert.True(menu is { IsOpen: true }, "the listing's menu did not open");

        menu!.Close();
        Settle();

        pane.OpenSelectedCommand.Execute(null);
        await Until(() => pane.CurrentPath == inner, "the menu's Open did not open the folder");

        var row = await BackAsync(pane);

        ClickOnce(window, row, 0.85);
        await Drain();

        Assert.Equal(_root, pane.CurrentPath);
    }

    /// <summary>
    /// **Not vacuous: two clicks on the row still open it.** Both tests above
    /// assert that nothing happened, which a click that never reached OnTapped
    /// would satisfy as well. The same two presses, with nothing between them,
    /// must open the folder.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_clicks_still_open_the_row()
    {
        var pane = await BuildAsync();
        var window = _window!;
        var row = Row(window, pane, "adir")!;

        ClickOnce(window, row, 0.6);
        Assert.Equal(_root, pane.CurrentPath);

        ClickOnce(window, row, 0.85);
        await Until(() => pane.CurrentPath == Path.Combine(_root!, "adir"),
                    "two clicks on the row did not open it, so the tests above prove nothing");
    }
}
