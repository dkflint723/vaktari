using System.Collections.Concurrent;
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

    private readonly ConcurrentStack<IUndoable> _undo = new();
    private readonly ConcurrentStack<IUndoable> _redo = new();

    public bool CanUndo => !_undo.IsEmpty;

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
        var handle = new OperationHandle { Paths = paths };

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
        var handle = new OperationHandle { Paths = paths };

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

        Remember(new UndoRename(target, path));
        return ValueTask.CompletedTask;
    }

    /// <summary>Records something new, and abandons the redo history — see the
    /// Windows implementation for why.</summary>
    public void RecordCreation(string path)
    {
        if (path.Length > 0) Remember(new UndoCreate(Trash, path));
    }

    private void Remember(IUndoable action)
    {
        _redo.Clear();
        _undo.Push(action);
    }

    public bool CanRedo => !_redo.IsEmpty;

    public string? UndoDescription => _undo.TryPeek(out var next) ? next.Describe : null;

    public string? RedoDescription => _redo.TryPeek(out var next) ? next.Describe : null;

    public async ValueTask RedoAsync(CancellationToken ct)
    {
        if (!_redo.TryPop(out var action)) return;

        if (await action.UndoAsync(ct).ConfigureAwait(false) is { } undo) _undo.Push(undo);
    }

    public async ValueTask UndoAsync(CancellationToken ct)
    {
        if (_undo.TryPop(out var action)
            && await action.UndoAsync(ct).ConfigureAwait(false) is { } redo)
            _redo.Push(redo);
    }

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
        var handle = new OperationHandle { Paths = [.. sources, destination] };

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
                        CopyLink(item.Source, target);
                        if (move) File.Delete(item.Source);
                    }
                    else if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                    }
                    else if (CanRename(item.Source, target, move))
                    {
                        // **A move within one filesystem is a rename.** Copying
                        // every byte and deleting the original is correct and
                        // ruinously slow — and the undo of a move has always
                        // used File.Move, so undoing was instant while the move
                        // itself rewrote the file.
                        File.Move(item.Source, target, overwrite: true);

                        // Reported so the bar advances: a rename moves the bytes
                        // without reading any, and a bar stuck at zero through
                        // the fast path reads as a hang.
                        handle.BytesCopied(item.Length);
                    }
                    else
                    {
                        await CopyFileAsync(item.Source, target, handle).ConfigureAwait(false);
                        if (move) File.Delete(item.Source);
                    }

                    // Only the items the user named, and the place they really
                    // went — not destination + name, which is true only when
                    // nothing was renamed or skipped along the way.
                    if (item.IsRoot) landings.Add((item.Source, target));
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

                if (landings.Count == 0)
                {
                    // nothing landed, nothing to take back
                }
                else if (move)
                {
                    Remember(new UndoMove(landings));
                }
                else
                {
                    // **Undoable now, into the bin.** The old note here said a
                    // copy could not be undone because undoing one means
                    // deleting files. True, and the bin is the answer: nothing
                    // is destroyed, and pasting into the wrong folder stops
                    // being a mistake you have to clean up by hand.
                    Remember(new UndoCopy(Trash, landings.Select(l => l.Target).ToList()));
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
    /// </summary>
    private static void CopyLink(string source, string target)
    {
        var pointsAt = new FileInfo(source).LinkTarget ?? new DirectoryInfo(source).LinkTarget;

        if (pointsAt is null) return;
        if (File.Exists(target) || Directory.Exists(target)) return;

        // Which call is decided by what the link points at, since that is what
        // the link itself records.
        if (Directory.Exists(source)) Directory.CreateSymbolicLink(target, pointsAt);
        else File.CreateSymbolicLink(target, pointsAt);
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

    private static async Task CopyFileAsync(string source, string target, OperationHandle handle)
    {
        var buffer = new byte[BufferSize];

        try
        {
            // Scoped so both handles close before the metadata is applied: a
            // timestamp set on an open file is overwritten when the stream
            // flushes.
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, handle.Token).ConfigureAwait(false)) > 0)
                {
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), handle.Token).ConfigureAwait(false);
                    handle.BytesCopied(read);
                }
            }
        }
        catch
        {
            // **A half-written file must not be left under the final name.**
            // The target is opened Create, so it exists and is truncated from
            // the first byte — cancelling a copy of a large file left something
            // that looked like the file, opened, and was silently incomplete.
            Discard(target);
            throw;
        }

        // The executable bit above all: a copied script that will not run is
        // the loss people notice, and a stream copy always drops it.
        FileMetadata.Carry(source, target);
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
    private sealed class UndoMove(
        IReadOnlyList<(string Source, string Target)> landings) : IUndoable
    {
        public string Describe => UndoNames.Of("move", [.. landings.Select(l => l.Target)]);

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            var undone = new List<(string Source, string Target)>();

            foreach (var (source, moved) in landings)
            {
                if (!File.Exists(moved) && !Directory.Exists(moved)) continue;

                XdgTrash.MoveAcrossDevices(moved, source);

                // Reversed for the redo: putting it back means moving it from
                // where it now is to where it had landed.
                undone.Add((moved, source));
            }

            return ValueTask.FromResult<IUndoable?>(
                undone.Count > 0 ? new UndoMove(undone) : null);
        }
    }
}
