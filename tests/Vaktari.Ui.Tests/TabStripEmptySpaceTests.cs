using System.Reflection;
using System.Xml.Linq;
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
/// Double-clicking the blank half of the tab strip.
///
/// **Nothing happened there.** Explorer, Dolphin and every browser open a tab
/// on that gesture; here it fell through to the row walk, found no file and
/// stopped — with the "+" itself scrolled out of reach behind a dozen tabs,
/// which is exactly when the blank space is aimed at.
///
/// The markup half is load-bearing: a ScrollViewer with no Background is
/// invisible to the pointer, so the press never reached the strip at all.
///
/// Nothing here drives OnDoubleTapped end to end — no test in this suite
/// constructs a MainWindow — so the wiring is pinned structurally, which is the
/// house trade for handler-ordering rules. If the gesture ever stopped reaching
/// the handler because something upstream marked it handled, these would stay
/// green.
/// </summary>
public sealed class TabStripEmptySpaceTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

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

    private static PaneGroupViewModel? EmptySpaceAt(object? source)
        => (PaneGroupViewModel?)typeof(MainWindow)
            .GetMethod("TabStripEmptySpaceAt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source]);

    /// <summary>The real arrangement: the strip's own scroller, classed and
    /// painted, with a tab and a "+" inside it.</summary>
    private (Window Window, ScrollViewer Strip, TabStrip Tabs, Button Plus, PaneGroupViewModel Group) Build()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));
        var tab = Own(new PaneViewModel(new Inert()));
        tab.CurrentPath = Path.GetTempPath();
        group.Tabs.Add(tab);
        group.ActiveTab = tab;

        var tabs = new TabStrip { ItemsSource = group.Tabs };
        var plus = new Button { Content = "+" };

        var row = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
        row.Children.Add(tabs);
        row.Children.Add(plus);

        var strip = new ScrollViewer
        {
            Classes = { "tab-space" },
            Background = global::Avalonia.Media.Brushes.Transparent,
            DataContext = group,
            Content = row,
        };

        var window = new Window { Content = strip, Width = 600, Height = 60 };

        window.Show();
        window.Measure(new Size(600, 60));
        window.Arrange(new Rect(0, 0, 600, 60));

        return (window, strip, tabs, plus, group);
    }

    [AvaloniaFact]
    public void The_blank_part_of_the_strip_is_the_group_that_owns_it()
    {
        var rig = Build();

        try
        {
            Assert.Same(rig.Group, EmptySpaceAt(rig.Strip));
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>A tab is not empty space — that gesture already means
    /// something.</summary>
    [AvaloniaFact]
    public void A_tab_is_not_empty_space()
    {
        var rig = Build();

        try
        {
            var header = Assert.IsType<TabStripItem>(rig.Tabs.ContainerFromIndex(0));

            Assert.Null(EmptySpaceAt(header));
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// Nor is the "+", which already opens a tab — double-clicking it would
    /// otherwise open two.
    /// </summary>
    [AvaloniaFact]
    public void The_plus_button_is_not_empty_space()
    {
        var rig = Build();

        try
        {
            Assert.Null(EmptySpaceAt(rig.Plus));
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>Scrolling is not an opening gesture — the same refusal the
    /// listing's empty-space walk makes.</summary>
    [AvaloniaFact]
    public void Nor_is_the_scrollbar()
    {
        var rig = Build();

        try
        {
            var bar = new ScrollBar();
            ((StackPanel)rig.Strip.Content!).Children.Add(bar);

            Assert.Null(EmptySpaceAt(bar));
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>Some other scroller entirely is not the tab strip.</summary>
    [AvaloniaFact]
    public void And_neither_is_a_scroller_that_is_not_the_strip()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));
        var other = new ScrollViewer { DataContext = group };

        Assert.Null(EmptySpaceAt(other));
    }

    // ---- the markup half ---------------------------------------------------

    /// <summary>
    /// Without a Background the scroller is invisible to the pointer and the
    /// press falls through to the chrome behind it — so the class alone buys
    /// nothing.
    /// </summary>
    [Fact]
    public void The_strip_is_painted_so_the_gap_can_be_hit_at_all()
    {
        var strip = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "ScrollViewer")
            .Single(v => ((string?)v.Attribute("Classes"))?.Contains("tab-space", StringComparison.Ordinal) == true);

        Assert.Equal("Transparent", (string?)strip.Attribute("Background"));

        // And it is the strip's own scroller, not some other one.
        Assert.Equal("OnTabStripScrollChanged", (string?)strip.Attribute("ScrollChanged"));
    }

    /// <summary>
    /// Asked BEFORE the single-click preference is read. That preference
    /// governs how a FILE is opened; reading it first would take this gesture
    /// away from everyone who opens files with one click, and the "+" beside
    /// the strip does not change meaning with that setting either.
    /// </summary>
    [Fact]
    public void The_strip_is_asked_before_the_single_click_preference()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnDoubleTapped(object? sender, TappedEventArgs e)");

        var strip = body.IndexOf("TabStripEmptySpaceAt(e.Source)", StringComparison.Ordinal);
        var singleClick = body.IndexOf("if (OpensOnSingleClick) return;", StringComparison.Ordinal);

        Assert.True(strip > 0, "the double-click handler never looks at the tab strip");
        Assert.True(singleClick > 0, "the single-click branch has moved or gone");
        Assert.True(strip < singleClick,
                    "the strip is asked after the single-click preference, so the gesture is "
                    + "unavailable to anyone who opens files with one click");
    }
}
