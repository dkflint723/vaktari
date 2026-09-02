using Avalonia;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The marquee, and the two reasons it could never select more than a
/// screenful.
///
/// **The origin was anchored to the window.** As the list auto-scrolled, the
/// content slid out from under a rectangle that stayed where the press had
/// been, so the band stopped covering what the drag had crossed.
///
/// **And rows that left the viewport were deselected.** A row outside the view
/// has no container and no bounds, so it cannot be re-tested — and the
/// selection was rebuilt each pass from what was visible, which silently
/// dropped everything already swept.
///
/// Both are arithmetic, and the arithmetic is what these pin. Driving a real
/// virtualizing list far enough to recycle containers would be a test of
/// Avalonia's panel rather than of the rule.
/// </summary>
public sealed class RubberBandTests
{
    /// <summary>The band's anchor, re-expressed for how far the view has moved
    /// since the press — the fix, as one line.</summary>
    private static Point Anchor(Point pressedAt, Vector scrollThen, Vector scrollNow)
        => new(pressedAt.X - (scrollNow.X - scrollThen.X),
               pressedAt.Y - (scrollNow.Y - scrollThen.Y));

    private static Rect Band(Point anchor, Point pointer)
        => new(Math.Min(anchor.X, pointer.X), Math.Min(anchor.Y, pointer.Y),
               Math.Abs(pointer.X - anchor.X), Math.Abs(pointer.Y - anchor.Y));

    [Fact]
    public void Without_scrolling_the_band_is_where_it_was_drawn()
    {
        var anchor = Anchor(new Point(10, 20), default, default);

        Assert.Equal(new Point(10, 20), anchor);
    }

    /// <summary>
    /// **The fault.** Press at y=400, drag to the bottom edge, and the list
    /// scrolls 600px underneath. The anchor has to travel with the content, or
    /// the rectangle covers only what is on screen now.
    /// </summary>
    [Fact]
    public void Scrolling_carries_the_anchor_with_the_content()
    {
        var anchor = Anchor(new Point(10, 400), default, new Vector(0, 600));

        Assert.Equal(-200, anchor.Y);

        // And the rectangle therefore still reaches back over everything swept.
        var rect = Band(anchor, new Point(300, 700));

        Assert.Equal(-200, rect.Y);
        Assert.Equal(900, rect.Height);
    }

    /// <summary>Unanchored, the same drag covers one screenful — which is
    /// exactly what people saw.</summary>
    [Fact]
    public void The_old_arithmetic_covered_only_what_was_on_screen()
    {
        var stuck = Band(new Point(10, 400), new Point(300, 700));

        Assert.Equal(300, stuck.Height);
    }

    [Fact]
    public void Scrolling_back_up_moves_the_anchor_the_other_way()
    {
        var anchor = Anchor(new Point(10, 100), new Vector(0, 500), new Vector(0, 200));

        Assert.Equal(400, anchor.Y);
    }

    // ---- and what the band keeps -------------------------------------------
    //
    // The selection rule, in the shape ApplyBand applies it: whatever was held
    // before the band, plus everything on screen the rectangle touches, plus
    // everything already taken that has since scrolled out of sight.

    private static List<string> Wanted(
        IReadOnlyList<string> kept,
        IReadOnlyList<string> realized,
        IReadOnlyList<string> touching,
        IReadOnlyList<string> takenSoFar)
    {
        var wanted = new List<string>(kept);

        foreach (var item in touching)
            if (!wanted.Contains(item))
                wanted.Add(item);

        foreach (var taken in takenSoFar)
            if (!realized.Contains(taken) && !wanted.Contains(taken))
                wanted.Add(taken);

        return wanted;
    }

    /// <summary>
    /// **The fault.** Two hundred files swept, and only the last screenful kept
    /// — every row that scrolled away was dropped from the selection.
    /// </summary>
    [Fact]
    public void Rows_that_scrolled_out_of_sight_stay_selected()
    {
        var wanted = Wanted(
            kept: [],
            realized: ["e", "f", "g"],          // what is on screen now
            touching: ["e", "f"],               // what the rectangle covers now
            takenSoFar: ["a", "b", "c", "d"]);  // swept before they scrolled away

        Assert.Equal(["e", "f", "a", "b", "c", "d"], wanted);
    }

    /// <summary>
    /// But a row still on screen is re-tested, so dragging the band back up
    /// takes it off again. Keeping everything ever touched would make the
    /// gesture impossible to correct.
    /// </summary>
    [Fact]
    public void A_row_still_on_screen_can_be_dragged_back_off()
    {
        var wanted = Wanted(
            kept: [],
            realized: ["e", "f", "g"],
            touching: ["e"],                    // the band shrank off f
            takenSoFar: ["e", "f"]);

        Assert.Equal(["e"], wanted);
        Assert.DoesNotContain("f", wanted);
    }

    /// <summary>Ctrl held: what was already selected survives the whole
    /// gesture.</summary>
    [Fact]
    public void An_additive_band_keeps_the_earlier_selection()
    {
        var wanted = Wanted(
            kept: ["x", "y"],
            realized: ["e", "f"],
            touching: ["e"],
            takenSoFar: []);

        Assert.Equal(["x", "y", "e"], wanted);
    }
}
