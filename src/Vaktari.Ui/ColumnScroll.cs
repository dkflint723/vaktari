using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The details columns scrolling sideways: the headings and the rows as one.
///
/// **Two scrollers, one offset.** The rows scroll in the ListBox's own
/// ScrollViewer; the headings sit in a second one above it, because they are
/// not part of the list and must not scroll out of sight vertically. Each
/// copies its horizontal offset to the other the moment it changes —
/// synchronously, from the property change itself, never from ScrollChanged
/// or a posted job, so no frame is ever drawn with the two apart (measured in
/// the headless window at 100–150% device scaling: 0.000 pixels between every
/// heading edge and every realised row, after one layout pass). Both ways,
/// because the heading's scroller moves on its own too: Shift+Tab onto a sort
/// heading that is off the edge brings it into view there, and a sideways
/// swipe over the headings lands there.
///
/// **Both content widths come from one number,** <see
/// cref="PaneViewModel.DetailsRowWidth"/>, applied the same way — a Width on
/// each scroller's panel. Given two numbers that round apart, the heading
/// stopped one device pixel short at the far right.
///
/// **Paired per pane, and unpaired when the pane's view goes.** Each half of a
/// split, and each tab, has its own heading and its own list; a subscription
/// that outlived its window would keep a closed window's controls alive.
/// </summary>
public static class ColumnScroll
{
    /// <summary>Set on the heading's ScrollViewer: the details ListBox whose
    /// rows it scrolls with.</summary>
    public static readonly AttachedProperty<ListBox?> RowsProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, ListBox?>("Rows", typeof(ColumnScroll));

    /// <summary>
    /// Set on the details ListBox by its pair: how far the rows are scrolled
    /// sideways, rounded to a device pixel the way the scroller rounds the
    /// rows' own position. The selection bar and the group headings are moved
    /// right by it, so they stay at the left of what is on screen.
    /// </summary>
    public static readonly AttachedProperty<double> PinnedProperty =
        AvaloniaProperty.RegisterAttached<ListBox, double>("Pinned", typeof(ColumnScroll));

    /// <summary>Set on the details items panel: a row brought into view moves
    /// the rows up or down only, never sideways. See <see cref="OnBringIntoView"/>.</summary>
    public static readonly AttachedProperty<bool> KeepsSidewaysProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("KeepsSideways", typeof(ColumnScroll));

    public static void SetRows(ScrollViewer viewer, ListBox? value) => viewer.SetValue(RowsProperty, value);
    public static ListBox? GetRows(ScrollViewer viewer) => viewer.GetValue(RowsProperty);

    public static void SetPinned(ListBox list, double value) => list.SetValue(PinnedProperty, value);
    public static double GetPinned(ListBox list) => list.GetValue(PinnedProperty);

    public static void SetKeepsSideways(Control panel, bool value) => panel.SetValue(KeepsSidewaysProperty, value);
    public static bool GetKeepsSideways(Control panel) => panel.GetValue(KeepsSidewaysProperty);

    /// <summary>Hidden on the headings while the columns overflow — their
    /// scroll bar is the rows' — and off while they fit.</summary>
    public static readonly IValueConverter HeadingBar =
        new FuncValueConverter<bool, ScrollBarVisibility>(over => over ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled);

    /// <summary>
    /// On the rows while the columns overflow, and off while they fit.
    ///
    /// **Off, not merely unused, while they fit.** With the axis on, the rows
    /// are measured at an infinite width, and a name that fills asks for its
    /// whole text — a scroll bar in a tab nobody has dragged a column in.
    /// Off, the layout is the one the listing always had.
    /// </summary>
    public static readonly IValueConverter RowsBar =
        new FuncValueConverter<bool, ScrollBarVisibility>(over => over ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);

    /// <summary>A pinned part's shift as a transform.</summary>
    public static readonly IValueConverter Shift =
        new FuncValueConverter<double, ITransform>(x => new TranslateTransform(x, 0));

    private static readonly ConditionalWeakTable<ListBox, Pair> Pairs = new();

