using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which crumbs survive when the path bar is too narrow for the path.
///
/// **The rule is "never lose the end", and nothing else in the application
/// enforces it.** The panel this replaced was a horizontal StackPanel, which
/// measures against infinite width — a long path ran off the right edge and the
/// folder you were actually in was the first thing gone. That is invisible to a
/// build and to every other test, and it only shows on a path long enough or a
/// window narrow enough.
///
/// Widths here are fixed on the children rather than measured from text, so the
/// arithmetic is exact and a font change cannot make these pass or fail for the
/// wrong reason.
/// </summary>
public class BreadcrumbPanelTests
{
    private static Control Crumb(double width, bool ellipsis = false) => new Border
    {
        Width = width,
        Height = 20,
        DataContext = ellipsis
            ? PathSegment.Ellipsis()
            : new PathSegment("x", "/x", null!, IsLast: false),
    };

    /// <summary>Lays out at a given width and reports where each child landed.</summary>
    private static double[] Layout(double available, params Control[] children)
    {
        var panel = new BreadcrumbPanel();
        foreach (var c in children) panel.Children.Add(c);

        panel.Measure(new Size(available, 20));
        panel.Arrange(new Rect(0, 0, available, 20));

        return children.Select(c => c.Bounds.X).ToArray();
    }

    private static bool OnScreen(double x) => x >= 0;

    [AvaloniaFact]
    public void When_it_all_fits_every_crumb_is_shown_in_order()
    {
        Control[] kids = [Crumb(40), Crumb(0, ellipsis: true), Crumb(50), Crumb(60)];

        var x = Layout(400, kids);

        Assert.Equal(0, x[0]);
        Assert.False(OnScreen(x[1]), "the ellipsis must be parked when nothing is dropped");
        Assert.Equal(40, x[2]);
        Assert.Equal(90, x[3]);
    }

    /// <summary>
    /// The whole point. The root and the last crumb are on screen; something in
    /// the middle is not.
    /// </summary>
    [AvaloniaFact]
    public void When_it_does_not_fit_the_middle_goes_and_the_ends_stay()
    {
        var root = Crumb(40);
        var ellipsis = Crumb(20, ellipsis: true);
        var middleA = Crumb(100);
        var middleB = Crumb(100);
        var here = Crumb(60);

        var x = Layout(160, root, ellipsis, middleA, middleB, here);

        Assert.True(OnScreen(x[0]), "the root must survive");
        Assert.True(OnScreen(x[1]), "the ellipsis must appear when crumbs are dropped");
        Assert.True(OnScreen(x[4]), "the folder you are in must always survive");

        Assert.False(OnScreen(x[2]) && OnScreen(x[3]),
            "at 160px there is no room for both middle crumbs");

        // And in the right order: root, ellipsis, then the tail.
        Assert.True(x[0] < x[1], "the ellipsis goes after the root");
        Assert.True(x[1] < x[4], "the tail goes after the ellipsis");
    }

    /// <summary>
    /// A single folder name wider than the whole bar still gets shown and
    /// clipped. Dropping it would leave a path bar that names no folder at all,
    /// which is worse than one showing the first few characters.
    /// </summary>
    [AvaloniaFact]
    public void The_current_folder_is_kept_even_when_it_cannot_fit()
    {
        var x = Layout(80, Crumb(40), Crumb(20, ellipsis: true), Crumb(500));

        Assert.True(OnScreen(x[2]), "the current folder is never dropped");
    }

    /// <summary>
    /// The ellipsis must not be what pushes a path over the edge: a bar exactly
    /// wide enough for every crumb shows every crumb, and no mark claiming
    /// otherwise.
    /// </summary>
    [AvaloniaFact]
    public void An_exact_fit_does_not_elide()
    {
        Control[] kids = [Crumb(50), Crumb(20, ellipsis: true), Crumb(50)];

        var x = Layout(100, kids);

        Assert.True(OnScreen(x[0]));
        Assert.True(OnScreen(x[2]));
        Assert.False(OnScreen(x[1]), "nothing was dropped, so nothing should say it was");
    }

    /// <summary>
    /// A root-only path has no middle to drop, and must not acquire an ellipsis
    /// from a bar that is merely narrow.
    /// </summary>
    [AvaloniaFact]
    public void A_single_crumb_is_never_elided()
    {
        var x = Layout(10, Crumb(300));

        Assert.True(OnScreen(x[0]));
    }
}
