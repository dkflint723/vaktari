using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Lays out the path bar's crumbs, dropping ancestors from the MIDDLE when
/// there is not room for all of them.
///
/// **The end of the path is the part you need.** A horizontal StackPanel — what
/// this replaces — measures its children against infinite width, so a long path
/// simply ran past the edge of the toolbar and the folder you were actually in
/// was the first thing to leave the screen. Trimming from the end is the one
/// thing a path bar must never do.
///
/// So the root and the tail are kept and the middle collapses to an ellipsis:
/// <c>C:\ › … › Vaktari.Ui</c>. The root stays because "which drive" is the
/// other question a truncated path leaves unanswered, and it costs three
/// characters to answer.
///
/// **Elided crumbs are moved off to the side rather than hidden.** Setting
/// IsVisible during arrange invalidates measure, which is how a layout loop
/// begins; arranging them outside the panel and clipping is decided within a
/// single pass and cannot oscillate.
///
/// **What was dropped is handed to the ellipsis**, into
/// <see cref="PathSegment.Hidden"/>, which the crumb's menu lists. It was
/// computed here and discarded, so the mark could say that ancestors were
/// missing and nothing in the application could say which.
/// </summary>
public sealed class BreadcrumbPanel : Panel
{
    /// <summary>Breathing room either side of the ellipsis crumb.</summary>
    private const double Parked = -100000;

    public BreadcrumbPanel() => ClipToBounds = true;

    /// <summary>
    /// The crumb standing in for what was dropped. Identified by its data
    /// rather than its position: the view model puts it second today, and a
    /// panel that silently depended on that would break the day anything else
    /// was added to the front.
    /// </summary>
    private static bool IsEllipsis(Control child)
        => child.DataContext is PathSegment { IsEllipsis: true };

    /// <summary>The crumb a child stands for, or null when it is not one.</summary>
    private static PathSegment? Segment(Control? child) => child?.DataContext as PathSegment;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;

        // Infinite width on purpose: a crumb's natural size is what decides
        // whether it fits, and constraining it here would let a long folder
        // name trim itself before this panel ever got to choose.
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            height = Math.Max(height, child.DesiredSize.Height);

            // The ellipsis is not part of the path, so it must not count
            // toward whether the path fits — otherwise a bar wide enough for
            // every crumb would still elide, to make room for the mark saying
            // it had.
            if (!IsEllipsis(child)) width += child.DesiredSize.Width;
        }

        // Never ask for more than offered, or the DockPanel hands us the space
        // and the toolbar's other controls are pushed out instead.
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        var ellipsis = Children.FirstOrDefault(IsEllipsis);
        var path = Children.Where(c => !IsEllipsis(c)).ToList();

        var total = path.Sum(c => c.DesiredSize.Width);

        if (total <= finalSize.Width || path.Count <= 1)
        {
            Park(ellipsis);

            // Nothing was dropped, so the ellipsis stands for nothing. Said out
            // loud rather than left over from the last arrange: the bar is
            // re-arranged as it widens, and a menu still offering the crumb now
            // visible beside it would be the stale half of the old answer.
            Segment(ellipsis)?.StandsFor([]);
            CloseMenu(ellipsis);

            var x = 0.0;
            foreach (var child in path)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width;
            }

            return finalSize;
        }

        var gap = ellipsis?.DesiredSize.Width ?? 0;
        var first = path[0];

        // How many trailing crumbs fit after the root and the ellipsis, counted
        // from the END, because the tail is what has to survive.
        var budget = finalSize.Width - first.DesiredSize.Width - gap;
        var tail = 0;
        var used = 0.0;

        for (var i = path.Count - 1; i >= 1; i--)
        {
            var next = used + path[i].DesiredSize.Width;
            if (next > budget) break;

            used = next;
            tail++;
        }

        // The folder you are in is never dropped, even when it alone is wider
        // than the bar. It gets clipped instead, which still shows its opening
        // characters; dropping it would show none of them.
        if (tail == 0) tail = 1;

        var firstTail = path.Count - tail;

        var cursor = 0.0;
        first.Arrange(new Rect(cursor, 0, first.DesiredSize.Width, finalSize.Height));
        cursor += first.DesiredSize.Width;

        if (ellipsis is not null)
        {
            ellipsis.Arrange(new Rect(cursor, 0, gap, finalSize.Height));
            cursor += gap;
        }

        // **Which ancestors went is decided HERE and nowhere else**, so this is
        // the only place that can tell the ellipsis what it is standing in for.
        // The view model cannot: it rebuilds the crumbs on navigation, and this
        // answer changes on a window resize or a splitter drag with the same
        // path on screen throughout.
        var hidden = new List<PathSegment>();

        for (var i = 1; i < path.Count; i++)
        {
            var child = path[i];

            if (i < firstTail)
            {
                Park(child);

                if (Segment(child) is { } dropped) hidden.Add(dropped);

                continue;
            }

            child.Arrange(new Rect(cursor, 0, child.DesiredSize.Width, finalSize.Height));
            cursor += child.DesiredSize.Width;
        }

        Segment(ellipsis)?.StandsFor(hidden);

        return finalSize;
    }

    /// <summary>
    /// Off to the left, where ClipToBounds removes it. Arranging at 0x0 instead
    /// leaves a control whose own content is not necessarily clipped, and it
    /// draws over the root.
    /// </summary>
    private static void Park(Control? child) => child?.Arrange(
        new Rect(Parked, 0, child.DesiredSize.Width, child.DesiredSize.Height));

    /// <summary>
    /// Shuts the mark's menu when the bar has just taken back every folder it
    /// was standing for.
    ///
    /// **A menu emptied while it was open laid out 2 by 32 with no rows in
    /// it.** Measured in a real window over a temp path: the flyout shown at
    /// 480px listed eight ancestors, and arranging the same window at 3200
    /// left it OPEN with `rows=0 bounds=0, 0, 2, 32` — the sliver
    /// CrumbMenuTests.A_menu_with_no_rows_yet_opens_as_a_sliver records and
    /// A_crumb_menu_never_opens_empty forbids for the chevron next door.
    /// Disabling the button does not take down a popup that is already up, and
    /// the button this one hangs off has just been parked 100000px away.
    ///
    /// Every flyout on the crumb rather than only the menu's: the mark also
    /// carries a chevron, and measured on a real window the mark's crumb
    /// realizes three buttons of which exactly one is effectively visible — the
    /// one carrying this menu. Nothing else here can be open to be shut, and
    /// Hide on a flyout that is already down is a return.
    ///
    /// Null-safe through the compiler rather than through a hand-written
    /// guard, the way <see cref="Park"/> is: a bar whose path is short enough
    /// has no mark to close.
    /// </summary>
    private static void CloseMenu(Control? mark)
    {
        foreach (var button in mark?.GetVisualDescendants().OfType<Button>() ?? [])
            button.Flyout?.Hide();
    }
}
