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
            _watcher = _fs.Watch(path, change => Queue(path, generation, change));
        }
        catch
        {
            // A directory we cannot watch still lists fine; F5 remains.
        }
    }

    /// <summary>
    /// Takes one event, on whatever thread the watcher raised it on, and folds
    /// it into the burst that <see cref="Drain"/> will apply.
    ///
    /// **This used to post a dispatcher job per event, and each job was a whole
    /// pass over the folder.** A removal ran FindIndex down `_all` and then
    /// walked `Entries` for the same path; an arrival did both again before
    /// inserting; and each ended by copying the entire visible list and
    /// relabelling every group heading from the copy. One file is nothing. An
    /// extraction, a build or a large download is thousands of files in a
    /// second, and thousands of full passes over a 100k-row listing is a window
    /// that has stopped answering.
    ///
    /// Only the FIRST event of a burst posts anything. Everything after it
    /// joins the sets, so the pass sees the whole burst — and it still runs in
    /// the very first dispatcher job the burst produces, which is where the
    /// per-event version ran and what a deletion's follow-up refresh has to
    /// stay behind.
    /// </summary>
    private void Queue(string watchedPath, int generation, FileSystemChange change)
    {
        // **Not per-file news, and handled before the child test** — the path
        // on these IS the watched folder, so the check below would discard
        // both. Posted rather than answered here: reloading is UI-thread work,
        // and this may not be the UI thread.
        if (change.Kind is ChangeKind.Lost or ChangeKind.Gone)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsLoading || generation != _generation || CurrentPath != watchedPath) return;

                LostTrack(change.Kind);
            });

            return;
        }

        // Direct children only — nothing nested is on screen.
        //
        // Compared as PATHS, not as strings. LoadListingAsync normalises what it
        // is given, so the two spellings should already agree — but this is the
        // line that silently discarded every event when they did not, and the
        // watcher is the one place where being wrong is invisible rather than
        // loud. Same is what the rest of the application compares paths with.
        //
        // Answered here rather than on the UI thread because it is pure path
        // arithmetic: an event about something nested now costs nothing at all
        // instead of a dispatcher job that exists only to be dropped.
        if (!Vaktari.Core.FileSystem.PathRules.Same(
                Path.GetDirectoryName(change.Path), watchedPath)) return;

        bool first;

        lock (_pendingGate)
        {
            // **The queue belongs to one listing.** Whatever is left over from
            // the folder we were in is not news about the one we are in now,
            // and the pass reads the generation from here rather than from a
            // closure so that it always judges the batch it is holding.
            if (_pendingGeneration != generation)
            {
                _pendingGone.Clear();
                _pendingHere.Clear();
                _pendingGeneration = generation;
                _pendingPath = watchedPath;
            }

            switch (change.Kind)
            {
                case ChangeKind.Removed:
                    Departing(change.Path);
                    break;

                case ChangeKind.Renamed:
                    if (change.OldPath is { } old) Departing(old);
                    Arriving(change.Path);
                    break;

                default:
                    Arriving(change.Path);
                    break;
            }

            first = !_applyQueued;
            _applyQueued = true;
        }

        if (first) Dispatcher.UIThread.Post(Drain);
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

    /// <summary>The row to select once a deletion has finished arriving.</summary>
    private string? _selectAfterRemoval;

    // ---- one pass per burst, not one pass per file --------------------------

    /// <summary>
    /// What the watcher has said that the listing has not caught up with yet:
    /// paths reported gone, and paths reported there and still to be described.
    ///
    /// Sets rather than a list, so ten writes to one file in one second cost
    /// one stat and one insertion rather than ten of each. Ordinal, which is
    /// what the per-path scans these replace compared paths with.
    ///
    /// Two of them rather than one map, because the two words are not
    /// alternatives: a path reported gone AND then reported back stays in both,
    /// so the old row goes even if the description of the new one fails.
    ///
    /// Written on the watcher's thread and emptied on the UI thread, so every
    /// touch of all four fields below is under <see cref="_pendingGate"/>.
    /// </summary>
    private readonly HashSet<string> _pendingGone = new(StringComparer.Ordinal);

    /// <inheritdoc cref="_pendingGone"/>
    private readonly HashSet<string> _pendingHere = new(StringComparer.Ordinal);

    /// <summary>The listing the queued events belong to. -1 is no listing:
    /// generations start at one.</summary>
    private int _pendingGeneration = -1;

    /// <summary>The folder they were reported for.</summary>
    private string _pendingPath = "";

    private bool _applyQueued;

    private readonly Lock _pendingGate = new();

    private void Arriving(string path) => _pendingHere.Add(path);

    private void Departing(string path)
    {
        // A path that went after it arrived has not arrived. The reverse is not
        // symmetric — see the field — so only this side clears the other.
        _pendingHere.Remove(path);
        _pendingGone.Add(path);
    }

    /// <summary>
    /// Takes the whole burst off the queue and gets it applied, once.
    ///
    /// The very first dispatcher job the burst produces, which is what the
    /// per-event version was too. That matters beyond speed: a finished
    /// deletion posts a refresh of its own from the pool, and a pass that let
    /// itself be overtaken by it would find the listing reloaded underneath and
    /// throw the batch away — taking with it the row Delete had promised the
    /// keyboard.
    /// </summary>
    private void Drain()
    {
        HashSet<string> departures;
        List<string> arriving;
        int generation;
        string watchedPath;

        lock (_pendingGate)
        {
            // Cleared before the work rather than after it, so an event that
            // arrives while this batch is being applied posts a pass of its own
            // instead of being swallowed by this one.
            _applyQueued = false;

            if (_pendingGone.Count == 0 && _pendingHere.Count == 0) return;

            departures = new HashSet<string>(_pendingGone, StringComparer.Ordinal);
            arriving = [.. _pendingHere];
            generation = _pendingGeneration;
            watchedPath = _pendingPath;

            _pendingGone.Clear();
            _pendingHere.Clear();
        }

        // Events can arrive after the user has navigated away, or mid-load.
        if (IsLoading || generation != _generation || CurrentPath != watchedPath) return;

        // The rows' sizes and timestamps are updated below, but their version
        // control marks are not — those come from a subprocess, and re-running
        // it per burst would be the per-row `git status` this design exists to
        // avoid. Queue it instead.
        QueueVcsRefresh();

        // Nothing to describe, and this is already the UI thread: a burst of
        // deletions is applied here rather than paying a task and a trip back
        // through the dispatcher to say so.
        if (arriving.Count == 0)
        {
            ApplyBatch([], departures, generation);
            return;
        }

        _ = StatAndApplyAsync(arriving, departures, generation);
    }

    /// <summary>
    /// Describes everything that arrived — off the UI thread, because a stat is
    /// a syscall and there may be thousands of them — and then hands the whole
    /// batch over in one go.
    /// </summary>
    private async Task StatAndApplyAsync(
        List<string> arriving, HashSet<string> departures, int generation)
    {
        var arrivals = new List<FileEntry>(arriving.Count);

        foreach (var path in arriving)
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
                continue;
            }

            if (entry is not { } value) continue;

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
            if (!ShowHidden && (value.IsHidden || (value.Flags & EntryFlags.System) != 0)) continue;

            // Here rather than when the queue was drained, and only for a row
            // that is actually going in. The stale row for this path has to go
            // or the insertion below doubles it — but a file that vanished
            // before the stat, or one this listing excludes, must leave the row
            // it already has alone, which is what dropping out above rather
            // than here means.
            departures.Add(path);

            arrivals.Add(value);
        }

        await Dispatcher.UIThread.InvokeAsync(
            () => ApplyBatch(arrivals, departures, generation));
    }

    /// <summary>
    /// The whole burst, applied to the two lists in one pass each.
    ///
    /// **This is the pass that used to run once per file.** A hash lookup per
    /// row rather than a list scan per path is what makes a thousand deletions
    /// cost one walk of the listing instead of a thousand.
    /// </summary>
    private void ApplyBatch(List<FileEntry> arrivals, HashSet<string> departures, int generation)
    {
        // Re-checked here as well as in the drain: describing the arrivals goes
        // through an await, and a listing may have started while it did.
        // Inserting into that one would duplicate what the enumeration is about
        // to produce.
        if (IsLoading || generation != _generation) return;

        if (departures.Count > 0)
        {
            _all.RemoveAll(e => departures.Contains(e.FullPath));

            // Backwards, so removing a row cannot slide one we have not looked
            // at yet past the cursor.
            for (var i = Entries.Count - 1; i >= 0; i--)
                if (departures.Contains(Entries[i].FullPath)) Entries.RemoveAt(i);
        }

        foreach (var value in arrivals)
        {
            _all.Insert(FindSortedIndex(_all, value), value);

            if (MatchesFilter(value))
                Entries.Insert(FindSortedIndex(Entries, value), value);
        }

        // **The bands go stale otherwise.** Headers are computed once per
        // rebuild, and a watcher event is not one — so deleting the first row
        // of a band took its heading with it, and a file arriving at the top of
        // one got none. Any download into a grouped folder left the headings
        // wrong until a manual refresh.
        //
        // Once for the whole burst, and against the listing itself rather than
        // a copy of it: both halves of every single event used to hand this a
        // freshly copied list, which allocated a 100k-element array and
        // relabelled every row in the folder for each one file that moved.
        RecomputeGroups(Entries);

        // And the splice, once for the whole burst. **This is a full rebuild,
        // not the incremental insert above it**, and it was measured as one:
        // with a folder open, one arriving file produces no Add notification
        // and one Reset over every row on screen. Paid deliberately — the
        // alternative is a top-level-index-to-projected-index walk per arriving
        // path, which is O(n) per PATH where this is O(n) per BURST, and the
        // burst is what this method exists for. Nothing is paid at all while
        // nothing is open, which is the ordinary case and the reason for the
        // guard below.
        //
        // A folder that has just left is taken out of the open set as well. Not
        // for what is on screen — the splice is derived from the listing, so a
        // row the listing no longer has cannot be drawn — but for what comes
        // back: the set outlives the rows on purpose, and a folder re-created
        // under the same name would otherwise return already open, holding the
        // contents its namesake had before it went.
        if (_open.Count > 0)
        {
            foreach (var path in departures) Forget(path);

            Republish();
        }

        // **Where the keyboard goes once the row it was on has gone.** Chosen
        // before the delete, applied here, when the rows it names are finally
        // on screen without the ones that went — so Delete, Delete, Delete
        // walks down a list the way it does in both references.
        //
        // Only when something actually left: a file arriving while a deletion
        // is still in flight must not consume the row the deletion picked.
        if (departures.Count > 0
            && _selectAfterRemoval is { } wanted
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
}
