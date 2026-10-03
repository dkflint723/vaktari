using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// A pane's half of the folder hand-over: letting go of everything it holds
/// at or under a folder that is about to move, holding off while it moves,
/// and following it afterwards. <see cref="FolderHandover"/> is the other
/// half, and the rename-notes plan (§2, §3.1) and its review (findings 3 and
/// 17) are where the measurements are.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// The hand-over the engine behind this pane was given — one per
    /// application, so every window's panes see the same folders held. Null
    /// for a pane with no engine, or one the application did not wire, which
    /// is every test that builds its own; such a pane holds nothing off.
    /// </summary>
    private FolderHandover? Hold => _ops?.Handover as FolderHandover;

    /// <summary>
    /// Loads that have started and not yet let go of what they opened: the
    /// watch they opened beside the enumeration, and the enumeration itself.
    /// Each completes once its watch is installed or disposed, so a folder
    /// being let go of can be waited for honestly. Written by the load on the
    /// dispatcher and completed from the pool, so under its own lock.
    /// </summary>
    private readonly List<Task> _unsettled = [];

    private readonly Lock _settleGate = new();

    /// <summary>A load starting: what the hand-over will wait for.</summary>
    private TaskCompletionSource BeginSettling()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_settleGate) _unsettled.Add(settled.Task);

        return settled;
    }

    /// <summary>A load that has let go of everything it opened, or handed it
    /// to the pane.</summary>
    private void Settled(TaskCompletionSource settled)
    {
        lock (_settleGate) _unsettled.Remove(settled.Task);

        settled.TrySetResult();
    }

    /// <summary>
    /// The version-control reads still running — each a `git status` whose
    /// working folder is the repository, which walks the folder on screen —
    /// pruned as they finish. On the dispatcher only, like everything that
    /// starts one.
    /// </summary>
    private readonly List<Task> _vcsReads = [];

    /// <summary>The token the newest version-control read was given, so a
    /// folder being let go of can stop it without stopping the listing.</summary>
    private CancellationTokenSource? _vcsCts;

    /// <summary>
    /// Starts a version-control read of <paramref name="path"/> off the
    /// dispatcher, on a token of its own linked to the listing's.
    ///
    /// **Its own token, because the listing's was the only one there was.**
    /// The pane a folder is renamed FROM is the folder's parent, and its git
    /// status walks the folder (review finding 17) — but cancelling the
    /// listing's token to stop it would leave a listing still arriving stuck
    /// half-read. This one stops the read alone.
    /// </summary>
    private void StartVcsRead(string path, int generation)
    {
        // **Not while a folder it would walk is held** — see HoldsAround.
        // Asked again once the hold ends.
        if (Hold is { } hold && hold.HoldsAround(path))
        {
            hold.Declined(this, reload: false);
            return;
        }

        CancellationTokenSource cts;

        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? default);
        }
        catch (ObjectDisposedException)
        {
            // The listing's source has gone with the pane.
            return;
        }

        _vcsCts = cts;

        var read = Task.Run(() => RefreshVcsAsync(path, generation, cts.Token));

        _vcsReads.RemoveAll(t => t.IsCompleted);
        _vcsReads.Add(read);

        // Let go of with the read, not before it: the read is still reading
        // the token, and a source disposed under it throws from Register.
        _ = read.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cts, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Stops the version-control read under way, if there is one.</summary>
    private void CancelVcsRead()
    {
        try { _vcsCts?.Cancel(); }
        catch (ObjectDisposedException) { /* it finished between the two */ }
    }

    /// <summary>
    /// What one pane let go of, so the hand-over can put it back: a reload,
    /// which re-opens the watch, the repository watch and the listing; or only
    /// the version-control read, for a pane whose folder holds the one that
    /// moved.
    /// </summary>
    internal sealed record LetGo(PaneViewModel Pane, bool Reload);

    /// <summary>
    /// Lets go of everything this pane holds at or under any of
    /// <paramref name="folders"/>, and adds to <paramref name="waits"/> what
    /// is still letting go. Null when the pane holds nothing there. On the
    /// dispatcher, with the folders already held off (so nothing let go of
    /// here can be re-opened behind it).
    ///
    /// **Everything a load can still install is stopped by the generation.**
    /// A load opens its watch on the pool and installs it from a dispatcher
    /// block that checks only the generation — cancelling the token does not
    /// unqueue that block (review finding 3; see Dispose for the same trap).
    /// So the generation moves on first; the watch a load has already opened
    /// is then disposed by the load itself, which is what the settling tasks
    /// wait for.
    ///
    /// **And the same set of panes is cancelled as is reloaded** (finding 3,
    /// second half): a load whose token was cancelled leaves IsLoading set
    /// and says nothing, because "the newer one owns the status" — so a pane
    /// cancelled here and not reloaded afterwards would sit "loading…" for
    /// good. Every pane this answers Reload for is reloaded by the hand-over,
    /// on success and on failure.
    /// </summary>
    internal LetGo? LetGoUnder(IReadOnlyList<string> folders, List<Task> waits, TimeSpan deadline)
    {
        if (_disposed) return null;

        var real = !string.IsNullOrEmpty(CurrentPath) && !VirtualPaths.IsVirtual(CurrentPath);

        var inside = real && folders.Any(f => PathRules.Contains(f, CurrentPath));

        // A search, a usage listing or a duplicates scan walks the disk while
        // it loads — through the folder if it is rooted above it, inside it if
        // below. A search over everywhere has "" for a root and walks through
        // everything when no index answers it.
        var walkRoot = VirtualPaths.WalkRootOf(CurrentPath);
        var walking = walkRoot is not null && IsLoading
            && folders.Any(f => walkRoot.Length == 0
                                || PathRules.Contains(f, walkRoot)
                                || PathRules.Contains(walkRoot, f));

        // Anything that has loaded, is loading, or is waiting for its folder
        // to come back holds something. A restored tab nobody has looked at
        // yet holds nothing, and is only followed.
        var live = IsLoaded || IsLoading || HasLoadError || _watcher is not null;

        if ((inside && live) || walking)
        {
            _generation++;
            _cts?.Cancel();

            _vcsRefresh?.Stop();
            CancelVcsRead();

            lock (_settleGate) waits.AddRange(_unsettled);

            waits.AddRange(_vcsReads.Where(t => !t.IsCompleted));

            waits.Add(IdleAfter(_watcher, deadline));
            ReplaceWatch(null);

            // Inside the folder, a repository's .git may be too; and if it is
            // above, letting go costs nothing, because the reload re-creates
            // it from the version-control read.
            waits.Add(IdleAfter(_repoWatcher, deadline));
            _repoWatcher?.Dispose();
            _repoWatcher = null;

            return new LetGo(this, Reload: true);
        }

        // **The folder is inside this one** — the pane F2 was pressed in, most
        // often. Its listing holds nothing inside the folder, but its
        // version-control read walks it (review finding 17). That read alone
        // is stopped, and asked again afterwards; the rows stay.
        if (real && folders.Any(f => PathRules.Contains(CurrentPath, f)))
        {
            var reading = _vcsReads.Any(t => !t.IsCompleted) || _vcsRefresh?.IsEnabled == true;

            _vcsRefresh?.Stop();
            CancelVcsRead();

            waits.AddRange(_vcsReads.Where(t => !t.IsCompleted));

            return reading || IsRepository ? new LetGo(this, Reload: false) : null;
        }

        return null;
    }

    /// <summary>
    /// What to wait on for a watch to have stopped reading the disk once it is
    /// disposed: a timer's read, or a wait's look, that was already under way
    /// (review finding 3). An ordinary watcher lets go when it is disposed.
    /// </summary>
    private static Task IdleAfter(IDisposable? watch, TimeSpan deadline) => watch switch
    {
        PollingWatch polled => polled.WhenIdleAsync(deadline),
        ReturnWait waiting => waiting.WhenIdleAsync(deadline),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Carries this pane from <paramref name="from"/> to <paramref name="to"/>:
    /// where it is, both histories, and the rows it means to select. Answers
    /// whether the pane itself moved — whether it now has to be read at its
    /// new place.
    ///
    /// **Undo history is not touched, only navigation history.** The engine's
    /// stack is last-in first-out with the rename on top, so undoing it first
    /// puts the old name back, under which every older entry is right again.
    /// </summary>
    internal bool Follow(string from, string to)
    {
        if (_disposed) return false;

        Rebased(_back, from, to);
        Rebased(_forward, from, to);

        for (var i = 0; i < _selectAfterLoad.Count; i++)
            _selectAfterLoad[i] = PathRules.Rebase(_selectAfterLoad[i], from, to) ?? _selectAfterLoad[i];

        if (VirtualPaths.Rebase(CurrentPath, from, to) is not { } now || now == CurrentPath)
        {
            NotifyNavigationState();
            return false;
        }

        // What was selected comes back selected at its new path. Taken from
        // the rows still on screen, which carry the old one.
        foreach (var selected in SelectedPaths())
            SelectAfterLoad(PathRules.Rebase(selected, from, to) ?? selected);

        // Folders opened in place are kept by their old paths, which the
        // reload at the new place would otherwise go on asking for.
        ClearExpansion();

        CurrentPath = now;
        PathText = now;

        NotifyNavigationState();
        return true;
    }

    private static void Rebased(Stack<string> stack, string from, string to)
    {
        if (stack.Count == 0) return;

        // Oldest first, so pushing them back keeps the order.
        var paths = stack.Reverse().Select(p => VirtualPaths.Rebase(p, from, to) ?? p).ToList();

        stack.Clear();

        foreach (var path in paths) stack.Push(path);
    }

    /// <summary>
    /// Whether this pane holds anything a hand-over should put back by reading
    /// its folder again: a listing that has loaded, is loading, or is waiting
    /// for its folder to return.
    /// </summary>
    internal bool IsLive => !_disposed && (IsLoaded || IsLoading || HasLoadError || _watcher is not null);

    /// <summary>
    /// Reads the folder again where the pane now is, which re-opens its watch,
    /// its repository watch and its version-control read — the end of a hold.
    /// </summary>
    internal void ReloadAfterHandover()
    {
        if (_disposed || string.IsNullOrEmpty(CurrentPath)) return;

        Detached(LoadAsync(CurrentPath), "reload");
    }

    /// <summary>The version-control read a hold stopped, asked for again.</summary>
    internal void RereadVcsAfterHandover()
    {
        if (_disposed) return;

        QueueVcsRefresh();
    }
}
