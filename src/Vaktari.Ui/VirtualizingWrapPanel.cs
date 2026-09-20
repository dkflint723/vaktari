using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Vaktari.Ui;

/// <summary>
/// A wrapping panel that realizes only the rows on screen.
///
/// This is the last real gap against Dolphin: `WrapPanel` realizes a container
/// for every item it is given, so the tile layouts refuse anything over
/// a five-thousand-item ceiling while other managers show 200,000 without one.
/// icon view without complaint.
///
/// **Uniform tile size is the whole trick.** Every tile is exactly
/// <see cref="ItemWidth"/> by <see cref="ItemHeight"/>, so the row containing
/// item N is arithmetic rather than a measurement — nothing off screen has to be
/// measured, which is what makes wrapping virtualizable at all. Vaktari already
/// has those numbers: `PaneScale` publishes `TileWidth` and `TileHeight`.
///
/// Scrolling is driven by <see cref="Layoutable.EffectiveViewportChanged"/>
/// rather than by implementing `ILogicalScrollable`, which the base class
/// documentation offers as the simpler of the two routes.
///
/// The container lifecycle follows `ItemContainerGenerator`'s documented
/// protocol exactly: NeedsContainer, CreateContainer, PrepareItemContainer,
/// AddInternalChild, ItemContainerPrepared — and on the way out
/// ClearItemContainer then into a pool keyed by the recycle key.
/// **Recycled containers stay in the panel with IsVisible false** rather than
/// being removed. The generator's docs require it, and it is also what keeps the
/// attached-property row decoration attached.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), 100);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemHeight), 100);

    /// <summary>
    /// Which way items flow before wrapping.
    ///
    /// **Horizontal** — the grid: items run left to right, wrap onto a new ROW,
    /// and the view scrolls DOWN.
    /// **Vertical** — compact: items run top to bottom, wrap into a new COLUMN,
    /// and the view scrolls ACROSS. That is what `WrapPanel Orientation` means
    /// too, so the compact template keeps the word it already used.
    ///
    /// Everything below is written in LANES and SLOTS rather than rows and
    /// columns, because the arithmetic is identical either way and only the axes
    /// swap: a lane is one row or one column, a slot is a position within it.
    /// </summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, Orientation>(
            nameof(Orientation), Orientation.Horizontal);

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>
    /// Gap around each tile, added to <see cref="ItemWidth"/> and
    /// <see cref="ItemHeight"/> to give the CELL size.
    ///
    /// Explicit because the grid's item template carries `Margin="3"`, so its
    /// desired size is six pixels larger than the tile in each direction.
    /// Arranging into a cell of exactly TileWidth would clip every tile by that
    /// margin — a quiet, uniform wrongness that is hard to spot and easy to
    /// misread as a styling problem.
    /// </summary>
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemSpacing), 6);

    /// <summary>Remembers how each container may be pooled. An attached property
    /// rather than a dictionary, so it travels with the container.</summary>
    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VirtualizingWrapPanel, Control, object?>("RecycleKey");

    /// <summary>Marks an item that IS its own container. Those can never be
    /// recycled or cleared — the generator's docs are explicit about it.</summary>
    private static readonly object ItemIsItsOwnContainer = new();

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<object, Stack<Control>> _pool = [];

    private Rect _viewport;
    /// <summary>Items per lane — columns when horizontal, rows when vertical.</summary>
    private int _slots = 1;

    /// <summary>Set VAKTARI_TILE_DEBUG=1 to print realized count, index range
    /// and viewport on every measure. This is the ground truth for "is this
    /// actually virtualizing" — the realized count cannot be ambiguous the way
    /// a timing figure can — and it is what proved the panel works at 100,000
    /// items. Kept for the compact port, which has to prove the same thing
    /// again.</summary>
    private static readonly bool Diagnose =
        Environment.GetEnvironmentVariable("VAKTARI_TILE_DEBUG") == "1";

    public VirtualizingWrapPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    /// <summary>
    /// **These three change the layout, and Avalonia has to be told.**
    ///
    /// Registered as plain styled properties, they were changing without ever
    /// invalidating measure — so the panel kept arranging children into cells
    /// sized by the PREVIOUS values. Shrinking looked survivable, because a
    /// child smaller than its cell just leaves a gap; growing overlapped, because
    /// a child larger than its cell spills into the next one. Switching layout
    /// and back forced a fresh measure and "fixed" it, which is what made the
    /// bug look intermittent rather than absent.
    /// </summary>
    static VirtualizingWrapPanel()
    {
        AffectsMeasure<VirtualizingWrapPanel>(
            ItemWidthProperty, ItemHeightProperty, ItemSpacingProperty,
            OrientationProperty);
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>The footprint of one item, tile plus gap. Every layout decision
    /// uses this rather than the tile size.</summary>
    private double CellWidth => Math.Max(1, ItemWidth + ItemSpacing);

    private double CellHeight => Math.Max(1, ItemHeight + ItemSpacing);

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;

        // Which rows should exist is a measure concern: arrange cannot create
        // containers, only place ones that are already there.
        InvalidateMeasure();
    }

    /// <summary>
    /// Turns a wheel notch into HORIZONTAL movement when the panel is laid out in
    /// columns.
    ///
    /// **A wheel is a vertical gesture and this layout scrolls sideways.** The
    /// compact listing sets `VerticalScrollBarVisibility="Disabled"`, so the
    /// ScrollViewer has nothing to do with a vertical delta and swallows it — the
    /// listing simply did not move. Home and End worked throughout, because they
    /// go through `BringIntoView` rather than the wheel, which is what proves the
    /// extent was right all along and only the gesture was missing.
    ///
    /// `delta.Y` is used even though the movement is horizontal: an ordinary
    /// mouse only reports Y, and a trackpad's sideways scroll arrives as X, so
    /// both are honoured and whichever is larger wins.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Orientation != Orientation.Vertical || Scroller() is not { } scroller)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;

        if (delta == 0)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        // One notch moves one lane, so a wheel click steps a whole column rather
        // than a fraction of one — the same unit the layout is built from.
        var step = CellWidth * -delta;

        var limit = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var next = Math.Clamp(scroller.Offset.X + step, 0, limit);

        if (Math.Abs(next - scroller.Offset.X) < 0.01) return;

        scroller.Offset = scroller.Offset.WithX(next);
        e.Handled = true;
    }

    /// <summary>The ScrollViewer this panel sits inside, if any.</summary>
    private ScrollViewer? Scroller()
    {
        for (var visual = this.GetVisualParent(); visual is not null;
             visual = visual.GetVisualParent())
            if (visual is ScrollViewer found) return found;

        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;

        if (items.Count == 0)
        {
            RecycleAll();
            return default;
        }

        var itemWidth = CellWidth;
        var itemHeight = CellHeight;

        var vertical = Orientation == Orientation.Vertical;

        // Along a lane vs. between lanes. Swapping these two is the whole of the
        // orientation change; every count below is the same arithmetic.
        var alongStep = vertical ? itemHeight : itemWidth;
        var laneStep = vertical ? itemWidth : itemHeight;
        var alongSpace = vertical ? availableSize.Height : availableSize.Width;

        _slots = Math.Max(1, (int)(alongSpace / alongStep));

        var lanes = (items.Count + _slots - 1) / _slots;

        var extent = vertical
            ? new Size(lanes * laneStep, _slots * alongStep)
            : new Size(_slots * alongStep, lanes * laneStep);

        // On the first pass the viewport is still empty, so fall back to the
        // space offered. Without this nothing is realized and the panel reports
        // an extent it never fills. **The measured side is the one lanes stack
        // along** — height when scrolling down, width when scrolling across.
        var known = vertical ? _viewport.Width > 0 : _viewport.Height > 0;

        var near = known ? (vertical ? _viewport.Left : _viewport.Top) : 0;
        var far = known
            ? (vertical ? _viewport.Right : _viewport.Bottom)
            : (vertical ? availableSize.Width : availableSize.Height);

        // A lane of slack either side, so scrolling does not expose a blank band
        // before the next measure catches up.
        var firstLane = Math.Max(0, (int)(near / laneStep) - 1);
        var lastLane = Math.Min(lanes - 1, (int)(far / laneStep) + 1);

        var first = firstLane * _slots;
        var last = Math.Min(items.Count - 1, ((lastLane + 1) * _slots) - 1);

        Realize(first, last, items);

        var tile = new Size(itemWidth, itemHeight);

        foreach (var container in _realized.Values) container.Measure(tile);

        if (Diagnose)
        {
            Console.Error.WriteLine(
                $"[vaktari] wrap: items={items.Count:N0} realized={_realized.Count} "
                + $"range={first}..{last} {(vertical ? "rows" : "cols")}={_slots} "
                // The INPUTS to the column count, not just its result. Without
                // them the line can show reflow failing and say nothing about
                // why — and cell size is exactly where a per-pane metric and a
                // global one disagree.
                + $"item={ItemWidth:F0}x{ItemHeight:F0} gap={ItemSpacing:F0} "
                + $"cell={ItemWidth + ItemSpacing:F0}x{ItemHeight + ItemSpacing:F0} "
                + $"viewport={near:F0}..{far:F0} "
                + $"avail={availableSize.Width:F0}x{availableSize.Height:F0} "
                + $"extent={extent.Width:F0}x{extent.Height:F0}");
        }

        return extent;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = CellWidth;
        var itemHeight = CellHeight;

        var vertical = Orientation == Orientation.Vertical;

        foreach (var (index, container) in _realized)
        {
            var lane = index / _slots;
            var slot = index % _slots;

            // Vertical fills a column downwards then steps right; horizontal
            // fills a row rightwards then steps down.
            container.Arrange(vertical
                ? new Rect(lane * itemWidth, slot * itemHeight, itemWidth, itemHeight)
                : new Rect(slot * itemWidth, lane * itemHeight, itemWidth, itemHeight));
        }

        return finalSize;
    }

    /// <summary>
    /// Brings the realized set to exactly the given range. Anything outside goes
    /// back to the pool FIRST, so those containers are available to be reused
    /// rather than newly created.
    /// </summary>
    private void Realize(int first, int last, IReadOnlyList<object?> items)
    {
        foreach (var index in _realized.Keys.Where(i => i < first || i > last).ToList())
            Recycle(index);

        for (var index = first; index <= last; index++)
        {
            if (_realized.ContainsKey(index)) continue;

            if (GetOrCreate(items[index], index) is { } container)
                _realized[index] = container;
        }
    }

    private Control? GetOrCreate(object? item, int index)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return null;

        if (!generator.NeedsContainer(item, index, out var recycleKey))
        {
            // The item IS the container. It can never be recycled, so it is
            // prepared exactly once and afterwards merely shown again.
            if (item is not Control own) return null;

            if (own.GetValue(RecycleKeyProperty) != ItemIsItsOwnContainer)
            {
                own.SetValue(RecycleKeyProperty, ItemIsItsOwnContainer);
                generator.PrepareItemContainer(own, item, index);
                AddInternalChild(own);
                generator.ItemContainerPrepared(own, item, index);
            }

            own.IsVisible = true;
            return own;
        }

        if (recycleKey is not null
            && _pool.TryGetValue(recycleKey, out var pooled)
            && pooled.Count > 0)
        {
            var reused = pooled.Pop();

            reused.IsVisible = true;
            generator.PrepareItemContainer(reused, item, index);
            generator.ItemContainerPrepared(reused, item, index);

            return reused;
        }

        var created = generator.CreateContainer(item, index, recycleKey);

        created.SetValue(RecycleKeyProperty, recycleKey);

        generator.PrepareItemContainer(created, item, index);
        AddInternalChild(created);
        generator.ItemContainerPrepared(created, item, index);

        return created;
    }

    /// <summary>
    /// Returns a container to the pool. A focused container is KEPT unless
    /// <paramref name="force"/>.
    /// </summary>
    private void Recycle(int index, bool force = false)
    {
        if (!_realized.TryGetValue(index, out var container)) return;

        // KEEPING THE FOCUSED CONTAINER REALIZED IS REQUIRED, NOT AN
        // OPTIMISATION. Recycling hides the control and clears its item, and
        // focus dies with it — leaving the window with NO focused element at
        // all, so every later keystroke does nothing until the list is clicked
        // again.
        //
        // Measured: ScrollIntoView realizes the target and hands it back, the
        // ListBox focuses it, and the measure that ScrollIntoView itself
        // triggers still sees the OLD viewport — BringIntoView only schedules
        // the scroll — so it computes a range excluding the row just realized
        // and recycles it. Home and End both jump far outside the realized
        // range, so this was the common case rather than an edge one.
        //
        // It also covers the ordinary case: focus a tile, scroll it off
        // screen, and focus should still be there. The container is held for
        // as long as focus stays on it — one extra realized container, visible
        // as realized=49 rather than 48 — and is recycled by the first measure
        // that finds it out of range once focus has moved on.
        //
        // `IsFocused` rather than a focus-within test because the focused
        // element measured here IS the container, and nothing in the grid item
        // template is focusable. Revisit if a template gains a focusable child.
        //
        // KNOWN GAP, deliberately not built: Avalonia's VirtualizingStackPanel
        // protects TWO elements — the focused one AND the current ScrollIntoView
        // target — so a scroll that does not also focus is still exposed to the
        // same stale-viewport recycle. Type-ahead is the path that would show
        // it, and it was tested and behaves correctly, so the second mechanism
        // is not warranted here. If a scroll ever lands in the wrong place,
        // this is the first thing to suspect.
        if (!force && container.IsFocused) return;

        _realized.Remove(index);

        var recycleKey = container.GetValue(RecycleKeyProperty);

        // An item that is its own container may not be cleared or pooled, only
        // hidden.
        if (ReferenceEquals(recycleKey, ItemIsItsOwnContainer))
        {
            container.IsVisible = false;
            return;
        }

        ItemContainerGenerator?.ClearItemContainer(container);

        if (recycleKey is null)
        {
            RemoveInternalChild(container);
            return;
        }

        container.IsVisible = false;

        if (!_pool.TryGetValue(recycleKey, out var pooled))
            _pool[recycleKey] = pooled = new Stack<Control>();

        pooled.Push(container);
    }

    /// <summary>
    /// Forces every container back, focused or not. Used when the items
    /// themselves change: the focused item may no longer exist, so keeping its
    /// container would keep stale content on screen — a worse fault than
    /// losing focus.
    /// </summary>
    private void RecycleAll()
    {
        foreach (var index in _realized.Keys.ToList()) Recycle(index, force: true);
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // Deliberately blunt: everything realized goes back and the next measure
        // rebuilds. Index-shuffling on insert and remove is where
        // VirtualizingStackPanel spends much of its complexity, and this panel's
        // items come from a listing that is reloaded wholesale anyway.
        RecycleAll();
        InvalidateMeasure();
    }

    // ---- the abstract contract ---------------------------------------------
    //
    // Declared `protected`, NOT `protected internal`, even though the base
    // declares them `protected internal`. Across an assembly boundary the
    // `internal` half does not apply, so C# requires the override to drop it
    // (CS0507). `GetControl` below is plain `protected` in the base and needs no
    // such adjustment.

    protected override Control? ContainerFromIndex(int index)
        => _realized.TryGetValue(index, out var container) ? container : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
            if (ReferenceEquals(realized, container)) return index;

        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers()
        => _realized.Values;

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;

        if (index < 0 || index >= items.Count) return null;

        var itemWidth = CellWidth;
        var itemHeight = CellHeight;
        var vertical = Orientation == Orientation.Vertical;
        var lane = index / Math.Max(1, _slots);

        // Bring the LANE into view, not the item: the lane is the scrolling
        // axis, and the other axis is fully visible by construction.
        this.BringIntoView(vertical
            ? new Rect(lane * itemWidth, 0, itemWidth, itemHeight)
            : new Rect(0, lane * itemHeight, itemWidth, itemHeight));

        // Realize the target NOW rather than waiting for the layout pass that
        // BringIntoView merely schedules.
        //
        // Returning null here is not harmless: the caller has nothing to focus,
        // so the key looks like it did not register — and the unhandled key then
        // falls through to whatever else is listening, which in this window is
        // plenty. Home and End arrive as First and Last, and both jump far
        // outside the realized range, so this was the common case rather than an
        // edge one.
        //
        // A container realized here that turns out to be off screen is recycled
        // by the next measure, exactly like any other.
        if (!_realized.TryGetValue(index, out var container))
        {
            container = GetOrCreate(items[index], index);

            if (container is not null)
            {
                _realized[index] = container;
                container.Measure(new Size(itemWidth, itemHeight));

                var slot = index % Math.Max(1, _slots);

                container.Arrange(vertical
                    ? new Rect(lane * itemWidth, slot * itemHeight, itemWidth, itemHeight)
                    : new Rect(slot * itemWidth, lane * itemHeight, itemWidth, itemHeight));
            }

            InvalidateMeasure();
        }

        return container;
    }

    /// <summary>
    /// Keyboard navigation. Left and right move by one; up and down move by a
    /// whole row — the only part that differs from a stack panel, and the reason
    /// this cannot simply reuse one.
    /// </summary>
    protected override IInputElement? GetControl(
        NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;

        var current = from is Control control ? IndexFromContainer(control) : -1;

        if (count == 0) return null;

        // NO ORIGIN — the list itself has focus rather than a row, which is
        // the normal state right after clicking empty space. Arithmetic on
        // `current = -1` produced item 7 for PageDown (`-1 + _columns`), which
        // reads as random. Measured 27 July 2026:
        // `nav: PageDown from=null current=-1` then `nav-target: 7`.
        //
        // Avalonia's VirtualizingStackPanel REFUSES this case outright —
        // `GetControl` returns null when `from` is null and the direction is
        // not First or Last. Selecting the first item is the friendlier
        // reading of "I clicked into the list and pressed a key", and matches
        // what Explorer does; flipping to Avalonia's behaviour is changing
        // this one return to `null`.
        if (current < 0
            && direction is not (NavigationDirection.First or NavigationDirection.Last))
            return ScrollIntoView(0);

        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            // Previous/Next are index order in both layouts — Tab and type-ahead
            // mean "the next item", not "the item to the right". Only the
            // ARROW keys care which way the lanes run.
            NavigationDirection.Previous => current - 1,
            NavigationDirection.Next => current + 1,

            NavigationDirection.Left
                => current - (Orientation == Orientation.Vertical ? _slots : 1),
            NavigationDirection.Right
                => current + (Orientation == Orientation.Vertical ? _slots : 1),
            // PageUp/PageDown are MEASURED to be nearly dead here. Once a row
            // has focus the ScrollViewer claims them and pages the viewport by
            // its own height (viewport 635..1275, then 1275..1916, …) with no
            // `nav:` line at all — and it moves the VIEW without moving focus
            // or selection. This panel sees them only on the first press,
            // while the list itself still holds focus, and that case is now
            // handled above. Left mapped rather than removed because nothing
            // has measured what happens in a folder short enough not to
            // scroll.
            // **The axes swap with the orientation.** In a column layout the
            // next item DOWN is the next index, and the next item RIGHT is a
            // whole lane away — the exact opposite of the grid.
            NavigationDirection.Up
                => current - (Orientation == Orientation.Vertical ? 1 : _slots),
            NavigationDirection.Down
                => current + (Orientation == Orientation.Vertical ? 1 : _slots),

            // **Page never arrives here.** In the grid the ScrollViewer claims
            // it and pages the viewport; in compact `MainWindow` claims it on the
            // tunnel phase and scrolls sideways, because mapping it here changed
            // nothing — the key was being swallowed before the panel saw it.
            // Left mapped for the case nothing has measured: a folder short
            // enough not to scroll at all.
            NavigationDirection.PageUp => current - _slots,
            NavigationDirection.PageDown => current + _slots,
            _ => -1,
        };

        if (target < 0 || target >= count)
        {
            if (!wrap) return null;

            target = target < 0 ? count - 1 : 0;
        }

        return ScrollIntoView(target);
    }
}
