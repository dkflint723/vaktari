using System.Collections.Concurrent;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// Copy, move, trash, delete and rename for Linux.
///
/// Everything destructive routes through here so there is exactly one place to
/// get right. Deletion means the XDG trash by default — recoverable from
/// Dolphin, from any trash browser, and from our own undo.
/// </summary>
public sealed class LinuxFileOperations : IFileOperations
{
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Called with a target just before something is renamed onto it: the one
    /// moment a rename leaves between a clash being asked about and the name
    /// being taken. Tests put a file there; the application never sets it.
    /// </summary>
    internal Action<string>? BeforeLanding { get; init; }

    /// <summary>
    /// Called with the path a link is about to be made at. Tests make it throw,
    /// to stand for a filesystem that refuses a link once the name has been
    /// taken; the application never sets it.
    /// </summary>
    internal Action<string>? BeforeLinking { get; init; }

    /// <summary>
    /// Called with (from, to) in place of the undo walk's own rename, and
    /// answering an errno: 0 for done, 17 for a name taken, 18 for another
    /// device. Tests reach the arms that need two filesystems without having
    /// two; the application never sets it.
    /// </summary>
    internal Func<string, string, int>? RenameForUndo { get; init; }

    /// <summary>
    /// How many steps back Undo reaches.
    ///
    /// **There was no ceiling.** Every operation pushed an entry holding the
    /// paths it landed and nothing ever let one go, so a long session carried
    /// its whole history until the window closed. A hundred is far past what
    /// anyone walks back through by hand, and the bin is what covers anything
    /// older.
    /// </summary>
    private const int UndoHistoryLimit = 100;

    private readonly BoundedStack<IUndoable> _undo = new(UndoHistoryLimit);

    // Redo needs no ceiling of its own: it only ever holds what was popped off
    // the undo stack, which is already bounded.
    private readonly ConcurrentStack<IUndoable> _redo = new();

    /// <summary>The open rename group, when a batch rename is running. See
    /// <see cref="BeginRenameGroup"/>.</summary>
    private UndoGroup? _group;

    /// <summary>
    /// **One undo or redo at a time, and neither offered while one runs.** Both
    /// walk the disk, and two walking at once would interleave renames over the
    /// same names — Ctrl+Z held down is enough to ask for it. The rows go quiet
    /// for the duration rather than queueing, because a press that is remembered
    /// and applied later is applied to a folder the person is no longer looking
    /// at.
    /// </summary>
    private int _walking;

    public bool CanUndo => _walking == 0 && !_undo.IsEmpty;

    public IOperationHandle Copy(
        IReadOnlyList<string> sources, string destination,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
        => Run(sources, destination, onConflict, move: false);

    public IOperationHandle Move(
        IReadOnlyList<string> sources, string destination,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
        => Run(sources, destination, onConflict, move: true);

    public IOperationHandle Trash(IReadOnlyList<string> paths)
    {
        var handle = new OperationHandle { Paths = paths, Kind = OperationKind.Trash };

        _ = Task.Run(async () =>
        {
            var restored = new List<(string TrashName, string Original)>();

            try
            {
                handle.Begin(paths.Count, totalBytes: 0);

                foreach (var path in paths)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    handle.ItemStarted(path);

                    // **Per item.** One try wrapped the whole loop, so a single
                    // file the user could not write abandoned every remaining
                    // item in the selection, and the message named the
                    // exception rather than the file.
                    try
                    {
                        var name = XdgTrash.Trash(path);

                        restored.Add((name, path));
                        handle.ItemFinished();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        handle.ItemFailed(path, ex);
                    }
                }

                // **Whatever did reach the bin is undoable**, even when
                // something else did not. Recorded outside the loop but
                // unconditionally on what succeeded: the old code only got here
                // if every single item went, so one failure lost the undo for
                // all the rest as well.
                if (restored.Count > 0)
                    Remember(new UndoTrash(restored));

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    /// <summary>
    /// Irreversible. Only ever reached from an explicit, separate user action —
    /// never as the default for the Delete key.
    /// </summary>
    public IOperationHandle Delete(IReadOnlyList<string> paths)
    {
        var handle = new OperationHandle { Paths = paths, Kind = OperationKind.Delete };

        _ = Task.Run(async () =>
        {
            try
            {
                handle.Begin(paths.Count, totalBytes: 0);

                // A delete has no target to carry: the item's own path is where
                // the retry goes again.
                var failed = new List<RetryRoot>();

                // Which of those were refused for want of permission, and so
                // are the ones pkexec could do anything about. Matched by the
                // same string the root carries rather than by path arithmetic —
                // both come from this loop.
                var denied = new HashSet<string>();

                foreach (var path in paths)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    handle.ItemStarted(path);

                    // Per item, for the same reason as the trash above: a
                    // permanent delete that stops at the first refusal, without
                    // naming it, leaves the user with no idea what went.
                    try
                    {
                        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                        else File.Delete(path);

                        handle.ItemFinished();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        handle.ItemFailed(path, ex);

                        failed.Add(new RetryRoot(path, path, Directory.Exists(path)));

                        if (ex is UnauthorizedAccessException) denied.Add(path);
                    }
                }

                // Before Complete, so a cancelled run offers nothing.
                if (RetryRoots.Outermost(failed) is { Count: > 0 } worthRetrying)
                    handle.Retry = new RetryOffer(
                        worthRetrying.Count,
                        () => Delete([.. worthRetrying.Select(r => r.Source)]),
                        RetryRoots.Administrator(
                            ElevatedVerb.Delete, null, worthRetrying, denied));

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
    {
        // Shared with the Windows twin so the two cannot drift, and so the
        // rules that are genuinely Windows-only stay Windows-only: ext4 takes
        // a colon happily, and refusing one here would stop a Linux user
        // naming a file something their filesystem is perfectly happy with.
        if (FileNames.Refuse(newName) is { } why)
            throw new ArgumentException(why, nameof(newName));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var target = Path.Combine(directory, newName);

        if (target == path) return ValueTask.CompletedTask;

        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"'{newName}' already exists here.");

        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target, overwrite: false);

        var back = new UndoRename(target, path);

        // A batch rename holds a group open, and its renames belong to it
        // rather than each becoming a press of Ctrl+Z of its own.
        if (_group is { } group) group.Add(back);
        else Remember(back);

        return ValueTask.CompletedTask;
    }

    /// <summary>Records something new, and abandons the redo history — see the
    /// Windows implementation for why.</summary>
    public void RecordCreation(string path)
    {
        if (path.Length > 0) Remember(new UndoCreate(Trash, path));
    }

    /// <summary>Bumped by every operation that records itself. A walk reads it
    /// before it starts and again when it ends: if it moved, what the walk did
    /// must not be stacked on top of history it does not belong to. The Windows
    /// twin carries the same counter and the same reasoning.</summary>
    private int _generation;

    private void Remember(IUndoable action)
    {
        _generation++;
        _redo.Clear();
        _undo.Push(action);
    }

    /// <summary>
    /// One undo step for however many renames follow — the Windows twin of
    /// this carries the same shape and the same reasoning.
    /// </summary>
    public IUndoGroup BeginRenameGroup()
    {
        // Groups do not nest: a rename inside two open groups would have to
        // pick a batch to belong to. Closing whatever is still open pushes its
        // renames rather than dropping them on the floor.
        _group?.Dispose();

        return _group = new UndoGroup(this);
    }

    public bool CanRedo => _walking == 0 && !_redo.IsEmpty;

    public string? UndoDescription => _undo.TryPeek(out var next) ? next.Describe : null;

    public string? RedoDescription => _redo.TryPeek(out var next) ? next.Describe : null;

    public async ValueTask RedoAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _walking, 1, 0) != 0) return;

        try
        {
            if (!_redo.TryPop(out var action)) return;

            var generation = _generation;

            try
            {
                var undo = await Walk(action, ct).ConfigureAwait(false);

                if (undo is not null && generation == _generation) _undo.Push(undo);
            }
            catch (PartlyUndone partly)
            {
                if (Stackable(partly, generation))
                {
                    _undo.Push(partly.Done!);
                    if (partly.Left is { } left) _redo.Push(left);
                }

                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _walking, 0);
        }
    }

    public async ValueTask UndoAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _walking, 1, 0) != 0) return;

        try
        {
            if (!_undo.TryPop(out var action)) return;

            var generation = _generation;

            try
            {
                var redo = await Walk(action, ct).ConfigureAwait(false);

                if (redo is not null && generation == _generation) _redo.Push(redo);
            }
            catch (PartlyUndone partly)
            {
                if (Stackable(partly, generation))
                {
                    _redo.Push(partly.Done!);
                    if (partly.Left is { } left) _undo.Push(left);
                }

                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _walking, 0);
        }
    }

