using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging the details panel's edge.
///
/// **The panel had a resize handle that could not resize anything.** It was a
/// GridSplitter, and a splitter works by editing the Row or ColumnDefinitions
/// it sits between — the group is a DockPanel, which has neither. Every drag
/// was inert while the bar still painted itself and still turned the pointer
/// into a west-east resize cursor, which is the worst arrangement available:
/// the interface advertised a thing it could not do.
///
/// The rule that matters here is the upper bound. Past it the group decides
/// there is no longer room for a listing, <c>CanShowInfo</c> goes false, and
/// the panel disappears — so without a clamp the handle would let you drag the
/// panel until it vanished, which is a fault rather than a resize.
/// </summary>
public sealed class InfoPanelResizeTests : OwnedViewModels
{
    private sealed class InertFileSystem : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>A group with one tab and a known width, which is all the resize
    /// rule reads.</summary>
    private PaneGroupViewModel Group(double width)
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new InertFileSystem())));
        group.Tabs.Add(Own(new PaneViewModel(new InertFileSystem())));
        group.ActiveTab = group.Tabs[0];
        group.GroupWidth = width;

        return group;
    }

    [AvaloniaFact]
    public void Dragging_the_handle_left_widens_the_panel()
    {
        var group = Group(1400);
        var before = group.InfoWidth;

        // Negative X: the panel is docked right, so its handle moves left to
        // make it bigger. Getting this sign wrong is a resize that runs away
        // from the pointer.
        group.ResizeInfoBy(-60);

        Assert.Equal(before + 60, group.InfoWidth);
    }

    [AvaloniaFact]
    public void Dragging_it_right_narrows_the_panel()
    {
        var group = Group(1400);
        var before = group.InfoWidth;

        group.ResizeInfoBy(40);

        Assert.Equal(before - 40, group.InfoWidth);
    }

    /// <summary>
    /// The one that matters: a drag long enough to squeeze the listing out must
    /// stop, not carry the panel past the point where the group hides it.
    /// </summary>
    [AvaloniaFact]
    public void The_panel_cannot_be_dragged_until_it_disappears()
    {
        var group = Group(1400);

        group.ResizeInfoBy(-100_000);

        Assert.True(group.CanShowInfo,
            $"width {group.InfoWidth} in a 1400 group leaves no listing");
    }

    [AvaloniaFact]
    public void It_cannot_be_dragged_away_to_nothing_either()
    {
        var group = Group(1400);

        group.ResizeInfoBy(100_000);

        Assert.True(group.InfoWidth >= 220, $"narrowed to {group.InfoWidth}");
    }

    /// <summary>
    /// A window with no room for both refuses the drag rather than clamping to
    /// a width it cannot show. 500 is under the 420 minimum listing plus the
    /// 220 floor, so there is no answer that satisfies both.
    /// </summary>
    [AvaloniaFact]
    public void A_window_too_narrow_for_both_leaves_the_width_alone()
    {
        var group = Group(500);
        var before = group.InfoWidth;

        group.ResizeInfoBy(-200);

        Assert.Equal(before, group.InfoWidth);
    }

    /// <summary>
    /// **The trap the first version of this left open.** The drag clamps
    /// against the group width at the time, so a panel widened on a large
    /// display and later opened in a smaller window had CanShowInfo false, the
    /// panel gone, and its handle gone with it — and getting it back meant
    /// finding a window as wide as the one it was dragged in.
    /// </summary>
    [AvaloniaFact]
    public void Narrowing_the_window_takes_the_width_back_rather_than_stranding_it()
    {
        var group = Group(2400);
        group.IsInfoVisible = true;

        group.ResizeInfoBy(-1000);
        Assert.True(group.InfoWidth > 1000, $"the wide drag did not land: {group.InfoWidth}");

        // The same session reopened on a laptop screen.
        group.GroupWidth = 1000;

        Assert.True(group.IsInfoUsable,
            $"panel stranded: width {group.InfoWidth} in a 1000 group");
    }

    /// <summary>
    /// It gives room back, but only to the floor. Below that there is nothing
    /// left to read and the old hide-until-there-is-room behaviour is right.
    /// </summary>
    [AvaloniaFact]
    public void A_window_with_no_room_at_all_still_hides_the_panel()
    {
        var group = Group(2400);
        group.IsInfoVisible = true;
        group.ResizeInfoBy(-1000);

        group.GroupWidth = 500;

        Assert.False(group.CanShowInfo);
        Assert.True(group.InfoWidth >= 220, $"narrowed past the floor: {group.InfoWidth}");
    }

    /// <summary>
    /// A drag has to reach the session or it is forgotten at the next launch —
    /// which nothing noticed, because until the handle worked the width was a
    /// value nothing could change.
    /// </summary>
    [AvaloniaFact]
    public void A_drag_announces_itself_so_the_session_is_written()
    {
        var group = Group(1400);
        var told = 0;
        group.LayoutChanged += (_, _) => told++;

        group.ResizeInfoBy(-40);

        Assert.Equal(1, told);
    }

    /// <summary>A drag that changes nothing says nothing: the handle fires
    /// continuously, and a dirty flag per pixel of a clamped drag would write
    /// the session over and over.</summary>
    [AvaloniaFact]
    public void A_drag_that_moves_nothing_is_not_announced()
    {
        var group = Group(1400);
        group.ResizeInfoBy(-100_000);

        var told = 0;
        group.LayoutChanged += (_, _) => told++;

        group.ResizeInfoBy(-100_000);

        Assert.Equal(0, told);
    }

    /// <summary>
    /// Before the first measure there is no width to reason about, and the
    /// handle still has to work — a drag during startup must not be swallowed
    /// or clamped against a zero-width group.
    /// </summary>
    [AvaloniaFact]
    public void Before_the_first_measure_the_drag_still_lands()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new InertFileSystem())));
        var before = group.InfoWidth;

        group.ResizeInfoBy(-50);

        Assert.Equal(before + 50, group.InfoWidth);
    }
}
