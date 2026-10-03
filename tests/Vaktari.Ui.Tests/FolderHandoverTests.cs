using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;
using Vaktari.Core.Sharing;
using Vaktari.Core.Vcs;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The folder hand-over, through shells and panes built on a file system that
/// counts what it is asked: which panes let go of a folder about to move,
/// that nothing re-opens there while it moves, that every pane is read and
/// watched again afterwards — and that the tabs, and everything else that
/// remembers a folder by name, follow it.
///
/// **Counted at the provider, where nothing else can supply the answer.** A
/// pane whose watch came back and a pane whose listing merely refreshed look
/// the same in the rows; the watches opened and still running say which
/// (flaky-test shapes, "supplied by something else"). The real self-block is
/// HandoverWindowTests', on a real window with real watchers.
/// </summary>
public sealed class FolderHandoverTests : OwnedViewModels
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-handover-ui-" + Guid.NewGuid().ToString("N")[..10]);

    private readonly IPlacesProvider? _placesBefore = PaneViewModel.Places;
    private readonly IFolderViewStore? _viewsBefore = PaneViewModel.FolderViews;
    private readonly IRecentStore? _recentsBefore = PaneViewModel.Recents;
    private readonly IVersionControl? _vcsBefore = PaneViewModel.Vcs;

    public FolderHandoverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "one", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "onetwo"));

        // Nothing of the application's: a real places provider, store or git
        // left installed by an earlier class would be written to by a follow.
        PaneViewModel.Places = null;
        PaneViewModel.FolderViews = null;
        PaneViewModel.Recents = null;
        PaneViewModel.Vcs = null;
    }

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.Places = _placesBefore;
        PaneViewModel.FolderViews = _viewsBefore;
        PaneViewModel.Recents = _recentsBefore;
        PaneViewModel.Vcs = _vcsBefore;

        CutMarks.Clear();

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp dir is not worth failing over */ }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    // ---- the rig ----------------------------------------------------------------

    /// <summary>
    /// The real folders, listed for real, with watches that are only counted:
    /// opened on which path, and still running or not. A path can be gated so
    /// its listing waits — on the token, as a real one does.
    /// </summary>
    private sealed class Fs : IFileSystemProvider
    {
        private readonly List<string> _opened = [];
        private readonly List<string> _live = [];

        public Dictionary<string, TaskCompletionSource> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Folders that will not be watched — the pane reads them on
        /// a timer instead, which is a real PollingWatch on the real folder.</summary>
        public HashSet<string> Unwatchable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Folders whose watch takes until the test says to open —
        /// the shape of a load that has finished reading and is still waiting
        /// for its watch.</summary>
        public Dictionary<string, ManualResetEventSlim> SlowWatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int _watchesStarted;

        public int WatchesStarted => Volatile.Read(ref _watchesStarted);

        public int OpenedOn(string path)
        {
            lock (_opened) return _opened.Count(p => PathRules.Same(p, path));
        }

        public int LiveOn(string path)
        {
            lock (_live) return _live.Count(p => PathRules.Same(p, path));
        }

        private int _listings;

        public int Listings => Volatile.Read(ref _listings);

        public int ListingsOf(string path)
        {
            lock (_listed) return _listed.Count(p => PathRules.Same(p, path));
        }

        private readonly List<string> _listed = [];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _listings);
            lock (_listed) _listed.Add(path);

            TaskCompletionSource? gate;
            lock (Gates) Gates.TryGetValue(PathRules.Normalise(path), out gate);

            if (gate is not null) await gate.Task.WaitAsync(ct).ConfigureAwait(false);

            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

            yield return [.. new DirectoryInfo(path).EnumerateFileSystemInfos().Select(info => new FileEntry(
                info.Name, info.FullName, 0, DateTimeOffset.UnixEpoch,
                info is DirectoryInfo ? EntryFlags.Directory : EntryFlags.None))];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            lock (Unwatchable)
                if (Unwatchable.Contains(PathRules.Normalise(path)))
                    throw new IOException("this folder cannot be watched");

            Interlocked.Increment(ref _watchesStarted);

            ManualResetEventSlim? slow;
            lock (SlowWatches) SlowWatches.TryGetValue(PathRules.Normalise(path), out slow);

            slow?.Wait(TimeSpan.FromSeconds(30));

            lock (_opened) _opened.Add(path);
            lock (_live) _live.Add(path);

            return new Held(this, path);
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(Directory.Exists(path));

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Held(Fs owner, string path) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                lock (owner._live) owner._live.Remove(path);
            }
        }
    }

    /// <summary>
    /// The engine's rename of a folder, as the real ones do it: held, moved,
    /// followed, ended — through the one FolderMoves both engines share. It
    /// can be told to fail the way a folder something else has open fails.
    /// </summary>
    private sealed class Ops : IFileOperations
    {
        public IFolderHandover? Handover { get; set; }

        public Exception? FailWith { get; set; }

        public async ValueTask RenameAsync(string path, string newName, CancellationToken ct)
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!, newName);

            await FolderMoves.RunAsync(Handover, [(path, target)], () =>
            {
                if (FailWith is { } refused) throw refused;

                Directory.Move(path, target);
                return ValueTask.CompletedTask;
            }, ct);
        }

        private static IOperationHandle Done()
        {
            var handle = new OperationHandle();
            handle.Begin(0, 0);
            handle.Complete();
            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Done();
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done();
        public void RecordCreation(string path) { }
        public IUndoGroup? BeginRenameGroup() => null;
        public bool CanUndo => false;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed record Rig(Fs Fs, Ops Ops, FolderHandover Handover, List<ShellViewModel> Shells)
    {
        public ShellViewModel Shell => Shells[0];
    }

    private Rig Build(int windows = 1, IClipboardService? clipboard = null)
    {
        var fs = new Fs();
        var ops = new Ops();
        var shells = new List<ShellViewModel>();

        for (var i = 0; i < windows; i++)
        {
            var shell = Own(new ShellViewModel(fs, ops, clipboard: clipboard));
            shell.Start(null, _root);
            shells.Add(shell);
        }

        var handover = new FolderHandover(() => shells);
        ops.Handover = handover;

        return new Rig(fs, ops, handover, shells);
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(condition(), because);
    }

    /// <summary>A tab opened in the background at <paramref name="path"/>,
    /// waited on until it has listed and is watching.</summary>
    private static async Task<PaneViewModel> TabAt(Rig rig, string path, ShellViewModel? shell = null)
    {
        var pane = (shell ?? rig.Shell).Left.AddTab(path, activate: false);

        await Until(() => pane.IsLoaded && rig.Fs.LiveOn(path) == 1, $"the tab at {path} never loaded and watched");

        return pane;
    }

    private FileEntry Folder(string name) => new(name, At(name), 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

    // ---- following -------------------------------------------------------------

    /// <summary>
    /// **A tab inside a renamed folder was left on a folder that no longer
    /// existed**, and its Back went there too. It follows: the new path, its
    /// history rebased, read again, and watched again at the new path — with
    /// the old watch let go of, which on Windows is what lets the rename
    /// happen at all.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tab_inside_a_renamed_folder_follows_it_with_its_history()
    {
        var rig = Build();
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        var inside = await TabAt(rig, At("one"));
        await inside.NavigateAsync(At("one", "sub"));
        await Until(() => rig.Fs.LiveOn(At("one", "sub")) == 1, "the tab never watched its subfolder");

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));

        await Until(() => PathRules.Same(inside.CurrentPath, At("uno", "sub"))
                          && inside.IsLoaded && rig.Fs.LiveOn(At("uno", "sub")) == 1,
            $"the tab inside is at {inside.CurrentPath}, loaded {inside.IsLoaded}, "
            + $"watching the new place {rig.Fs.LiveOn(At("uno", "sub"))} time(s)");

        Assert.Equal(0, rig.Fs.LiveOn(At("one", "sub")));
        Assert.Contains(inside.BackSteps, s => PathRules.Same(s.FullPath, At("uno")));
        Assert.DoesNotContain(inside.BackSteps, s => PathRules.Same(s.FullPath, At("one")));
        Assert.False(inside.IsLoading);
    }

    /// <summary>
    /// **"one" is not "onetwo".** A tab in a folder whose name merely starts
    /// with the renamed one's is neither let go of nor moved.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_whose_name_starts_the_same_is_left_alone()
    {
        var rig = Build();
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        var sibling = await TabAt(rig, At("onetwo"));

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));
        await Until(() => Directory.Exists(At("uno")), "the rename never landed");

        for (var i = 0; i < 40; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        Assert.True(PathRules.Same(At("onetwo"), sibling.CurrentPath));
        Assert.Equal(1, rig.Fs.OpenedOn(At("onetwo")));
        Assert.Equal(1, rig.Fs.LiveOn(At("onetwo")));
    }

    /// <summary>
    /// **A rename that failed left the tabs inside let go of.** Every pane
    /// the hold touched is read again where it was, loaded and watched — none
    /// left "loading…", none left unwatched — and the person is offered Try
    /// again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_failed_rename_puts_every_pane_back_loaded_and_watched()
    {
        var rig = Build();
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        var inside = await TabAt(rig, At("one", "sub"));

        InUseOffer? offered = null;
        from.InUseRequested += (_, offer) => offered = offer;

        rig.Ops.FailWith = new InUseException(At("one"), isDirectory: true, unchecked((int)0x80070005));

        Assert.False(await from.TryRenameAsync(Folder("one"), "uno"));

        await Until(() => rig.Fs.OpenedOn(At("one", "sub")) == 2 && rig.Fs.LiveOn(At("one", "sub")) == 1
                          && inside.IsLoaded && !inside.IsLoading,
            $"after the failed rename the tab inside opened {rig.Fs.OpenedOn(At("one", "sub"))} watch(es), "
            + $"{rig.Fs.LiveOn(At("one", "sub"))} running; loaded {inside.IsLoaded}, loading {inside.IsLoading}");

        Assert.True(PathRules.Same(At("one", "sub"), inside.CurrentPath));
        Assert.NotNull(offered);
        Assert.Equal("could not rename “one” — something has a file inside that folder open", offered!.Sentence);
        Assert.Equal("something has a file inside that folder open", from.Status);
    }

    /// <summary>
    /// A folder moved between folders goes item by item and nothing of ours
    /// can block it, so it is not held (review finding 11) — but the tabs
    /// inside still follow it, once it has landed.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_moved_elsewhere_is_followed_without_a_hold()
    {
        var rig = Build();
        await Until(() => rig.Shell.ActiveTab!.IsLoaded, "the first tab never loaded");

        var inside = await TabAt(rig, At("one", "sub"));

        Directory.CreateDirectory(At("onetwo", "moved"));
        Directory.Move(At("one"), At("onetwo", "moved", "one"));

        rig.Handover.Followed(At("one"), At("onetwo", "moved", "one"));

        await Until(() => PathRules.Same(inside.CurrentPath, At("onetwo", "moved", "one", "sub"))
                          && inside.IsLoaded && rig.Fs.LiveOn(At("onetwo", "moved", "one", "sub")) == 1,
            $"the tab inside is at {inside.CurrentPath}");
    }

    /// <summary>The hold reaches every window, not only the one that asked.</summary>
    [AvaloniaFact]
    public async Task A_tab_in_another_window_follows_too()
    {
        var rig = Build(windows: 2);
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded && rig.Shells[1].ActiveTab!.IsLoaded, "the windows never loaded");

        var elsewhere = await TabAt(rig, At("one", "sub"), rig.Shells[1]);

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));

        await Until(() => PathRules.Same(elsewhere.CurrentPath, At("uno", "sub"))
                          && rig.Fs.LiveOn(At("uno", "sub")) == 1 && rig.Fs.LiveOn(At("one", "sub")) == 0,
            $"the other window's tab is at {elsewhere.CurrentPath}");
    }

    /// <summary>
    /// Ctrl+Shift+T and reopening the split put a tab back where it was —
    /// which, once its folder has been renamed, is under the new name.
    /// </summary>
    [AvaloniaFact]
    public async Task Closed_tabs_and_a_closed_split_follow()
    {
        var rig = Build();
        var shell = rig.Shell;
        var from = shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        var closing = await TabAt(rig, At("one", "sub"));
        shell.Left.CloseTab(closing);

        shell.ToggleSplit();
        await Until(() => shell.Right?.ActiveTab is { IsLoaded: true }, "the split never opened");
        await shell.Right!.ActiveTab!.NavigateAsync(At("one"));
        shell.ToggleSplit();

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));
        await Until(() => Directory.Exists(At("uno")), "the rename never landed");

        var reopened = shell.Left.ReopenClosedTab();
        Assert.True(PathRules.Same(At("uno", "sub"), reopened!.CurrentPath), $"reopened at {reopened.CurrentPath}");

        shell.ToggleSplit();
        Assert.True(PathRules.Same(At("uno"), shell.Right!.ActiveTab!.CurrentPath),
                    $"the split came back at {shell.Right.ActiveTab.CurrentPath}");
    }

    /// <summary>
    /// The views each folder remembers and the recent lists follow, by
    /// prefix and never by a bare string match.
    /// </summary>
    [AvaloniaFact]
    public async Task Folder_views_and_recents_follow_and_a_sibling_does_not()
    {
        var state = Path.Combine(_root, ".state");
        Directory.CreateDirectory(state);

        var views = new JsonFolderViewStore(state);
        var recents = new JsonRecentStore(state);
        PaneViewModel.FolderViews = views;
        PaneViewModel.Recents = recents;

        views.Write(At("one", "sub"), new FolderViewState { View = ViewMode.Grid });
        recents.Record(At("one", "sub"), RecentKind.Folder);
        recents.Record(At("onetwo"), RecentKind.Folder);

        var rig = Build();
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));
        await Until(() => Directory.Exists(At("uno")), "the rename never landed");
        await Until(() => views.Read(At("uno", "sub")) is not null, "the folder's view stayed with the old name");

        Assert.Null(views.Read(At("one", "sub")));

        var folders = recents.Recent(RecentKind.Folder, 50).Select(r => r.Path).ToList();

        Assert.Contains(folders, p => PathRules.Same(p, At("uno", "sub")));
        Assert.Contains(folders, p => PathRules.Same(p, At("onetwo")));
        Assert.DoesNotContain(folders, p => PathRules.Same(p, At("one", "sub")));
    }

    /// <summary>
    /// A link shared from inside the folder is found by its local path, so it
    /// follows, and is saved.
    /// </summary>
    [AvaloniaFact]
    public async Task A_shared_link_follows_its_folder()
    {
        var rig = Build();
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        IReadOnlyList<DriveLink>? saved = null;
        rig.Shell.UseDriveLinks(null, [new DriveLink(At("one", "sub"), "/remote/sub", "https://example.invalid/x")],
            links => saved = links);

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));
        await Until(() => saved is not null, "the moved link was never saved");

        Assert.True(PathRules.Same(At("uno", "sub"), Assert.Single(rig.Shell.DriveLinks).LocalPath));
    }

    /// <summary>A clipboard that hands back what it was last given.</summary>
    private sealed class Clipboard : IClipboardService
    {
        public ClipboardPayload? Held { get; set; }

        public Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
        {
            Held = new ClipboardPayload(action, paths);
            return Task.FromResult(true);
        }

        public Task<ClipboardPayload?> GetFilesAsync() => Task.FromResult(Held);
        public Task<bool> HasFilesAsync() => Task.FromResult(Held is not null);
        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
    }

    /// <summary>
    /// **A cut waiting to be pasted followed on the clipboard as well as in
    /// the marks** (review finding 18) — while the clipboard still holds
    /// exactly that cut. Anything else on it is left alone, and the marks go.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pending_cut_follows_on_the_clipboard_it_still_owns()
    {
        var file = At("one", "sub", "a.txt");
        File.WriteAllText(file, "a");

        var clipboard = new Clipboard { Held = new ClipboardPayload(ClipboardAction.Cut, [file]) };
        CutMarks.Mark([file]);

        var rig = Build(clipboard: clipboard);
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));

        var moved = At("uno", "sub", "a.txt");
        await Until(() => CutMarks.Paths.Contains(moved), "the cut marks stayed on the old path");

        Assert.Equal(ClipboardAction.Cut, clipboard.Held!.Action);
        Assert.True(PathRules.Same(moved, Assert.Single(clipboard.Held.Paths)));
    }

    [AvaloniaFact]
    public async Task A_cut_somebody_else_replaced_is_not_rewritten()
    {
        var file = At("one", "sub", "a.txt");
        File.WriteAllText(file, "a");

        // A COPY of the same file: only the verb says it is not Vaktari's cut.
        var theirs = new ClipboardPayload(ClipboardAction.Copy, [file]);
        var clipboard = new Clipboard { Held = theirs };
        CutMarks.Mark([file]);

        var rig = Build(clipboard: clipboard);
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));
        await Until(() => CutMarks.Paths.Count == 0, "marks for a cut that is no longer on the clipboard stayed");

        Assert.Same(theirs, clipboard.Held);
    }

    /// <summary>
    /// The sidebar's folder tree keeps what was open open, under the new name.
    /// </summary>
    [AvaloniaFact]
    public async Task The_folder_tree_follows_and_keeps_its_branch_open()
    {
        var rig = Build();
        var from = rig.Shell.ActiveTab!;
        await Until(() => from.IsLoaded, "the first tab never loaded");

        var tree = rig.Shell.Sidebar.Tree!;
        tree.SetRoots([(_root, "root")]);
        await tree.RevealAsync(At("one", "sub"));

        Assert.Contains(tree.Rows, n => PathRules.Same(n.Path, At("one", "sub")));

        Assert.True(await from.TryRenameAsync(Folder("one"), "uno"));
        await Until(() => tree.Rows.Any(n => PathRules.Same(n.Path, At("uno", "sub"))),
            "the tree went on naming the old folder");

        var renamed = Assert.Single(tree.Rows, n => PathRules.Same(n.Path, At("uno")));
        Assert.Equal("uno", renamed.Label);
        Assert.True(renamed.IsExpanded);
        Assert.True(PathRules.Same(At("uno", "sub"), tree.CurrentPath));
    }

    // ---- what is let go of, and held off -----------------------------------------

    /// <summary>
    /// **A load in flight when the hold begins** (review finding 3). Its watch
    /// opened before its listing started reading, and the dispatcher block
    /// that installs it checks only the generation — so the hold moves the
    /// generation on and waits for the load to let go. Nothing opens under
    /// the held folder while it is held, and the pane is read and watched
    /// again once the hold ends.
    /// </summary>
    [AvaloniaFact]
    public async Task A_load_in_flight_is_let_go_of_and_nothing_reopens_until_the_hold_ends()
    {
        var rig = Build();
        await Until(() => rig.Shell.ActiveTab!.IsLoaded, "the first tab never loaded");

        var sub = At("one", "sub");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (rig.Fs.Gates) rig.Fs.Gates[PathRules.Normalise(sub)] = gate;

        var pane = rig.Shell.Left.AddTab(sub, activate: false);

        await Until(() => rig.Fs.OpenedOn(sub) == 1 && rig.Fs.ListingsOf(sub) == 1, "the load never started");

        var lease = await rig.Handover.ReleaseAsync([At("one")], CancellationToken.None);

        Assert.Equal(0, rig.Fs.LiveOn(sub));

        // Held: a refresh asked for now opens nothing under the folder.
        lock (rig.Fs.Gates) rig.Fs.Gates.Remove(PathRules.Normalise(sub));
        gate.SetResult();

        await pane.RefreshAsync();

        Assert.Equal(1, rig.Fs.OpenedOn(sub));
        Assert.Equal(0, rig.Fs.LiveOn(sub));

        await lease.DisposeAsync();

        await Until(() => rig.Fs.LiveOn(sub) == 1 && pane.IsLoaded && !pane.IsLoading,
            $"after the hold the pane watched {rig.Fs.LiveOn(sub)} time(s), loaded {pane.IsLoaded}, loading {pane.IsLoading}");
    }

    /// <summary>
    /// A folder read on a timer instead of watched is let go of with its read
    /// waited for, and read on a timer again afterwards.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_read_on_a_timer_is_let_go_of_and_read_again_after()
    {
        var before = PaneViewModel.PollInterval;
        PaneViewModel.PollInterval = TimeSpan.FromMilliseconds(20);

        try
        {
            var rig = Build();
            await Until(() => rig.Shell.ActiveTab!.IsLoaded, "the first tab never loaded");

            var sub = At("one", "sub");
            lock (rig.Fs.Unwatchable) rig.Fs.Unwatchable.Add(PathRules.Normalise(sub));

            var watcher = typeof(PaneViewModel).GetField("_watcher",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var pane = rig.Shell.Left.AddTab(sub, activate: false);

            await Until(() => pane.IsLoaded && watcher.GetValue(pane) is PollingWatch, "the folder was never read on a timer");

            var polled = (PollingWatch)watcher.GetValue(pane)!;

            var lease = await rig.Handover.ReleaseAsync([At("one")], CancellationToken.None);

            Assert.Null(watcher.GetValue(pane));
            Assert.True(await polled.WhenIdleAsync(TimeSpan.Zero), "the hold ended its wait with a read still inside the folder");

            await lease.DisposeAsync();

            await Until(() => watcher.GetValue(pane) is PollingWatch again && !ReferenceEquals(again, polled) && pane.IsLoaded,
                "the pane was left unwatched");
        }
        finally
        {
            PaneViewModel.PollInterval = before;
        }
    }

    /// <summary>
    /// **The watch a finished load is still waiting for** (review finding 3).
    /// The listing has been read; the watch is still opening, and the block
    /// that installs it checks only the generation. Held here until the hold
    /// has begun: it must be let go of, not installed under the folder.
    /// </summary>
    [AvaloniaFact]
    public async Task A_watch_that_finishes_opening_after_the_hold_began_is_not_installed()
    {
        var rig = Build();
        await Until(() => rig.Shell.ActiveTab!.IsLoaded, "the first tab never loaded");

        var sub = At("one", "sub");
        using var slow = new ManualResetEventSlim();
        lock (rig.Fs.SlowWatches) rig.Fs.SlowWatches[PathRules.Normalise(sub)] = slow;

        var pane = rig.Shell.Left.AddTab(sub, activate: false);

        await Until(() => rig.Fs.WatchesStarted > 1 && rig.Fs.ListingsOf(sub) == 1, "the load never began opening its watch");

        var releasing = rig.Handover.ReleaseAsync([At("one")], CancellationToken.None).AsTask();
        slow.Set();

        var lease = await releasing;

        Assert.Equal(1, rig.Fs.OpenedOn(sub));
        Assert.Equal(0, rig.Fs.LiveOn(sub));

        lock (rig.Fs.SlowWatches) rig.Fs.SlowWatches.Clear();
        await lease.DisposeAsync();

        await Until(() => rig.Fs.LiveOn(sub) == 1 && pane.IsLoaded, "the pane was left unwatched after the hold");
    }

    /// <summary>
    /// **A tab opened into the folder while it was held** is refused its watch
    /// — and is owed a reload when the hold ends, or it would stay unwatched
    /// for good.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tab_opened_into_the_folder_during_the_hold_is_watched_after_it()
    {
        var rig = Build();
        await Until(() => rig.Shell.ActiveTab!.IsLoaded, "the first tab never loaded");

        var sub = At("one", "sub");

        var lease = await rig.Handover.ReleaseAsync([At("one")], CancellationToken.None);

        var late = rig.Shell.Left.AddTab(sub, activate: false);
        await Until(() => late.IsLoaded, "the tab opened during the hold never listed");

        Assert.Equal(0, rig.Fs.OpenedOn(sub));

        await lease.DisposeAsync();

        await Until(() => rig.Fs.LiveOn(sub) == 1, "the tab opened during the hold was never watched");
    }

    /// <summary>
    /// A Size total being measured inside the folder is stopped, and the hold
    /// waits for its walk to leave the tree. The walk is held mid-tree on the
    /// one hook it asks for every folder it enters.
    /// </summary>
    [AvaloniaFact]
    public async Task A_size_being_measured_inside_the_folder_is_stopped_and_waited_for()
    {
        Directory.CreateDirectory(At("one", "gate"));

        var settings = AppSettings.Current;
        var provider = Thumbnails.RowMetadata.Provider;
        var hook = SafeWalk.DoNotEnter;

        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        try
        {
            AppSettings.Apply(settings with
            {
                Views = settings.Views with
                {
                    Details = settings.Views.Details with { FolderSize = Core.Settings.FolderSizeMode.ContentSize },
                },
            });

            Thumbnails.RowMetadata.Provider = new Describes();

            SafeWalk.DoNotEnter = path =>
            {
                if (path.EndsWith("gate", StringComparison.Ordinal))
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(30));
                }

                return false;
            };

            var cell = new Avalonia.Controls.TextBlock();
            Thumbnails.RowMetadata.SetSize(cell, Folder("one"));

            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the measure never walked into the folder");

            var handover = new FolderHandover(() => []);
            var releasing = handover.ReleaseAsync([At("one")], CancellationToken.None).AsTask();

            for (var i = 0; i < 40; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

            Assert.False(releasing.IsCompleted, "the hold did not wait for the walk inside the folder");

            gate.Set();

            await (await releasing).DisposeAsync();
        }
        finally
        {
            gate.Set();
            SafeWalk.DoNotEnter = hook;
            Thumbnails.RowMetadata.Provider = provider;
            AppSettings.Apply(settings);
        }
    }

    private sealed class Describes : IFileMetadataProvider
    {
        public bool CanDescribe(string path, bool isDirectory) => true;
        public ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct) => ValueTask.FromResult<string?>(null);
        public ValueTask<string?> DescribeAccessAsync(string path, bool isDirectory, CancellationToken ct) => ValueTask.FromResult<string?>(null);
    }

    /// <summary>A version-control backend whose read of one folder waits for
    /// its token, and counts how often it was asked.</summary>
    private sealed class Waiting(string waitsOn) : IVersionControl
    {
        private int _asked;
        private int _cancelled;

        public int Asked => Volatile.Read(ref _asked);
        public int Cancelled => Volatile.Read(ref _cancelled);

        public string Name => "waiting";
        public bool IsAvailable => true;
        public string? FindRoot(string folder) => folder;

        public async Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
        {
            if (!PathRules.Same(folder, waitsOn)) return null;

            Interlocked.Increment(ref _asked);

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            return null;
        }
    }

    /// <summary>
    /// **The pane F2 was pressed in walks the folder with its git status**
    /// (review finding 17). That read is stopped, and waited for, before the
    /// folder moves — without stopping the listing, which is not re-read — and
    /// asked again afterwards.
    /// </summary>
    [AvaloniaFact]
    public async Task The_version_control_read_of_the_folder_above_is_stopped_and_asked_again()
    {
        var vcs = new Waiting(_root);
        PaneViewModel.Vcs = vcs;

        var rig = Build();
        var above = rig.Shell.ActiveTab!;

        await Until(() => above.IsLoaded && vcs.Asked == 1, "the folder above never asked for its status");

        var listings = rig.Fs.ListingsOf(_root);

        var lease = await rig.Handover.ReleaseAsync([At("one")], CancellationToken.None);

        Assert.Equal(1, vcs.Cancelled);

        // Held: a refresh of the marks asked for now (a watcher event, a
        // settings save) is put off rather than walking the folder. Its timer
        // fires 600 ms on, so a second and a half says it did not run.
        above.RefreshDecorations();

        for (var i = 0; i < 150; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }

        Assert.Equal(1, vcs.Asked);

        await lease.DisposeAsync();

        await Until(() => vcs.Asked == 2, "the status was never asked for again after the hold");

        Assert.Equal(listings, rig.Fs.ListingsOf(_root));
        Assert.True(above.IsLoaded);
    }

    /// <summary>
    /// A search walking under the folder is stopped, and run again once the
    /// hold is over — not left half-walked and not left "loading".
    /// </summary>
    [AvaloniaFact]
    public async Task A_search_walking_through_the_folder_is_stopped_and_run_again()
    {
        var rig = Build();
        await Until(() => rig.Shell.ActiveTab!.IsLoaded, "the first tab never loaded");

        var walking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;

        UseSearch(new SlowSearch(walking, () => Interlocked.Increment(ref asked)));

        var pane = rig.Shell.Left.AddTab(VirtualPaths.Search("x", _root, scoped: true), activate: false);

        await Until(() => Volatile.Read(ref asked) == 1 && pane.IsLoading, "the search never started walking");

        var lease = await rig.Handover.ReleaseAsync([At("one")], CancellationToken.None);
        walking.TrySetResult();

        await lease.DisposeAsync();

        await Until(() => Volatile.Read(ref asked) == 2 && pane.IsLoaded && !pane.IsLoading,
            $"the search ran {Volatile.Read(ref asked)} time(s); loaded {pane.IsLoaded}, loading {pane.IsLoading}");
    }

    /// <summary>A search backend that walks until it is let go of or its
    /// token is cancelled.</summary>
    private sealed class SlowSearch(TaskCompletionSource walking, Action asked) : Vaktari.Core.Search.ISearchProvider
    {
        public string BackendName => "slow";
        public bool SupportsContentSearch => false;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            Vaktari.Core.Search.SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            asked();

            await walking.Task.WaitAsync(ct);

            yield break;
        }
    }
}
