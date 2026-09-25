using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a tab session restore left standing does when its folder has gone.
///
/// **The probe was written, implemented on both providers, and called from
/// nowhere.** IsReachableAsync said on itself that lazy session restore used it
/// to mark a tab dead rather than hang on it, and no code in the tree did:
/// RefreshIfUnloaded and the activate-a-restored-tab handler both went straight
/// into LoadAsync. A restored tab pointed at a share whose server had gone away
/// therefore entered the listing and stayed in it, with nothing on screen
/// separating "still reading" from "never going to work", until the enumeration
/// itself failed. How long that takes is the providers' own comments to state;
/// what is measured here is that the pane had no answer of its own in the
/// meantime.
/// </summary>
public sealed class RestoredTabReachabilityTests : OwnedViewModels
{
    private PaneViewModel Restored(Share fs, string path)
    {
        var pane = Own(new PaneViewModel(fs) { ViewportWidth = 1400 });

        pane.RestoreFrom(new TabState { Path = path });

        return pane;
    }

    /// <summary>A dead host, spelled so no platform can accidentally have
    /// one.</summary>
    private const string Gone = "//vaktari-no-such-host/share";

    private static async Task Drain()
    {
        for (var i = 0; i < 60; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    // ---- the tab is marked dead rather than left loading ---------------------

    /// <summary>
    /// Putting back the last closed tab goes through RefreshIfUnloaded, which
    /// is one of the two doors into a restored tab's first load.
    /// </summary>
    [AvaloniaFact]
    public void A_restored_tab_whose_folder_does_not_answer_says_so()
    {
        var fs = new Share(answer: false);
        var pane = Restored(fs, Gone);

        pane.RefreshIfUnloaded();

        Assert.Equal(1, fs.Probes);

        // The whole point: it never went near the listing.
        Assert.Equal(0, fs.Listings);

        Assert.True(pane.HasLoadError);
        Assert.Equal("that folder could not be reached", pane.LoadError);
        Assert.Equal("that folder could not be reached", pane.Status);

        // And it is not still claiming to be reading, which is the state this
        // whole change exists to get out of.
        Assert.False(pane.IsLoading);
        Assert.False(pane.IsLoaded);
    }

    /// <summary>
    /// The other door: a restored tab that has never loaded, clicked for the
    /// first time.
    /// </summary>
    [AvaloniaFact]
    public void Activating_a_restored_tab_whose_folder_does_not_answer_says_so()
    {
        var fs = new Share(answer: false);
        var pane = Restored(fs, Gone);

        pane.IsActive = true;

        Assert.Equal(1, fs.Probes);
        Assert.Equal(0, fs.Listings);
        Assert.Equal("that folder could not be reached", pane.LoadError);
    }

    /// <summary>
    /// The other half of the same rule: a folder that DOES answer is still
    /// listed, and listed once. The failure mode of wiring a probe in front of
    /// a load is that nothing loads any more, and every other test in the suite
    /// uses a double that answers true, so none of them would notice.
    /// </summary>
    [AvaloniaFact]
    public async Task A_restored_tab_whose_folder_answers_still_lists_it()
    {
        var fs = new Share(answer: true);
        var pane = Restored(fs, Path.GetTempPath());

        pane.RefreshIfUnloaded();
        await Drain();

        Assert.Equal(1, fs.Probes);
        Assert.Equal(1, fs.Listings);
        Assert.True(pane.IsLoaded);
        Assert.Equal("", pane.LoadError);
        Assert.Contains(pane.Entries, e => e.Name == "a.txt");
    }

    // ---- what the probe is allowed to cost ----------------------------------

    /// <summary>
    /// **The timeout is the entire feature.** An unbounded probe is the hang it
    /// was added to replace, one call earlier — Directory.Exists on a dead UNC
    /// path blocks on the redirector's own timeout and ignores cancellation,
    /// which is why both providers put it on the pool and abandon it.
    /// </summary>
    [AvaloniaFact]
    public void The_probe_is_given_a_bounded_time_to_answer_in()
    {
        var fs = new Share(answer: false);
        var pane = Restored(fs, Gone);

        pane.RefreshIfUnloaded();

        Assert.True(fs.LastTimeout > TimeSpan.Zero,
                    $"the probe was given {fs.LastTimeout}, which is not a timeout");

        Assert.True(fs.LastTimeout <= TimeSpan.FromSeconds(10),
                    $"the probe was given {fs.LastTimeout}, which a person will not wait");
    }

    // ---- what must not be probed --------------------------------------------

    /// <summary>
    /// **Directory.Exists("vaktari:trash") is false.** A virtual listing is not
    /// a folder, so probing one would report the bin, This PC, either recent
    /// listing and every saved search as unreachable — a restored bin tab would
    /// never open again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_restored_virtual_tab_is_never_probed()
    {
        var fs = new Share(answer: false);
        var pane = Restored(fs, VirtualPaths.Trash);

        pane.RefreshIfUnloaded();
        await Drain();

        Assert.Equal(0, fs.Probes);
        Assert.Equal("", pane.LoadError);
    }

    // ---- the two doors are one load -----------------------------------------

    /// <summary>
    /// **Both doors fire for one tab.** ReopenClosedTab assigns ActiveTab —
    /// which reaches the activate handler — and then calls RefreshIfUnloaded.
    /// Before the probe, the second caller found IsLoading already true because
    /// LoadListingAsync sets it before it yields; putting an await in front of
    /// that moved the first yield earlier, so the flag has to be claimed before
    /// the probe or one tab probes and lists twice.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_callers_on_one_restored_tab_probe_it_once()
    {
        var fs = new Share(answer: true, hold: true);
        var pane = Restored(fs, Path.GetTempPath());

        pane.RefreshIfUnloaded();
        pane.RefreshIfUnloaded();

        Assert.Equal(1, fs.Probes);

        fs.Answer();
        await Drain();

        Assert.Equal(1, fs.Listings);
    }

    /// <summary>
    /// **A probe that answers late must not drag the pane back.** The probe on
    /// a dead share can take the whole timeout, and a person is not going to sit
    /// and watch it — they type somewhere else. The generation the rest of this
    /// view model uses for exactly this is what stops the stale answer writing
    /// its sentence over the folder they are now in.
    /// </summary>
    [AvaloniaFact]
    public async Task A_probe_that_answers_after_you_have_moved_on_is_dropped()
    {
        var fs = new Share(answer: false, hold: true);
        var pane = Restored(fs, Gone);

        pane.RefreshIfUnloaded();

        await pane.NavigateAsync(Path.GetTempPath());
        await Drain();

        Assert.True(pane.IsLoaded);

        // Only now does the dead share get around to saying no.
        fs.Answer();
        await Drain();

        Assert.Equal("", pane.LoadError);
        Assert.True(pane.IsLoaded);
        Assert.False(pane.IsLoading);
    }

    // ---- a settings save is not a first load --------------------------------

    /// <summary>
    /// A window restored with three tabs, the first one showing. Paths that
    /// are not there, because nothing here reads the disk: the Share answers
    /// for all of them.
    /// </summary>
    private ShellViewModel ThreeRestoredTabs(Share fs)
    {
        var shell = Own(new ShellViewModel(fs));

        TabState Tab(string name) => new() { Path = Path.Combine(Path.GetTempPath(), name) };

        shell.Start(new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes =
                    [
                        new PaneState
                        {
                            Tabs = [Tab("vaktari-lazy-a"), Tab("vaktari-lazy-b"), Tab("vaktari-lazy-c")],
                            ActiveTabIndex = 0,
                        },
                    ],
                },
            ],
        });

