namespace Vaktari.Ui.Input;

/// <summary>
/// The parts of the window F6 moves the keyboard between.
///
/// <see cref="Elsewhere"/> is not a destination — it is "the keyboard is on
/// something that is none of the three", which is a real and common state: a
/// toolbar button, the tab strip, a crumb, or nothing at all at startup.
/// </summary>
public enum KeyboardRegion { Listing, Location, Sidebar, Elsewhere }

/// <summary>
/// Where F6 goes next.
///
/// **F6 only ever went one place.** Explorer cycles three regions and Dolphin's
/// F6 is Replace Location; here it put the keyboard in the listing and did
/// nothing else, so from the listing — which is where it had just put you — it
/// did nothing at all.
///
/// Pure arithmetic, split from the window the way <see cref="SideButtons"/> is,
/// because the interesting case is one a headless test cannot easily stage: the
/// keyboard being on none of the three.
/// </summary>
public static class FocusCycle
{
    /// <summary>
    /// The region F6 should move to from <paramref name="from"/>.
    ///
    /// <paramref name="sidebarShowing"/> is whether the sidebar panel is on
    /// screen at all — hidden, it is skipped rather than focused invisibly.
    /// </summary>
    public static KeyboardRegion Next(KeyboardRegion from, bool sidebarShowing) => from switch
    {
        // **F6's first job is still the rescue, and cycling onward would undo
        // it.** The handler this replaces existed because focus could be left
        // nowhere — at startup, on a toolbar button, or after an editor
        // collapsed — and the arrow keys were then dead until the mouse was
        // used. Treating that state as "the listing" and moving on from it
        // answers a plea for the rows with a text field, and takes three
        // presses to do what one press used to do.
        KeyboardRegion.Elsewhere => KeyboardRegion.Listing,

        KeyboardRegion.Listing => KeyboardRegion.Location,

        // Past the sidebar when it is not on screen: a region nobody can see is
        // not a place to put the keyboard, and Ctrl+B or F9 taking the panel
        // away must not leave a press of F6 apparently doing nothing.
        KeyboardRegion.Location =>
            sidebarShowing ? KeyboardRegion.Sidebar : KeyboardRegion.Listing,

        _ => KeyboardRegion.Listing,
    };
}
