using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The two buttons under the thumb, which navigate back and forward.
///
/// Two separate things are pinned here, and the second is the one that would
/// actually break.
///
/// **Which button means what.** Getting the pair the wrong way round produces
/// an application that works perfectly and feels wrong, and nothing in a build
/// would say a word. The nearer button is back, everywhere.
///
/// **That the press reaches us at all.** A side button has to arrive as
/// PointerUpdateKind.XButton1Pressed on the tunnel, before a listing treats the
/// press as a selection gesture. That is Avalonia's behaviour rather than this
/// application's — precisely the kind of thing that is true until a framework
/// upgrade quietly makes it false, which is the same reason
/// <see cref="RightClickSelectionTests"/> exists.
/// </summary>
public sealed class SideButtonNavigationTests : OwnedViewModels
{
    private MainWindow? _real;

    public override void Dispose()
    {
        // A shown window flushes the session on close, and one left open is
        // torn down later on whatever thread xunit is on — which surfaces as a
        // threading failure in some unrelated test that merely ran afterwards.
        _real?.Close();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(PointerUpdateKind.XButton1Pressed, SideButtonAction.Back)]
    [InlineData(PointerUpdateKind.XButton2Pressed, SideButtonAction.Forward)]
    [InlineData(PointerUpdateKind.LeftButtonPressed, SideButtonAction.None)]
    [InlineData(PointerUpdateKind.MiddleButtonPressed, SideButtonAction.None)]
    [InlineData(PointerUpdateKind.RightButtonPressed, SideButtonAction.None)]
    public void The_nearer_button_goes_back(PointerUpdateKind kind, SideButtonAction expected)
    {
        Assert.Equal(expected, SideButtons.For(kind));
    }

    /// <summary>
    /// Built the same way <see cref="RightClickSelectionTests"/> builds its
    /// window, and for the same reason: a real listing under the pointer is the
    /// thing that would otherwise swallow the press.
    /// </summary>
    private static (Window Window, ListBox List) Build()
    {
        var list = new ListBox
        {
            ItemsSource = new[] { "one", "two", "three" },
            Width = 200,
            Height = 300,
        };

        var window = new Window { Content = list, Width = 300, Height = 400 };
        window.Show();

        window.Measure(new Size(300, 400));
        window.Arrange(new Rect(0, 0, 300, 400));

        return (window, list);
    }

    [AvaloniaTheory]
    [InlineData(MouseButton.XButton1, SideButtonAction.Back)]
    [InlineData(MouseButton.XButton2, SideButtonAction.Forward)]
    public void A_side_button_arrives_on_the_tunnel_as_a_navigation(
        MouseButton button, SideButtonAction expected)
    {
        var (window, list) = Build();

        var seen = SideButtonAction.None;

        // The tunnel, which is where the real handler sits — a listing treats a
        // press as a selection gesture, so seeing it first is what keeps a side
        // button from moving the selection as well as the folder.
        window.AddHandler(
            InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                seen = SideButtons.For(e.GetCurrentPoint(window).Properties.PointerUpdateKind);

                if (seen is not SideButtonAction.None) e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        var container = (Control)list.ContainerFromIndex(1)!;
        var point = container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)
            ?? new Point(0, 0);

        window.MouseDown(point, button);
        window.MouseUp(point, button);

        Assert.Equal(expected, seen);

        // And the row it landed on is not selected: handling it on the way down
        // is what stops a navigation button from also moving the selection.
        Assert.Null(list.SelectedItem);

        window.Close();
    }

    /// <summary>An ordinary click still selects, so the guard above is not
    /// swallowing everything that reaches it.</summary>
    [AvaloniaFact]
    public void An_ordinary_click_is_left_alone()
    {
        var (window, list) = Build();

        window.AddHandler(
            InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                if (SideButtons.For(e.GetCurrentPoint(window).Properties.PointerUpdateKind)
                    is not SideButtonAction.None)
                    e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        var container = (Control)list.ContainerFromIndex(1)!;
        var point = container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)
            ?? new Point(0, 0);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Equal("two", list.SelectedItem);

        window.Close();
    }