        return shell;
    }

    /// <summary>
    /// **Every settings save loaded each restored tab nobody had opened.** The
    /// save ends by refreshing the panes so a new sort rule reaches rows
    /// already on screen, and it refreshed every tab — and a refresh is a load
    /// of CurrentPath, which RestoreFrom sets. A tab that has never been listed
    /// has no rows sorted under the old rule, so the tab on screen is listed
    /// again and the two behind it are left for their first activation.
    /// </summary>
    [AvaloniaFact]
    public async Task A_settings_save_relists_the_tab_on_screen_and_not_the_ones_never_opened()
    {
        var fs = new Share(answer: true);
        var shell = ThreeRestoredTabs(fs);
        await Drain();

        // GUARD: the start listed the tab on screen and nothing else, so the
        // count below is the save's own.
        Assert.Equal(1, fs.Listings);

        shell.OnSettingsChanged();
        await Drain();

        Assert.Equal(2, fs.Listings);
        Assert.All(shell.Left.Tabs.Skip(1), tab => Assert.False(tab.IsLoaded));
    }

    /// <summary>
    /// The other caller of the same loop: the icons changing source re-lists
    /// panes so their rows are drawn again, and a tab that has drawn no rows
    /// has nothing to draw again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_change_of_icons_relists_the_tab_on_screen_and_not_the_ones_never_opened()
    {
        var fs = new Share(answer: true);
        var shell = ThreeRestoredTabs(fs);
        await Drain();

        Assert.Equal(1, fs.Listings);

        shell.RefreshPaneListings();
        await Drain();

        Assert.Equal(2, fs.Listings);
        Assert.All(shell.Left.Tabs.Skip(1), tab => Assert.False(tab.IsLoaded));
    }

    // ---- nor a way round the probe --------------------------------------------

    /// <summary>
    /// A window restored with one tab, on a share that has gone, whose probe
    /// is still waiting for an answer.
    /// </summary>
    private ShellViewModel ProbingShell(Share fs)
    {
        var shell = Own(new ShellViewModel(fs));

        shell.Start(new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes = [new PaneState { Tabs = [new TabState { Path = Gone }], ActiveTabIndex = 0 }],
                },
            ],
        });

        Assert.Equal(1, fs.Probes);
        Assert.True(shell.ActiveTab!.IsLoading, "GUARD: the restored tab is not in its probe");

        return shell;
    }

    /// <summary>
    /// **A settings save during the probe went round it.** The save re-lists
    /// every pane that is loading, a probing pane is loading, and the refresh
    /// was a listing with no probe in front of it — it moved the generation
    /// on, so when the probe said no, nobody was listening, and the pane sat
    /// in a listing of the dead share until the server's own timeout. The
    /// probe's own load reads the new settings anyway, so leaving it alone
    /// costs nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_settings_save_during_the_probe_leaves_it_to_answer()
    {
        var fs = new Share(answer: false, hold: true);
        var shell = ProbingShell(fs);

        shell.OnSettingsChanged();

        fs.Answer();
        await Drain();

        Assert.Equal(0, fs.Listings);
        Assert.Equal("that folder could not be reached", shell.ActiveTab!.LoadError);
    }

    /// <summary>
    /// The same through the other caller: a change of icon source, which can
    /// arrive at startup unasked — a theme read in the background lands — at
    /// just the moment a restored tab is probing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_change_of_icons_during_the_probe_leaves_it_to_answer()
    {
        var fs = new Share(answer: false, hold: true);
        var shell = ProbingShell(fs);

        shell.RefreshPaneListings();

        fs.Answer();
        await Drain();

        Assert.Equal(0, fs.Listings);
        Assert.Equal("that folder could not be reached", shell.ActiveTab!.LoadError);
    }

    /// <summary>Answers every search with the rows it was given, counting how
    /// many times it has been asked to walk.</summary>
    private sealed class Searches(params FileEntry[] found) : Vaktari.Core.Search.ISearchProvider
    {
        private int _walks;

        public int Walks => Volatile.Read(ref _walks);

        public string BackendName => "counting";

        public bool SupportsContentSearch => false;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            Vaktari.Core.Search.SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _walks);

            await Task.CompletedTask;

            foreach (var entry in found) yield return entry;
        }
    }

    /// <summary>
    /// **A walk left in the background walked again on every save.** The
    /// relist skipped only the tabs that had never loaded, so a search, a space
    /// listing or a duplicates scan finished in another tab was run again from
    /// nothing — and a duplicates scan hashed its candidates again — for a save
    /// that at most changed how its rows sort. Its rows are kept, and put in
    /// the new order where they stand.
    /// </summary>
    [AvaloniaFact]
    public async Task A_settings_save_does_not_walk_a_search_in_the_background_again()
    {
        var searches = new Searches(
            new FileEntry("found.txt", Path.Combine(Path.GetTempPath(), "found.txt"), 1,
                          DateTimeOffset.UnixEpoch, EntryFlags.None));

        UseSearch(searches);
        UseSearchHistory(null);

        var fs = new Share(answer: true);
        var shell = Own(new ShellViewModel(fs));

        shell.Start(null, Path.GetTempPath());

        var folder = shell.ActiveTab!;
        var search = shell.Left.AddTab(VirtualPaths.Search("found", null, scoped: false));

        for (var i = 0; i < 40 && !(search.IsLoaded && folder.IsLoaded); i++) await Drain();

        Assert.True(search.IsLoaded, "GUARD: the search never finished");
        Assert.Equal(1, searches.Walks);

        shell.Left.ActiveTab = folder;
        await Drain();

        shell.OnSettingsChanged();
        await Drain();

        Assert.Equal(1, searches.Walks);
        Assert.True(search.IsLoaded);
        Assert.Equal(["found.txt"], search.Entries.Select(e => e.Name));
    }

    /// <summary>
    /// A provider that answers whether a path is there, counts what it was
    /// asked, and can be made to take its time about it.
    /// </summary>
    private sealed class Share : IFileSystemProvider
    {
        private readonly bool _answer;
        private readonly TaskCompletionSource<bool>? _held;

        public Share(bool answer, bool hold = false)
        {
            _answer = answer;

            if (hold) _held = new TaskCompletionSource<bool>();
        }

        public int Probes { get; private set; }
        public int Listings { get; private set; }

        /// <summary>Infinite until asked, so a test that expects a bounded
        /// timeout fails rather than passing on a default of zero.</summary>
        public TimeSpan LastTimeout { get; private set; } = Timeout.InfiniteTimeSpan;

        public void Answer() => _held?.TrySetResult(_answer);

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            Probes++;
            LastTimeout = timeout;

            return _held is null ? ValueTask.FromResult(_answer) : new ValueTask<bool>(_held.Task);
        }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Listings++;

            await Task.CompletedTask;

            yield return
            [
                new FileEntry("a.txt", Path.Combine(path, "a.txt"), 2,
                              DateTimeOffset.UnixEpoch, EntryFlags.None),
            ];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
