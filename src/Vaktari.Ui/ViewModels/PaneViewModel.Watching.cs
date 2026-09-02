using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Keeping a listing current: the filesystem watcher, the repository watcher,
/// and the debounce that stops a burst of changes redrawing the pane per file.
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- live updates --------------------------------------------------

    /// <summary>
    /// Watches the open directory so changes made by anything else — Dolphin,
    /// a terminal, a download finishing — appear without a manual refresh.
    /// Updates are applied entry by entry; re-enumerating on every event would
    /// throw away the whole point of streaming the listing in the first place.
    /// </summary>
    private IDisposable? _repoWatcher;

    /// <summary>
    /// Watches the repository's own metadata, not the folder on screen.
    ///
    /// **`git commit` and `git checkout` do not touch the working tree the way
    /// the folder watcher can see.** A commit writes `.git/index` and
    /// `.git/HEAD` and moves no file the listing is showing, so every mark
    /// stayed stale until a navigation or F5. A checkout rewrites both AND the
    /// tree, which is exactly why this shares `QueueVcsRefresh`'s debounce
    /// rather than refreshing directly — otherwise one branch switch would fire
    /// a `git status` per file it touched.
    /// </summary>
    private void StartWatchingRepository(string? root)
    {
        _repoWatcher?.Dispose();
        _repoWatcher = null;

        if (root is null) return;

        // `.git` is a FILE in a submodule or a linked worktree, holding a gitdir
        // pointer rather than the metadata itself. Watching it would then be
        // watching the wrong thing, so those are left to F5 rather than followed
        // to their real directory — a resolver for one line of indirection is
        // more machinery than the case is worth today.
        var metadata = Path.Combine(root, ".git");
        if (!Directory.Exists(metadata)) return;

        // Safe because `Watch` is NON-RECURSIVE (`IncludeSubdirectories = false`).
        // Watching `.git` recursively would mean watching `objects/`, where a
        // single fetch writes thousands of files — and inotify has a per-user
        // watch ceiling. Direct children are exactly what is wanted: HEAD,
        // index, ORIG_HEAD.

        try
        {
            _repoWatcher = _fs.Watch(metadata, _ =>
                Dispatcher.UIThread.Post(QueueVcsRefresh));
        }
        catch
        {
            // Unwatchable metadata is not fatal; the marks simply wait for F5.
        }
    }

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            var generation = _generation;
            _watcher = _fs.Watch(path, change =>
                Dispatcher.UIThread.Post(() => ApplyChange(path, generation, change)));
        }
        catch
        {
            // A directory we cannot watch still lists fine; F5 remains.
        }
    }

    private void ApplyChange(string watchedPath, int generation, FileSystemChange change)
    {
        // Events can arrive after the user has navigated away, or mid-load.
        if (IsLoading || generation != _generation || CurrentPath != watchedPath) return;

        // **Not per-file news, and handled before the child test** — the path
        // on these IS the watched folder, so the check below would discard
        // both.
        if (change.Kind is ChangeKind.Lost or ChangeKind.Gone)
        {
            LostTrack(change.Kind);
            return;
        }

        // Direct children only — nothing nested is on screen.
        //
        // Compared as PATHS, not as strings. LoadListingAsync normalises what it
        // is given, so the two spellings should already agree — but this is the
        // line that silently discarded every event when they did not, and the
        // watcher is the one place where being wrong is invisible rather than
        // loud. Same is what the rest of the application compares paths with.
        if (!Vaktari.Core.FileSystem.PathRules.Same(
                Path.GetDirectoryName(change.Path), watchedPath)) return;

        switch (change.Kind)
        {
            case ChangeKind.Removed:
                RemoveByPath(change.Path);
                break;

            case ChangeKind.Renamed:
                if (change.OldPath is { } old) RemoveByPath(old);
                _ = AddOrUpdateAsync(change.Path, generation);
                break;

            default:
                _ = AddOrUpdateAsync(change.Path, generation);
                break;
        }

        // The row's size and timestamp are updated above, but its version
        // control mark is not — that comes from a subprocess, and re-running it
        // per event would be the per-row `git status` this design exists to
        // avoid. Queue it instead.
        QueueVcsRefresh();
    }

    private DispatcherTimer? _vcsRefresh;

    /// <summary>
    /// Re-reads version-control status a moment after the folder settles.
    ///
    /// **Debounced, and that is the whole point.** A build, a checkout or a
    /// branch switch fires hundreds of watcher events in a second; one
    /// `git status` each would be the same mistake as spawning `xdg-mime` per
    /// row, which once turned a listing into 44 seconds. Each event restarts
    /// the timer, so the subprocess runs once after the storm rather than once
    /// per raindrop.
    /// </summary>
    /// <summary>
    /// Public so a settings save can take effect immediately.
    ///
    /// Turning the decorations off must clear what is already on screen, and
    /// turning them on must populate it — waiting for the next navigation is
    /// the same trap as a setting that lands in a resource and never gets its
    /// applier re-run.
    /// </summary>
    public void RefreshDecorations() => QueueVcsRefresh();

    private void QueueVcsRefresh()
    {
        if (Vcs is null || VirtualPaths.IsVirtual(CurrentPath)) return;

        if (_vcsRefresh is null)
        {
            _vcsRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };

            // Attached ONCE. Wiring it on every call would stack handlers and
            // fire one refresh per event after all — the exact thing the
            // debounce is here to prevent.
            _vcsRefresh.Tick += (_, _) =>
            {
                _vcsRefresh!.Stop();

                // Off the dispatcher for the same reason as the load path: the
                // synchronous head of this call starts a process, and a tick
                // handler runs on the UI thread.
                var path = CurrentPath;
                var generation = _generation;
                var token = _cts?.Token ?? default;

                _ = Task.Run(() => RefreshVcsAsync(path, generation, token));
            };
        }

        _vcsRefresh.Stop();
        _vcsRefresh.Start();
    }

    /// <summary>
    /// The work that has to happen once the dust settles, rather than once per
    /// event.
    ///
    /// **This is what froze the window on a big folder.** Every watcher event
    /// recomputed the look-alike set over the WHOLE listing and rewrote the
    /// count — measured at 28.9 ms a pass — on the UI thread. An extraction, a
    /// build or a large download in the folder on screen fires thousands of
    /// events, and the application stopped answering for as long as it took.
    ///
    /// Neither answer is needed per file: the count is read by eye and a
    /// look-alike pair is about the folder, not about the row that just
    /// arrived. Once things stop moving is soon enough, and the timer restarts
    /// on every event so a steady stream costs one pass at the end rather than
    /// one per file.
    /// </summary>
    private void SettleSoon()
    {
        _settle ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };

        if (!_settleWired)
        {
            _settleWired = true;

            _settle.Tick += (_, _) =>
            {
                _settle!.Stop();

                UpdateCountStatus();
                RefreshConfusable();
            };
        }

        _settle.Stop();
        _settle.Start();
    }

    private DispatcherTimer? _settle;
    private bool _settleWired;

    /// <summary>
    /// The watcher has stopped being able to tell us what changed.
    ///
    /// Lost means events were dropped — the buffer overran — so what is on
    /// screen may be wrong in any direction and the only honest answer is to
    /// read the folder again. Gone means the folder itself is not there any
    /// more, and the rows describe a place that no longer exists; reloading
    /// says so properly, through the same failure path as any other unreadable
    /// folder, rather than leaving the listing sitting on a phantom.
    /// </summary>
    private void LostTrack(ChangeKind kind)
    {
        Console.Error.WriteLine(
            kind == ChangeKind.Gone
                ? $"[vaktari] watch: the folder is gone, reloading · {CurrentPath}"
                : $"[vaktari] watch: events were dropped, reloading · {CurrentPath}");

        Detached(LoadAsync(CurrentPath), "reload");
    }

    private void RemoveByPath(string path)
    {
        var masterIndex = _all.FindIndex(e => e.FullPath == path);
        if (masterIndex >= 0) _all.RemoveAt(masterIndex);

        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].FullPath != path) continue;
            Entries.RemoveAt(i);
            break;
        }

        // **The bands go stale otherwise.** Headers are computed once per
        // rebuild, and a watcher event is not one — so deleting the first row
        // of a band took its heading with it, and a file arriving at the top of
        // one got none. Any download into a grouped folder left the headings
        // wrong until a manual refresh.
        RecomputeGroups(Entries.ToList());

        // **Where the keyboard goes once the row it was on has gone.** Chosen
        // before the delete, applied here, when the row it names is finally on
        // screen without the ones that went — so Delete, Delete, Delete walks
        // down a list the way it does in both references.
        if (_selectAfterRemoval is { } wanted
            && Entries.FirstOrDefault(e => e.FullPath == wanted) is { FullPath: not null } row)
        {
            _selectAfterRemoval = null;

            SelectedEntry = row;

            var selection = SelectedEntries;

            selection.Clear();
            selection.Add(row);
        }

        SettleSoon();
    }

    /// <summary>The row to select once a deletion has finished arriving.</summary>
    private string? _selectAfterRemoval;

    private async Task AddOrUpdateAsync(string path, int generation)
    {
        FileEntry? entry;

        try
        {
            entry = await _fs.GetEntryAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // **This runs on every filesystem event, and it is fire-and-forget.**
            // A file created and deleted between the notification and the stat is
            // ordinary — a build writing temporaries does it constantly — but
            // without this the throw became an unobserved task exception rather
            // than a shrug.
            Vaktari.Core.Quiet.Swallowed("watch", ex);
            return;
        }

        if (entry is not { } value) return;

        // **Asked of the entry, not of its name.** This was `name.StartsWith('.')`
        // — the freedesktop rule — while what governs the listing is the
        // provider's own: Windows excludes by the Hidden and System attributes
        // and treats a leading dot as an ordinary visible file. The two
        // disagreed in both directions on Windows, and the listing lost either
        // way.
        //
        // A dotfile another program wrote — `.editorconfig` from an extraction,
        // `.env` from a terminal — never appeared until F5, and a rename INTO a
        // dotted name removed the row outright, because the removal lands and
        // the re-add was dropped. In the other direction Word's hidden
        // `~$Report.docx` was let in, into a listing defined to exclude it,
        // where it also skewed the item count and survived every filter and
        // re-sort because it had been inserted into _all as well.
        //
        // Hidden OR System, because that is exactly what the Windows
        // enumeration excludes; the flags carry them separately. Asking the
        // entry means there is one rule again rather than two that agree only
        // on Linux.
        if (!ShowHidden && (value.IsHidden || (value.Flags & EntryFlags.System) != 0)) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Re-checked after the await: a listing may have started while we
            // were off fetching the entry, and inserting into it would duplicate
            // whatever the enumeration is about to produce.
            if (IsLoading || generation != _generation) return;

            RemoveByPathSilently(path);

            var masterAt = FindSortedIndex(_all, value);
            _all.Insert(masterAt, value);

            if (MatchesFilter(value))
            {
                var visibleAt = FindSortedIndex(Entries, value);
                Entries.Insert(visibleAt, value);
            }

            // Same reason as the removal above: a row arriving at the top of a
            // band is precisely when a heading has to appear.
            RecomputeGroups(Entries.ToList());

            SettleSoon();
        });
    }
}
