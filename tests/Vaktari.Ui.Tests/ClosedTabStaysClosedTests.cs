using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Vcs;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What reaches a tab after it has been closed.
///
/// **An operation outlives the tab that started it, and its ending reloaded
/// the closed pane.** Dispose tore down the watcher and nulled the token
/// source, but set nothing a later load could read: the finished operation's
/// refresh made a new token source, listed the folder, and started a watcher
/// and a repository watch that nothing would ever dispose — each one keeping
/// the dead pane alive, and on Linux each one an inotify instance. A load
/// already past its enumeration when the tab closed did the same from its
/// queued completion block, and a Retry pressed afterwards handed its work to
/// the closed pane in the first place.
/// </summary>
public sealed class ClosedTabStaysClosedTests : OwnedViewModels
{
    /// <summary>
    /// Counts what the pane asks of the file system, which is exactly what a
    /// closed pane must stop asking — and which of the watches it was given
    /// are still running, which is what a closed pane must not leave behind.
    /// The gate holds an enumeration open so a tab can be closed with its load
    /// in flight.
    /// </summary>
    private sealed class Counting : IFileSystemProvider
    {
        private int _enumerations;
        private int _watches;
        private readonly List<string> _live = [];

        public int Enumerations => Volatile.Read(ref _enumerations);

        /// <summary>Every watch ever opened, running or not.</summary>
        public int Watches => Volatile.Read(ref _watches);

        /// <summary>Watches opened and not yet disposed.</summary>
        public int Live
        {
            get { lock (_live) return _live.Count; }
        }

        /// <summary>Watches on <paramref name="path"/> opened and not yet
        /// disposed.</summary>
        public int LiveOn(string path)
        {
            lock (_live) return _live.Count(p => PathRules.Same(p, path));
        }

        /// <summary>How many watches have been opened on <paramref name="path"/>.</summary>
        public int OpenedOn(string path)
        {
            lock (_opened) return _opened.Count(p => PathRules.Same(p, path));
        }

        private readonly List<string> _opened = [];

        public TaskCompletionSource? Gate { get; set; }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _enumerations);

            // Deliberately deaf to the token: this stands in for an enumeration
            // that had already finished when the tab closed, whose completion
            // block is on its way regardless.
            if (Gate is { } gate) await gate.Task.ConfigureAwait(false);

            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            Interlocked.Increment(ref _watches);

            lock (_opened) _opened.Add(path);
            lock (_live) _live.Add(path);

            return new Held(this, path);
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

