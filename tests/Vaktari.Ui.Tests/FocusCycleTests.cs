using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where F6 goes next.
///
/// **It only ever went one place.** Explorer cycles three regions and Dolphin's
/// F6 is Replace Location; here it put the keyboard in the listing and did
/// nothing else — so pressed from the listing, which is where it had just put
/// you, it did nothing at all.
/// </summary>
public sealed class FocusCycleTests
{
    [Fact]
    public void From_the_listing_it_goes_to_the_address_bar()
        => Assert.Equal(KeyboardRegion.Location,
                        FocusCycle.Next(KeyboardRegion.Listing, sidebarShowing: true));

    [Fact]
    public void From_the_address_bar_it_goes_to_the_sidebar()
        => Assert.Equal(KeyboardRegion.Sidebar,
                        FocusCycle.Next(KeyboardRegion.Location, sidebarShowing: true));

    [Fact]
    public void And_from_the_sidebar_back_to_the_listing()
        => Assert.Equal(KeyboardRegion.Listing,
                        FocusCycle.Next(KeyboardRegion.Sidebar, sidebarShowing: true));

    /// <summary>
    /// A region nobody can see is not a place to put the keyboard, and Ctrl+B
    /// or F9 taking the panel away must not leave a press of F6 apparently
    /// doing nothing.
    /// </summary>
    [Fact]
    public void With_the_sidebar_hidden_the_cycle_is_two_places()
        => Assert.Equal(KeyboardRegion.Listing,
                        FocusCycle.Next(KeyboardRegion.Location, sidebarShowing: false));

    /// <summary>
    /// **The rescue F6 was written for, which cycling would have undone.** The
    /// handler this replaces existed because focus could be left nowhere — at
    /// startup, on a toolbar button, or after an editor collapsed — and the
    /// arrow keys were then dead until the mouse was used.
    ///
    /// Treating that state as "the listing" and moving on from it answers a
    /// plea for the rows with a text field, and takes three presses to do what
    /// one press used to do. It is not a rare state either: every chrome
    /// control that is not the sidebar or a listing lands here.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void From_nowhere_it_still_puts_the_keyboard_in_the_listing(bool sidebarShowing)
        => Assert.Equal(KeyboardRegion.Listing,
                        FocusCycle.Next(KeyboardRegion.Elsewhere, sidebarShowing));
}
