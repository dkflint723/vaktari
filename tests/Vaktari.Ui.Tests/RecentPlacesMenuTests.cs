using System.ComponentModel;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The address bar's own history.
///
/// **Back and Forward were the only history the window offered, and both are
/// per-tab and per-walk.** A folder opened in another tab, or in a previous
/// run, was in neither of them — and going back and then somewhere else
/// discards the forward stack, so a place left five minutes ago could become
/// unreachable except by typing it out again.
///
/// The store that answers this has recorded every user-initiated folder
/// navigation since it was written; the only thing that ever read it was the
/// sidebar's virtual "Recent locations" listing, which is a whole pane away
/// from the bar you are standing at when you want it.
/// </summary>
public sealed class RecentPlacesMenuTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly IRecentStore? _storeBefore = PaneViewModel.Recents;
    private readonly Vaktari.Core.Settings.SettingsState _settingsBefore =
        Vaktari.Ui.Settings.AppSettings.Current;

    public override void Dispose()
    {
        base.Dispose();

        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);
        PaneViewModel.Recents = _storeBefore;

        GC.SuppressFinalize(this);
    }

    private static string In(params string[] parts)
        => Path.Combine([Path.GetTempPath(), .. parts]);

    /// <summary>A pane that has walked three folders, with a store behind
    /// it.</summary>
    private async Task<PaneViewModel> Walked()
    {
        PaneViewModel.Recents = new Remembering();

        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        foreach (var step in new[] { "one", "two", "three" })
            await pane.NavigateAsync(In(step));

        // **Drained, or every test below sees the walk's own notices.** Each
        // arrival is recorded off the UI thread and its notice is posted, so
        // three of them are still queued here — and a test that hooks
        // PropertyChanged and then runs the dispatcher catches those instead of
        // the one it asked about. Measured: without this, deleting the settings
        // subscription outright left
        // Turning_the_recording_off_takes_the_menu_with_it green.
        Dispatcher.UIThread.RunJobs();

        return pane;
    }

    /// <summary>Newest first, which is the order the store already keeps and
    /// the order a "take me back" list has to be in.</summary>
    [AvaloniaFact]
    public async Task The_address_bar_lists_where_you_have_been()
    {
        var pane = await Walked();

        Assert.Equal(["three", "two", "one"], pane.RecentPlaces.Select(p => p.Name));
        Assert.True(pane.HasRecentPlaces);
    }

    /// <summary>Each row carries its own command and goes where it says.</summary>
    [AvaloniaFact]
    public async Task A_row_goes_where_it_says()
    {
        var pane = await Walked();

        pane.RecentPlaces.Single(p => p.Name == "one").Open.Execute(null);

        await Task.Delay(60);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(In("one"), pane.CurrentPath);
    }

    /// <summary>
    /// Nothing remembered is no button. An empty dropdown teaches nothing and
    /// costs a click to find that out.
    /// </summary>
    [AvaloniaFact]
    public void Nothing_remembered_is_no_menu()
    {
        PaneViewModel.Recents = new Remembering();

        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        Assert.Empty(pane.RecentPlaces);
        Assert.False(pane.HasRecentPlaces);
    }

    /// <summary>Everything <paramref name="pane"/> announces from now on.</summary>
    private static List<string> Announcements(PaneViewModel pane)
    {
        var announced = new List<string>();

        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        return announced;
    }

    /// <summary>
    /// **The switch that stops folders being recorded has to stop them being
    /// offered.** Otherwise turning it off leaves a menu of everywhere you went
    /// before you turned it off, sitting on the most visible control in the
    /// window — which is the opposite of what the setting was ticked for.
    ///
    /// The announcement is asserted beside the value, and it is the half that
    /// bites. **Both properties are computed and read the setting fresh, so the
    /// value assert alone is satisfied by the way they are written** and would
    /// stay green with every notification in this change deleted — which is
    /// exactly the state that leaves the chevron on screen with its full menu
    /// until something else happens to redraw it.
    /// </summary>
    [AvaloniaFact]
    public async Task Turning_the_recording_off_takes_the_menu_with_it()
    {
        var pane = await Walked();

        Assert.NotEmpty(pane.RecentPlaces);

        var announced = Announcements(pane);

        var settings = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(settings with
        {
            General = settings.General with { RememberRecent = false },
        });

        Dispatcher.UIThread.RunJobs();

        Assert.Empty(pane.RecentPlaces);
        Assert.False(pane.HasRecentPlaces);

        Assert.Contains(nameof(PaneViewModel.HasRecentPlaces), announced);
        Assert.Contains(nameof(PaneViewModel.RecentPlaces), announced);
    }

    /// <summary>
    /// And so does emptying the store, which is the other thing the settings
    /// save does — ticking "forget recent places when I close" clears it on the
    /// way past.
    ///
    /// **Nothing in that path goes near a navigation.** The save applies the
    /// settings, empties the store and asks each tab to refresh; the refresh
    /// reloads the listing and never reaches the recording site, so a menu
    /// announced only from there described a store that had just been emptied
    /// as still full.
    /// </summary>
    [AvaloniaFact]
    public async Task Forgetting_everything_takes_the_menu_with_it()
    {
        var pane = await Walked();

        Assert.NotEmpty(pane.RecentPlaces);

        var announced = Announcements(pane);

        PaneViewModel.Recents!.ForgetAll();

        Dispatcher.UIThread.RunJobs();

        Assert.Empty(pane.RecentPlaces);
        Assert.False(pane.HasRecentPlaces);

        Assert.Contains(nameof(PaneViewModel.HasRecentPlaces), announced);
    }

    /// <summary>
    /// The menu is announced by the write, not by the load — through the
    /// store's own Changed, which the pane subscribes to.
    ///
    /// **NotifyNavigationState runs at the START of a load and the recording
    /// happens after it finishes**, so a notice raised there described the store
    /// as it was one navigation ago and the folder just walked into was never in
    /// the menu. This asserts the notice arrives with the folder already in the
    /// list rather than merely that a notice arrives.
    /// </summary>
    [AvaloniaFact]
    public async Task Arriving_somewhere_refreshes_the_menu_with_that_somewhere_in_it()
    {
        PaneViewModel.Recents = new Remembering();

        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        var announced = new List<int>();

        void Watch(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PaneViewModel.RecentPlaces))
                announced.Add(pane.RecentPlaces.Count);
        }

        pane.PropertyChanged += Watch;

        try
        {
            await pane.NavigateAsync(In("one"));

            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        }
        finally
        {
            pane.PropertyChanged -= Watch;
        }

        Assert.Contains(1, announced);
    }

    /// <summary>
    /// And the notice reaches the menu on the UI thread, whatever thread wrote
    /// the store.
    ///
    /// **The recording site sits after an await that resumes on a pool thread**
    /// — undo, redo and every operation that ends in a refresh take that route
    /// — and this list is a menu's ItemsSource.
    /// </summary>
    [AvaloniaFact]
    public async Task The_notice_arrives_on_the_ui_thread()
    {
        PaneViewModel.Recents = new Remembering();

        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        var offThread = 0;
        var announced = 0;

        void Watch(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PaneViewModel.RecentPlaces)) return;

            Interlocked.Increment(ref announced);

            if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref offThread);
        }

        pane.PropertyChanged += Watch;

        try
        {
            await Task.Run(async () => await pane.NavigateAsync(In("one")).ConfigureAwait(false));

            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            pane.PropertyChanged -= Watch;
        }

        Assert.True(announced > 0, "nothing announced the menu at all");
        Assert.Equal(0, offThread);
    }

    /// <summary>
    /// And a closed tab stops listening to both of them.
    ///
    /// **Both are statics that outlive every pane**: the settings are process
    /// wide and the store is a settable static on this class. A tab closed
    /// while the window stays open would otherwise answer every later save and
    /// every later write, forever, by posting to a dispatcher — which in these
    /// tests is a dispatcher whose headless session has already finished, and
    /// which surfaces as "a different thread owns it" in whatever test runs
    /// next.
    /// </summary>
    [AvaloniaFact]
    public async Task A_closed_tab_stops_listening()
    {
        var pane = await Walked();

        pane.Dispose();

        var announced = Announcements(pane);

        PaneViewModel.Recents!.Record(In("four"), RecentKind.Folder);

        var settings = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(settings with
        {
            General = settings.General with { RememberRecent = false },
        });

        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(nameof(PaneViewModel.HasRecentPlaces), announced);
    }

    // ---- and the button that opens it ---------------------------------------

    private static XElement Markup()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml")).Root!;

    /// <summary>
    /// The button is in the markup, hides itself when there is nothing to show,
    /// and its rows bind their own command — the same rule the two chevrons
    /// beside it follow, and for the same reason: a flyout is its own popup
    /// root, so a reach-out through $parent would resolve to the shell's active
    /// tab rather than this pane.
    /// </summary>
    [Fact]
    public void The_bar_carries_a_menu_of_recent_locations()
    {
        var flyout = Markup().Descendants(Avalonia + "MenuFlyout")
            .Single(f => (string?)f.Attribute("ItemsSource") == "{Binding ActiveTab.RecentPlaces}");

        var button = flyout.Ancestors(Avalonia + "Button").First();

        Assert.Equal("{Binding ActiveTab.HasRecentPlaces}", (string?)button.Attribute("IsVisible"));
        Assert.Equal("Recent locations", (string?)button.Attribute("AutomationProperties.Name"));

        var theme = flyout.Descendants(Avalonia + "ControlTheme").Single();

        Assert.Equal("vm:RecentPlace", (string?)theme.Attribute(X + "DataType"));

        Assert.Equal(
            "{Binding Open}",
            (string?)theme.Elements(Avalonia + "Setter")
                .Single(s => (string?)s.Attribute("Property") == "Command")
                .Attribute("Value"));
    }

    /// <summary>
    /// Bounded, so a long-lived store does not produce a flyout with a
    /// scrollbar of its own.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_is_bounded()
    {
        PaneViewModel.Recents = new Remembering();

        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        for (var i = 0; i < 20; i++)
            await pane.NavigateAsync(In("deep" + i));

        Assert.Equal(12, pane.RecentPlaces.Count);
    }

    /// <summary>
    /// A store that behaves like the real one: newest first, one entry per
    /// path, and it answers only for the kind that was asked for.
    ///
    /// **Every route that changes the lists raises Changed**, because
    /// JsonRecentStore does — Record, Forget and ForgetAll each end in one — and
    /// a double that announced fewer of them would let a reader hang on an
    /// event the real store never raises and pass.
    /// </summary>
    private sealed class Remembering : IRecentStore
    {
        private readonly List<RecentEntry> _entries = [];

        public void Record(string path, RecentKind kind)
        {
            _entries.RemoveAll(e => e.Path == path);
            _entries.Insert(0, new RecentEntry(path, kind, DateTimeOffset.Now));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count)
            => _entries.Where(e => e.Kind == kind).Take(count).ToList();

        public void Forget(string path)
        {
            if (_entries.RemoveAll(e => e.Path == path) > 0)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        public int Count => _entries.Count;

        public int ForgetAll()
        {
            var had = _entries.Count;

            if (had == 0) return 0;

            _entries.Clear();
            Changed?.Invoke(this, EventArgs.Empty);

            return had;
        }

        public event EventHandler? Changed;
    }

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
}
