using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// F6 in the real window.
///
/// The arithmetic is FocusCycleTests' job; this is the half that says the
/// window agrees with it — which region it thinks the keyboard is in, and where
/// it actually puts it.
///
/// **A real window, because the interesting failures are not reachable from a
/// view model.** Whether F6 can leave a text box at all depends on where its
/// arm sits relative to the guard that gives a focused box the keyboard, and
/// that is a fact about one method's statement order.
/// </summary>
public sealed class F6RegionTests : OwnedViewModels
{
    /// <summary>
    /// Down to Background, because that is where the window queues "put the
    /// keyboard back in the listing" — the job the sidebar step has to land
    /// behind.
    /// </summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static ListBox? Listing(Window window, ShellViewModel shell)
        => window.GetVisualDescendants().OfType<ListBox>()
                 .FirstOrDefault(l => l.IsVisible
                                      && ReferenceEquals(l.DataContext, shell.ActiveTab)
                                      && l.SelectionMode.HasFlag(SelectionMode.Multiple));

    /// <summary>
    /// The listing has to really have the keyboard before a test about leaving
    /// it means anything — without this precondition the test passes whether or
    /// not F6 works, because focus at startup is nowhere and "nowhere" also
    /// leads to the listing.
    /// </summary>
    private static ListBox Focused(Window window, ShellViewModel shell)
    {
        var listing = Listing(window, shell);

        Assert.NotNull(listing);

        listing!.Focus();
        Settle();

        Assert.True(listing.IsFocused,
                    "the listing has to have the keyboard for this to mean anything");

        return listing;
    }

    [AvaloniaFact]
    public void From_the_listing_it_opens_the_address_bar()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            Focused(window, shell);

            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Settle();

            Assert.True(shell.ActiveTab!.IsPathEditing);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The step the guard would have eaten.** The address bar is a text box,
    /// and a focused text box owns the keyboard everywhere else in this
    /// handler — so behind that guard the second press of the cycle could never
    /// be taken, and F6 would be a key that works exactly once.
    /// </summary>
    [AvaloniaFact]
    public void And_from_the_address_bar_it_can_leave_again()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            Focused(window, shell);

            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Settle();

            Assert.True(shell.ActiveTab!.IsPathEditing, "the first press did not get there");

            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Settle();

            Assert.False(shell.ActiveTab.IsPathEditing,
                         "F6 was swallowed by the box it had just opened");

            // **And it went ON rather than back.** The address bar has to be
            // recognised as a region of its own: read as "somewhere else", the
            // cycle would rescue from it to the listing and the sidebar would
            // never be reachable by keyboard at all.
            Assert.True(
                window.FindControl<Border>("SidebarPanel") is { } panel
                && window.FocusManager?.GetFocusedElement() is Visual landed
                && panel.GetVisualDescendants().Contains(landed),
                "the second press went back to the listing instead of on to the sidebar");

            // **And it landed on a place, not on a heading.** Every section
            // heading is a ToggleButton that folds the section away, and a
            // ToggleButton IS a Button — so "the first visible button in the
            // panel" became PLACES rather than Home, and F6 handed the keyboard
            // to a control whose Space bar hides the list you were reaching for.
            Assert.IsNotType<ToggleButton>(window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **F6's original job, which the cycle must not cost.** The handler this
    /// replaces existed because focus could be left nowhere and the arrow keys
    /// were then dead until the mouse was used. Pressed from a toolbar button —
    /// which is neither the listing, the address bar nor the sidebar — one
    /// press still has to put the keyboard on the rows.
    /// </summary>
    [AvaloniaFact]
    public void From_a_toolbar_button_one_press_still_reaches_the_rows()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var listing = Listing(window, shell);

            Assert.NotNull(listing);

            // Chrome, and not a sidebar place row: a row there IS a region, so
            // a press from one is an ordinary step of the cycle rather than
            // the rescue this test is about.
            var sidebar = window.FindControl<Border>("SidebarPanel");

            var button = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.IsVisible && b.IsEffectivelyEnabled
                                     && b.FindAncestorOfType<ListBox>() is null
                                     && (sidebar is null
                                         || !sidebar.GetVisualDescendants().Contains(b)));

            Assert.NotNull(button);

            button!.Focus();
            Settle();

            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Settle();

            Assert.False(shell.ActiveTab!.IsPathEditing,
                         "F6 from a toolbar button opened the address bar — the rescue it "
                         + "exists for now takes three presses");

            Assert.True(listing!.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The rename editor is a text box too, and its tenancy is what the guard
    /// above this really protects — F6 must not pull the keyboard out from
    /// under a name being typed.
    ///
    /// A REAL row in a real folder, because the box is drawn by the listing's
    /// item template now: a rename staged onto an entry the listing does not
    /// hold opens nothing for the keyboard to be pulled out of, and the window
    /// deliberately lets a key through when a rename has lost its box.
    /// </summary>
    [AvaloniaFact]
    public async Task It_leaves_a_rename_in_progress_alone()
    {
        UseSearch(PaneViewModel.Search);

        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vaktari-f6rename-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        File.WriteAllText(System.IO.Path.Combine(root, "first.txt"), "x");

        var window = new MainWindow();

        try
        {
            window.Show();
            Settle();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            Settle();
            window.UpdateLayout();
            Settle();

            Focused(window, shell);

            // Staged through the pane, the way the tenancy tests do: pressing
            // F2 with nothing selected opens nothing, and a test of "F6 during
            // a rename" that never starts one measures the ordinary path.
            pane.SelectedEntry = pane.Entries.Single(e => e.Name == "first.txt");

            pane.BeginRenameCommand.Execute(null);
            Settle();
            window.UpdateLayout();
            Settle();

            var box = window.GetVisualDescendants().OfType<TextBox>()
                            .Single(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);

            Assert.True(box.IsFocused,
                        "the name is not being typed anywhere, so this proves nothing");

            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Settle();

            Assert.False(pane.IsPathEditing,
                         "F6 pulled the keyboard out from under a name being typed");

            Assert.Equal("first.txt", pane.RenameText);
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }
}
