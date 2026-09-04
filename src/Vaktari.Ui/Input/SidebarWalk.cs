using Avalonia.Input;

namespace Vaktari.Ui.Input;

/// <summary>Which way a keystroke moves through the sidebar.</summary>
public enum SidebarStep
{
    Next,
    Previous,
    First,
    Last,
}

/// <summary>
/// Walking the sidebar with the keyboard.
///
/// **F6 delivered you to a panel with no way to move in it.** The keyboard
/// reached the sidebar and then the arrow keys did nothing — Up, Down, Home and
/// End were unbound anywhere in the application — so the only thing to do from
/// a place row was Tab through every button in the panel or press F6 again.
///
/// Worse than doing nothing: the keys that WERE bound went on acting on the
/// listing. Delete trashed the files selected in a folder that no longer had
/// the keyboard, and nothing on screen said which of the two the keystroke had
/// gone to.
///
/// The arithmetic lives here rather than in the window because it is the part
/// worth testing on its own: which row a keystroke lands on is a pure function
/// of how many stops there are and where you were.
/// </summary>
public static class SidebarWalk
{
    /// <summary>
    /// Which stop a step lands on, or -1 when there is nothing to land on.
    ///
    /// <paramref name="from"/> is -1 when the keyboard is inside the panel but
    /// not on a stop — a scroll arrow, which is a Button the stop list refuses.
    /// From there Down enters at the top and Up enters at the bottom, which is
    /// what "come into the list" means from outside it.
    ///
    /// **It does not wrap.** Down on the last row stays on the last row, the
    /// way a tree view behaves in both references: a list that jumps back to
    /// the top when you hold an arrow down is a list you cannot hold an arrow
    /// down in.
    /// </summary>
    public static int Landing(int count, int from, SidebarStep step)
    {
        if (count <= 0) return -1;

        return step switch
        {
            SidebarStep.Next => from < 0 ? 0 : Math.Min(from + 1, count - 1),
            SidebarStep.Previous => from < 0 ? count - 1 : Math.Max(from - 1, 0),
            SidebarStep.First => 0,
            SidebarStep.Last => count - 1,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
        };
    }

    /// <summary>
    /// Keys the window answers by acting on the LISTING'S SELECTION, which is
    /// not what the keyboard is pointing at while it is in the sidebar.
    ///
    /// **Delete is the one that matters.** With the keyboard on a place row it
    /// trashed whatever happened to be selected in the folder behind it — files
    /// that were not on screen, chosen by a click that may have been minutes
    /// ago, with the confirmation naming them and the person reading it as being
    /// about the row they were actually looking at.
    ///
    /// The line is the SELECTION. Navigation keeps working from here (Alt+← and
    /// the rest are markup KeyBindings, dispatched ahead of the window's handler
    /// — they could not be refused here even if that were wanted), and so does
    /// anything application-wide: Ctrl+Z undoes the last file operation wherever
    /// you are standing, which is what undo means.
    ///
    /// Enter and Space are not here. A focused Button takes both itself, and
    /// every stop is a Button — so a clause for them would be one that cannot
    /// fire, and one the next reader has to work out is decorative.
    /// </summary>
    public static bool ActsOnTheListing(Key key, KeyModifiers modifiers)
        => key switch
        {
            // Trash, and delete for good.
            Key.Delete => true,

            // Rename, and rename in bulk.
            Key.F2 => modifiers is KeyModifiers.None or KeyModifiers.Shift,

            // Copy and cut the selection.
            Key.C or Key.X => modifiers == KeyModifiers.Control,

            // Select all, and invert the selection.
            Key.A => modifiers == KeyModifiers.Control
                     || modifiers == (KeyModifiers.Control | KeyModifiers.Shift),

            // The properties of the selection.
            Key.Enter => modifiers.HasFlag(KeyModifiers.Alt),

            // Open a folder in the listing where it stands, and shut it again.
            // The listing the keyboard is not pointing at, exactly like the
            // five above it: measured in the real window, Right on a place row
            // opened a folder in the listing behind it, with nothing on screen
            // saying which of the two the keystroke had gone to.
            Key.Left or Key.Right => modifiers == KeyModifiers.None,

            _ => false,
        };
}
