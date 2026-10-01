using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
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

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.Places = _placesBefore;
        PaneViewModel.PollInterval = _pollBefore;

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

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
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

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => ValueTask.FromResult(true);
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

    private (PaneViewModel Pane, Quiet Fs, Places Places) Pane()
    {
        var fs = new Quiet();
        var places = new Places();

        PaneViewModel.Places = places;
        PaneViewModel.PollInterval = Timeout.InfiniteTimeSpan;

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
}
