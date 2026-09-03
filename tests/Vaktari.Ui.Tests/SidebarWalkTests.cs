using Avalonia.Input;
using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The arithmetic of walking the sidebar.
///
/// **F6 delivered you to a panel with no way to move in it.** Up, Down, Home
/// and End were unbound anywhere in the application, so from a place row the
/// only way on was Tab through every button in the panel — and the keys that
/// WERE bound went on acting on the listing.
/// </summary>
public sealed class SidebarWalkTests
{
    [Theory]
    [InlineData(0, SidebarStep.Next)]
    [InlineData(2, SidebarStep.Previous)]
    [InlineData(3, SidebarStep.First)]
    [InlineData(4, SidebarStep.Last)]
    public void An_empty_sidebar_has_nowhere_to_go(int from, SidebarStep step)
        => Assert.Equal(-1, SidebarWalk.Landing(0, from, step));

    [Fact]
    public void Down_goes_down_and_up_goes_up()
    {
        Assert.Equal(3, SidebarWalk.Landing(5, 2, SidebarStep.Next));
        Assert.Equal(1, SidebarWalk.Landing(5, 2, SidebarStep.Previous));
    }

    /// <summary>
    /// **Neither end wraps.** A list that jumps back to the top when you hold
    /// an arrow down is a list you cannot hold an arrow down in, and both
    /// references stop at the end.
    /// </summary>
    [Fact]
    public void Neither_end_wraps()
    {
        Assert.Equal(4, SidebarWalk.Landing(5, 4, SidebarStep.Next));
        Assert.Equal(0, SidebarWalk.Landing(5, 0, SidebarStep.Previous));
    }

    /// <summary>
    /// From inside the panel but not on a stop — a scroll arrow, which the stop
    /// list refuses — Down enters at the top and Up enters at the bottom.
    /// </summary>
    [Fact]
    public void From_nowhere_in_particular_it_enters_from_the_nearest_end()
    {
        Assert.Equal(0, SidebarWalk.Landing(5, -1, SidebarStep.Next));
        Assert.Equal(4, SidebarWalk.Landing(5, -1, SidebarStep.Previous));
    }

    [Fact]
    public void Home_and_End_go_to_the_ends_from_anywhere()
    {
        Assert.Equal(0, SidebarWalk.Landing(5, 3, SidebarStep.First));
        Assert.Equal(4, SidebarWalk.Landing(5, 3, SidebarStep.Last));

        Assert.Equal(0, SidebarWalk.Landing(5, -1, SidebarStep.First));
        Assert.Equal(4, SidebarWalk.Landing(5, -1, SidebarStep.Last));
    }

    /// <summary>One stop is both ends of the walk, and no step moves off it.</summary>
    [Fact]
    public void A_single_stop_is_every_answer()
    {
        foreach (var step in Enum.GetValues<SidebarStep>())
            Assert.Equal(0, SidebarWalk.Landing(1, 0, step));
    }

    /// <summary>
    /// A step that is not one of the four is an argument fault rather than a
    /// silent landing — the arm exists so a cast integer cannot quietly become
    /// "stay where you are".
    /// </summary>
    [Fact]
    public void A_step_that_is_not_a_step_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SidebarWalk.Landing(5, 2, (SidebarStep)99));

    // ---- what the sidebar refuses -----------------------------------------

    /// <summary>
    /// **The one that costs files.** With the keyboard on a place row, Delete
    /// trashed whatever happened to be selected in the folder behind it.
    /// </summary>
    [Theory]
    [InlineData(Key.Delete, KeyModifiers.None)]
    [InlineData(Key.Delete, KeyModifiers.Shift)]
    [InlineData(Key.F2, KeyModifiers.None)]
    [InlineData(Key.F2, KeyModifiers.Shift)]
    [InlineData(Key.C, KeyModifiers.Control)]
    [InlineData(Key.X, KeyModifiers.Control)]
    [InlineData(Key.A, KeyModifiers.Control)]
    [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.Enter, KeyModifiers.Alt)]
    public void These_act_on_a_selection_the_keyboard_is_not_pointing_at(
        Key key, KeyModifiers modifiers)
        => Assert.True(SidebarWalk.ActsOnTheListing(key, modifiers));

    /// <summary>
    /// And these are not refused. Undo is application-wide — it undoes the last
    /// file operation wherever you are standing — and paste, back and a bare
    /// Enter are not about the listing's selection.
    /// </summary>
    [Theory]
    [InlineData(Key.Z, KeyModifiers.Control)]
    [InlineData(Key.Y, KeyModifiers.Control)]
    [InlineData(Key.V, KeyModifiers.Control)]
    [InlineData(Key.Back, KeyModifiers.None)]
    [InlineData(Key.Enter, KeyModifiers.None)]
    [InlineData(Key.Space, KeyModifiers.None)]
    [InlineData(Key.C, KeyModifiers.None)]
    [InlineData(Key.F5, KeyModifiers.None)]
    public void And_these_are_left_alone(Key key, KeyModifiers modifiers)
        => Assert.False(SidebarWalk.ActsOnTheListing(key, modifiers));
}