    /// <summary>
    /// Whether a partial result may go back on the stacks at all.
    ///
    /// **Nothing moved, so nothing is pushed back on top**: a press that put
    /// nothing back and pushed itself back would wedge Ctrl+Z against its own
    /// obstacle, burying anything recorded since. And if the history moved while
    /// the walk ran, neither half goes anywhere. The Windows twin carries the
    /// same rule with the same words.
    /// </summary>
    /// <summary>
    /// The walk, off the thread that asked for it. The Windows twin carries
    /// the same hop and the same measurement: a cross-volume undo of a large
    /// folder copies every byte, and it did so where the window is drawn.
    /// One hop, not a progress bar — watching it and stopping it is its own
    /// change — but a frozen window is not the price of asking.
    /// </summary>
    private static Task<IUndoable?> Walk(IUndoable action, CancellationToken ct)
        => Task.Run(() => action.UndoAsync(ct).AsTask(), ct);

    private bool Stackable(PartlyUndone partly, int generation)
        => partly.Done is not null && generation == _generation;

    /// <summary>
    /// <paramref name="retrying"/> is the second pass: the items a previous run
    /// could not do, each back to the place THAT run decided to put it. The
    /// Windows twin carries the same parameter and the same reasoning.
    /// </summary>
    private IOperationHandle Run(
        IReadOnlyList<string> sources, string destination,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict, bool move,
        IReadOnlyList<RetryRoot>? retrying = null)
    {
        // Sources and destination together: a copy ONTO a stick claims it
        // through the destination, a move OFF one claims it through the
        // sources, and the eject guard has to see both.
        //
        // **Destination LAST**, which is what a row of its own reads to say
        // where the bytes are going; IOperationHandle.Kind records that
        // arrangement as the contract it now is rather than an accident of how
        // the list was spelled.
        var handle = new OperationHandle
        {
            Paths = [.. sources, destination],
            Kind = move ? OperationKind.Move : OperationKind.Copy,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                // Enumerating first means the progress bar is honest from the
                // start rather than discovering the total as it goes.

                // **A folder cannot be copied or moved into itself.** Neither
                // into itself nor into one of its own subfolders: the plan is
                // built by walking the source, and the destination is inside
                // what is being walked, so a copy feeds itself and a MOVE
                // dismantles the tree it is halfway through reading. Explorer
                // and Dolphin both refuse outright.
                //
                // This also covers dropping a selection onto a folder that is
                // part of that selection — the destination IS one of the
                // sources — which a six-pixel twitch over a selected folder was
                // enough to start.
                //
                // Checked here rather than at each caller so every route is
                // covered at once: Ctrl+V, Copy to, Move to and a drop.
                // Deduplicating into the PARENT is untouched, which is what
                // makes Duplicate still work.
                // Skipped on a retry: the shape of the operation was settled
                // by the run that failed.
                foreach (var source in retrying is null ? sources : [])
                {
                    if (!Directory.Exists(source)) continue;
                    if (!PathRules.Contains(source, destination)) continue;

                    var name = PathRules.LeafName(source);

                    handle.Failed(new IOException(
                        PathRules.Same(source, destination)
                            ? $"\"{name}\" cannot be copied into itself."
                            : $"\"{name}\" cannot be copied into a folder inside it."));

                    return;
                }

                var unreadable = new List<(string Path, Exception Error)>();

                // What a retry would go again on. Fed ONLY by the per-item
                // catch, deliberately — see the Windows twin: a folder the plan
                // could not read is recorded before the redirect map exists, so
                // re-attempting it after a Keep both would merge the subtree
                // into the folder the user asked to keep separate.
                var failed = new List<RetryRoot>();

                // Which of those were refused for want of permission, and so
                // are the ones pkexec could do anything about. Matched by the
                // same string the root carries rather than by path arithmetic —
                // both come from the catch below.
                var denied = new HashSet<string>();

                // On the first pass a root goes to destination + its own name;
                // on a retry it goes back to wherever the failed run decided.
                var roots = retrying is { } again
                    ? again.Select(r => (r.Source, r.Target)).ToList()
                    : sources.Select(source =>
                      {
                          var full = Path.GetFullPath(source);

                          return (full, Path.Combine(destination, Path.GetFileName(full)));
                      }).ToList();

                var plan = BuildPlan(roots, handle.Token, unreadable);

                // Asked before a byte moves, the same as the Windows twin: a
                // copy that fills the disk and then fails leaves a part-written
                // tree and a machine with nothing left. A move within one
                // volume is exempt, being a rename.
                // Asked on a retry too, over the retry's own plan.
                if (!move || !SameVolume([.. roots.Select(r => r.Item1)], destination))
                {
                    var needed = plan.Sum(p => p.Length);

                    if (FreeSpaceOn(destination) is { } free && needed > free)
                    {
                        handle.Failed(new IOException(
                            $"there is not enough room on {PathRules.LeafName(destination)}: "
                            + $"{ByteSize.Format(needed)} needed, {ByteSize.Format(free)} free"));

                        return;
                    }
                }

                handle.Begin(plan.Count, plan.Sum(p => p.Length));

                // Reported before anything is copied: nothing beneath these
                // will be attempted, and the person should know which.
                foreach (var (path, error) in unreadable) handle.ItemFailed(path, error);

                // Where each named item actually landed, and where a renamed
                // folder sends its contents. Both exist because a target is a
                // guess until the conflict at it has been settled.
                var landings = new List<(string Source, string Target)>();
                var redirects = new List<(string From, string To)>();

                // **What an undo takes back: exactly what this run wrote.** It
                // was each root's landing, and a root that already existed was
                // merged into rather than made, so undoing a copy of "photos"
                // onto an existing "photos" sent the whole folder to the bin,
                // the files that were there before included -- measured.
                // A folder merged into is never recorded; what landed in it is,
                // each at the top of what it brought.
                var undoable = new List<(string Source, string Target)>();

                // Targets left as they were because a name turned up at the
                // last moment and was answered Skip. See the removal below.
                var keptBack = new List<string>();
                var mergedInto = new HashSet<string>(PathRules.Comparer);

                // Targets of folders the user chose to skip. Everything planned
                // underneath one of them is skipped too.
                var skippedRoots = new List<string>();

                foreach (var item in plan)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    var target = Redirect(item.Target, redirects);

                    // Under a folder that was skipped: skip it too, before the
                    // conflict prompt can ask about it a second time.
                    if (Under(target, skippedRoots))
                    {
                        handle.ItemFinished();
                        continue;
                    }

                    // Pasting into the folder it already lives in: the target
                    // IS the source, so the prompt could only offer to replace
                    // a file with itself and the copy would open one path for
                    // both reading and writing. A duplicate is what was meant;
                    // a move to where it already is has nothing to do.
                    if (PathRules.Same(item.Source, target))
                    {
                        if (move)
                        {
                            handle.ItemFinished();
                            continue;
                        }

                        // **The rename has to travel down.** Every descendant
                        // was planned against the original name, and without a
                        // redirect each one hit this same branch and was
                        // deduplicated where it stood -- so duplicating a
                        // folder produced an empty "A - Copy" and littered the
                        // ORIGINAL with "x - Copy" twins of every file inside
                        // it, reported as success. The KeepBoth branch below
                        // has always recorded one.
                        var deduped = XdgTrash.Deduplicate(target, item.IsDirectory);

                        if (item.IsDirectory) redirects.Add((target, deduped));

                        target = deduped;
                    }

                    // Whether this item may take the place of what is at its
                    // target, which only an Overwrite answer gives it. See
                    // LandAsync.
                    var replace = false;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        switch (await onConflict(new FileConflict(item.Source, target)).ConfigureAwait(false))
                        {
                            case ConflictResolution.Skip:
                                // **The whole subtree, not just this entry.**
                                // Skipping a folder skipped only the folder
                                // itself: every file planned inside it still
                                // went into the existing folder -- a merge
                                // nobody asked for -- and on a move the source
                                // folder was then deleted as "empty". Skip in
                                // both references leaves the folder untouched
                                // at both ends.
                                //
                                // Nothing landed, so nothing is recorded for
                                // undo — putting back a file the user asked to
                                // leave alone would move the bystander sitting
                                // at that name instead.
                                if (item.IsDirectory) skippedRoots.Add(target);

                                handle.ItemFinished();
                                continue;
                            case ConflictResolution.KeepBoth:
                                // The kind travels with the call: a folder name
                                // is atomic, and without saying so "my.photos"
                                // kept-both as "my (1).photos". The Windows
                                // twin has passed it since the same fault was
                                // found there.
                                var kept = XdgTrash.Deduplicate(target, item.IsDirectory);
                                if (item.IsDirectory) redirects.Add((target, kept));
                                target = kept;
                                break;
                            case ConflictResolution.Cancel:
                                throw new OperationCanceledException();
                            case ConflictResolution.Overwrite:
                                replace = true;

                                // A folder overwritten is a folder merged into.
                                if (item.IsDirectory && Directory.Exists(target))
                                    mergedInto.Add(target);
                                break;
                        }
                    }

                    handle.ItemStarted(item.Source);

                    // **One item's failure is not the batch's.** This block used
                    // to sit inside a single try around the whole loop, so one
                    // unreadable file abandoned every item after it, naming
                    // neither the file nor what was left undone. Cancellation
                    // still ends everything, because that is what was asked for.
                    try
                    {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    if (item.IsLink)
                    {
                        // Reproduced, not followed: the link is the thing being
                        // copied. Moving one deletes the link and leaves what it
                        // pointed at exactly where it was.
                        //
                        // **The link is deleted only once its copy exists.**
                        // CopyLink returned without a word when the name was taken,
                        // and the delete below ran regardless: a clash answered
                        // Overwrite left the bystander standing and the link gone —
                        // measured, onto a taken file and a taken folder alike — and
                        // the lines after this recorded the item as landed and as
                        // undoable all the same. An undo built on that record would
                        // move the bystander to where the link had been. CopyLink
                        // throws now whenever it writes nothing, which skips this
                        // delete and those records together.
                        CopyLink(item.Source, target, replace, BeforeLinking);
                        if (move) File.Delete(item.Source);
                    }
                    else if (item.IsDirectory)
                    {
                        // **Asked before the folder is made, because making it
                        // is what erases the answer.** "Overwrite" on a folder
                        // means merge into it, and CreateDirectory on a folder
                        // that is already there is a no-op — so without this,
                        // carrying below wrote the source folder's tags onto a
                        // folder of the user's own that they had only agreed to
                        // merge into, replacing its Baloo tags with a different
                        // folder's.
                        var made = !Directory.Exists(target);

                        Directory.CreateDirectory(target);

                        // A folder carries user attributes of its own, and a
                        // folder is recreated rather than copied — so this
                        // branch is the only place they could travel. It is
                        // also the only metadata a copied folder gets today:
                        // times and mode on directories were never carried
                        // either, and that is a different hole from this one.
                        if (made) Xattrs.Carry(item.Source, target);
                    }
                    else if (CanRename(item.Source, target, move))
                    {
                        // **A move within one filesystem is a rename.** Copying
                        // every byte and deleting the original is correct and
                        // ruinously slow — and the undo of a move has always
                        // used File.Move, so undoing was instant while the move
                        // itself rewrote the file.
                        if (await LandAsync(item.Source, item.Source, target, replace, onConflict, handle)
                                .ConfigureAwait(false) is not { } renamed)
                        {
                            // Left where it was: a name that turned up at the
                            // last moment was answered Skip.
                            keptBack.Add(target);
                            handle.ItemFinished();
                            continue;
                        }

                        target = renamed;

                        // Reported so the bar advances: a rename moves the bytes
                        // without reading any, and a bar stuck at zero through
                        // the fast path reads as a hang.
                        handle.BytesCopied(item.Length);
                    }
                    else
                    {
                        var staged = await CopyFileAsync(item.Source, target, handle).ConfigureAwait(false);

                        string? landed;

                        try
                        {
                            landed = await LandAsync(staged, item.Source, target, replace, onConflict, handle)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            Discard(staged);
                            throw;
                        }

                        if (landed is null)
                        {
                            Discard(staged);
                            keptBack.Add(target);
                            handle.ItemFinished();
                            continue;
                        }

                        target = landed;

                        if (move) File.Delete(item.Source);
                    }

                    // Only the items the user named, and the place they really
                    // went — not destination + name, which is true only when
                    // nothing was renamed or skipped along the way.
                    if (item.IsRoot) landings.Add((item.Source, target));

                    // At the top of what it brought: a root, or something that
                    // landed directly in a folder merged into. Never the merged
                    // folder itself, which was there before and stays after.
                    if ((item.IsRoot || mergedInto.Contains(PathRules.Parent(target) ?? ""))
                        && !mergedInto.Contains(target))
                        undoable.Add((item.Source, target));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        handle.ItemFailed(item.Source, ex);

                        // The post-redirect target, which is the whole reason
                        // the recipe carries one.
                        failed.Add(new RetryRoot(item.Source, target, item.IsDirectory));

                        if (ex is UnauthorizedAccessException) denied.Add(item.Source);
                    }

                    handle.ItemFinished();
                }

