using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which gesture puts the caret in the filter box.
///
/// **The box took the keyboard every time it APPEARED, and a tab switch is an
/// appearance.** One field lives in the pane group's chrome with its visibility
/// bound to the ACTIVE tab's flag, so coming back to a tab that had the filter
/// open flipped it from hidden to shown — and the behaviour that focuses on
/// that edge answered it exactly as it answered Ctrl+I. An ordinary Ctrl+Tab
/// left the arrow keys, Enter, Delete and type-ahead dead in a listing that
/// looked ready for all four.
/// </summary>
public sealed class FilterFocusTests : OwnedViewModels
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

    private sealed record Rig(Window Window, PaneGroupViewModel Group, TextBox Listing, TextBox Filter);

    /// <summary>
    /// The real arrangement: ONE filter box in the group's chrome, bound
    /// through ActiveTab, beside something else that can hold the keyboard.
    /// The bug only exists because the box is shared and the binding re-points.
    /// </summary>
    private Rig Build()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert())));

        for (var i = 0; i < 2; i++)
        {
            var tab = Own(new PaneViewModel(new Inert()));
            tab.CurrentPath = Path.Combine(Path.GetTempPath(), "tab" + i);
            group.Tabs.Add(tab);
        }

        group.ActiveTab = group.Tabs[0];

        var listing = new TextBox();
        var filter = new TextBox();

        filter.Bind(Visual.IsVisibleProperty, new Binding("ActiveTab.IsFilterVisible"));
        filter.Bind(FocusBehavior.FocusWhenProperty, new Binding("ActiveTab.FocusFilter"));

        var panel = new StackPanel { DataContext = group };
        panel.Children.Add(listing);
        panel.Children.Add(filter);

        var window = new Window { Content = panel, Width = 600, Height = 200 };

        window.Show();
        window.Measure(new Size(600, 200));
        window.Arrange(new Rect(0, 0, 600, 200));

        return new Rig(window, group, listing, filter);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
    }

    /// <summary>Asking for the box still puts the caret in it — the gesture the
    /// whole thing exists for.</summary>
    [AvaloniaFact]
    public void Asking_for_the_filter_puts_the_caret_in_it()
    {
        var rig = Build();

        try
        {
            rig.Listing.Focus();
            Settle();

            rig.Group.ActiveTab!.ToggleFilter();
            Settle();

            Assert.True(rig.Filter.IsFocused);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// The finding. Open the filter on one tab, go away, come back: the box
    /// reappears, and the keyboard stays where the person left it.
    /// </summary>
    [AvaloniaFact]
    public void But_coming_back_to_a_tab_that_had_it_open_does_not()
    {
        var rig = Build();

        try
        {
            rig.Group.ActiveTab!.ToggleFilter();
            Settle();

            // Away, and back. The box hides and shows, which is the edge the
            // old behaviour answered.
            rig.Group.ActiveTab = rig.Group.Tabs[1];
            Settle();

            Assert.False(rig.Filter.IsVisible);

            rig.Listing.Focus();
            Settle();

            rig.Group.ActiveTab = rig.Group.Tabs[0];
            Settle();

            Assert.True(rig.Filter.IsVisible);
            Assert.False(rig.Filter.IsFocused);
            Assert.True(rig.Listing.IsFocused);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// **The single most load-bearing assertion here.** The two pulses this
    /// otherwise copies end LATCHED TRUE, and they can — they bind straight off
    /// the sidebar, which does not re-point. This one binds through ActiveTab,
    /// so a value left true is pushed onto the control as a fresh false-to-true
    /// edge every time the active tab changes, which is the bug wearing a
    /// different hat. One edge per gesture, and the flag ends false.
    /// </summary>
    [AvaloniaFact]
    public void The_signal_is_a_pulse_and_not_a_state()
    {
        var pane = Own(new PaneViewModel(new Inert()));
        var edges = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.FocusFilter) && pane.FocusFilter) edges++;
        };

        pane.ToggleFilter();

        Assert.Equal(1, edges);
        Assert.False(pane.FocusFilter,
                     "the signal is left latched true, so it re-fires on every tab switch");
    }

    /// <summary>And closing the filter asks for nothing at all.</summary>
    [AvaloniaFact]
    public void Closing_it_asks_for_no_focus()
    {
        var pane = Own(new PaneViewModel(new Inert()));
        var edges = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.FocusFilter) && pane.FocusFilter) edges++;
        };

        pane.ToggleFilter();
        pane.ToggleFilter();

        Assert.Equal(1, edges);
        Assert.False(pane.IsFilterVisible);
    }

    /// <summary>
    /// The markup half: the box must follow the SIGNAL, not its own
    /// appearance. The address bar above it keeps FocusOnVisible, which is
    /// right there — it is created by the gesture rather than revealed by one.
    /// </summary>
    [Fact]
    public void The_box_follows_the_gesture_rather_than_its_own_appearance()
    {
        var filter = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "TextBox")
            .Single(t => (string?)t.Attribute("Text") == "{Binding ActiveTab.FilterText}");

        var focusWhen = filter.Attributes()
            .SingleOrDefault(a => a.Name.LocalName == "FocusBehavior.FocusWhen");

        Assert.NotNull(focusWhen);
        Assert.Equal("{Binding ActiveTab.FocusFilter}", focusWhen!.Value);

        Assert.DoesNotContain(filter.Attributes(),
                              a => a.Name.LocalName == "FocusBehavior.FocusOnVisible");
    }
}