    static ColumnScroll()
    {
        RowsProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            Pair.Of(viewer)?.Dispose();

            if (e.NewValue is ListBox list) _ = new Pair(viewer, list);
        });

        KeepsSidewaysProperty.Changed.AddClassHandler<Control>((panel, e) =>
        {
            panel.RemoveHandler(Control.RequestBringIntoViewEvent, OnBringIntoView);

            if (e.NewValue is true) panel.AddHandler(Control.RequestBringIntoViewEvent, OnBringIntoView);
        });
    }

    /// <summary>
    /// **A row brought into view keeps the horizontal offset.** A row is as
    /// wide as the columns, wider than the pane once they scroll, and the
    /// scroller brings a rectangle wider than itself into view by lining up
    /// its left edge — so arrowing down past the bottom, a selection made from
    /// code and type-ahead all threw the view back to the left (measured: 300
    /// to 0). The request's rectangle is narrowed here to the part already on
    /// screen, so only the vertical half acts.
    ///
    /// **On the items panel, not the ListBox**: rewritten at the ListBox it is
    /// too late, the scroller has already acted. And only for a request whose
    /// target is a row: the rename box raises its own, and that one must bring
    /// the box into view sideways.
    ///
    /// **No mutation reddens the rewrite yet, and one was looked for.** With
    /// the rows' panel as wide as the columns, removing it left X where it was
    /// through twenty-five Downs past the bottom, a selection of the last row
    /// made from code, ScrollIntoView and Home (measured in the headless
    /// window). The adversarial review measured the snap to 0 with a
    /// fixed-width panel, where this handler kept X in all three of its cases;
    /// it stays on that measurement, and on QA's real window, until a case is
    /// found here. Its other half — rows only — is held by the F2 test.
    /// </summary>
    private static void OnBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not ListBoxItem item
            || sender is not Control panel
            || panel.FindAncestorOfType<ScrollViewer>() is not { } scroller)
            return;

        var left = scroller.Offset.X - item.Bounds.X;

        e.TargetRect = new Rect(left, e.TargetRect.Y, scroller.Viewport.Width, e.TargetRect.Height);
    }

    /// <summary>
    /// After type-ahead has chosen <paramref name="entry"/>: back to the left
    /// if the start of its name is scrolled out. Typing a name is looking for
    /// names, so the one found should be readable; anywhere else is kept.
    /// </summary>
    internal static void RevealName(ListBox list, PaneViewModel pane, FileEntry entry)
    {
        if (!Pairs.TryGetValue(list, out var pair) || pair.Rows is not { } rows) return;
        if (pair.Grid is not { ColumnDefinitions.Count: > 1 } grid) return;

        var indent = pane.Indents.TryGetValue(entry.FullPath, out var by) ? by : 0;
        var nameLeft = grid.Margin.Left + grid.ColumnDefinitions[0].ActualWidth + indent;

        if (nameLeft < rows.Offset.X) rows.Offset = rows.Offset.WithX(0);
    }

    /// <summary>
    /// One pane's heading scroller and rows scroller, and the subscriptions
    /// that keep them together.
    /// </summary>
    private sealed class Pair : IDisposable
    {
        private readonly ScrollViewer _heading;
        private readonly ListBox _list;
        private PaneViewModel? _pane;
        private bool _copying;
        private CancellationTokenSource? _fitting;

        public ScrollViewer? Rows { get; private set; }

        /// <summary>The heading's grid: the heading scroller's panel's child.</summary>
        public Grid? Grid => (_heading.Content as Panel)?.Children.OfType<Grid>().FirstOrDefault();

        public Pair(ScrollViewer heading, ListBox list)
        {
            _heading = heading;
            _list = list;

            Pairs.AddOrUpdate(list, this);
            Owners.AddOrUpdate(heading, this);

            heading.AttachedToVisualTree += OnAttached;
            heading.DetachedFromVisualTree += OnDetached;

            if (heading.IsAttachedToVisualTree()) Connect();
        }

        private static readonly ConditionalWeakTable<ScrollViewer, Pair> Owners = new();

        public static Pair? Of(ScrollViewer heading) => Owners.TryGetValue(heading, out var pair) ? pair : null;

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Connect();

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Disconnect();

        private void Connect()
        {
            Disconnect();

            _heading.PropertyChanged += OnHeadingChanged;
            _heading.DataContextChanged += OnContext;
            _list.TemplateApplied += OnTemplate;


            UseRows(_list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault());
            UsePane(_heading.DataContext as PaneViewModel);
        }

        private void Disconnect()
        {
            _heading.PropertyChanged -= OnHeadingChanged;
            _heading.DataContextChanged -= OnContext;
            _list.TemplateApplied -= OnTemplate;

            UseRows(null);
            UsePane(null);
        }

        /// <summary>A ListBox given a new template gets a new scroller; the
        /// pair follows it rather than holding on to the old one.</summary>
        private void OnTemplate(object? sender, TemplateAppliedEventArgs e)
            => UseRows(e.NameScope.Find<ScrollViewer>("PART_ScrollViewer")
                       ?? _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault());

        private void UseRows(ScrollViewer? rows)
        {
            if (Rows is not null) Rows.PropertyChanged -= OnRowsChanged;

            Rows = rows;

            if (rows is null) return;

            rows.PropertyChanged += OnRowsChanged;
            FromRows();
        }

        private void OnContext(object? sender, EventArgs e) => UsePane(_heading.DataContext as PaneViewModel);

        private void UsePane(PaneViewModel? pane)
        {
            if (_pane is not null)
            {
                _pane.PropertyChanged -= OnPaneChanged;
                _pane.ColumnFitRequested -= OnFitRequested;
            }

            StopFitting();

            _pane = pane;

            if (pane is null) return;

            pane.PropertyChanged += OnPaneChanged;
            pane.ColumnFitRequested += OnFitRequested;
        }

        private void OnRowsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // The extent and viewport too: an offset clamped by a narrower
            // extent is a change the heading has to follow as well.
            if (e.Property == ScrollViewer.OffsetProperty
                || e.Property == ScrollViewer.ExtentProperty
                || e.Property == ScrollViewer.ViewportProperty)
                FromRows();
        }

        private void OnHeadingChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.OffsetProperty) FromHeading();
            else if (e.Property == ScrollViewer.ExtentProperty) FromRows();
        }

        private void FromRows()
        {
            if (_copying || Rows is not { } rows) return;

            _copying = true;
            try
            {
                if (_heading.Offset.X != rows.Offset.X) _heading.Offset = new Vector(rows.Offset.X, 0);
                Pin(rows.Offset.X);
            }
            finally
            {
                _copying = false;
            }
        }

        private void FromHeading()
        {
            if (_copying || Rows is not { } rows) return;

            _copying = true;
            try
            {
                if (rows.Offset.X != _heading.Offset.X) rows.Offset = rows.Offset.WithX(_heading.Offset.X);
                Pin(rows.Offset.X);
            }
            finally
            {
                _copying = false;
            }
        }

        /// <summary>
        /// Moves the pinned parts by the rows' offset as the scroller draws it:
        /// rounded to a device pixel, or the 3-pixel selection bar sits up to
        /// half a pixel off the pane's edge, part-clipped and blurred.
        /// </summary>
        private void Pin(double x)
        {
            var scaling = TopLevel.GetTopLevel(_list)?.RenderScaling ?? 1.0;

            _list.SetValue(PinnedProperty, LayoutHelper.RoundLayoutValue(x, scaling));
        }

        private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
        {
            // **Back to the left in another folder.** Its rows are other rows,
            // read by their names first; the vertical offset goes back to the
            // top there already. A refresh, a sort or a folder opened in place
            // is the same folder, and keeps its place: a watcher must never
            // jerk the view sideways under somebody reading a column.
            if (e.PropertyName != nameof(PaneViewModel.CurrentPath)) return;

            StopFitting();

            if (Rows is { } rows && rows.Offset.X != 0) rows.Offset = rows.Offset.WithX(0);
        }

        private void OnFitRequested(object? sender, DetailsColumn? column)
        {
            if (_pane is not { } pane || Grid is not { } grid) return;

            StopFitting();
            _fitting = new CancellationTokenSource();

            ColumnFitter.Fit(pane, grid, _heading, _list, column, _fitting.Token);
        }

        private void StopFitting()
        {
            _fitting?.Cancel();
            _fitting?.Dispose();
            _fitting = null;
        }

        public void Dispose()
        {
            Disconnect();

            _heading.AttachedToVisualTree -= OnAttached;
            _heading.DetachedFromVisualTree -= OnDetached;

            Pairs.Remove(_list);
            Owners.Remove(_heading);
        }
    }
}
