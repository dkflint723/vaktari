using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging a tab into another slot.
///
/// **Tabs stayed in the order they were opened in.** Pressing a tab and
/// dragging did nothing at all — no move on the group, nothing in the window
/// handling a press-and-drag on the strip, no menu item and no key gesture —
/// while Explorer, Dolphin and every browser reorder by dragging. The order was
/// already persisted and already restored; nothing could alter it.
/// </summary>
public sealed class TabReorderTests : OwnedViewModels
{
    private sealed class Inert : IFileSystemProvider
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

    private PaneGroupViewModel Group(int tabs)
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));

        for (var i = 0; i < tabs; i++)
        {
            var tab = Own(new PaneViewModel(new Inert()));
            tab.CurrentPath = Path.Combine(Path.GetTempPath(), "tab" + i);
            group.Tabs.Add(tab);
        }

        group.ActiveTab = group.Tabs[0];

        return group;
    }

    // ---- the rule ----------------------------------------------------------

    /// <summary>Three 100-wide tabs.</summary>
    private static readonly double[] Even = [50, 150, 250];

    /// <summary>
    /// The neighbour's MIDDLE, not its near edge — one pixel either side of it
    /// and the answer changes.
    /// </summary>
    [Fact]
    public void A_tab_does_not_move_until_it_passes_the_next_ones_middle()
    {
        Assert.Equal(0, DragReorder.SlotFor(149, Even, 0));
        Assert.Equal(1, DragReorder.SlotFor(151, Even, 0));
    }

    [Fact]
    public void Dragging_left_is_the_same_rule_backwards()
    {
        Assert.Equal(2, DragReorder.SlotFor(151, Even, 2));
        Assert.Equal(1, DragReorder.SlotFor(149, Even, 2));
        Assert.Equal(0, DragReorder.SlotFor(49, Even, 2));
    }

    [Fact]
    public void A_tab_cannot_be_dragged_off_either_end()
    {
        Assert.Equal(0, DragReorder.SlotFor(-5000, Even, 1));
        Assert.Equal(2, DragReorder.SlotFor(5000, Even, 1));
    }

    /// <summary>
    /// The load-bearing one, and the reason the rule is centre-against-centre.
    /// A 200-wide tab dragged past a 60-wide one: comparing against the box the
    /// pointer is INSIDE would put the wide tab back under the pointer and flip
    /// the answer again on the very next frame. Two calls, because one call
    /// cannot show a flip-flop.
    /// </summary>
    [Fact]
    public void Dragging_past_a_narrow_tab_does_not_come_straight_back()
    {
        Assert.Equal(0, DragReorder.SlotFor(99, [100, 230], 1));

        // The layout that move produces, fed back in.
        Assert.Equal(0, DragReorder.SlotFor(99, [30, 160], 0));
    }

    // ---- the move ----------------------------------------------------------

    [AvaloniaFact]
    public void Moving_a_tab_changes_the_order_and_not_which_one_is_active()
    {
        var group = Group(3);
        var moved = group.Tabs[0];
        var was = group.ActiveTab;
        var paths = group.Tabs.Select(t => t.CurrentPath).ToList();

        group.MoveTab(moved, 2);

        Assert.Equal([paths[1], paths[2], paths[0]], group.Tabs.Select(t => t.CurrentPath));
        Assert.Same(was, group.ActiveTab);
    }

    /// <summary>
    /// A drag calls this on every pointer move; only the ones that cross a
    /// boundary are a layout change, or the session is marked dirty by a wobble.
    /// </summary>
    [AvaloniaFact]
    public void A_reorder_marks_the_session_dirty_and_a_no_op_does_not()
    {
        var group = Group(3);
        var moves = 0;
        group.LayoutChanged += (_, _) => moves++;

        group.MoveTab(group.Tabs[0], 2);
        Assert.Equal(1, moves);

        group.MoveTab(group.Tabs[1], 1);
        Assert.Equal(1, moves);

        group.MoveTab(Own(new PaneViewModel(new Inert())), 0);
        Assert.Equal(1, moves);
    }

    /// <summary>The reorder has to survive a restart, which is what the
    /// LayoutChanged raise is for.</summary>
    [AvaloniaFact]
    public void The_new_order_is_what_gets_saved()
    {
        var group = Group(3);
        var moved = group.Tabs[2];
        group.ActiveTab = moved;

        group.MoveTab(moved, 0);

        var state = group.ToPaneState();

        Assert.Equal(moved.CurrentPath, state.Tabs[0].Path);
        Assert.Equal(0, state.ActiveTabIndex);
    }

    /// <summary>
    /// Against widths Avalonia actually produced, and against the strip's own
    /// selection: ObservableCollection.Move must not drop it, which is the
    /// specific way a remove-then-add would blank the listing underneath.
    /// </summary>
    [AvaloniaFact]
    public void A_reordered_strip_keeps_the_tab_you_grabbed_selected()
    {
        var group = Group(3);
        var strip = new TabStrip { ItemsSource = group.Tabs, DataContext = group };

        // The markup's own arrangement: SelectedItem="{Binding ActiveTab}",
        // two-way. Without the binding this test cannot see the trap it exists
        // for -- the strip writing its stale index back into the group.
        strip.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new global::Avalonia.Data.Binding("ActiveTab")
            {
                Mode = global::Avalonia.Data.BindingMode.TwoWay,
            });
        var window = new Window { Content = strip, Width = 600, Height = 120 };

        window.Show();
        window.Measure(new Size(600, 120));
        window.Arrange(new Rect(0, 0, 600, 120));

        var grabbed = group.Tabs[0];
        group.ActiveTab = grabbed;

        var middles = new List<double>();

        for (var i = 0; i < group.Tabs.Count; i++)
        {
            var box = Assert.IsType<TabStripItem>(strip.ContainerFromIndex(i));
            var at = box.TranslatePoint(default, window);

            Assert.NotNull(at);
            middles.Add(at.Value.X + box.Bounds.Width / 2);
        }

        group.MoveTab(grabbed, DragReorder.SlotFor(middles[2] + 1, middles, 0));

        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, group.Tabs.IndexOf(grabbed));

        // Both ends. Avalonia keeps the INDEX across a Move, so without the
        // restore in MoveTab the strip selects whatever slid into slot 0 and
        // writes it straight back into the group.
        Assert.Same(grabbed, group.ActiveTab);
        Assert.Same(grabbed, strip.SelectedItem);

        window.Close();
    }

    // ---- that the rule is reachable ---------------------------------------

    private static string Window(string declaration)
        => RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"), declaration);

    [Fact]
    public void A_press_on_a_tab_arms_a_reorder()
    {
        Assert.Contains(
            "ArmTabDrag(e, properties)",
            Window("private void OnPointerPressedAnywhere(object? sender, Avalonia.Input.PointerPressedEventArgs e)"));

        Assert.Contains(
            "TabStripItem",
            Window("private void ArmTabDrag(PointerPressedEventArgs e, PointerPointProperties properties)"));
    }

    /// <summary>
    /// A tab arms no <c>_dragSource</c> — it is not a row — so a block placed
    /// after the line that gives up on the file drag would never run at all.
    /// This is why the tab drag did nothing.
    /// </summary>
    [Fact]
    public void A_tab_drag_is_answered_before_the_file_drag_gives_up()
    {
        var body = Window("private void OnPointerMovedAnywhere(object? sender, PointerEventArgs e)");

        var tab = body.IndexOf("_tabDrag is not null", StringComparison.Ordinal);
        var givesUp = body.IndexOf("if (_dragging || _dragSource is null) return;", StringComparison.Ordinal);

        Assert.True(tab > 0, "the moved handler never looks at a tab drag");
        Assert.True(tab < givesUp, "the tab drag is answered after the file drag has already returned");
    }

    /// <summary>Or the strip goes on reordering after the button is up.</summary>
    [Fact]
    public void A_release_ends_the_tab_drag()
        => Assert.Contains("EndTabDrag()", Window("public MainWindow()"));

    /// <summary>
    /// The other half of this finding, already fixed and until now untested: a
    /// press that is not on a ROW must not arm the file drag, or a six-pixel
    /// twitch on the tab strip drags the selection and a drop moves real files.
    /// </summary>
    [Fact]
    public void A_press_that_is_not_on_a_row_still_arms_no_file_drag()
        => Assert.Contains(
            // The LEFT arm specifically -- the right-button arm has had EntryAt
            // all along, and asserting the bare call matches that one instead.
            "(properties.IsLeftButtonPressed && _bandList is null && EntryAt(e.Source) is not null)",
            Window("private void OnPointerPressedAnywhere(object? sender, Avalonia.Input.PointerPressedEventArgs e)"));
}
