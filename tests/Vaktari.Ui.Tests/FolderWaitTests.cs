using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The pane's side of waiting for a folder that is not there
/// (PaneViewModel.WaitForReturn), driven through a provider of the test's
/// own: its folders are real temporary ones, but its watches hear only what
/// the test tells them, so a folder can come back without a word and an
/// arrival can be said at the moment the test chooses.
/// </summary>
public sealed class FolderWaitTests : OwnedViewModels
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-wait").FullName;
    private readonly IPlacesProvider? _placesBefore = PaneViewModel.Places;
    private readonly TimeSpan? _pollBefore = PaneViewModel.PollInterval;
    private readonly INetworkChanges? _networkBefore = PaneViewModel.Network;

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.Places = _placesBefore;
        PaneViewModel.PollInterval = _pollBefore;
        PaneViewModel.Network = _networkBefore;

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Lists real folders; its watches say only what the test says.</summary>
    private sealed class Quiet : IFileSystemProvider
    {
        private readonly List<(string Path, Action<FileSystemChange> Heard)> _live = [];

        public List<string> Listed { get; } = [];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            lock (Listed) Listed.Add(path);

            await Task.Yield();

            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");

            yield return Directory.GetFileSystemEntries(path)
                .Select(p => new FileEntry(Path.GetFileName(p), p, 0, DateTimeOffset.UnixEpoch, EntryFlags.None))
                .ToList();
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        /// <summary>While set, a watch asked for waits on <see cref="Gate"/> — a disk that does not answer.</summary>
        public volatile bool Hold;

        public readonly ManualResetEventSlim Gate = new();

        /// <summary>How many watches have been asked for on a folder: one for
        /// each look that watches afresh.</summary>
        public int Opened(string path)
        {
            lock (_opened) return _opened.Count(p => p == path);
        }

        private readonly List<string> _opened = [];

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            lock (_opened) _opened.Add(path);

            if (Hold) Gate.Wait();

            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

            var entry = (path, onChange);
            lock (_live) _live.Add(entry);
            return new Off(() => { lock (_live) _live.Remove(entry); });
        }

        public bool Watching(string path)
        {
            lock (_live) return _live.Any(l => l.Path == path);
        }

        public void Say(string folder, FileSystemChange change)
        {
            Action<FileSystemChange>[] heard;
            lock (_live) heard = _live.Where(l => l.Path == folder).Select(l => l.Heard).ToArray();
            foreach (var h in heard) h(change);
        }

        private sealed class Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }

        /// <summary>A restored tab's probe: whether the folder is there.</summary>
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(Directory.Exists(path));
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    /// <summary>A places list whose only use here is to say it changed.</summary>
    private sealed class Places : IPlacesProvider
    {
        public event EventHandler? PlacesChanged;

        public void Changed() => PlacesChanged?.Invoke(this, EventArgs.Empty);

        public int Listening => PlacesChanged?.GetInvocationList().Length ?? 0;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([]);
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private static async Task<bool> Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();

        while (!done())
        {
            if (clock.Elapsed > Ceiling) return false;

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        return true;
    }

    /// <summary>Says the network changed when the test raises it.</summary>
    private sealed class Network
    {
        private Action? _raise;

        public int LetGo;

        public IDisposable? Subscribe(Action raise)
        {
            _raise = raise;
            return new Off(() => { _raise = null; LetGo++; });
        }

        public void Raise() => _raise?.Invoke();

        private sealed class Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }
    }

    /// <summary>The network the panes in this class hear, with a spacing long
    /// enough that only the first of a burst is passed on in any test here.</summary>
    private readonly Network _network = new();

    private NetworkChanges? _changes;

    private NetworkChanges Changes => _changes ??= new NetworkChanges(_network.Subscribe, TimeSpan.FromMinutes(10));

    private (PaneViewModel Pane, Quiet Fs, Places Places) Pane()
    {
        var fs = new Quiet();
        var places = new Places();

        PaneViewModel.Places = places;
        PaneViewModel.PollInterval = Timeout.InfiniteTimeSpan;
        PaneViewModel.Network = Changes;

        return (Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 }), fs, places);
    }

    /// <summary>A pane on a folder that is not there, waiting at the folder above.</summary>
    private async Task<(PaneViewModel Pane, Quiet Fs, Places Places, string Missing)> Waiting()
    {
        var (pane, fs, places) = Pane();
        var missing = Path.Combine(_root, "x");

        _ = pane.NavigateAsync(missing);

        Assert.True(await Until(() => pane.HasLoadError && fs.Watching(_root)), "the pane did not start waiting at the folder above");

        return (pane, fs, places, missing);
    }

    /// <summary>
    /// **A drive mounted again under a waiting pane is heard through the
    /// places list** (batch-0.11.2c QA, round 3): the mount point was there
    /// throughout, so the folder above heard nothing arrive — here the folder
    /// comes back without a word, and the places list saying it changed is
    /// what brings the pane back to it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_change_to_the_places_brings_back_a_folder_that_returned_unheard()
    {
        var (pane, fs, places, missing) = await Waiting();

        Assert.Equal(1, places.Listening);

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "a.txt"), "a");

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.True(pane.HasLoadError, "the folder was noticed without being heard of at all");

        places.Changed();

        Assert.True(await Until(() => !pane.HasLoadError && pane.Entries.Any(e => e.Name == "a.txt")),
            "the places list changing did not bring the pane back to its folder");
    }

    /// <summary>The places list is let go of with the wait: a pane that has
    /// moved on listens to it no more.</summary>
    [AvaloniaFact]
    public async Task A_pane_that_moves_on_stops_listening_to_the_places()
    {
        var (pane, _, places, _) = await Waiting();

        Assert.Equal(1, places.Listening);

        await pane.NavigateAsync(_root);
        Assert.True(await Until(() => pane.IsLoaded && !pane.HasLoadError && pane.CurrentPath == _root), "the pane did not move on");

        Assert.Equal(0, places.Listening);
    }

    /// <summary>
    /// **A return heard after the pane has moved on brings it nowhere**
    /// (QA's P3, untested until now). The folder comes back and is said; the
    /// reload that asks for is posted, and before it runs the pane goes
    /// somewhere else. When the post runs it finds the pane elsewhere and does
    /// nothing — the folder is not read, and the pane stays where it went.
    /// </summary>
    [AvaloniaFact]
    public async Task A_return_heard_after_the_pane_moved_on_brings_it_nowhere()
    {
        var (pane, fs, _, missing) = await Waiting();
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;

        Directory.CreateDirectory(missing);

        // Said from another thread, as a watcher says it: the reload is posted
        // to the window's thread, and nothing runs it yet. Joined rather than
        // awaited, because an await would resume through the dispatcher —
        // behind the very post this is about, which would then run first.
        var saying = new Thread(() => fs.Say(_root, new FileSystemChange(ChangeKind.Added, missing)));
        saying.Start();
        saying.Join();

        int listedBefore;
        lock (fs.Listed) listedBefore = fs.Listed.Count(p => p == missing);

        var moved = pane.NavigateAsync(other);

        Assert.True(await Until(() => moved.IsCompleted && pane.IsLoaded && pane.CurrentPath == other), "the pane did not move on");
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(other, pane.CurrentPath);
        Assert.False(pane.HasLoadError);

        lock (fs.Listed) Assert.Equal(listedBefore, fs.Listed.Count(p => p == missing));
    }

    /// <summary>
    /// **The places list is never made to wait on the disk** (batch-0.11.2e QA,
    /// round 8: asking again on the places list's own thread left every test
    /// green). The places list says it changed from its own thread — the
    /// device watch's — and the pane asks the wait again; that ask watches the
    /// folder above afresh, and here the watch is held, as a share that does
    /// not answer holds it. Saying the places changed must still return at
    /// once.
    /// </summary>
    [AvaloniaFact]
    public async Task A_change_to_the_places_does_not_wait_on_the_disk()
    {
        var (_, fs, places, _) = await Waiting();

        fs.Hold = true;
        var saying = new Thread(places.Changed) { IsBackground = true };

        try
        {
            saying.Start();
            Assert.True(saying.Join(TimeSpan.FromSeconds(5)), "saying the places changed waited for the held watch");
        }
        finally
        {
            fs.Gate.Set();
            fs.Hold = false;
            saying.Join(Ceiling);
        }
    }

    // ---- the network changing ----------------------------------------------

    /// <summary>
    /// **A share answering again told the pane nothing until its slow look**
    /// (batch-0.11.2g): the places list does not change for a server, and the
    /// slow look comes every 30 s — once a minute on a share that does not
    /// answer. The machine's network changing — a VPN connecting, Wi-Fi back —
    /// asks again at once. The folder comes back here without a word, and the
    /// test's ceiling is a third of the slow look's interval, so only the
    /// network changing can be what brings the pane back.
    /// </summary>
    [AvaloniaFact]
    public async Task A_change_to_the_network_brings_back_a_folder_that_returned_unheard()
    {
        var (pane, _, _, missing) = await Waiting();

        Assert.Equal(1, Changes.Listeners);
        Assert.True(FolderReturnWatch.RetryInterval > Ceiling, "the slow look could bring the pane back inside the test's ceiling");

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "a.txt"), "a");

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.True(pane.HasLoadError, "the folder was noticed without being heard of at all");

        _network.Raise();

        Assert.True(await Until(() => !pane.HasLoadError && pane.Entries.Any(e => e.Name == "a.txt")),
            "the network changing did not bring the pane back to its folder");
    }

    /// <summary>The network is let go of with the wait: a pane that has moved
    /// on is held by nothing that hears it, and with no pane waiting the
    /// system is not listened to at all.</summary>
    [AvaloniaFact]
    public async Task A_pane_that_moves_on_stops_listening_to_the_network()
    {
        var (pane, _, _, _) = await Waiting();

        Assert.Equal(1, Changes.Listeners);
        Assert.True(Changes.Subscribed);

        await pane.NavigateAsync(_root);
        Assert.True(await Until(() => pane.IsLoaded && !pane.HasLoadError && pane.CurrentPath == _root), "the pane did not move on");

        Assert.Equal(0, Changes.Listeners);
        Assert.False(Changes.Subscribed);
        Assert.Equal(1, _network.LetGo);
    }

    /// <summary>
    /// **A burst of network events is a bounded number of looks.** Twenty
    /// events, each after the look the one before asked for has had time to
    /// end — so the wait's own rule, one more look for any number of asks
    /// while one runs, cannot be what bounds them. Each look watches the
    /// folder above afresh, which is what is counted.
    /// </summary>
    [AvaloniaFact]
    public async Task A_burst_of_network_changes_makes_a_bounded_number_of_looks()
    {
        var (_, fs, _, _) = await Waiting();

        await Task.Delay(200);
        var before = fs.Opened(_root);

        for (var i = 0; i < 20; i++)
        {
            _network.Raise();
            await Task.Delay(30);
        }

        await Task.Delay(500);

        var looks = fs.Opened(_root) - before;

        Assert.True(looks is >= 1 and <= 2, $"twenty network events made {looks} looks");
    }

    /// <summary>The network's own thread is never made to wait on the disk:
    /// the look it asks for watches afresh, and the watch is held here as a
    /// share that does not answer holds it.</summary>
    [AvaloniaFact]
    public async Task A_change_to_the_network_does_not_wait_on_the_disk()
    {
        var (_, fs, _, _) = await Waiting();

        fs.Hold = true;
        var saying = new Thread(_network.Raise) { IsBackground = true };

        try
        {
            saying.Start();
            Assert.True(saying.Join(TimeSpan.FromSeconds(5)), "saying the network changed waited for the held watch");
        }
        finally
        {
            fs.Gate.Set();
            fs.Hold = false;
            saying.Join(Ceiling);
        }
    }

    // ---- a restored tab -----------------------------------------------------

    /// <summary>A shell over this class's provider, places and network, whose
    /// first tab is on a folder of its own — so that nothing it watches is the
    /// folder above the one a test makes go missing.</summary>
    private (ShellViewModel Shell, Quiet Fs, Places Places, string Missing) Shell(SessionState? session = null)
    {
        var fs = new Quiet();
        var places = new Places();

        PaneViewModel.Places = places;
        PaneViewModel.PollInterval = Timeout.InfiniteTimeSpan;
        PaneViewModel.Network = Changes;

        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var shell = Own(new ShellViewModel(fs));

        shell.Start(session, other);

        return (shell, fs, places, Path.Combine(_root, "x"));
    }

    private const string Unreachable = "that folder could not be reached";

    /// <summary>A tab closed while it waited on a missing folder, put back.</summary>
    private async Task<(ShellViewModel Shell, Quiet Fs, Places Places, string Missing, PaneViewModel Reopened)> Reopened()
    {
        var (shell, fs, places, missing) = Shell();
        var waiting = shell.Left.AddTab(missing);

        Assert.True(await Until(() => waiting.HasLoadError && places.Listening == 1), "the tab did not start waiting");

        shell.Left.CloseTab(waiting);
        Assert.True(await Until(() => places.Listening == 0), "the closed tab went on waiting");

        var reopened = shell.Left.ReopenClosedTab()!;

        Assert.True(await Until(() => reopened.LoadError == Unreachable && fs.Watching(_root) && places.Listening == 1),
            $"the reopened tab did not wait for its folder: '{reopened.LoadError}', listening {places.Listening}");

        return (shell, fs, places, missing, reopened);
    }

    /// <summary>
    /// **A tab put back on a missing folder never came back to it** (release
    /// 0.11.2 QA, F1, Windows and Fedora). Reopen closed tab goes through the
    /// restored tab's probe, and a probe that failed said "could not be
    /// reached" and returned before the load that installs the wait — so the
    /// folder made again was never noticed, 40 s on, until F5. It waits now,
    /// and the folder arriving in the folder above brings it back.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tab_put_back_on_a_missing_folder_comes_back_when_the_folder_does()
    {
        var (_, fs, _, missing, reopened) = await Reopened();

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "back.txt"), "b");
        fs.Say(_root, new FileSystemChange(ChangeKind.Added, missing));

        Assert.True(await Until(() => !reopened.HasLoadError && reopened.Entries.Any(e => e.Name == "back.txt")),
            "the folder came back and the reopened tab did not");
    }

    /// <summary>The same for a tab the session put back at startup, first
    /// listed when its side is shown: the stick it was on is plugged in again
    /// a while later, and the drives changing brings it back.</summary>
    [AvaloniaFact]
    public async Task A_tab_restored_with_the_session_on_a_missing_folder_comes_back_when_the_folder_does()
    {
        var missing = Path.Combine(_root, "x");

        var (shell, _, places, _) = Shell(new SessionState
        {
            Windows =
            [
                new WindowSession
                {
                    Panes = [new PaneState { Tabs = [new TabState { Path = missing }] }],
                },
            ],
        });

        var restored = shell.Left.ActiveTab!;

        Assert.Equal(missing, restored.CurrentPath);
        Assert.True(await Until(() => restored.LoadError == Unreachable && places.Listening == 1),
            $"the restored tab did not wait for its folder: '{restored.LoadError}', listening {places.Listening}");

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "back.txt"), "b");
        places.Changed();

        Assert.True(await Until(() => !restored.HasLoadError && restored.Entries.Any(e => e.Name == "back.txt")),
            "the folder came back and the restored tab did not");
    }

    /// <summary>A tab put back and waiting, then closed again, leaves
    /// nothing behind: no wait, nothing listening to the places or the
    /// network, no watch on the folder above.</summary>
    [AvaloniaFact]
    public async Task A_tab_put_back_waiting_and_closed_again_leaves_nothing_listening()
    {
        var (shell, fs, places, _, reopened) = await Reopened();

        Assert.Equal(1, Changes.Listeners);

        shell.Left.CloseTab(reopened);

        Assert.True(await Until(() => places.Listening == 0 && Changes.Listeners == 0 && !fs.Watching(_root)),
            $"the closed tab left listening: places {places.Listening}, network {Changes.Listeners}, watching {fs.Watching(_root)}");
        Assert.False(Changes.Subscribed);
    }

    /// <summary>
    /// **Starting the wait holds up nothing.** A tab put back on a share that
    /// does not answer starts a wait whose first look asks the share; that
    /// look runs on a thread of its own, so putting the tab back returns at
    /// once while the look is held — here the watch it opens is held, as a
    /// dead share holds it. Released after five seconds whatever happens, so
    /// a look on this thread fails the test rather than hanging it.
    /// </summary>
    [AvaloniaFact]
    public async Task Putting_back_a_tab_whose_wait_is_held_does_not_wait_for_it()
    {
        var (shell, fs, places, missing) = Shell();
        var waiting = shell.Left.AddTab(missing);

        Assert.True(await Until(() => waiting.HasLoadError && places.Listening == 1), "the tab did not start waiting");

        shell.Left.CloseTab(waiting);
        Assert.True(await Until(() => places.Listening == 0), "the closed tab went on waiting");

        fs.Hold = true;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => fs.Gate.Set(), TaskScheduler.Default);

        try
        {
            var clock = Stopwatch.StartNew();
            var reopened = shell.Left.ReopenClosedTab()!;

            Assert.True(await Until(() => reopened.LoadError == Unreachable), "the reopened tab said nothing");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"putting the tab back took {clock.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            fs.Gate.Set();
            fs.Hold = false;
        }
    }
}