        /// <summary>Stops counting as live on its first Dispose, and only
        /// then: a watch disposed twice is still one watch.</summary>
        private sealed class Held(Counting owner, string path) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                lock (owner._live) owner._live.Remove(path);
            }
        }
    }

    private static string Root => Path.Combine(Path.GetTempPath(), "vaktari-closed-tab");

    /// <summary>
    /// Pumps long enough for a finished operation's posted refresh to land and
    /// its load to run. There is nothing positive to wait FOR — the assertion
    /// is that nothing happens — so the bound is generous: the continuation is
    /// a pool hop and a post, measured in milliseconds.
    /// </summary>
    private static async Task Pump()
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(condition(), because);
    }

    private async Task<(PaneViewModel Pane, Counting Fs)> Loaded()
    {
        var fs = new Counting();
        var pane = Own(new PaneViewModel(fs));

        await pane.NavigateAsync(Root);

        Assert.True(pane.IsLoaded, "the pane never loaded, so nothing below is about a closed one");
        Assert.Equal(1, fs.Enumerations);
        Assert.Equal(1, fs.Watches);
        Assert.Equal(1, fs.Live);

        return (pane, fs);
    }

    /// <summary>
    /// The finding: an operation that finishes after its tab has closed does
    /// not come back into it. Nothing is listed, nothing is watched, and the
    /// pane is not touched at all — its undo state is the first thing the
    /// finishing operation used to refresh.
    /// </summary>
    [AvaloniaFact]
    public async Task An_operation_finishing_after_its_tab_closed_leaves_it_closed()
    {
        var (pane, fs) = await Loaded();

        var handle = new OperationHandle();

        pane.Adopt(handle);
        pane.Dispose();

        var touched = new List<string?>();
        pane.PropertyChanged += (_, e) => touched.Add(e.PropertyName);

        handle.Complete();
        await handle.Completion;
        await Pump();

        Assert.Equal(1, fs.Enumerations);
        Assert.Equal(1, fs.Watches);
        Assert.DoesNotContain(nameof(PaneViewModel.UndoLabel), touched);
    }

    /// <summary>
    /// And every other door that ends in a refresh — an undo whose work
    /// finished on the pool after the tab closed, a settings save, a retry —
    /// is shut at the load itself, which is where they all arrive. The watch
    /// the tab had is let go with it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_of_a_closed_tab_lists_and_watches_nothing()
    {
        var (pane, fs) = await Loaded();

        pane.Dispose();

        Assert.Equal(0, fs.Live);

        await pane.RefreshAsync();
        await Pump();

        Assert.Equal(1, fs.Enumerations);
        Assert.Equal(1, fs.Watches);
        Assert.Equal(0, fs.Live);
    }

    /// <summary>
    /// **A load already in flight when the tab closes leaves no watcher
    /// running.** Cancelling the token does not unqueue a completion block that
    /// is on its way, and that block is what installs the watch; moving the
    /// generation on in Dispose is what it checks.
    ///
    /// Counted as watches still RUNNING, not as watches opened. The load opens
    /// its watch before its enumeration reads anything, so by the time this
    /// test can close the tab one has been opened — and it is the load's own
    /// ending that must let it go. That happens on the pool once the watch has
    /// finished opening, so the wait is for the count to reach nothing rather
    /// than a fixed number of turns.
    /// </summary>
    [AvaloniaFact]
    public async Task A_load_in_flight_when_the_tab_closes_starts_no_watcher()
    {
        var fs = new Counting { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pane = Own(new PaneViewModel(fs));

        var loading = pane.NavigateAsync(Root);

        await Until(() => fs.Enumerations > 0, "the load never started reading, so the tab was not closed mid-load");

        Assert.Equal(1, fs.Enumerations);

        pane.Dispose();
        fs.Gate.SetResult();

        await loading;

        await Until(() => fs.Live == 0, $"a closed tab kept {fs.Live} watch(es) running");

        // And nothing opened another one after the tab had gone.
        Assert.True(fs.Watches <= 1, $"{fs.Watches} watches were opened for one load");
        Assert.False(pane.IsLoaded, "a closed tab finished loading");
    }

    // ---- a load begun on the pool ------------------------------------------

    /// <summary>
    /// **A refresh begun on the pool ran its first half there.** Undo, redo and
    /// a failed step await their refresh with ConfigureAwait(false), so the
    /// load's prologue — the closed-tab check, the new generation, the token
    /// source, letting go of the old watch — ran beside Dispose and beside the
    /// other loads' completion blocks on the dispatcher, sharing all of those
    /// with them unguarded. It could pass the closed-tab check just before
    /// Dispose ran and then take a generation its completion block would
    /// match; that moment is too narrow for a test to land in, but where the
    /// prologue runs is not. It also raised the pane's bound properties —
    /// IsLoading, CurrentPath, PathText — off the UI thread, which is the part
    /// a test can see.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_begun_on_the_pool_is_begun_on_the_dispatcher()
    {
        var (pane, _) = await Loaded();

        var offThread = new List<string?>();

        pane.PropertyChanged += (_, e) =>
        {
            if (!Dispatcher.UIThread.CheckAccess())
                lock (offThread) offThread.Add(e.PropertyName);
        };

        var refresh = Task.Run(pane.RefreshAsync);

        await Until(() => refresh.IsCompleted, "the refresh begun on the pool never finished");
        await refresh;

        Assert.True(pane.IsLoaded, "GUARD: the refresh never finished loading, so it proves nothing");

        lock (offThread)
            Assert.True(offThread.Count == 0, "raised off the UI thread: " + string.Join(", ", offThread));
    }

    /// <summary>
    /// **And a watch could be installed after its teardown and then
    /// overwritten, still running.** The finding's own interleaving, made
    /// exact: a load's completion block raises ListingSettled before it
    /// installs its watch, and a refresh is begun on the pool from inside it —
    /// as an undo finishing at that moment would be. On the pool, that
    /// refresh's prologue let go of the watch before the block installed its
    /// own, and the refresh's own completion then assigned over it.
    ///
    /// Two things now stand in the way, and this test needs both gone to go
    /// red: the refresh is moved to the dispatcher (the test above), and the
    /// install disposes whatever it replaces (the closed-tab test above).
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_begun_mid_completion_leaves_no_watch_running()
    {
        var (pane, fs) = await Loaded();

        Task? second = null;

        void Begin(object? sender, EventArgs e)
        {
            pane.ListingSettled -= Begin;

            // Waited for here, on the dispatcher, so the block cannot go on
            // to install its watch until the refresh has begun.
            Task.Run(() => { second = pane.RefreshAsync(); }).Wait();
        }

        pane.ListingSettled += Begin;

        await pane.RefreshAsync();

        await Until(() => second is { IsCompleted: true }, "the refresh begun mid-completion never finished");
        await second!;

        Assert.Equal(1, fs.Live);

        pane.Dispose();

        await Until(() => fs.Live == 0, $"{fs.Live} watch(es) outlived the tab");
    }

    // ---- the repository ----------------------------------------------------

    /// <summary>
    /// A repository at <paramref name="root"/> with nothing to mark, counting
    /// the folders it is asked about.
    /// </summary>
    private sealed class Repository(string root) : IVersionControl
    {
        private readonly List<string> _asked = [];

        public int AskedAbout(string folder)
        {
            lock (_asked) return _asked.Count(f => PathRules.Same(f, folder));
        }

        public string Name => "counting";

        public bool IsAvailable => true;

        public string? FindRoot(string folder) => root;

        public Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
        {
            lock (_asked) _asked.Add(folder);

            return Task.FromResult<VcsSnapshot?>(
                new VcsSnapshot(root, new Dictionary<string, VcsState>()));
        }
    }

    /// <summary>
    /// **A closed tab went on following its repository.** The .git watcher
    /// posts its refresh from its own thread, so one already on its way when
    /// the tab closed — a checkout writes HEAD and the index over and over —
    /// arrived after Dispose and started the debounce again. Its tick read the
    /// generation Dispose had moved on to, so the status it asked for matched,
    /// and the answer started a new repository watch on the dead pane.
    ///
    /// RefreshDecorations stands in for that late post: it is the same call.
    /// A second pane, left open, is asked to refresh straight after, on the
    /// same debounce, and is the clock — once its answer has come back, the
    /// closed one's would have too.
    /// </summary>
    [AvaloniaFact]
    public async Task A_closed_tab_does_not_follow_its_repository_again()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-closed-repo").FullName;
        var git = Directory.CreateDirectory(Path.Combine(root, ".git")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;

        var vcsBefore = PaneViewModel.Vcs;
        var repository = new Repository(root);

        PaneViewModel.Vcs = repository;

        try
        {
            var fs = new Counting();
            var closed = Own(new PaneViewModel(fs));
            var open = Own(new PaneViewModel(fs));

            await closed.NavigateAsync(root);
            await open.NavigateAsync(sub);

            await Until(() => closed.IsRepository && open.IsRepository, "GUARD: the panes never found the repository");
            await Until(() => fs.LiveOn(git) == 2, "GUARD: the two panes are not both following .git");

            closed.Dispose();

            Assert.Equal(1, fs.LiveOn(git));

            var askedBefore = repository.AskedAbout(root);
            var clockBefore = repository.AskedAbout(sub);
            var watchedBefore = fs.OpenedOn(git);

            closed.RefreshDecorations();
            open.RefreshDecorations();

            // The open pane's answer re-opens its watch on .git, so both halves
            // of its refresh have landed once this holds.
            await Until(
                () => repository.AskedAbout(sub) > clockBefore && fs.OpenedOn(git) > watchedBefore,
                "the open pane's refresh never came back, so the clock never ran");

            await Pump();

            Assert.Equal(askedBefore, repository.AskedAbout(root));
            Assert.Equal(1, fs.LiveOn(git));
        }
        finally
        {
            PaneViewModel.Vcs = vcsBefore;

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    // ---- the retry ---------------------------------------------------------

    private sealed class Running
    {
        public int Count { get; private set; }

        public Running(PaneViewModel pane) => pane.OperationStarted += (_, _) => Count++;
    }

    /// <summary>
    /// **A Retry pressed after its tab has closed reports in a tab that is
    /// open.** The offer stays on the bar until it is pressed or dismissed and
    /// closing a tab touches neither, so the retry went to the pane the
    /// failure finished in — disposed, its progress invisible, its refresh at
    /// the end reloading a tab that no longer exists.
    /// </summary>
    [AvaloniaFact]
    public async Task A_retry_after_its_tab_closed_goes_to_the_active_tab()
    {
        var fs = new Counting();
        var shell = Own(new ShellViewModel(fs));

        shell.Start(null, Root);

        var failed = shell.ActiveTab!;
        var survivor = shell.Left.AddTab(Path.Combine(Root, "other"), activate: false);

        var handle = new OperationHandle { Retry = new RetryOffer(1, () => new OperationHandle()) };

        failed.Adopt(handle);
        handle.Complete();

        for (var i = 0; i < 200 && shell.Retryable is null; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(shell.Retryable);

        shell.CloseTabCommand.Execute(failed);

        Assert.DoesNotContain(failed, shell.Left.Tabs);
        Assert.Same(survivor, shell.ActiveTab);

        var inClosed = new Running(failed);
        var inSurvivor = new Running(survivor);

        shell.RetryOperationCommand.Execute(null);

        Assert.Equal(0, inClosed.Count);
        Assert.Equal(1, inSurvivor.Count);
    }
}