    // ---- which pane the button drives -------------------------------------

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

    private static PaneViewModel? NavigationTargetAt(object? source)
        => (PaneViewModel?)typeof(MainWindow)
            .GetMethod("NavigationTargetAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source]);

    private (Window Window, TabStrip Strip, PaneGroupViewModel Group) Strip(int tabs)
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));

        for (var i = 0; i < tabs; i++)
        {
            var tab = Own(new PaneViewModel(new Inert()));
            tab.CurrentPath = Path.Combine(Path.GetTempPath(), "tab" + i);
            group.Tabs.Add(tab);
        }

        group.ActiveTab = group.Tabs[0];

        var strip = new TabStrip { ItemsSource = group.Tabs, DataContext = group };
        var window = new Window { Content = strip, Width = 600, Height = 120 };

        window.Show();
        window.Measure(new Size(600, 120));
        window.Arrange(new Rect(0, 0, 600, 120));

        return (window, strip, group);
    }

    /// <summary>
    /// **The side buttons navigated a tab nobody could see.** A tab header
    /// carries its own pane as its data context, so walking up from the press
    /// answered with the tab that was pointed AT rather than the listing on
    /// screen. Pressing back while aiming at the third tab's label rewound the
    /// third tab: nothing visible moved and nothing said anything.
    /// </summary>
    [AvaloniaFact]
    public void A_press_on_another_tabs_header_navigates_the_visible_pane()
    {
        var (window, strip, group) = Strip(3);

        try
        {
            var header = Assert.IsType<TabStripItem>(strip.ContainerFromIndex(2));

            // The real press lands INSIDE the header — on the label, not on the
            // item — so it is the walk that has to recognise the tab.
            var inside = header.GetVisualDescendants().OfType<Control>().FirstOrDefault() ?? header;

            Assert.Same(group.ActiveTab, NavigationTargetAt(inside));
            Assert.NotSame(group.Tabs[2], NavigationTargetAt(inside));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And the rule it must not swallow: point at a half of a split, press
    /// back, that half moves. An over-correction to "always the group's active
    /// tab" would fail this.
    ///
    /// The two panes are deliberately different objects — in the running
    /// application only the active listing is hit-testable, so this pins the
    /// rule rather than a scenario.
    /// </summary>
    [AvaloniaFact]
    public void A_press_in_a_listing_still_navigates_the_pane_it_landed_in()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));
        var visible = Own(new PaneViewModel(new Inert()));
        var aimed = Own(new PaneViewModel(new Inert()));

        group.Tabs.Add(visible);
        group.ActiveTab = visible;

        var listing = new Border { DataContext = aimed };
        var side = new Panel { DataContext = group, Children = { listing } };
        var window = new Window { Content = side, Width = 300, Height = 200 };

        window.Show();
        window.Measure(new Size(300, 200));
        window.Arrange(new Rect(0, 0, 300, 200));

        try
        {
            Assert.Same(aimed, NavigationTargetAt(listing));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- where each button actually goes ------------------------------------

    /// <summary>
    /// Three real folders arranged so Back and Up cannot give the same answer:
    /// the pane walks to <c>far</c> and then to <c>near/deep</c>, so Back is
    /// <c>far</c> and Up is <c>near</c>. Copied from
    /// <see cref="BackspacePreferenceTests"/>, which needs the same shape for
    /// the same reason.
    /// </summary>
    private sealed record Tree(string Root, string Far, string Near, string Deep) : IDisposable
    {
        public static Tree Make()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "vaktari-sidebutton-" + Guid.NewGuid().ToString("N")[..8]);

            var tree = new Tree(root,
                                Path.Combine(root, "far"),
                                Path.Combine(root, "near"),
                                Path.Combine(root, "near", "deep"));

            Directory.CreateDirectory(tree.Far);
            Directory.CreateDirectory(tree.Deep);

            return tree;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// A real window walked to <see cref="Tree.Deep"/> with <see cref="Tree.Far"/>
    /// behind it, so that both buttons have somewhere to go.
    /// </summary>
    private async Task<(MainWindow Window, PaneViewModel Pane)> Walked(Tree tree)
    {
        UseSearch(PaneViewModel.Search);

        var window = _real = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;

        await pane.NavigateAsync(tree.Far);
        await pane.NavigateAsync(tree.Deep);
        Settle();

        // Nothing is pressed until the pane is provably where this test put it:
        // a real window opens on whatever folders the session restored, and the
        // assertions below would otherwise be about that folder's history.
        Assert.Equal(tree.Deep, pane.CurrentPath);
        Assert.True(pane.CanGoBack, "there is no history for Back to walk");
        Assert.True(pane.CanGoUp, "there is no parent for Up to reach");

        return (window, pane);
    }

    /// <summary>
    /// **The back button went UP one folder for four commits.** fee6393 made
    /// BACKSPACE a choice between Back and Up, and swept this line up with it —
    /// so the button under the thumb, which is unlabelled precisely because
    /// every browser and every file manager agrees what it does, quietly did
    /// the other thing. Nothing caught it: the theory above pins which button
    /// maps to which <see cref="SideButtonAction"/>, and that mapping stayed
    /// right the whole time. What was wrong was what the action then DID.
    ///
    /// The two coincide in an ordinary walk straight down a tree, which is why
    /// it was not noticed by hand either. This walks sideways first, so a pane
    /// that goes up lands somewhere Back never was.
    ///
    /// Pressed rather than called, for the reason
    /// <see cref="BackspacePreferenceTests"/> records: invoking GoBackAsync
    /// directly would pass with the whole branch deleted.
    /// </summary>
    [AvaloniaFact]
    public async Task The_back_button_goes_back_rather_than_up()
    {
        using var tree = Tree.Make();

        var (window, pane) = await Walked(tree);

        window.MouseDown(new Point(400, 300), MouseButton.XButton1);
        window.MouseUp(new Point(400, 300), MouseButton.XButton1);
        Settle();

        Assert.Equal(tree.Far, pane.CurrentPath);
    }

    /// <summary>
    /// And the far button forward, which is the half that was always right —
    /// here so that a fix which simply swapped the two branches cannot pass.
    /// </summary>
    [AvaloniaFact]
    public async Task The_far_button_goes_forward()
    {
        using var tree = Tree.Make();

        var (window, pane) = await Walked(tree);

        await pane.GoBackAsync();
        Settle();
        Assert.Equal(tree.Far, pane.CurrentPath);

        window.MouseDown(new Point(400, 300), MouseButton.XButton2);
        window.MouseUp(new Point(400, 300), MouseButton.XButton2);
        Settle();

        Assert.Equal(tree.Deep, pane.CurrentPath);
    }

    /// <summary>
    /// That the handler actually asks. The tests above pass on the helper alone
    /// even if nothing calls it, and the ordering is what stops this matching a
    /// call in some other branch.
    /// </summary>
    [Fact]
    public void The_press_handler_asks_which_pane_before_navigating()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnPointerPressedAnywhere(object? sender, Avalonia.Input.PointerPressedEventArgs e)");

        var side = body.IndexOf("Input.SideButtons.For(", StringComparison.Ordinal);
        var target = body.IndexOf("NavigationTargetAt(e.Source)", StringComparison.Ordinal);
        var middle = body.IndexOf("PointerUpdateKind.MiddleButtonPressed", StringComparison.Ordinal);

        Assert.True(side > 0, "the side buttons are not handled the way this test looks for them");
        Assert.True(middle > side, "the middle-button branch has moved above the side buttons");
        Assert.True(target > side && target < middle,
            "the side-button branch does not ask NavigationTargetAt, so a press on a tab "
            + "header navigates a tab nobody can see");
    }
}