                // Directories are removed only after their contents moved, so a
                // cancelled move never deletes a folder it hasn't emptied.
                if (move)
                {
                    // **Every directory the plan touched, deepest first.**
                    // This used to walk `sources` — the caller's top-level list
                    // — so a nested folder was never a candidate for removal,
                    // and a root is never empty while its own subdirectories
                    // are still standing. A moved tree therefore left its whole
                    // skeleton behind at the source. The Windows twin was fixed
                    // for exactly this and carries a comment saying so; the
                    // port never happened, and there was no Linux test to
                    // notice.
                    //
                    // A link to a directory is not a directory to empty and
                    // remove — Directory.Exists says yes to both.
                    foreach (var directory in plan
                                 .Where(i => i.IsDirectory && !i.IsLink)
                                 .Select(i => i.Source)
                                 .Reverse())
                        if (Directory.Exists(directory)
                            && !IsLink(directory)
                            && !Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                }

                // **What the pane selects when the rows come back.** The list
                // the undo is built from is also the only honest answer to
                // "what did this just put here": a Keep both renamed the
                // arrival, a Skip means nothing arrived under that name at all,
                // and destination-plus-source-name is wrong in both cases.
                //
                // On the undo's own control-flow line, which is what decides
                // when it is NOT reported: past the loop, so every conflict has
                // been settled, and inside the try, which a cancel leaves by
                // throwing. So a run cancelled at a clash remembers no undo and
                // reports no landings even though the items before the clash
                // did land — see A_cancelled_copy_reports_nothing.
                handle.Arrived(landings.Select(l => l.Target));

                // **A folder holding something this run did not write is not
                // the run's to take back.** A name that turned up inside a
                // folder this run made, answered Skip, leaves the source file
                // standing there — and undoing the folder as one thing carries
                // that file off with it, while putting a folder back onto a
                // source folder still standing copies over what is in it.
                // Left out, the undo does less, and less cannot destroy
                // anything.
                if (keptBack.Count > 0)
                    undoable.RemoveAll(u => keptBack.Any(kept => PathRules.Contains(u.Target, kept)));

                if (undoable.Count == 0)
                {
                    // nothing written, nothing to take back
                }
                else if (move)
                {
                    Remember(new UndoMove(undoable, RenameForUndo));
                }
                else
                {
                    // **Undoable now, into the bin.** The old note here said a
                    // copy could not be undone because undoing one means
                    // deleting files. True, and the bin is the answer: nothing
                    // is destroyed, and pasting into the wrong folder stops
                    // being a mistake you have to clean up by hand.
                    Remember(new UndoCopy(Trash, undoable.Select(l => l.Target).ToList()));
                }


                // **Set immediately before Complete**, so a cancelled or failed run
                // leaves it null. The closure carries the SAME conflict callback, so
                // an "apply to the rest" already answered is not asked again.
                if (RetryRoots.Outermost(failed) is { Count: > 0 } worthRetrying)
                    handle.Retry = new RetryOffer(
                        worthRetrying.Count,
                        () => Run(sources, destination, onConflict, move, worthRetrying),
                        RetryRoots.Administrator(
                            move ? ElevatedVerb.Move : ElevatedVerb.Copy,
                            destination, worthRetrying, denied));

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    /// <summary>
    /// **Takes each root WITH the place it is going**, rather than a
    /// destination to derive it from — see the Windows twin. Deriving it here a
    /// second time is how a retry lands in the folder the user asked to keep
    /// separate.
    /// </summary>
    private static List<PlannedItem> BuildPlan(
        IReadOnlyList<(string Source, string Target)> roots, CancellationToken ct,
        List<(string Path, Exception Error)>? unreadable = null)
    {
        var plan = new List<PlannedItem>();

        foreach (var (source, target) in roots)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source);

            // **Before the directory test, because a link to a directory
            // answers to both.** SearchOption.AllDirectories follows symlinks,
            // so a linked folder was descended into and its TARGET's contents
            // copied out - and on a move, deleted from the real folder
            // afterwards. Copying a home directory holding a link to a photo
            // library duplicated the library; moving one emptied it.
            if (IsLink(full))
            {
                plan.Add(new PlannedItem(
                    full, target, 0, IsDirectory: false, IsRoot: true, IsLink: true));
            }
            else if (Directory.Exists(full))
            {
                plan.Add(new PlannedItem(full, target, 0, IsDirectory: true, IsRoot: true));

                foreach (var (path, isDirectory, isLink, length) in Descend(full, ct, unreadable))
                    plan.Add(new PlannedItem(
                        path, Path.Combine(target, Path.GetRelativePath(full, path)),
                        length, isDirectory, IsRoot: false, IsLink: isLink));
            }
            else if (File.Exists(full))
            {
                plan.Add(new PlannedItem(
                    full, target, new FileInfo(full).Length,
                    IsDirectory: false, IsRoot: true));
            }
        }

