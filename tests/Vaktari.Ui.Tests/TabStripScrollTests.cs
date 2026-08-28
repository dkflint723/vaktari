using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The arithmetic behind the tab strip's overflow chevrons.
///
/// The chevrons exist because the wheel and a three-pixel line were the only
/// ways to reach an overflowed tab, and neither looks clickable. What a test
/// can hold is the part a chevron gets wrong quietly: stepping past either end,
/// and appearing when there is nowhere to go.
/// </summary>
public class TabStripScrollTests
{
    /// <summary>The step lands inside the strip, never past its far end.</summary>
    [Fact]
    public void A_step_right_clamps_at_the_far_end()
    {
        // 1000 of content in a 400 window: the last legal offset is 600.
        var landed = TabStripScroll.Toward(500, viewport: 400, extent: 1000, direction: +1);

        Assert.Equal(600, landed);
    }

    [Fact]
    public void A_step_left_clamps_at_zero()
    {
        Assert.Equal(0, TabStripScroll.Toward(50, viewport: 400, extent: 1000, direction: -1));
    }

    /// <summary>Half a viewport per press: one click visibly moves without
    /// teleporting past the tab being looked for.</summary>
    [Fact]
    public void An_ordinary_step_moves_half_the_viewport()
    {
        Assert.Equal(300, TabStripScroll.Toward(100, viewport: 400, extent: 2000, direction: +1));
    }

    /// <summary>A very narrow pane still moves visibly rather than nudging.</summary>
    [Fact]
    public void A_narrow_pane_still_takes_a_worthwhile_step()
    {
        Assert.Equal(60, TabStripScroll.Toward(0, viewport: 80, extent: 2000, direction: +1));
    }

    /// <summary>
    /// **The gate on the chevrons existing at all.** Content that fits must
    /// produce none — their presence is itself the signal that tabs are
    /// hidden, so a chevron over a strip that fits is a false statement.
    /// </summary>
    [Theory]
    [InlineData(400, 400, false)]
    [InlineData(399, 400, false)]
    [InlineData(400.4, 400, false)] // layout jitter is not an overflow
    [InlineData(401, 400, true)]
    public void Chevrons_exist_only_when_there_is_overflow(
        double extent, double viewport, bool expected)
        => Assert.Equal(expected, TabStripScroll.Overflows(extent, viewport));

    /// <summary>Each end dims exactly at its own wall.</summary>
    [Fact]
    public void The_ends_know_when_they_are_reached()
    {
        Assert.False(TabStripScroll.CanGoLeft(0));
        Assert.True(TabStripScroll.CanGoLeft(10));

        Assert.True(TabStripScroll.CanGoRight(0, viewport: 400, extent: 1000));
        Assert.False(TabStripScroll.CanGoRight(600, viewport: 400, extent: 1000));
    }
}
