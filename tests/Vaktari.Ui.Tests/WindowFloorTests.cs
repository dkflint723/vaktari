using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// How small the window may be dragged.
///
/// **There was no floor at all.** The chrome has fixed-width parts — a 210px
/// sidebar, and a path bar whose right-hand cluster is a 230px search field
/// beside a 200px filter — so a window dragged narrow ran them into the
/// breadcrumbs and then into each other, with nothing stopping the drag before
/// the window held nothing usable.
///
/// Two headings, and two sections that are not the same thing, are the other
/// half of this file: connected shares and servers being discovered were both
/// called NETWORK, one directly above the other.
///
/// The settings window is here too, for the same fault: it is the other window
/// people resize, and it had no floor either.
/// </summary>
public sealed class WindowFloorTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XElement Window()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml")).Root!;

    private static XElement SettingsMarkup()
        => XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml")).Root!;

    private static SettingsWindow ShownSettings()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new SettingsState()),
        };

        window.Show();
        window.UpdateLayout();

        return window;
    }

    [Fact]
    public void The_window_declares_a_floor()
    {
        var window = Window();

        Assert.Equal(560d, (double?)window.Attribute("MinWidth"));
        Assert.Equal(380d, (double?)window.Attribute("MinHeight"));
    }

    /// <summary>
    /// **Wide enough for the sidebar AND a listing**, which is the pair the
    /// finding is about: a floor that fits only the sidebar is a floor that
    /// still allows a window with nothing in it.
    /// </summary>
    [AvaloniaFact]
    public void The_floor_leaves_room_beside_the_sidebar()
    {
        var window = Window();
        var floor = (double)window.Attribute("MinWidth")!;

        var sidebar = new SidebarViewModel(places: null);

        Assert.True(floor - sidebar.Width >= 300,
                    $"a {floor}px window leaves only {floor - sidebar.Width}px "
                    + "of listing beside a sidebar that is open by default");
    }

    /// <summary>
    /// And it is really applied.
    ///
    /// **This used to Show a window and THEN assign it a tiny Width, and a shown
    /// Avalonia window ignores that assignment outright.** The bounds stayed at
    /// the 1000x680 it opened with, so "at least 560x380" passed without the
    /// floor ever being consulted — setting MinWidth to zero left it passing.
    /// The size has to be set BEFORE Show, which is also the order the real
    /// thing uses: ApplyGeometry runs in the constructor.
    ///
    /// The real MainWindow, rather than a bare Window with the same two numbers
    /// typed into it, because the floor under test is the one in the markup:
    /// deleting that attribute has to fail this.
    /// </summary>
    [AvaloniaFact]
    public void A_main_window_told_to_be_tiny_stops_at_the_floor()
    {
        // The constructor assigns the platform's real search backend to
        // PaneViewModel's static; this borrows it so Dispose gives it back.
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        var wasWidth = window.Width;
        var wasHeight = window.Height;

        try
        {
            window.Width = 120;
            window.Height = 100;

            window.Show();
            window.UpdateLayout();

            // 120 really is what this measures without a floor: deleting
            // MinWidth from the markup makes both of these fail.
            Assert.True(window.Bounds.Width >= 560, $"width fell to {window.Bounds.Width}");
            Assert.True(window.Bounds.Height >= 380, $"height fell to {window.Bounds.Height}");
        }
        finally
        {
            // Closing flushes the REAL session, and CaptureGeometry writes the
            // Width property — so the size this test asked for is put back
            // before the window is allowed to save it.
            window.Width = wasWidth;
            window.Height = wasHeight;
            window.Close();
        }
    }

    /// <summary>
    /// A saved size below the floor comes back AT the floor, rather than as
    /// itself with the layout quietly overruling it at the first measure.
    /// </summary>
    [AvaloniaFact]
    public void A_saved_size_below_the_floor_is_raised_to_it()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        var wasWidth = window.Width;
        var wasHeight = window.Height;

        try
        {
            // Not shown, deliberately: showing it would raise the size itself,
            // and this is about what the restore leaves behind before that.
            window.ApplyGeometry(new SessionState
            {
                Windows = [new WindowSession { Width = 300, Height = 200 }],
            });

            Assert.Equal(560d, window.Width);
            Assert.Equal(380d, window.Height);
        }
        finally
        {
            window.Width = wasWidth;
            window.Height = wasHeight;
            window.Close();
        }
    }

    /// <summary>
    /// **And a session that never stored a size must not be dragged down to the
    /// floor.** Zero is what an absent key deserializes to, so zero means
    /// nothing was saved, and the answer to that is the size the markup opens
    /// at. Clamping it would shrink every window upgrading from a file written
    /// before the field existed, on first launch, for everybody.
    /// </summary>
    [AvaloniaFact]
    public void A_session_with_no_saved_size_leaves_the_window_alone()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        var wasWidth = window.Width;
        var wasHeight = window.Height;

        try
        {
            // A known size first, so this does not depend on what the machine
            // running the suite happens to have in its own session file.
            window.ApplyGeometry(new SessionState
            {
                Windows = [new WindowSession { Width = 900, Height = 700 }],
            });

            Assert.Equal(900d, window.Width);
            Assert.Equal(700d, window.Height);

            window.ApplyGeometry(new SessionState
            {
                Windows = [new WindowSession { Width = 0, Height = 0 }],
            });

            Assert.Equal(900d, window.Width);
            Assert.Equal(700d, window.Height);
        }
        finally
        {
            window.Width = wasWidth;
            window.Height = wasHeight;
            window.Close();
        }
    }

    // ---- and the settings window -------------------------------------------

    [Fact]
    public void The_settings_window_declares_a_floor()
    {
        var window = SettingsMarkup();

        Assert.Equal(520d, (double?)window.Attribute("MinWidth"));
        Assert.Equal(420d, (double?)window.Attribute("MinHeight"));
    }

    /// <summary>
    /// **Wide enough for the page list AND a page.** The list is docked left and
    /// never shrinks, so a floor that fits only the list is a floor that allows
    /// a settings window with no settings in it.
    /// </summary>
    [AvaloniaFact]
    public void The_settings_floor_leaves_room_beside_its_page_list()
    {
        var floor = (double)SettingsMarkup().Attribute("MinWidth")!;

        var window = ShownSettings();

        try
        {
            var pages = window.GetVisualDescendants().OfType<TabItem>().ToList();

            Assert.NotEmpty(pages);

            var strip = pages.Max(p => p.Bounds.Width);

            Assert.True(floor - strip >= 300,
                        $"a {floor}px window leaves only {floor - strip}px of page "
                        + $"beside a {strip}px list of pages");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// **Tall enough that every page can still be clicked.** The strip has no
    /// scroller and the footer is docked Bottom, so the footer is paid first
    /// and the whole of a short window's shortfall comes out of the pages —
    /// whereupon the strip's WrapPanel answers by wrapping into a second column
    /// taken from the page's own width, and far enough down, off the right edge
    /// of the window entirely. A floor that clears the whole list plus the
    /// footer is a floor that keeps the list one column wide. Measured rather
    /// than asserted as a constant, so adding a seventh page fails this instead
    /// of silently costing the floor.
    /// </summary>
    [AvaloniaFact]
    public void The_settings_floor_keeps_every_page_reachable()
    {
        var floor = (double)SettingsMarkup().Attribute("MinHeight")!;

        var window = ShownSettings();

        try
        {
            var pages = window.GetVisualDescendants().OfType<TabItem>().ToList();

            Assert.NotEmpty(pages);

            var strip = pages.Sum(p => p.Bounds.Height);

            // Whatever the TabControl does not occupy is the footer: the shell
            // is a DockPanel with the footer docked Bottom and the pages filling
            // the rest.
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            var footer = window.Bounds.Height - tabs.Bounds.Height;

            Assert.True(floor - (strip + footer) >= 60,
                        $"a {floor}px window leaves {floor - (strip + footer)}px "
                        + $"under {strip}px of pages and a {footer}px footer");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// And the settings floor is really applied. Set before Show, for the reason
    /// spelled out on the main window's version of this.
    /// </summary>
    [AvaloniaFact]
    public void A_settings_window_told_to_be_tiny_stops_at_the_floor()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new SettingsState()),
            Width = 120,
            Height = 100,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.True(window.Bounds.Width >= 520, $"width fell to {window.Bounds.Width}");
            Assert.True(window.Bounds.Height >= 420, $"height fell to {window.Bounds.Height}");
        }
        finally { window.Close(); }
    }

    // ---- and the two headings ----------------------------------------------

    /// <summary>
    /// **Two consecutive sections both read NETWORK** once a mapped drive was
    /// present, and they hold different things: the group from the places
    /// provider is the shares you have already connected and can open, while
    /// the literal section below it is servers announcing themselves that you
    /// have not connected to. Two identical headings in a row read as one
    /// section drawn twice.
    /// </summary>
    [Fact]
    public void The_sidebar_has_one_section_called_network()
    {
        var literal = Window()
            .Descendants(Avalonia + "TextBlock")
            .Count(t => (string?)t.Attribute("Text") == "NETWORK");

        Assert.Equal(1, literal);
    }

    /// <summary>
    /// And the group the providers supply is not called that either — its label
    /// is upper-cased into the heading above it, so a group labelled "network"
    /// draws the second one. Both providers read the name from one place, so
    /// this is asked once; the platforms cannot drift apart on it.
    /// </summary>
    [Fact]
    public void The_connected_shares_are_not_called_network_either()
    {
        Assert.NotEqual("network", Vaktari.Core.Places.PlaceGroups.Shares);

        // And it still says what it holds, rather than merely differing.
        Assert.Equal("shares", Vaktari.Core.Places.PlaceGroups.Shares);
    }
}