        return plan;
    }

    /// <summary>
    /// Whether a path is a symbolic link, asked without following it. The same
    /// question LinuxFileSystemProvider.ToFlags asks to set EntryFlags.Symlink.
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes the same link somewhere else, target text and all.
    ///
    /// The target is reproduced VERBATIM, relative links included: rewriting
    /// one to an absolute path would silently change what it means, and a
    /// relative link is usually relative on purpose.
    ///
    /// **It writes the link or it throws.** Both early returns here were silent
    /// — a link whose target could not be read, and a name already taken — and
    /// the caller could not tell "done" from "declined", so it deleted the
    /// source of a move and recorded a landing for a link that was never made.
    ///
    /// <paramref name="replace"/> is true only when the clash was answered
    /// Overwrite. A file or a link at the name is replaced then. A FOLDER is
    /// refused, because a folder may hold anything and emptying one to stand a
    /// link in its place is a larger act than the answer gave; so is the very
    /// file the link points at.
    ///
    /// **Nothing at the name is removed until the link replacing it exists.**
    /// A replacement that removed the original first and then made the link
    /// would leave neither whenever the link is refused — FAT and exFAT take no
    /// symbolic links at all. So the link is made under a staging name beside
    /// the target; only then is the original renamed aside, the link renamed
    /// in, and the original removed, and if the link cannot be renamed in, the
    /// original is renamed back. It is the rule CopyFileAsync keeps for files:
    /// the original is untouched until its replacement exists in full.
    /// </summary>
    /// <param name="beforeLinking">Called with the path a link is about to be
    /// made at. Tests make it throw to stand for a filesystem that refuses the
    /// link; the application passes nothing.</param>
    private static void CopyLink(
        string source, string target, bool replace, Action<string>? beforeLinking = null)
    {
        var pointsAt = new FileInfo(source).LinkTarget ?? new DirectoryInfo(source).LinkTarget
            ?? throw new IOException(
                $"\"{Path.GetFileName(source)}\" is a link whose target could not be read.");

        // A link whose target has gone is still something at the name, though
        // both Exists checks answer false for it: they follow it.
        if (!File.Exists(target) && !Directory.Exists(target) && !IsLink(target))
        {
            beforeLinking?.Invoke(target);
            MakeLink(source, target, pointsAt);
            return;
        }

        if (Directory.Exists(target) && !IsLink(target))
            throw new IOException(
                $"\"{Path.GetFileName(target)}\" is a folder, and a link cannot replace a folder.");

        // Not asked, so not replaced: the clash was settled with the name free,
        // and something has arrived in the moment since.
        if (!replace)
            throw new IOException(
                $"\"{Path.GetFileName(target)}\" turned up at the destination, so the link was left where it was.");

        // **The name may be the very file the link is for.** A link on the
        // desktop to Documents/report.pdf, moved into Documents and answered
        // Overwrite: removing what stood at the name destroyed report.pdf, the
        // only copy, and the link made there pointed at itself — measured on a
        // move, on a copy, and with a relative link. Asked from both ends,
        // because a relative link names a different file from where it stands
        // now than from where it is going.
        if (Names(pointsAt, source, target) || Names(pointsAt, target, target))
            throw new IOException(
                $"\"{Path.GetFileName(target)}\" is the file this link points at, so it was not replaced.");

        var staged = Staging(target);

        beforeLinking?.Invoke(staged);
        MakeLink(source, staged, pointsAt);

        var aside = Staging(target);

        try
        {
            Rename(target, aside);
        }
        catch
        {
            Remove(staged);
            throw;
        }

        try
        {
            Rename(staged, target);
        }
        catch
        {
            try { Rename(aside, target); }
            catch (Exception restoring) { Vaktari.Core.Quiet.Swallowed("file-ops", restoring); }

            Remove(staged);
            throw;
        }

        // The link stands, so the original may go. A failure here is swallowed,
        // as Discard's is: the link has landed, and a leftover under a staging
        // name is not a reason to report it failed.
        Remove(aside);
    }

    /// <summary>Which call is decided by what the link points at, since that is
    /// what the link itself records.</summary>
    private static void MakeLink(string source, string at, string pointsAt)
    {
        if (Directory.Exists(source)) Directory.CreateSymbolicLink(at, pointsAt);
        else File.CreateSymbolicLink(at, pointsAt);
    }

    /// <summary>
    /// Whether a link reading <paramref name="pointsAt"/>, standing at
    /// <paramref name="at"/>, names the same file as <paramref name="target"/>.
    ///
    /// **This asked whether two paths were spelled alike, and one file answers
    /// to many names.** Path.GetFullPath resolves nothing: it collapses
    /// "…/sym/.." to the folder "sym" stands in rather than the folder it
    /// points at, and it says nothing at all about a folder link in the middle
    /// of a path. So a link moved into a symlinked folder — one folder under
    /// two names is ordinary, /home being a link to var/home on Fedora Atomic —
    /// passed this question, and the file at the name was then renamed aside
    /// and deleted to make room for a link that pointed at itself. Measured in
    /// WSL Fedora on a move, on a copy, through a chain of links, and through
    /// ".." past a link.
    ///
    /// The target side is resolved only as far as its FOLDER, deliberately: a
    /// link standing at the name is something this may replace, and following
    /// that last step would compare what it points at rather than the link
    /// itself.
    /// </summary>
    private static bool Names(string pointsAt, string at, string target)
        => PathRules.Same(
            Resolved(Path.Combine(Path.GetDirectoryName(at)!, pointsAt)),
            Path.Combine(Resolved(Path.GetDirectoryName(target)!), Path.GetFileName(target)));

    /// <summary>
    /// An absolute path with every link along it followed: the file itself
    /// rather than one of the names that reach it.
    ///
    /// A component at a time, because a link can stand anywhere along a path
    /// and each one is read from where it stands. ".." is taken from what has
    /// been resolved so far, which is what the kernel does, and what collapsing
    /// the text cannot do.
    ///
    /// <paramref name="budget"/> is ELOOP: links can point in a circle, and a
    /// circle has no file at the end of it. The path built so far is returned
    /// rather than throwing — it still names a link, so the comparison above
    /// simply does not match, which is the right answer when there is no file
    /// to protect.
    /// </summary>
    private static string Resolved(string path, int budget = 40)
    {
        var built = "/";

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is ".") continue;

            if (part is "..")
            {
                built = Path.GetDirectoryName(built) ?? "/";
                continue;
            }

            built = Path.Combine(built, part);

            if (budget <= 0) continue;

            // The last component is followed too: what the caller is asking
            // about is the file at the end, not the link that names it.
            if ((new FileInfo(built).LinkTarget ?? new DirectoryInfo(built).LinkTarget) is { } text)
                built = Resolved(Path.Combine(Path.GetDirectoryName(built)!, text), budget - 1);
        }

        return built;
    }

    /// <summary>Renames the entry itself — a file, or a link of either kind —
    /// and never what a link points at: rename(2) does not follow.</summary>
    private static void Rename(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Removes a file or a link, never what a link points at. Swallows
    /// a failure, because it runs to tidy up, and tidying must neither replace
    /// the reason a replacement failed nor call a landed link a failure.</summary>
    private static void Remove(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("file-ops", e);
        }
    }

    /// <summary>
    /// Everything under a folder, with links as leaves.
    ///
    /// Hand-rolled rather than SearchOption.AllDirectories, which follows links
    /// and walks out of the tree it was asked about - into a photo library, or
    /// round a loop. WindowsFileOperations.Descend exists for the same reason
    /// and this is its twin.
    /// </summary>
    /// <summary>
    /// Walks a tree for the plan, recording what it could not read.
    ///
    /// **It used to swallow and carry on**, so a protected folder made the plan
    /// silently short and the copy reported success having quietly left files
    /// behind. The Windows twin had the opposite fault and threw, ending the
    /// whole operation before anything was copied. Skip and REPORT is the only
    /// honest answer to either.
    /// </summary>
    private static IEnumerable<(string Path, bool IsDirectory, bool IsLink, long Length)> Descend(
        string root, CancellationToken ct, List<(string Path, Exception Error)>? unreadable = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var folder = pending.Pop();

            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(folder).EnumerateFileSystemInfos(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable?.Add((folder, e));
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    yield return (child.FullName, false, true, 0);
                }
                else if (child is DirectoryInfo)
                {
                    yield return (child.FullName, true, false, 0);
                    pending.Push(child.FullName);
                }
                else
                {
                    yield return (child.FullName, false, false, ((FileInfo)child).Length);
                }
            }
        }
    }

    /// <summary>
    /// Whether this item can be moved by renaming rather than rewritten. Pulled
    /// out so the rule can be tested: it decides between an instant operation
    /// and one that reads and writes every byte, and both paths leave the same
    /// files behind, so it is invisible from the outside.
    /// </summary>
    internal static bool CanRename(string source, string target, bool move)
        => move && Volumes.Same(source, target);

    /// <summary>
    /// Removes a partly-written target after a failed or cancelled copy.
    /// Swallows everything: it runs while another exception is on its way up,
    /// and failing to tidy must not replace the reason the copy failed.
    /// </summary>
    private static void Discard(string target)
    {
        try
        {
            if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed("file-ops", ex);
        }
    }

    /// <summary>
    /// Puts <paramref name="from"/> at <paramref name="target"/> and says where
    /// it went, or null when a name that had turned up there was answered Skip.
    ///
    /// **Over what is at the target only when the clash was answered
    /// Overwrite.** A clash is asked about when the engine reaches an item,
    /// and the rename at the end replaced whatever stood at the name by then:
    /// a file saved there while a large copy was on its way was replaced
    /// without a word, and the prompt was never asked. Measured with a 64 MB
    /// copy held half-way, and with a move reached through BeforeLanding. A
    /// name that has turned up since now fails the rename and is asked about
    /// like any other clash.
    /// </summary>
    private async Task<string?> LandAsync(
        string from, string source, string target, bool replace,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict, OperationHandle handle)
    {
        while (true)
        {
            BeforeLanding?.Invoke(target);

            // **Asked before the rename, not after one fails.** A failed
            // rename does not say a name turned up: .NET's own no-overwrite
            // move falls back to copying where a filesystem has no hard
            // links, and a copy that fails part-way leaves a partial file
            // under the real name, while a move whose source has gone throws
            // an IOException too. Both would have read as a late arrival, and
            // Skip would then have kept the wrong file and thrown away the
            // whole one.
            //
            // **The sliver between this question and the rename is not closed
            // here.** .NET's no-overwrite move looks the destination up and
            // then calls rename(2), which replaces silently, so a name that
            // appears between those two syscalls is still replaced without a
            // word: minutes narrowed to microseconds, not to nothing. Closing
            // it needs renameat2(RENAME_NOREPLACE), which this engine does not
            // call yet.
            if (!replace && (File.Exists(target) || Directory.Exists(target)))
            {
                switch (await onConflict(new FileConflict(source, target)).ConfigureAwait(false))
                {
                    // **A file cannot replace a folder**, and Windows says so
                    // with a permission error, which offers "try again as
                    // administrator" for something no privilege can fix.
                    case ConflictResolution.Overwrite when Directory.Exists(target):
                        throw new IOException(
                            $"\"{Path.GetFileName(target)}\" is a folder now, and a file cannot replace a folder.");

                    case ConflictResolution.Overwrite:
                        replace = true;
                        break;

                    // A clash in another folder, as the prompt's own Keep both is.
                    case ConflictResolution.KeepBoth:
                        target = XdgTrash.Deduplicate(target, isDirectory: false);
                        break;

                    case ConflictResolution.Cancel:
                        throw new OperationCanceledException();

                    default:
                        return null;
                }

                // Cancelled while the question stood: the answer is stale, and
                // landing now would leave a file behind that a stopped run no
                // longer counts.
                handle.Token.ThrowIfCancellationRequested();

                continue;
            }

            File.Move(from, target, overwrite: replace);

            return target;
        }
    }

    /// <summary>
    /// The name a copy is written under until it is whole: beside the target,
    /// so the final rename never crosses a filesystem, and marked so a listing can
    /// tell it from a file and a sweep can tell it from anything worth keeping.
    /// </summary>
    internal static string Staging(string target)
        => Path.Combine(
            Path.GetDirectoryName(target) ?? "",
            $".{Path.GetFileName(target)}.vaktari-{Guid.NewGuid():N}");

    private static async Task<string> CopyFileAsync(string source, string target, OperationHandle handle)
    {
        var buffer = new byte[BufferSize];

        // **Written beside the target under a staging name, and renamed over
        // it only once every byte is down.** This wrote straight into the
        // target, opened Create — which truncates from the first byte — and
        // deleted it on failure. A copy that died halfway left a file under
        // the real name that looked complete and was not, until the delete
        // ran; a crash meant it never ran. And an Overwrite truncated the
        // ORIGINAL before a byte of the replacement had been read, so a source
        // that failed at 60% left neither file. rename(2) on one filesystem is
        // atomic: the target is the old file or the whole new one, never a
        // partial, and the original is untouched until the replacement exists
        // in full.
        var staging = Staging(target);

        try
        {
            // Scoped so both handles close before the metadata is applied: a
            // timestamp set on an open file is overwritten when the stream
            // flushes.
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var output = new FileStream(
                staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, handle.Token).ConfigureAwait(false)) > 0)
                {
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), handle.Token).ConfigureAwait(false);
                    handle.BytesCopied(read);
                }
            }

            // Onto the staging file, so what lands under the real name is
            // already complete in every respect — tags, dates and mode.
            //
            // **Extended attributes before the permission bits, never after.**
            // The kernel checks write permission on the inode before it will
            // take a user.* attribute (xattr(7)), and FileMetadata.Carry is
            // exactly what takes that permission away — it reproduces a 0400
            // private key as 0400 — so the reverse order would drop the tags
            // on precisely the files whose modes are most restrictive. The
            // same shape as "times before attributes" inside FileMetadata.
            //
            // Not reproducible on the Windows agent this was written on; the
            // order is pinned by reading this file instead. See
            // ExtendedAttributeTests.The_attributes_are_carried_before_the_mode_is.
            Xattrs.Carry(source, staging);

            // The executable bit above all: a copied script that will not run
            // is the loss people notice, and a stream copy always drops it.
            FileMetadata.Carry(source, staging);

            // Landed by the caller, which alone can ask about a name that has
            // turned up while this was being written. See LandAsync.
            return staging;
        }
        catch
        {
            // Only ever the staging file. The target is either the untouched
            // original or was never created, and both are exactly right.
            Discard(staging);
            throw;
        }
    }

    /// <summary>
    /// <paramref name="IsRoot"/> marks the items the user actually named, as
    /// opposed to everything found underneath them. Only those are undone, so
    /// only those need their landing site recorded.
    /// </summary>
    private readonly record struct PlannedItem(
        string Source, string Target, long Length, bool IsDirectory,
        bool IsRoot = false, bool IsLink = false);

    /// <summary>
    /// Carries a renamed folder down to everything planned inside it.
    ///
    /// **"Keep both" renames the folder; the plan still points its contents at
    /// the old name.** BuildPlan fixes every descendant's target against the
    /// original folder name before any conflict is known about, so without this
    /// the new folder is created empty while the tree merges into the one the
    /// user asked to keep separate — and on a move, that is the source
    /// disappearing into a folder they were trying not to touch.
    ///
    /// The same routine as WindowsFileOperations.Redirect, which has had it
    /// since the day the same fault was found there.
    /// </summary>
    /// <summary>
    /// Whether a planned target sits at or beneath one of these roots. The same
    /// prefix rule <see cref="Redirect"/> uses: the separator is part of the
    /// test, so "work 2" is not treated as living inside "work".
    /// </summary>
    private static bool Under(string target, List<string> roots)
    {
        foreach (var root in roots)
        {
            if (string.Equals(target, root, PathRules.Comparison)) return true;

            if (target.StartsWith(root + Path.DirectorySeparatorChar, PathRules.Comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// What is left on the filesystem a path lives on, or null when it cannot
    /// be asked — a network mount often cannot, and refusing a copy because the
    /// question failed would be worse than trying it.
    /// </summary>
    private static long? FreeSpaceOn(string path)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether every source is on the same filesystem as the destination, in
    /// which case a move is a rename and costs no space.
    ///
    /// By mount point rather than by device: DriveInfo.Name on Linux is the
    /// mount point, which is exactly the boundary a rename cannot cross.
    /// </summary>
    private static bool SameVolume(IReadOnlyList<string> sources, string destination)
    {
        try
        {
            var target = new DriveInfo(Path.GetFullPath(destination)).Name;

            return sources.All(s =>
                string.Equals(new DriveInfo(Path.GetFullPath(s)).Name, target, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Redirect(string target, List<(string From, string To)> redirects)
    {
        foreach (var (from, to) in redirects)
        {
            if (string.Equals(target, from, PathRules.Comparison)) return to;

            var prefix = from + Path.DirectorySeparatorChar;
            if (target.StartsWith(prefix, PathRules.Comparison))
                target = to + target[from.Length..];
        }

        return target;
    }

    /// <summary>
    /// Something that can be put back. Undoing returns what would redo it —
    /// see the Windows implementation, which carries the reasoning.
    /// </summary>
    private interface IUndoable
    {
        ValueTask<IUndoable?> UndoAsync(CancellationToken ct);

        /// <summary>What this would take back, for the menu row and the status
        /// line — see the Windows implementation.</summary>
        string Describe { get; }
    }


    /// <summary>
    /// Undoing a copy, by sending what arrived to the bin.
    ///
    /// **Copies were not undoable at all**, and the reason given was a good
    /// one: undoing a copy means removing files, and an undo that deletes is
    /// not a safe default. Pasting into the wrong folder is one of the easiest
    /// mistakes a file manager lets you make, though, and Ctrl+Z doing nothing
    /// at all is its own kind of unsafe — the files stay where they should not
    /// be, and the person has to find and remove them by hand.
    ///
    /// The bin is what settles it. Explorer undoes a copy the same way, and
    /// nothing is destroyed: what the undo takes away is sitting in the bin,
    /// recoverable, exactly like anything else deleted from the listing.
    ///
    /// Only what this operation actually created, and only if it is still
    /// there — a copy that landed on top of something the user then edited is
    /// not this operation's to remove.
    /// </summary>
    private sealed class UndoCopy(
        Func<IReadOnlyList<string>, IOperationHandle> trash,
        IReadOnlyList<string> landed) : IUndoable
    {
        public string Describe => UndoNames.Of("copy", landed);

        public async ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            var here = landed
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToList();

            if (here.Count == 0) return null;

            await trash(here).Completion.ConfigureAwait(false);

            // No redo. Copying again would be a new act against sources that
            // may have moved on, and the honest way back is the bin.
            return null;
        }
    }

    /// <summary>
    /// Undoing the creation of a folder, a file or a template, the same way —
    /// into the bin.
    ///
    /// New folder, new file and new-from-template all went straight to the
    /// filesystem without passing through this layer, so none of them could be
    /// taken back: Ctrl+Z immediately after Ctrl+Shift+N did nothing.
    /// </summary>
    private sealed class UndoCreate(
        Func<IReadOnlyList<string>, IOperationHandle> trash,
        string created) : IUndoable
    {
        public string Describe => "creating " + PathRules.LeafName(created);

        public async ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            if (!File.Exists(created) && !Directory.Exists(created)) return null;

            await trash([created]).Completion.ConfigureAwait(false);

            return null;
        }
    }

    /// <summary>
    /// Several steps taken back as one — the Windows twin carries the long
    /// note on why the list is walked from its end in both directions, why one
    /// obstructed name is skipped rather than fatal, and why a batch of one
    /// comes back unwrapped.
    /// </summary>
    private sealed class UndoBatch(string describe, IReadOnlyList<IUndoable> steps) : IUndoable
    {
        public string Describe => describe;

        public async ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            var back = new List<IUndoable>(steps.Count);

            for (var i = steps.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (await steps[i].UndoAsync(ct).ConfigureAwait(false) is { } again)
                        back.Add(again);
                }
                // A partial result is not one name failing: it carries the two
                // halves of a walk back to the engine, and swallowing it here
                // would drop both.
                catch (PartlyUndone) { throw; }
                catch (IOException) { /* this one name, and only this one */ }
            }

            if (back.Count == 0) return null;

            return back.Count == 1 ? back[0] : new UndoBatch(describe, back);
        }
    }

    /// <summary>
    /// The open group. Renames land here instead of on the stack until it is
    /// disposed, and then the whole lot goes on as one step.
    /// </summary>
    private sealed class UndoGroup(LinuxFileOperations owner) : IUndoGroup
    {
        private readonly List<IUndoable> _steps = [];

        public string Description { get; set; } = "";

        /// <summary>
        /// The redo goes here rather than waiting for <see cref="Dispose"/>: a
        /// rename that has only joined a group has departed from the history
        /// just as much as one that went straight on the stack. Measured on the
        /// Windows twin, whose history is the same shape.
        /// </summary>
        public void Add(IUndoable step)
        {
            owner._redo.Clear();
            _steps.Add(step);
        }

        public void Dispose()
        {
            // Already closed. Not a `using` running twice — that calls Dispose
            // exactly once whether or not the body threw. Opening a second
            // group force-closes this one, and the caller that owns it then
            // disposes it again on the way out of its own `using`.
            if (owner._group != this) return;

            owner._group = null;

            // Nothing was renamed, so the history has not been departed from
            // and the redo stack keeps what it had — a dialog opened and
            // cancelled must not cost a Ctrl+Y.
            if (_steps.Count == 0) return;

            // Wrapped even when it holds one step, so the row is the name the
            // caller gave rather than the step's own — a batch that stops on
            // the rename after a swap's staging move leaves exactly that move
            // behind, and unwrapped it made the parked file's machine name the
            // whole Undo row. A lone rename gets its own name back on the way
            // out, in UndoBatch.
            owner.Remember(new UndoBatch(Description, _steps));
        }
    }

    private sealed class UndoRename(string current, string original) : IUndoable
    {
        public string Describe => UndoNames.Of("rename", [current]);

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            if (Directory.Exists(current)) Directory.Move(current, original);
            else if (File.Exists(current)) File.Move(current, original, overwrite: false);
            else return ValueTask.FromResult<IUndoable?>(null);

            return ValueTask.FromResult<IUndoable?>(new UndoRename(original, current));
        }
    }

    /// <summary>
    /// **No inverse, deliberately.** Redoing a restore would mean trashing the
    /// files again, and the trash entry they came from is gone — the redo would
    /// create a new one, which is a different act from the one being repeated.
    /// </summary>
    private sealed class UndoTrash(List<(string TrashName, string Original)> items) : IUndoable
    {
        // This side kept the originals from the start, so nothing has to be
        // carried in beside the keys the way the Windows one needs.
        public string Describe => UndoNames.Of("delete", [.. items.Select(i => i.Original)]);

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            foreach (var (trashName, _) in items)
                XdgTrash.Restore(trashName);

            return ValueTask.FromResult<IUndoable?>(null);
        }
    }

    /// <summary>
    /// Puts moved items back where they came from.
    ///
    /// **Takes where they LANDED, not where they were sent.** This used to
    /// reconstruct the landing site as destination + name, which is only true
    /// when nothing was renamed or skipped on the way. Move notes.txt into a
    /// folder that already has one and answer "Keep both": the file lands as
    /// "notes (1).txt", the undo computed "notes.txt", found the pre-existing
    /// bystander sitting there, and moved THAT out — under the name of a file
    /// it had nothing to do with. Answering "Skip" was worse still: the item
    /// the user explicitly refused to move was the one undo relocated.
    ///
    /// Carrying the pairs also fixes the redo. Reconstructing a second time
    /// found nothing to put back, so Ctrl+Y after a bad undo quietly did
    /// nothing while the pane refreshed as though it had worked.
    /// </summary>
    /// <summary>
    /// What an undo or a redo did in part and could not finish. The Windows twin
    /// carries the same two halves and the same reasoning: an IOException on
    /// purpose, so Failures.Describe passes the sentence through, with the
    /// HResult left where the base class puts it.
    /// </summary>
    private sealed class PartlyUndone(string said, IUndoable? done, IUndoable? left)
        : IOException(said)
    {
        /// <summary>What went, as the step that would take it the other way.</summary>
        public IUndoable? Done { get; } = done;

        /// <summary>What did not, as the step that would try it again.</summary>
        public IUndoable? Left { get; } = left;
    }

    /// <summary>One thing an undo does. See <see cref="UndoMove"/>.</summary>
    private abstract record Step;

    /// <summary>Put the entry at <paramref name="From"/> at <paramref name="To"/>,
    /// whatever it is — renamed, never followed.</summary>
    private sealed record Travel(string From, string To) : Step;

    /// <summary>Make a folder at <paramref name="Path"/> if nothing is there,
    /// wearing the mode captured from the folder it stands for.</summary>
    private sealed record MakeFolder(string Path, UnixFileMode Mode) : Step;

    /// <summary>Remove <paramref name="Path"/> only if it is a real folder with
    /// nothing in it — never recursive.</summary>
    private sealed record RemoveIfEmpty(string Path) : Step;

    /// <summary>
    /// The undo of a move, as steps that know how to run themselves backwards.
    /// The Windows twin carries the same model, and the differences below are
    /// the ones the two systems actually have.
    ///
    /// **This went through the trash's cross-device route, which copied whatever
    /// a rename refused.** Measured 2026-09-12 in WSL Fedora: a file written at
    /// the source since left copies in both places with both stacks gone; links
    /// came back as full copies of what they pointed at, a linked library
    /// duplicated whole; and a dangling link stopped the undo part-way.
    ///
    /// Every name is decided by the kernel in one <c>renameat2</c> that will not
    /// replace, which also removes the kind split .NET forces: one call renames
    /// a file, a folder, a link to either, and a link whose target has gone.
    /// </summary>
    private sealed class UndoMove : IUndoable
    {
        private readonly IReadOnlyList<Step> _steps;

        private readonly string _describe;

        private readonly bool _forward;

        private readonly Func<string, string, int>? _renaming;

        /// <summary>What a move records: the pairs it landed, read backwards.</summary>
        public UndoMove(
            IReadOnlyList<(string Source, string Target)> landings,
            Func<string, string, int>? renaming = null)
            : this(
                [.. landings.Select(l => (Step)new Travel(l.Target, l.Source))],
                UndoNames.Of("move", [.. landings.Select(l => l.Target)]),
                forward: false,
                renaming)
        {
        }

        private UndoMove(
            IReadOnlyList<Step> steps, string describe, bool forward, Func<string, string, int>? renaming)
        {
            _steps = steps;
            _describe = describe;
            _forward = forward;
            _renaming = renaming;
        }

        /// <summary>The name of the roots the move was asked about, carried
        /// rather than read off the steps — see the Windows twin.</summary>
        public string Describe => _describe;

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            var inverses = new List<Step>();
            var left = new List<Step>();
            var blocked = new List<(string Name, string? Why)>();
            var notes = new List<string>();

            foreach (var step in _steps)
            {
                ct.ThrowIfCancellationRequested();

                switch (step)
                {
                    case Travel travel: RunTravel(travel, inverses, left, blocked, notes); break;
                    case MakeFolder make: RunMakeFolder(make, inverses, left, blocked); break;
                    case RemoveIfEmpty remove: RunRemoveIfEmpty(remove, inverses, notes); break;
                }
            }

            inverses.Reverse();

            var done = inverses.Count > 0
                ? new UndoMove(inverses, _describe, !_forward, _renaming)
                : null;

            if (left.Count == 0 && notes.Count == 0) return ValueTask.FromResult<IUndoable?>(done);

            throw new PartlyUndone(
                Sentence(blocked, notes),
                done,
                left.Count > 0 ? new UndoMove(left, _describe, _forward, _renaming) : null);
        }

        private void RunTravel(
            Travel travel, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked, List<string> notes)
        {
            FileAttributes attributes;

            try
            {
                // An lstat, and it answers for a dangling link where Exists does
                // not: on Linux File.Exists follows, so a link to nothing reads
                // as nothing at all and would be passed over as already gone —
                // which is how one used to stop the undo part-way.
                attributes = File.GetAttributes(travel.From);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception e)
            {
                Remaining(travel, e, left, blocked);
                return;
            }

            var link = (attributes & FileAttributes.ReparsePoint) != 0;
            var realFolder = (attributes & FileAttributes.Directory) != 0 && !link;

            if (!Ancestors(travel.To, inverses, left, blocked)) { left.Add(travel); return; }

            var errno = Rename(travel.From, travel.To);

            if (errno == 0)
            {
                inverses.Add(new Travel(travel.To, travel.From));
                return;
            }

            if (errno == Renames.NameTaken)
            {
                Taken(travel, realFolder, link, inverses, left, blocked, notes);
                return;
            }

            if (errno == Renames.NotSameDevice)
            {
                Travelling(travel, realFolder, link, inverses, left, blocked, notes);
                return;
            }

            Remaining(travel, Failed(errno, travel.From), left, blocked);
        }

        /// <summary>
        /// The rename, and the one road for a filesystem that has no
        /// RENAME_NOREPLACE.
        ///
        /// **/mnt/c answers EINVAL for a name that is free**, so refusing to work
        /// where the flag is missing would take the undo away from everyone
        /// browsing a Windows drive from WSL. The fallback looks and then
        /// renames, which is the window this class exists to close — accepted
        /// only here, only where there is no other way, and stated rather than
        /// hidden. LandAsync already accepts the same window everywhere else.
        /// </summary>
        private int Rename(string from, string to)
        {
            if (_renaming is { } seam) return seam(from, to);

            var errno = Renames.WithoutReplacing(from, to);

            if (!Renames.NoFlagHere(errno)) return errno;

            // An lstat, so a dangling link at the name counts as taken.
            if (File.Exists(to) || Directory.Exists(to) || IsLink(to)) return Renames.NameTaken;

            return Renames.Plainly(from, to);
        }

        private void Taken(
            Travel travel, bool realFolder, bool link, List<Step> inverses, List<Step> left,
            List<(string Name, string? Why)> blocked, List<string> notes)
        {
            if (realFolder && Directory.Exists(travel.To) && !IsLink(travel.To))
            {
                Merge(travel, make: false, inverses, left, blocked, notes);
                return;
            }

            // **A link pointing at the same place is already put back.** Byte
            // exact, and deliberately not PathRules.Same: a Linux path is a byte
            // string, two spellings are two different targets, and this is the
            // only place the walk removes anything on the strength of comparing
            // text.
            if (link && IsLink(travel.To) && Target(travel.From) is { } mine
                && Target(travel.To) is { } theirs && string.Equals(mine, theirs, StringComparison.Ordinal))
            {
                try
                {
                    // File.Delete removes a link of any kind and leaves what it
                    // points at; Directory.Delete throws at a file or dangling
                    // link, and Remove swallows.
                    File.Delete(travel.From);
                    inverses.Add(new Travel(travel.To, travel.From));
                }
                catch (Exception e)
                {
                    Remaining(travel, e, left, blocked);
                }

                return;
            }

            Remaining(travel, refusal: null, left, blocked);
        }

        /// <summary>
        /// Across a device boundary, where a rename cannot reach.
        ///
        /// **Nothing that landed is ever removed again.** When the copy arrives
        /// and the source will not go, both stand and the sentence names both
        /// places: a failed removal does not prove the source survived — a
        /// server may have removed it and then reported an error — and the
        /// rename here is by path, not by identity, so there is nothing to prove
        /// the copy is ours to take back.
        /// </summary>
        private void Travelling(
            Travel travel, bool realFolder, bool link, List<Step> inverses, List<Step> left,
            List<(string Name, string? Why)> blocked, List<string> notes)
        {
            if (realFolder)
            {
                Merge(travel, make: true, inverses, left, blocked, notes);
                return;
            }

            var staging = Staging(travel.To);

            try
            {
                if (link) CopyLink(travel.From, staging, replace: false);
                else
                {
                    // **File.Copy, not a stream copy, and that decides what has
                    // to be carried afterwards.** On Unix it fchmods and
                    // futimenses the copy itself, so the mode and the times
                    // arrive with it — measured by revert-check, where deleting
                    // a FileMetadata.Carry here left both still correct, which
                    // is a line no test could ever defend. CopyFileAsync needs
                    // that call because it writes through a FileStream, which
                    // carries neither.
                    File.Copy(travel.From, staging);

                    // Extended attributes are the one thing File.Copy does not
                    // take, and after the copy rather than before it, so a
                    // restrictive mode is never in place while they are written.
                    Xattrs.Carry(travel.From, staging);
                }
            }
            catch (Exception e)
            {
                // The staging name is this walk's own, made with a guid a moment
                // ago, so it is the one thing here that may be taken back.
                Discard(staging);
                Remaining(travel, e, left, blocked);
                return;
            }

            if (Rename(staging, travel.To) is var landed && landed != 0)
            {
                Discard(staging);
                Remaining(travel, Failed(landed, travel.To), left, blocked);
                return;
            }

            try
            {
                File.Delete(travel.From);
            }
            catch (Exception e)
            {
                notes.Add(
                    $"{Path.GetFileName(travel.From)} was copied back but the one at "
                    + $"{Path.GetDirectoryName(travel.From)} could not be removed: {e.Message}");
            }

            inverses.Add(new Travel(travel.To, travel.From));
        }

        private void Merge(
            Travel travel, bool make, List<Step> inverses, List<Step> left,
            List<(string Name, string? Why)> blocked, List<string> notes)
        {
            var mine = new List<Step>();
            var created = false;

            if (make)
            {
                switch (RunMakeFolder(new MakeFolder(travel.To, Mode(travel.From)), mine, left, blocked))
                {
                    case Made.Created: created = true; break;
                    case Made.AlreadyThere: break;
                    default: left.Add(travel); return;
                }
            }

            FileSystemInfo[] children;

            try
            {
                children = new DirectoryInfo(travel.From).GetFileSystemInfos();
            }
            catch (Exception e)
            {
                inverses.AddRange(mine);
                Remaining(travel, e, left, blocked);
                left.Add(new RemoveIfEmpty(travel.From));
                return;
            }

            var before = left.Count;

            foreach (var child in children)
                RunTravel(
                    new Travel(child.FullName, Path.Combine(travel.To, child.Name)),
                    mine, left, blocked, notes);

            if (left.Count > before)
            {
                inverses.AddRange(mine);
                left.Add(new RemoveIfEmpty(travel.From));
                return;
            }

            RunRemoveIfEmpty(new RemoveIfEmpty(travel.From), mine, notes);

            // The folder goes back whole, so it comes forward whole — keyed on
            // what was actually made and on the source being gone, both read off
            // the disk. The Windows twin carries the same rule and the measured
            // reason for keying it this way.
            if (created && !Directory.Exists(travel.From))
            {
                inverses.Add(new Travel(travel.To, travel.From));
                return;
            }

            inverses.AddRange(mine);
        }

        private static bool Ancestors(
            string to, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked)
        {
            if (PathRules.Parent(to) is not { } parent) return true;

            var missing = new List<string>();

            for (var at = parent; at is not null; at = PathRules.Parent(at))
            {
                if (Directory.Exists(at) && !IsLink(at)) break;

                missing.Add(at);
            }

            missing.Reverse();

            foreach (var at in missing)
                if (RunMakeFolder(new MakeFolder(at, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                                  inverses, left, blocked) == Made.Refused)
                    return false;

            return true;
        }

        private enum Made { Created, AlreadyThere, Refused }

        private static Made RunMakeFolder(
            MakeFolder make, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked)
        {
            if (Directory.Exists(make.Path) && !IsLink(make.Path)) return Made.AlreadyThere;

            if (File.Exists(make.Path) || IsLink(make.Path))
            {
                left.Add(make);
                blocked.Add((Path.GetFileName(make.Path), null));
                return Made.Refused;
            }

            try
            {
                // **Made narrow, filled, then given its own mode.** A folder
                // created 0555 refuses every child that should go into it, and
                // the creation mode is filtered by the umask in any case, so it
                // is set afterwards rather than asked for up front.
                Directory.CreateDirectory(make.Path);
                File.SetUnixFileMode(make.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                inverses.Add(new RemoveIfEmpty(make.Path));
                return Made.Created;
            }
            catch (Exception e)
            {
                left.Add(make);
                blocked.Add((Path.GetFileName(make.Path), e.Message));
                return Made.Refused;
            }
        }

        private static void RunRemoveIfEmpty(RemoveIfEmpty remove, List<Step> inverses, List<string> notes)
        {
            if (!Directory.Exists(remove.Path) || IsLink(remove.Path)) return;

            var mode = Mode(remove.Path);

            try
            {
                Directory.Delete(remove.Path, recursive: false);
                inverses.Add(new MakeFolder(remove.Path, mode));
            }
            catch (Exception e)
            {
                notes.Add(e.Message);
            }
        }

        private static void Remaining(
            Travel travel, Exception? refusal, List<Step> left, List<(string Name, string? Why)> blocked)
        {
            left.Add(travel);
            blocked.Add((Path.GetFileName(travel.From), refusal?.Message));
        }

        private static string? Target(string path)
        {
            try { return new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget; }
            catch (Exception) { return null; }
        }

        private static UnixFileMode Mode(string path)
        {
            try { return File.GetUnixFileMode(path); }
            catch (Exception) { return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; }
        }

        private static void Discard(string staging)
        {
            try { if (File.Exists(staging) || IsLink(staging)) File.Delete(staging); }
            catch (Exception) { /* a staging name is not worth failing over */ }
        }

        private static IOException Failed(int errno, string path)
            => new($"\"{Path.GetFileName(path)}\" could not be renamed: errno {errno}.");

        private string Sentence(List<(string Name, string? Why)> blocked, List<string> notes)
        {
            var way = _forward ? "go forward" : "go back";

            if (blocked.Count == 0)
                return notes.Count > 0
                    ? $"everything went {(_forward ? "forward" : "back")}, but {notes[0]}"
                    : $"something could not {way}";

            var named = string.Join(", ", blocked.Take(3).Select(b => b.Name));

            if (blocked.Count > 3) named += $" and {blocked.Count - 3} more";

            var why = blocked.FirstOrDefault(b => b.Why is not null).Why;

            return why is not null
                ? $"{named} could not {way}: {why}"
                : $"{named} could not {way}, because something of that name is there now";
        }
    }
}
