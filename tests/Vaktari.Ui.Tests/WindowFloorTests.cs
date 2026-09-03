using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// </summary>
public sealed class WindowFloorTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XElement Window()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml")).Root!;

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
    /// And it is really applied — a declared minimum that some later Width
    /// assignment overrides is not a minimum. The session restore assigns one
    /// on every launch.
    /// </summary>
    [AvaloniaFact]
    public void A_window_told_to_be_tiny_stops_at_the_floor()
    {
        var window = new Window { Width = 1000, Height = 680, MinWidth = 560, MinHeight = 380 };

        window.Show();

        window.Width = 120;
        window.Height = 100;

        window.UpdateLayout();

        Assert.True(window.Bounds.Width >= 560, $"width fell to {window.Bounds.Width}");
        Assert.True(window.Bounds.Height >= 380, $"height fell to {window.Bounds.Height}");

        window.Close();
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
