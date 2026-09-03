using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The sidebar's own keys, in the real window.
///
/// The arithmetic is SidebarWalkTests' job; this is the half that says the
/// keystroke reaches it at all — which region the window thinks it is in, what
/// it stops on, and what it now refuses to pass on to the listing.
/// </summary>
public sealed class SidebarKeysTests : OwnedViewModels
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>Every focusable Button in the panel, the way the window walks them.</summary>
    private static List<Button> Stops(Window window)
        => window.FindControl<Border>("SidebarPanel") is { } panel
            ? panel.GetVisualDescendants().OfType<Button>()
                   .Where(b => b is not RepeatButton)
                   .Where(b => b.Focusable && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)
                   .ToList()
            : [];

    /// <summary>
    /// Puts the keyboard in the sidebar the way a person does, and refuses to
    /// go on if it did not land there — every assertion below is about what
    /// happens NEXT, and would pass vacuously from anywhere else.
    /// </summary>
    private static List<Button> InTheSidebar(Window window)
    {
        var stops = Stops(window);

        Assert.True(stops.Count >= 2, "the sidebar needs a few stops for this to mean anything");

        stops[0].Focus(NavigationMethod.Directional);
        Settle();

        Assert.Same(stops[0], window.FocusManager?.GetFocusedElement());

        return stops;
    }

    private MainWindow Shown()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        window.Show();
        Settle();

        return window;
    }

    /// <summary>The whole finding: the arrows move.</summary>
    [AvaloniaFact]
    public void Down_and_up_walk_the_sidebar()
    {
        var window = Shown();

        try
        {
            var stops = InTheSidebar(window);

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Settle();

            Assert.Same(stops[1], window.FocusManager?.GetFocusedElement());

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Settle();

            Assert.Same(stops[0], window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>And Home and End reach the ends.</summary>
    [AvaloniaFact]
    public void Home_and_End_reach_the_ends()
    {
        var window = Shown();

        try
        {
            var stops = InTheSidebar(window);

            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Settle();

            Assert.Same(stops[^1], window.FocusManager?.GetFocusedElement());

            window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
            Settle();

            Assert.Same(stops[0], window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **End must not hand the keyboard to a scroll arrow.** The panel's
    /// content sits in a ScrollViewer, and a scrollbar's PART_LineUpButton is a
    /// RepeatButton, which derives from Button — and the scrollbar sits after
    /// the rows in the visual tree, so an unfiltered walk would make an arrow
    /// the LAST stop.
    ///
    /// The arrows are staged focusable first, deliberately. Fluent marks them
    /// unfocusable, so with this theme the focusable filter already keeps them
    /// out, and a test that did not stage the state would agree with the code
    /// for a reason the code does not state — and go on agreeing after the rule
    /// was deleted. Whether a theme's scroll arrows take focus is a template
    /// detail; this walk must not depend on one.
    ///
    /// Driven through the real keystroke rather than a copy of the filter: a
    /// test that reimplements the rule it is checking cannot see the rule
    /// change.
    /// </summary>
    [AvaloniaFact]
    public void End_does_not_land_on_a_scroll_arrow_even_when_one_can_be_focused()
    {
        var window = Shown();

        try
        {
            var panel = window.FindControl<Border>("SidebarPanel")!;

            var arrows = panel.GetVisualDescendants().OfType<RepeatButton>().ToList();

            Assert.NotEmpty(arrows);

            foreach (var arrow in arrows) arrow.Focusable = true;

            Settle();

            Assert.Contains(arrows,
                            a => a.Focusable && a.IsEffectivelyVisible && a.IsEffectivelyEnabled);

            InTheSidebar(window);

            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Settle();

            var landed = window.FocusManager?.GetFocusedElement();

            Assert.IsNotType<RepeatButton>(landed);
            Assert.Null((landed as Visual)?.FindAncestorOfType<ScrollBar>());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **Delete no longer reaches the listing from here.** With the keyboard on
    /// a place row it trashed whatever was selected in the folder behind the
    /// sidebar — files not on screen, chosen by a click that may have been
    /// minutes ago, and named in a confirmation that reads as being about the
    /// row you were actually looking at.
    ///
    /// The same keystroke is pressed from the listing first. Without that half
    /// the test passes for any reason the prompt fails to open — including the
    /// one that was really happening: the window applies the settings it loads
    /// from disk over whatever a test has set, so the confirmation was off, and
    /// Delete quietly trashed the file instead of asking.
    /// </summary>
    [AvaloniaFact]
    public async Task Delete_in_the_sidebar_does_not_open_the_listings_confirmation()
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        var folder = Path.Combine(
            Path.GetTempPath(), "vaktari-sidebardel-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.txt"), "x");

        var window = Shown();

        try
        {
            // AFTER the window: its constructor applies the settings it loaded
            // from disk over whatever was set before it.
            Vaktari.Ui.Settings.AppSettings.Apply(Vaktari.Ui.Settings.AppSettings.Current with
            {
                General = Vaktari.Ui.Settings.AppSettings.Current.General with
                {
                    ConfirmMoveToTrash = true,
                },
            });

            var bar = window.FindControl<Border>("PromptBar");
            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            Assert.NotNull(bar);

            await pane.NavigateAsync(folder);
            Settle();

            Assert.Single(pane.Entries);

            pane.SelectedEntry = pane.Entries[0];

            Assert.True(pane.HasSelection, "nothing is selected, so nothing could be trashed");

            // The control half: from the listing the key really does ask.
            window.GetVisualDescendants().OfType<ListBox>()
                  .FirstOrDefault(l => l.IsVisible && ReferenceEquals(l.DataContext, pane))
                  ?.Focus();
            Settle();

            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Settle();

            Assert.True(bar!.IsVisible,
                        "the harness never reaches the confirmation, so the other half proves nothing");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle();

            Assert.False(bar.IsVisible, "the prompt did not close");

            // And from the sidebar it does not.
            InTheSidebar(window);

            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Settle();

            Assert.False(bar.IsVisible,
                         "Delete in the sidebar asked to trash the listing's selection");
        }
        finally
        {
            window.Close();

            Vaktari.Ui.Settings.AppSettings.Apply(before);

            try { Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* a temp dir this test made is not worth failing over */ }
        }
    }

    /// <summary>
    /// The Menu key opens the menu for the ROW. Avalonia raises
    /// ContextRequested for a right-click and for nothing else, so this is the
    /// only keyboard route into a place's own menu.
    /// </summary>
    [AvaloniaFact]
    public void The_menu_key_asks_the_focused_row_for_its_menu()
    {
        var window = Shown();

        try
        {
            var stops = InTheSidebar(window);

            var asked = false;

            // handledEventsToo, because a Button with a ContextMenu already has
            // Avalonia's own handler on it — which opens the menu and marks the
            // event handled before an ordinary handler would see it.
            stops[0].AddHandler(Control.ContextRequestedEvent, (_, _) => asked = true,
                                RoutingStrategies.Bubble, handledEventsToo: true);

            window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            Settle();

            Assert.True(asked, "the Menu key did not reach the focused row");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **After F6, not before it.** The sidebar claims Up, Down, Home and End
    /// while it has the keyboard; claimed ahead of F6 it would claim nothing
    /// F6 uses, so this cannot be caught by pressing keys — it is a fact about
    /// the order of two blocks in one method, and the cost of getting it wrong
    /// is a panel you can be delivered to and not leave.
    /// </summary>
    [Fact]
    public void The_sidebars_keys_are_claimed_after_F6_has_had_the_keystroke()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnWindowKeyDown(object? sender, KeyEventArgs e)");

        var f6 = body.IndexOf("e.Key == Key.F6", StringComparison.Ordinal);
        var sidebar = body.IndexOf("CurrentRegion() == Input.KeyboardRegion.Sidebar",
                                   StringComparison.Ordinal);

        Assert.True(f6 >= 0, "F6 is no longer claimed the way this test looks for it");
        Assert.True(sidebar > f6,
                    "the sidebar's keys are claimed before F6, so the panel F6 delivers "
                    + "you to is one F6 cannot take you out of");
    }
}
