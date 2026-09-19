using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Copy, move, delete and rename for Windows. Everything destructive routes
/// through here so there is exactly one place to get right.
///
/// <see cref="Trash"/> recycles, via SHFileOperation — see its own note for why
/// that P/Invoke rather than the modern COM IFileOperation.
///
/// **This header used to say ITrashMaintenance was still null**, so there was
/// no Trash view and no Restore and the bin could only be reached from
/// Explorer. WindowsTrashMaintenance shipped and made every word of that false,
/// and nobody came back to delete it — which is how Ctrl+Z after a delete stayed
/// broken here while working on Linux, with a comment explaining why it could
/// not possibly work.
///
/// **<see cref="Delete"/> is never a stand-in for <see cref="Trash"/>.** There
/// is no BCL API for the Recycle Bin, and the tempting shortcut when the
/// P/Invoke looks like work is to delete instead. Deleting a file the user
/// asked to be able to get back is not a degraded version of trashing it; it is
/// the one outcome the trash exists to prevent. When the recycle route refuses,
/// it reports a failure the UI can show, and the user still has their file.
/// </summary>
public sealed class WindowsFileOperations : IFileOperations
{
    private const int BufferSize = 1 << 20;

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

    /// <summary>
    /// What undoing a copy sends things to the bin with: the Recycle Bin in the
    /// application. A test hands in a recorder instead, because the real one is
    /// the user's own.
    /// </summary>
    internal Func<IReadOnlyList<string>, IOperationHandle>? TrashForUndo { get; init; }

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
    /// Called with (from, to) before each of the undo walk's no-replace renames.
    /// Tests make it throw for one path, to stand for a rename the filesystem
    /// refused — which is how the cross-volume arm of the walk is reached on one
    /// volume, since a Windows folder rename refused for any reason other than a
    /// taken name takes that arm. The application never sets it.
    /// </summary>
    internal Action<string, string>? BeforeRenaming { get; init; }

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

    /// <summary>
    /// Reads the bin, so a recycle can be undone.
    ///
    /// **Injected rather than constructed here** so the undo can be tested
    /// without recycling anything on the machine running the tests.
    /// </summary>
    internal ITrashMaintenance? Bin { get; init; }

    /// <summary>
    /// Stands in for the recycle itself.
    ///
    /// **So the tests do not fill the developer's Recycle Bin.** Everything
    /// worth testing here — which items the undo remembers, what happens when
    /// the bin cannot be read — is about the bookkeeping around
    /// SHFileOperation, not the call, and a test suite that genuinely recycles
    /// leaves litter on the machine that ran it.
    ///
    /// **It returns the shell's own answer rather than a bool.** The wording of
    /// a refusal, and the per-path re-run that names which file refused, are
    /// both decided from the status code, and neither is reachable from
    /// true/false.
    /// </summary>
    internal Func<IReadOnlyList<string>, RecycleResult>? RecycleOverride { get; init; }

    /// <summary>
    /// The Recycle Bin, via SHFileOperation.
    ///
    /// **SHFileOperation rather than IFileOperation**, which is the supported
    /// modern route and is COM. docs/history/WINDOWS.md §6 warns that COM under NativeAOT is
    /// the risky combination and needs source-generated interop or it fails at
    /// runtime rather than at build time. This call is one struct and one
    /// function, source-generated by `LibraryImport` and AOT-clean today.
    /// IFileOperation buys progress and collision UI we do not use here, since
    /// <see cref="OperationHandle"/> already reports progress.
    ///
    /// **FOF_WANTNUKEWARNING is load-bearing, not decoration.** With
    /// FOF_ALLOWUNDO alone, a file too large for the bin — or any file when the
    /// bin is disabled for that drive — is *permanently deleted*, silently,
    /// because FOF_NOCONFIRMATION suppressed the question Explorer would have
    /// asked. WANTNUKEWARNING partially overrides NOCONFIRMATION for exactly
    /// that case: ordinary recycling stays silent, and destruction still has to
    /// be agreed to. Removing it turns this method into <see cref="Delete"/>
    /// without saying so.
    /// </summary>
    /// <summary>
    /// One call to the shell, owning the memory it marshals.
    ///
    /// Extracted because the refusal path calls it AGAIN, once per path, to
    /// learn which file the single status code was about — and one finally at
    /// the top of Trash could only ever free the first allocation.
    /// </summary>
    private static RecycleResult Recycle(IReadOnlyList<string> full)
    {
        var from = Native.DoubleNullTerminated(full);

        try
        {
            var operation = new Native.SHFILEOPSTRUCTW
            {
                wFunc = Native.FO_DELETE,
                pFrom = from,
                fFlags = Native.FOF_ALLOWUNDO
                       | Native.FOF_NOCONFIRMATION
                       | Native.FOF_WANTNUKEWARNING
                       | Native.FOF_SILENT
                       | Native.FOF_NOERRORUI,
            };

            var status = Native.SHFileOperation(ref operation);

            return new RecycleResult(status, operation.fAnyOperationsAborted != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>
    /// Records whatever reached the bin, so it can be taken back out.
    ///
    /// **A refusal used to return before this.** A batch that stumbled on its
    /// last file lost the undo for every file ahead of it, which is precisely
    /// the moment somebody reaches for Ctrl+Z. Wrapped so that no exit from
    /// Trash can skip it by forgetting to repeat two lines.
    /// </summary>
    private void RememberArrivals(
        ITrashMaintenance? bin, HashSet<string>? before, IReadOnlyList<string> asked)
    {
        var landed = Arrivals(bin, before);

        // The names the person used, carried alongside the trash keys. A key
        // here is a $I metadata path, which is not something to put in a menu
        // row — and the undo has to be described from the outside anyway.
        if (landed.Count > 0) Remember(new UndoTrash(bin!, landed, asked));
    }

    public IOperationHandle Trash(IReadOnlyList<string> paths)
    {
        // **Neither, and the recycle below is why.** The whole batch goes
        // through ONE SHFileOperation, which blocks until the shell is done
        // with it: there is no loop between items to await the pause gate in,
        // and the shell reads no cancellation token. The shell's own progress
        // dialog carries a Cancel of its own for a batch big enough to get one,
        // and pressing THAT comes back as Aborted a few lines down.
        var handle = new OperationHandle
        {
            Paths = paths,
            Kind = OperationKind.Trash,
            CanPause = false,
            CanCancel = false,
        };

        _ = Task.Run(() =>
        {
            // **Noted before the recycle, so the undo can find what moved.**
            // SHFileOperation reports nothing about what it recycled, which is
            // why this was undoable on Linux and not here. The bin knows,
            // though: a recycled item appears in the listing with the path it
            // came from, so the difference between the listing before and after
            // names exactly what just went in.
            var bin = Bin;
            var before = Snapshot(bin);

            try
            {
                handle.Begin(paths.Count, totalBytes: 0);

                // SHFileOperation wants absolute paths — a relative one is
                // resolved against the process working directory, which is not
                // the folder being listed.
                // Same stripping, and the shell is no better at it: the path
                // reaching SHFileOperation has already lost the trailing
                // character, so the bin would swallow the neighbouring file.
                // GetFullPath itself is where it happens, which is why the
                // check comes first.
                foreach (var path in paths)
                    if (ReachablePath.Refuse(path) is { } unreachable)
                    {
                        handle.Failed(new IOException(unreachable));
                        return;
                    }

                var full = paths.Select(Path.GetFullPath).ToList();

                RecycleResult Attempt(IReadOnlyList<string> some)
                    => RecycleOverride is { } fake ? fake(some) : Recycle(some);

                var outcome = Attempt(full);

                // Aborted covers the user declining the warning about a file too
                // big for the bin, which is a cancellation rather than a failure.
                if (outcome.Aborted)
                {
                    // **What did go still has to be undoable.** The bin may
                    // already hold everything but the one file the warning was
                    // about, and returning here left all of them with no way
                    // back.
                    RememberArrivals(bin, before, paths);
                    handle.Cancelled();
                    return;
                }

                if (outcome.Status == 0)
                {
                    // One SHFileOperation covers the whole batch, so there is no
                    // per-item progress along the way — but the count the handle
                    // was opened with still has to be reached, or trashing three
                    // files finishes reading "1/3".
                    for (var i = 0; i < full.Count; i++) handle.ItemFinished();
                }
                else if (full.Count == 1)
                {
                    handle.ItemFailed(full[0], RecycleRefusal.For(outcome.Status));
                }
                else
                {
                    // **One number for a whole batch names nothing.** The shell
                    // reports a single int for however many paths it was given,
                    // so a file held open by another program produced
                    // "SHFileOperation returned 32" and left the person to work
                    // out which of their twenty files it was. Asked one at a
                    // time, it answers one at a time.
                    foreach (var one in full)
                    {
                        // Skipped, not re-asked: the batch call may have
                        // recycled several before it refused, and asking again
                        // would answer "not there any more" — a success
                        // reported as a failure.
                        if (!File.Exists(one) && !Directory.Exists(one))
                        {
                            handle.ItemFinished();
                            continue;
                        }

                        var each = Attempt([one]);

                        if (each.Aborted) handle.ItemFailed(one, new OperationCanceledException());
                        else if (each.Status != 0) handle.ItemFailed(one, RecycleRefusal.For(each.Status));
                        else handle.ItemFinished();
                    }
                }

                RememberArrivals(bin, before, paths);

                // Completed with Problems rather than Failed: the ones that went
                // really did go, and the status line reports what was left
                // behind. Delete and the Linux engine already do this.
                handle.Complete();
            }
            catch (Exception ex)
            {
                handle.Failed(ex);
            }
        });

        return handle;
    }

    /// <summary>
    /// The trash names currently in the bin, or null when there is no bin to
    /// ask — which is how the undo stays absent rather than wrong.
    ///
    /// **Keys rather than a listing**, here and in <see cref="Arrivals"/>. Both
    /// ends of the difference used to call List, which on Windows opens and
    /// parses a sidecar per item and stats the payload beside it — twice per
    /// Delete key press, to produce a set of names the directory entries
    /// already held. Timed over a whole <see cref="Trash"/> call against a real
    /// bin holding 107 entries on two volumes, with the recycle itself stubbed
    /// out so only the bookkeeping is counted: 19-24 ms through List against
    /// 0.4-0.8 ms through Keys.
    /// </summary>
    private static HashSet<string>? Snapshot(ITrashMaintenance? bin)
    {
        if (bin is null) return null;

        try
        {
            return bin.Keys().ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A bin that will not answer means no undo entry, never a failed
            // delete: the files really did go where the user asked.
            Vaktari.Core.Quiet.Swallowed("file-ops", ex);
            return null;
        }
    }

    /// <summary>
    /// What appeared in the bin since the snapshot.
    ///
    /// **By difference, not by matching original paths.** Matching on where an
    /// item came from picks the wrong entry when the same name has been deleted
    /// before — and deleting, restoring and deleting again is exactly when
    /// somebody reaches for undo. A trash name is unique and new ones can only
    /// be what just arrived.
    /// </summary>
    private static List<string> Arrivals(ITrashMaintenance? bin, HashSet<string>? before)
    {
        if (bin is null || before is null) return [];

        try
        {
            return bin.Keys().Where(name => !before.Contains(name)).ToList();
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed("file-ops", ex);
            return [];
        }
    }

    /// <summary>
    /// Puts recycled items back where they came from.
    ///
    /// **This was impossible until the bin could be read.** The comment that
    /// stood here said restoring needed a COM decision that was still
    /// outstanding — WindowsTrashMaintenance shipped and made that false, and
    /// nothing came back to remove the claim. So Ctrl+Z after a delete did
    /// nothing on Windows while working on Linux, with no sign of why.
    /// </summary>
    private sealed class UndoTrash(
        ITrashMaintenance bin,
        List<string> trashNames,
        IReadOnlyList<string> originals) : IUndoable
    {
        public string Describe => UndoNames.Of("delete", originals);

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            foreach (var name in trashNames)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    bin.Restore(name);
                }
                catch (Exception ex)
                {
                    // One item that will not come back must not strand the
                    // rest — the same rule the copy engine now follows.
                    Vaktari.Core.Quiet.Swallowed("file-ops", ex);
                }
            }

            // No redo: putting them back in the bin would need the paths they
            // were restored to, and Restore reports where each one went. That
            // is the next increment, not a guess made here.
            return ValueTask.FromResult<IUndoable?>(null);
        }
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
                // the retry goes again, and there is no conflict machinery to
                // land in.
                var failed = new List<RetryRoot>();

                // Which of those were refused for want of permission, and so
                // are the ones an administrator run could do anything about.
                // Matched by the same string the root carries rather than by
                // path arithmetic — both come from this loop, so there is
                // nothing to normalise and nothing to get wrong.
                var denied = new HashSet<string>();

                foreach (var path in paths)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    handle.ItemStarted(path);

                    // **The wrong file, silently.** A name ending in a space or
                    // a dot is stripped by the path layer before the call, so
                    // deleting "report " deletes "report" and leaves "report "
                    // standing. Refused per item rather than for the whole
                    // batch: the other twenty files the user selected are fine
                    // and should still go.
                    if (ReachablePath.Refuse(path) is { } unreachable)
                    {
                        handle.ItemFailed(path, new IOException(unreachable));
                        continue;
                    }

                    // A read-only file refuses to delete on Windows where it
                    // would go on Linux, because the permission is on the file
                    // rather than its directory. The user asked for it to go.
                    //
                    // The whole tree, not just its root: Directory.Delete stops
                    // at the first read-only file inside and throws, leaving
                    // half of what the user asked to remove standing. One
                    // cloned repository is enough to hit that — git writes its
                    // pack files read-only.
                    ClearReadOnlyTree(path);

                    // **Per item, the way the copy engine already does it.**
                    // One try wrapped the whole loop, so a single file open in
                    // another program abandoned every remaining item in the
                    // selection — and the message named the exception rather
                    // than the file, so nobody could tell which one stopped it
                    // or what had already gone.
                    try
                    {
                        if (Directory.Exists(path)) DeleteTree(path);
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

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Let the delete itself report the problem.
        }
    }

    /// <summary>
    /// The same, for everything a recursive delete is about to touch. Reparse
    /// points are cleared but not descended through, for the reason
    /// <see cref="Descend"/> gives — and the delete removes the link rather
    /// than what is behind it, so what is behind it is not its business.
    /// </summary>
    /// <summary>
    /// Internal so the Recycle Bin's own purge uses this rather than growing a
    /// second copy. One rule in one place: a duplicate that drifts is how the
    /// bin came to half-destroy read-only payloads while this path handled them
    /// correctly.
    /// </summary>
    internal static void ClearReadOnlyTree(string path)
    {
        ClearReadOnly(path);

        if (!Directory.Exists(path) || IsLink(path)) return;

        foreach (var (entry, _, _) in Descend(path, CancellationToken.None))
            ClearReadOnly(entry);
    }

    /// <summary>
    /// Removes a folder and everything under it, taking every link out as a
    /// link.
    ///
    /// **Directory.Delete(recursive: true) emptied a folder holding a junction
    /// and then refused to remove the folder itself.** .NET's own walk, on
    /// reaching a child whose reparse tag is IO_REPARSE_TAG_MOUNT_POINT — the
    /// tag a junction carries, sharing it with volume mount points — called
    /// DeleteVolumeMountPoint on it. That call is the mount manager's, and it
    /// fails for a junction: refused outright to a caller without administrator
    /// rights, and answered "the parameter is incorrect" to one who has them,
    /// since a junction to a folder is not a mounted volume. The error was
    /// recorded, the junction removed with RemoveDirectory anyway, the rest of
    /// the folder emptied, and then it threw without removing the top folder.
    ///
    /// Measured on 12 September 2026, .NET 10.0.12, Windows 10.0.26100, on a
    /// folder holding `a.txt` and a junction: unelevated it threw
    /// UnauthorizedAccessException, "Access to the path 'j' is denied";
    /// elevated it threw IOException, "The parameter is incorrect." Both left
    /// the folder standing with both its children gone. What the junction
    /// pointed at was untouched in every run — the fault was never that the
    /// delete went through the link, only that it could not finish.
    ///
    /// So the walk is <see cref="Descend"/>, which reports a reparse point as a
    /// leaf and never goes through one, every leaf goes out through
    /// <see cref="DeleteLink"/>, whose removal is the link and never what is
    /// behind it, and the folders follow deepest-first — Descend yields a
    /// parent before its children, so backwards is bottom-up, the same order
    /// the move engine's source cleanup walks.
    ///
    /// **One thing this does that the framework's delete did not: it stops at
    /// the first thing it cannot remove**, rather than emptying everything it
    /// can reach and reporting the failure afterwards. A tree half-destroyed is
    /// not recoverable and a tree left standing is; the item is reported and
    /// offered back for a retry either way.
    ///
    /// A folder the walk could not read is not passed over in silence, though
    /// Descend itself steps over one: nothing beneath it is removed, so the
    /// folder still has something in it when its turn comes, and
    /// Directory.Delete refuses a folder that is not empty. The item is
    /// reported and offered back for a retry rather than reported done.
    ///
    /// Internal, and shared with the Recycle Bin's purge for the reason
    /// <see cref="ClearReadOnlyTree"/> is shared: one rule in one place. A
    /// second copy of that rule is how the bin came to half-destroy read-only
    /// payloads while this path handled them correctly.
    /// </summary>
    internal static void DeleteTree(string path)
    {
        // Before the folder test, because a junction answers to both, and a
        // walk that took this one for a folder would empty a tree nobody
        // selected. BuildPlan orders its own two tests the same way.
        if (IsLink(path))
        {
            DeleteLink(path);
            return;
        }

        var folders = new List<string>();

        foreach (var (entry, kind, _) in Descend(path, CancellationToken.None))
        {
            if (kind == ItemKind.Directory) folders.Add(entry);
            else DeleteLink(entry);
        }

        // Descend yields a parent before its children, so backwards is
        // deepest-first — and a folder will not go while anything is still in it.
        for (var i = folders.Count - 1; i >= 0; i--) Directory.Delete(folders[i]);

        Directory.Delete(path);
    }

    public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
    {
        // **Every character Windows refuses, not just the separators.** A
        // colon used to reach the filesystem and come back as the raw "The
        // parameter is incorrect."; worse, "d:notes" is drive-RELATIVE, so
        // Path.Combine discarded the folder and the file silently left the
        // listing for the current directory of drive D:.
        if (FileNames.Refuse(newName) is { } why)
            throw new ArgumentException(why, nameof(newName));

        // The name being renamed FROM matters more than the one typed: a
        // trailing space on the existing name means File.Move would rename the
        // neighbour instead, and the row that was clicked would sit there
        // apparently untouched.
        if (ReachablePath.Refuse(path) is { } unreachable)
            throw new IOException(unreachable);

        var directory = PathRules.Parent(Path.GetFullPath(path))
            ?? throw new IOException("A drive root cannot be renamed.");

        var target = Path.Combine(directory, newName);

        // Ordinal, deliberately, where the rest of this file compares paths with
        // PathRules.Same. Same is case-insensitive on Windows, so it calls
        // "readme.txt" and "README.txt" one name and returns here without
        // renaming anything — silently swallowing the exact correction the
        // check below is written to let through.
        if (string.Equals(target, path, StringComparison.Ordinal))
            return ValueTask.CompletedTask;

        // Case-insensitively, so renaming "readme" to "README" is not rejected
        // as already existing — it is the same file, and the rename is exactly
        // how a user fixes the case of a name.
        if ((File.Exists(target) || Directory.Exists(target))
            && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"'{newName}' already exists here.");

        if (Directory.Exists(path)) RenameDirectory(path, target);
        else File.Move(path, target, overwrite: false);

        // Before the undo entry, so a rename that is immediately undone leaves
        // the index where it started rather than one step behind.

        var back = new UndoRename(target, path);

        // A batch rename holds a group open, and its renames belong to it
        // rather than each becoming a press of Ctrl+Z of its own.
        if (_group is { } group) group.Add(back);
        else Remember(back);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Directory.Move, plus the case-only rename it will not do.
    ///
    /// It compares its two paths case-insensitively on Windows and throws
    /// "source and destination path must be different" when they match, so
    /// fixing the case of a folder name has to go through a staging name — the
    /// two-step Explorer performs on your behalf. Files need none of this:
    /// File.Move hands the pair straight to MoveFileEx, which renames in place.
    /// </summary>
    private static void RenameDirectory(string from, string to)
    {
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }

        var parent = PathRules.Parent(to)
            ?? throw new IOException("A drive root cannot be renamed.");

        string staging;
        do
        {
            staging = Path.Combine(parent, $".vaktari-rename-{Guid.NewGuid():N}");
        }
        while (Directory.Exists(staging) || File.Exists(staging));

        Directory.Move(from, staging);

        try
        {
            Directory.Move(staging, to);
        }
        catch
        {
            // Never leave the folder sitting under the staging name: the user
            // would be looking for it by either of the two names they know.
            Directory.Move(staging, from);
            throw;
        }
    }

    /// <summary>
    /// Records something new, and abandons the redo history.
    ///
    /// **Every application with an undo does this.** Once the history has been
    /// departed from, a redo would apply to a state that no longer exists — it
    /// would move a file that has since been renamed, or put back something
    /// that was overwritten in the meantime.
    /// </summary>
    public void RecordCreation(string path)
    {
        if (path.Length > 0) Remember(new UndoCreate(Trash, path));
    }

    /// <summary>
    /// Bumped by every operation that records itself. A walk reads it before it
    /// starts and again when it ends: if it moved, the history is no longer the
    /// one this step belongs to, and what the walk did must not be stacked on
    /// top of it. Remember clears the redo stack, so a paste finishing while an
    /// undo walks would otherwise have its clearing undone by the partial
    /// result landing afterwards.
    /// </summary>
    private int _generation;

    private void Remember(IUndoable action)
    {
        _generation++;
        _redo.Clear();
        _undo.Push(action);
    }

    /// <summary>
    /// One undo step for however many renames follow. See
    /// <see cref="IFileOperations.BeginRenameGroup"/> for why renames and
    /// nothing else.
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

    // Peeked rather than popped, and read on every ask: the menu row is built
    // when the menu opens and the status line is written after the work, so
    // both want the answer as it is now.
    public string? UndoDescription => _undo.TryPeek(out var next) ? next.Describe : null;

    public string? RedoDescription => _redo.TryPeek(out var next) ? next.Describe : null;

    /// <summary>
    /// Puts back what an undo took away.
    ///
    /// The stack is emptied by any new operation, which is what every
    /// application with an undo does: once the history has been departed from,
    /// a redo would apply to a state that no longer exists.
    /// </summary>
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

                // The same guard the partial result gets, and for the same
                // reason: if an operation recorded itself while this walk ran,
                // Remember has cleared the other stack and pushed its own, and
                // stacking this on top of it puts the two in an order the
                // history never had.
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

    /// <summary>
    /// The walk, off the thread that asked for it.
    ///
    /// **Ctrl+Z ran the whole thing where the window is drawn.** A cross-volume
    /// undo of a large folder copies every byte, and it did so on the UI thread:
    /// the window stopped painting and Windows offered to close it. This is one
    /// hop, not a progress bar — there is still no way to watch it or stop it,
    /// which is its own change — but a frozen window is not the price of asking.
    /// </summary>
    private static Task<IUndoable?> Walk(IUndoable action, CancellationToken ct)
        => Task.Run(() => action.UndoAsync(ct).AsTask(), ct);

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

                // As in RedoAsync: an operation recorded while this walk ran has
                // already cleared the redo stack, and this would land on top of
                // the clearing.
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
    /// **Nothing moved, so nothing is pushed back on top.** A press that put
    /// nothing back and then pushed itself back would wedge Ctrl+Z: the next
    /// press meets the same obstacle, and a rename or a trash recorded in
    /// between sits underneath it, so Ctrl+Z would start reversing the person's
    /// own way out. Dropping it instead leaves the older history reachable,
    /// which is what today's behaviour already gives by losing the step.
    ///
    /// And if the history moved while the walk ran, neither half goes anywhere:
    /// stacking what this step did on top of an operation recorded since would
    /// put the two in the wrong order, and Remember has already cleared the redo
    /// stack this would push onto.
    /// </summary>
    private bool Stackable(PartlyUndone partly, int generation)
        => partly.Done is not null && generation == _generation;

    /// <summary>
    /// <paramref name="retrying"/> is the second pass: the items a previous run
    /// could not do, each back to the place THAT run decided to put it.
    ///
    /// **The targets are carried rather than recomputed**, because "sources into
    /// destination" no longer says where anything goes once a Keep both has
    /// renamed the root or a duplicate in place has given it a " - Copy" name.
    /// Recomputing would put the retried items into the folder the user asked to
    /// keep separate — which is the documented fault the redirect map exists to
    /// prevent, arriving a second time by a different road.
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
                // **Checked before the plan is built**, and for the whole
                // operation rather than per item. A copy reads the source by
                // name and writes the target by name; with a trailing space at
                // either end the read would come from the neighbouring file and
                // a move would then delete a source that was never copied. The
                // destination counts too — a folder called "work " is a
                // different folder to Windows than the one on screen.
                //
                // Skipped on a retry: these same paths were accepted by the run
                // that produced the failures, and a retry adds no new ones.
                if (retrying is null)
                    foreach (var path in sources.Append(destination))
                        if (ReachablePath.Refuse(path) is { } unreachable)
                        {
                            handle.Failed(new IOException(unreachable));
                            return;
                        }


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
                //
                // Also skipped on a retry, and for the same reason: the shape
                // of the operation was settled by the run that failed.
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
                // catch, deliberately: a folder the plan could not read is
                // recorded before the redirect map exists, so its target is the
                // pre-conflict guess — and re-attempting it after a Keep both
                // would merge the subtree into the folder the user asked to
                // keep separate. That is the fault the redirect map was written
                // to prevent, and an unreadable folder is the case a retry
                // least often fixes anyway.
                var failed = new List<RetryRoot>();

                // Which of those were refused for want of permission, and so
                // are the ones an administrator run could do anything about.
                // Matched by the same string the root carries rather than by
                // path arithmetic — both come from the catch below, so there is
                // nothing to normalise and nothing to get wrong.
                var denied = new HashSet<string>();

                // On the first pass a root goes to destination + its own name;
                // on a retry it goes back to wherever the failed run decided.
                var roots = retrying is { } again
                    ? again.Select(r => (r.Source, r.Target)).ToList()
                    : sources.Select(source =>
                      {
                          var full = Path.GetFullPath(source);

                          return (full, Path.Combine(destination, PathRules.LeafName(full)));
                      }).ToList();

                var plan = BuildPlan(roots, handle.Token, unreadable);

                // **Asked before a byte moves.** A fifty-gigabyte copy onto a
                // drive with room for thirty filled the disk and then failed
                // somewhere in the middle, leaving a part-written tree and a
                // machine with nothing left. Explorer refuses up front and says
                // how much short it is.
                //
                // A move within one volume is exempt: it is a rename and needs
                // no space at all.
                // Asked on a retry too, over the retry's own plan. A retry is
                // NOT a strict subset of what was measured: nothing under a
                // folder that could not be read was ever counted, so a second
                // pass that finally reads it can be arbitrarily large.
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

                // Reported before anything is copied: these are folders the
                // plan could not see into, so nothing beneath them will be
                // attempted and the person should know which.
                foreach (var (path, error) in unreadable) handle.ItemFailed(path, error);

                // A folder resolved with KeepBoth lands under a name the plan
                // did not know about, and every item beneath it was planned
                // against the old one. Without carrying the change down, the
                // new folder is created empty while the tree merges into the
                // one the user asked to keep separate — which on a move is the
                // source disappearing into it.
                var redirects = new List<(string From, string To)>();

                // Where each top-level source actually ended up, and what was
                // left behind. The undo used to reconstruct the landing site as
                // destination + name, which is only true when nothing was
                // deduplicated or skipped.
                var landings = new List<(string Source, string Target)>();
                var skipped = new List<(string Source, string Target)>();

                // **What an undo takes back: exactly what this run wrote.** It
                // was each root's landing, and a root that already existed was
                // merged into rather than made, so undoing a copy of "photos"
                // onto an existing "photos" sent the whole folder to the bin,
                // the files that were there before included -- measured on both
                // engines. A folder merged into is never recorded; what landed
                // in it is, each at the top of what it brought.
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

                    // **Pasting into the folder it already lives in.** The
                    // target then IS the source, so the conflict prompt could
                    // only offer to replace a file with itself — and Replace
                    // cannot work, because the copy opens the same path for
                    // reading and writing and the error blames "something else
                    // has that file open". Explorer makes a duplicate, which is
                    // plainly what was meant; a MOVE to where it already is has
                    // nothing to do at all.
                    //
                    // **Asked of the entry, not of the spelling.** Path text was
                    // all this compared, so the same folder under a second name
                    // — a junction beside it is enough — walked straight past
                    // it: the name was then "taken" by the entry itself, the
                    // clash was answered, CopyLink did its replace dance on the
                    // source's own directory entry, and the DeleteLink after it
                    // took away what had just landed. 3c9a45c closed the same
                    // fault for a link moved into a DIFFERENT folder reached by
                    // another name; this is the case its tests did not cover.
                    if (SameEntry(item.Source, target))
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
                        // In place: this is the duplicate branch, where the
                        // target IS the source, so " - Copy" is the truth.
                        var deduped = Deduplicate(
                            target, item.Kind == ItemKind.Directory, inPlace: true);

                        if (item.Kind == ItemKind.Directory) redirects.Add((target, deduped));

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
                                skipped.Add((item.Source, target));

                                if (item.Kind == ItemKind.Directory)
                                    skippedRoots.Add(target);

                                handle.ItemFinished();
                                continue;
                            case ConflictResolution.KeepBoth:
                                // A conflict in ANOTHER folder. Nothing here
                                // is a copy of anything the user can see, so
                                // this arrives as "(2)" rather than claiming a
                                // provenance it does not have.
                                var kept = Deduplicate(
                                    target, item.Kind == ItemKind.Directory, inPlace: false);
                                if (item.Kind == ItemKind.Directory)
                                    redirects.Add((target, kept));
                                target = kept;
                                break;
                            case ConflictResolution.Cancel:
                                throw new OperationCanceledException();
                            case ConflictResolution.Overwrite:
                                replace = true;

                                // A folder overwritten is a folder merged into.
                                if (item.Kind == ItemKind.Directory && Directory.Exists(target))
                                    mergedInto.Add(target);
                                break;
                        }
                    }

                    handle.ItemStarted(item.Source);

                    // **One item's failure is not the batch's.** This whole
                    // block used to sit inside a single try around the entire
                    // loop, so copying twelve files with the third open in
                    // another program copied two and abandoned nine — and said
                    // neither which file nor what was left undone. Cancellation
                    // still ends everything, because that is what was asked for.
                    try
                    {
                    Directory.CreateDirectory(PathRules.Parent(target) ?? destination);

                    switch (item.Kind)
                    {
                        case ItemKind.Directory:
                            Directory.CreateDirectory(target);
                            break;

                        case ItemKind.Link:
                            // **The link is deleted only once its copy exists.**
                            // CopyLink was never told how the clash was answered.
                            // A junction moved onto a taken file and answered
                            // Overwrite was not written; moved onto a taken EMPTY
                            // folder, it was laid over that folder without a word
                            // and the source deleted as though all were well. Both
                            // measured. CopyLink is given the answer now and throws
                            // whenever it writes nothing, which skips this delete
                            // and the records below together.
                            CopyLink(item.Source, target, replace, BeforeLinking);
                            if (move) DeleteLink(item.Source);
                            break;

                        default:
                            // **A move within one volume is a rename.** Copying
                            // every byte and deleting the original is correct
                            // and ruinously slow: moving a folder of video
                            // inside one drive rewrote the whole folder. The
                            // giveaway that the trick was already known is that
                            // UNDOING a move has always used File.Move, so the
                            // undo of a fifty-gigabyte move was instant while
                            // the move itself was not.
                            if (CanRename(item.Source, target, move))
                            {
                                if (await LandAsync(item.Source, item.Source, target, replace, onConflict, handle)
                                        .ConfigureAwait(false) is not { } renamed)
                                {
                                    // Left where it was: a name that turned up at
                                    // the last moment was answered Skip.
                                    keptBack.Add(target);
                                    handle.ItemFinished();
                                    continue;
                                }

                                target = renamed;

                                // Reported so the bar still advances: a rename
                                // moves the bytes without reading any, and a
                                // progress bar that sits at zero through the
                                // fast path looks like a hang.
                                handle.BytesCopied(item.Length);
                            }
                            else
                            {
                                var staged = await CopyFileAsync(item.Source, target, handle)
                                    .ConfigureAwait(false);

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

                                if (move)
                                {
                                    ClearReadOnly(item.Source);
                                    File.Delete(item.Source);
                                }
                            }
                            break;
                    }

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
                        // Cancelling means stop, so this one does leave the loop.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Recorded and carried on. A directory that could not be
                        // created also means its contents will fail, and each of
                        // those is reported on its own — noisy, but honest, and
                        // far better than nine files silently not arriving.
                        handle.ItemFailed(item.Source, ex);

                        // The post-redirect target, which is the whole reason
                        // the recipe carries one.
                        failed.Add(new RetryRoot(
                            item.Source, target, item.Kind == ItemKind.Directory));

                        if (ex is UnauthorizedAccessException) denied.Add(item.Source);
                    }

                    handle.ItemFinished();
                }

                // Directories are removed only after their contents moved, so a
                // cancelled move never deletes a folder it has not emptied.
                // Every directory the plan touched, deepest first — checking
                // the top-level sources alone left the whole skeleton standing,
                // because a root is never empty while its own subdirectories
                // are still in it.
                if (move)
                {
                    foreach (var directory in plan
                                 .Where(i => i.Kind == ItemKind.Directory)
                                 .Select(i => i.Source)
                                 .Reverse())
                        if (Directory.Exists(directory)
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
                // that file off with it, while the undo of a move onto a source
                // folder still standing copies over what is in it and then
                // deletes the lot. Left out, the undo does less, and less
                // cannot destroy anything.
                if (keptBack.Count > 0)
                    undoable.RemoveAll(u => keptBack.Any(kept => PathRules.Contains(u.Target, kept)));

                if (undoable.Count == 0)
                {
                    // nothing written, nothing to take back
                }
                else if (move)
                {
                    Remember(new UndoMove(undoable, BeforeRenaming));
                }
                else
                {
                    // **Undoable now, into the bin.** The old note here said a
                    // copy could not be undone because undoing one means
                    // deleting files. True, and the bin is the answer: nothing
                    // is destroyed, and pasting into the wrong folder stops
                    // being a mistake you have to clean up by hand.
                    Remember(new UndoCopy(TrashForUndo ?? Trash, undoable.Select(l => l.Target).ToList()));
                }


                // **Set immediately before Complete**, so a cancelled or failed run
                // leaves it null: somebody who pressed cancel is not asking to be
                // offered the same work back. The closure carries the SAME conflict
                // callback, so an "apply to the rest" already answered is not asked
                // again.
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
    /// **Windows' convention, not freedesktop's.** XdgTrash.Deduplicate produces
    /// "file (1).txt"; Explorer produces "file - Copy.txt" and then
    /// "file - Copy (2).txt". docs/history/WINDOWS.md flagged this as the one place a
    /// platform helper was misleadingly named — the rule differs, so the code
    /// does too rather than being shared.
    /// </summary>
    /// <summary>
    /// A free name beside <paramref name="target"/>.
    ///
    /// **One naming served two different questions, and only one of them is
    /// about copying.** Keeping both across a MOVE named the arrival
    /// "report - Copy.txt" — after an operation that copied nothing and left
    /// nothing behind, in a folder where no "report.txt" of yours had ever
    /// been. The word was describing the mechanism rather than what happened.
    ///
    /// Explorer splits them and this now does too: " - Copy" is what a
    /// duplicate IN PLACE is called, because there the word is true and the two
    /// files really are one beside its copy; a conflict resolved by keeping
    /// both is "(2)", which says only that this is the second thing here
    /// wanting that name.
    ///
    /// No default. A third caller has to say which of the two it is, because
    /// getting it wrong is silent and only shows up in a filename somebody
    /// reads a week later.
    ///
    /// The kind matters: see PathRules.SplitLeaf for why a folder — and a
    /// dotfile — is atomic.
    /// </summary>
    internal static string Deduplicate(string target, bool isDirectory, bool inPlace)
    {
        var directory = PathRules.Parent(target);
        if (directory is null) return target;

        var (stem, extension) = PathRules.SplitLeaf(PathRules.LeafName(target), isDirectory);

        if (inPlace)
        {
            var copy = Path.Combine(directory, $"{stem} - Copy{extension}");

            for (var n = 2; File.Exists(copy) || Directory.Exists(copy); n++)
                copy = Path.Combine(directory, $"{stem} - Copy ({n}){extension}");

            return copy;
        }

        // From two, because the thing already sitting there is the first.
        var arrival = Path.Combine(directory, $"{stem} (2){extension}");

        for (var n = 3; File.Exists(arrival) || Directory.Exists(arrival); n++)
            arrival = Path.Combine(directory, $"{stem} ({n}){extension}");

        return arrival;
    }

    /// <summary>
    /// Carries a renamed ancestor down to the descendants planned against its
    /// old name. Applied in order, so a redirect recorded later is already
    /// expressed in terms of the ones before it and nesting composes.
    /// </summary>
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
    /// What is left on the volume a path lives on, or null when it cannot be
    /// asked — a network share often cannot, and refusing a copy because the
    /// question failed would be worse than trying it.
    /// </summary>
    private static long? FreeSpaceOn(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            return root is { Length: > 0 } ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether every source is on the same volume as the destination,
    /// in which case a move is a rename and costs no space.</summary>
    private static bool SameVolume(IReadOnlyList<string> sources, string destination)
    {
        try
        {
            var target = Path.GetPathRoot(Path.GetFullPath(destination));

            return sources.All(s => string.Equals(
                Path.GetPathRoot(Path.GetFullPath(s)), target, PathRules.Comparison));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the entry at <paramref name="path"/> is a link.
    ///
    /// **The ReparsePoint attribute alone was the answer here, and on Windows
    /// that is not what makes a link.** The walk stopped believing it in
    /// 0633df3 — a link is a reparse point whose tag is a NAME SURROGATE, and
    /// the app execution aliases under WindowsApps, cloud placeholders and
    /// third-party tags are not links but carry the attribute — while these
    /// operations went on believing it. The two then disagreed about the same
    /// entry, and the disagreement decides whether a folder may be replaced by
    /// a link: a folder wearing a tag no filter owns answered "link" here, so
    /// the refusal that protects a folder from being written over was skipped.
    ///
    /// Asked of <see cref="SafeWalk.IsLink"/> now, so there is one answer. The
    /// attribute is read first rather than Exists, because Exists follows and
    /// cannot tell a link from what it names; the Directory bit picks which
    /// kind of info to build and comes from the same lstat.
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            return SafeWalk.IsLink(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reproduces a link rather than what it points at.
    ///
    /// A directory link is reproduced as a junction, via
    /// <see cref="Native.CreateJunction"/>, because that is the one form an
    /// ordinary user can create — Directory.CreateSymbolicLink needs Developer
    /// Mode and would make copying a folder containing a junction fail on a
    /// stock machine. A relative target cannot be expressed as a junction, and
    /// a symbolic link with one was made by someone who held the privilege and
    /// therefore still holds it, so that case goes the BCL route.
    /// </summary>
    /// <param name="beforeLinking">Called with the path a link is about to be
    /// made at. Tests make it throw to stand for a filesystem that refuses the
    /// link; the application passes nothing.</param>
    private static void CopyLink(
        string source, string target, bool replace, Action<string>? beforeLinking = null)
    {
        FileSystemInfo info = Directory.Exists(source)
            ? new DirectoryInfo(source)
            : new FileInfo(source);

        var points = info.LinkTarget ?? throw new IOException(
            $"'{PathRules.LeafName(source)}' is a reparse point with no readable target.");

        // A link whose target has gone still stands at the name, and IsLink is
        // what says so — but NOT for the reason this used to give.
        //
        // *(Corrected 2026-09-18.)* It said "both Exists checks answer false for
        // it: they follow it". Measured since, and recorded in this file's own
        // walk: a dangling JUNCTION answers Directory.Exists TRUE and
        // File.Exists false, reading back [Directory, ReparsePoint] — so Exists
        // does not miss it and does not simply follow. What the pair cannot do
        // is tell a link from the thing it names, and on a dangling FILE link
        // both do answer false. IsLink is here for the second case and for
        // clarity about the first; the condition was right either way, since a
        // third conjunct can only narrow a branch that is already about a free
        // name.
        if (!File.Exists(target) && !Directory.Exists(target) && !IsLink(target))
        {
            beforeLinking?.Invoke(target);
            MakeLinkAt(info, target, points);
            return;
        }

        // **A folder at the name is refused.** It may hold anything, and turning
        // it into a link — or emptying it to make room for one — is a larger act
        // than Overwrite gave. A junction moved onto a taken empty folder was laid
        // straight over it and the source junction deleted as though all were
        // well; measured.
        if (Directory.Exists(target) && !IsLink(target))
            throw new IOException(
                $"\"{PathRules.LeafName(target)}\" is a folder, and a link cannot replace a folder.");

        // Not asked, so not replaced: the clash was settled with the name free,
        // and something has arrived in the moment since.
        if (!replace)
            throw new IOException(
                $"\"{PathRules.LeafName(target)}\" turned up at the destination, so the link was left where it was.");

        // The name may be the very thing the link is for. Replacing it destroys
        // what the link exists to reach and leaves a link pointing at itself —
        // measured on the Linux engine, where a link to a file needs no
        // privilege to make. Asked from both ends, because a relative link names
        // a different place from where it stands than from where it is going.
        if (LinkNames(points, source, target) || LinkNames(points, target, target))
            throw new IOException(
                $"\"{PathRules.LeafName(target)}\" is what this link points at, so it was not replaced.");

        // **Nothing at the name is removed until the link replacing it exists.**
        // Removing the original first would leave neither whenever the link is
        // then refused — a symbolic link without the privilege to make one, or a
        // junction onto a volume that holds no reparse points. So the link is made
        // under a staging name beside the target; only then is the original
        // renamed aside, the link renamed in, and the original removed, and if the
        // link cannot be renamed in, the original is renamed back. The rule
        // CopyFileAsync keeps for files: the original is untouched until its
        // replacement exists in full.
        var staged = Staging(target);

        beforeLinking?.Invoke(staged);
        MakeLinkAt(info, staged, points);

        var aside = Staging(target);

        try
        {
            RenameEntry(target, aside);
        }
        catch
        {
            RemoveEntry(staged);
            throw;
        }

        try
        {
            RenameEntry(staged, target);
        }
        catch
        {
            try { RenameEntry(aside, target); }
            catch (Exception restoring) { Vaktari.Core.Quiet.Swallowed("file-ops", restoring); }

            RemoveEntry(staged);
            throw;
        }

        // The link stands, so the original may go. A failure here is swallowed,
        // as Discard's is: the link has landed, and a leftover under a staging
        // name is not a reason to report it failed.
        RemoveEntry(aside);
    }

    /// <summary>
    /// Makes the link at <paramref name="at"/>: a junction where one can
    /// express it, the BCL's symbolic link otherwise.
    /// </summary>
    private static void MakeLinkAt(FileSystemInfo info, string at, string points)
    {
        if (info is not DirectoryInfo || !Path.IsPathFullyQualified(points))
        {
            if (info is DirectoryInfo) Directory.CreateSymbolicLink(at, points);
            else File.CreateSymbolicLink(at, points);
            return;
        }

        Directory.CreateDirectory(at);

        try
        {
            Native.CreateJunction(at, points);
        }
        catch (IOException)
        {
            // Leave nothing half-made behind before trying the other route.
            Directory.Delete(at);
            Directory.CreateSymbolicLink(at, points);
        }
    }

    /// <summary>
    /// Whether a link reading <paramref name="points"/>, standing at
    /// <paramref name="at"/>, names the same entry as <paramref name="target"/>.
    ///
    /// **This asked whether two paths were spelled alike, and one file answers
    /// to many names.** Path.GetFullPath resolves nothing: it collapses
    /// "…\sym\.." to the folder "sym" stands in rather than the folder it points
    /// at, and it says nothing about a junction or a folder symlink in the
    /// middle of a path.
    ///
    /// The Linux twin of this line was measured destroying the file a link
    /// pointed at, in WSL Fedora, when the link was moved into a folder reached
    /// by another name. **This one was not measured, and no test here can reach
    /// it:** the name being replaced has to be the file a link points at, and
    /// making a file symbolic link on Windows needs Developer Mode or an
    /// elevated run, while a junction is refused one step earlier by the folder
    /// rule. It is written to the same rule as its twin because the fault is
    /// the same on a machine where that link can be made.
    ///
    /// The target side is resolved only as far as its FOLDER, deliberately: a
    /// link standing at the name is something this may replace, and following
    /// that last step would compare what it points at rather than the link
    /// itself.
    /// </summary>
    private static bool LinkNames(string points, string at, string target)
        => PathRules.Same(
            Resolved(Path.Combine(Path.GetDirectoryName(at)!, points)),
            Path.Combine(Resolved(Path.GetDirectoryName(target)!), Path.GetFileName(target)));

    /// <summary>
    /// A path with every link along it followed: the entry itself rather than
    /// one of the names that reach it. The Linux engine's twin of this.
    ///
    /// A component at a time, because a link can stand anywhere along a path
    /// and each one is read from where it stands. ".." is taken from what has
    /// been resolved so far, which is what the filesystem does, and what
    /// collapsing the text cannot do. The root is kept whole rather than split,
    /// so a drive letter and a UNC share both survive the walk.
    ///
    /// <paramref name="budget"/> is the reparse limit: links can point in a
    /// circle, and a circle has nothing at the end of it. The path built so far
    /// is returned rather than throwing — it still names a link, so the
    /// comparison above simply does not match, which is the right answer when
    /// there is nothing to protect.
    /// </summary>
    /// <summary>
    /// Whether two paths name the SAME directory entry, however each is spelled.
    ///
    /// **The folders are resolved and the leaf is not.** Following the last
    /// component would call a link and the thing it points at one entry, which
    /// is the opposite of what every rule here needs; following only the folders
    /// is what turns two spellings of one place into one answer. The same
    /// asymmetry <see cref="LinkNames"/> uses, which is where this fault was
    /// first found and fixed for a different folder.
    /// </summary>
    private static bool SameEntry(string a, string b)
    {
        if (PathRules.Same(a, b)) return true;

        if (PathRules.Parent(a) is not { } here || PathRules.Parent(b) is not { } there) return false;

        return string.Equals(PathRules.LeafName(a), PathRules.LeafName(b), PathRules.Comparison)
            && string.Equals(Resolved(here), Resolved(there), PathRules.Comparison);
    }

    private static string Resolved(string path, int budget = 40)
    {
        var root = Path.GetPathRoot(path);

        // Nothing sensible to walk from: every caller here passes a rooted
        // path, and a relative one is returned as it came.
        if (string.IsNullOrEmpty(root)) return path;

        var built = root;

        foreach (var part in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is ".") continue;

            if (part is "..")
            {
                built = Path.GetDirectoryName(built) ?? root;
                continue;
            }

            built = Path.Combine(built, part);

            if (budget <= 0) continue;

            // The last component is followed too: what the caller is asking
            // about is the entry at the end, not the link that names it.
            if ((new FileInfo(built).LinkTarget ?? new DirectoryInfo(built).LinkTarget) is { } text)
                built = Resolved(Path.Combine(Path.GetDirectoryName(built)!, text), budget - 1);
        }

        return built;
    }

    /// <summary>Renames the entry itself — a file, a junction, or a symbolic
    /// link — and never what a link points at.</summary>
    private static void RenameEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Removes a file or a link, never what a link points at. Swallows
    /// a failure, because it runs to tidy up, and tidying must neither replace
    /// the reason a replacement failed nor call a landed link a failure.</summary>
    private static void RemoveEntry(string path)
    {
        try
        {
            // **File.Exists is false for every directory link**, so a ReadOnly
            // junction or folder symlink kept its mark all the way into
            // DeleteLink's Directory.Delete, which refuses one — and this method
            // swallows, so the refusal was silent and the leftover stayed. The
            // attribute is read instead, which answers for the entry itself and
            // for a link whose target has gone.
            if (Wearing(path, FileAttributes.ReadOnly)) ClearReadOnly(path);

            DeleteLink(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("file-ops", e);
        }
    }

    /// <summary>
    /// Removes the link and never what is behind it, which is the whole reason
    /// <see cref="Descend"/> refuses to walk through one.
    /// </summary>
    private static void DeleteLink(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path);
        else File.Delete(path);
    }

    /// <summary>Whether the entry itself carries <paramref name="mark"/>, asked
    /// of the entry rather than of whatever it may point at, and false when it
    /// cannot be asked at all.</summary>
    private static bool Wearing(string path, FileAttributes mark)
    {
        try { return (File.GetAttributes(path) & mark) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Every entry beneath <paramref name="root"/>, parents before their
    /// children, with reparse points reported as leaves.
    ///
    /// **A junction is not a subdirectory.** SearchOption.AllDirectories walks
    /// straight through one, and the three ways that goes wrong are all
    /// reachable from an ordinary folder: a copy silently duplicates whatever
    /// tree the link points at, a move then deletes the originals out of that
    /// tree, and a junction resolving back to an ancestor never terminates at
    /// all. WindowsSearchProvider and WindowsPropertiesProvider already refuse
    /// to follow them, the latter noting that "a junction can point at an
    /// ancestor, and following one turns a measurement into a loop".
    ///
    /// Parent-before-child is relied on twice over: descendants are planned
    /// after the ancestor whose conflict resolution they inherit, and the
    /// source cleanup walks the same order backwards to empty a tree from the
    /// bottom up.
    /// </summary>
    /// <summary>
    /// Walks a tree for the plan, recording what it could not read.
    ///
    /// **An unreadable folder used to end the whole operation**, thrown from
    /// during planning before a single file had been copied — one protected
    /// directory anywhere under the selection and nothing happened at all. The
    /// Linux twin had the opposite fault: it swallowed and carried on, so the
    /// plan was silently short and the copy reported success having quietly
    /// left files behind. Neither told anyone.
    ///
    /// Now it skips and REPORTS, which is the only honest answer: the rest of
    /// the tree really can be copied, and the person has to learn which part
    /// could not be.
    /// </summary>
    internal static IEnumerable<(string Path, ItemKind Kind, long Length)> Descend(
        string root, CancellationToken ct, List<(string Path, Exception Error)>? unreadable = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var folder = pending.Pop();

            IEnumerable<FileSystemInfo> children;

            try
            {
                children = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable?.Add((folder, e));
                continue;
            }

            foreach (var entry in children)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    yield return (entry.FullName, ItemKind.Link, 0);
                }
                else if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    yield return (entry.FullName, ItemKind.Directory, 0);
                    pending.Push(entry.FullName);
                }
                else
                {
                    yield return (entry.FullName, ItemKind.File, ((FileInfo)entry).Length);
                }
            }
        }
    }

    /// <summary>
    /// **Takes each root WITH the place it is going**, rather than a
    /// destination folder to derive it from. On a first pass the caller derives
    /// that the same way this used to; on a retry it is whatever the run that
    /// failed had settled on, after Keep both and the redirect map have had
    /// their say. Deriving it here a second time is how a retry would land in
    /// the folder the user asked to keep separate.
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

            // Before the directory test, because a junction answers to both.
            if (IsLink(full))
            {
                plan.Add(new PlannedItem(full, target, 0, ItemKind.Link, IsRoot: true));
            }
            else if (Directory.Exists(full))
            {
                plan.Add(new PlannedItem(full, target, 0, ItemKind.Directory, IsRoot: true));

                foreach (var (path, kind, length) in Descend(full, ct, unreadable))
                    plan.Add(new PlannedItem(
                        path, Path.Combine(target, Path.GetRelativePath(full, path)),
                        length, kind, IsRoot: false));
            }
            else if (File.Exists(full))
            {
                plan.Add(new PlannedItem(
                    full, target, new FileInfo(full).Length, ItemKind.File, IsRoot: true));
            }
        }

        return plan;
    }

    /// <summary>
    /// Whether this item can be moved by renaming it rather than rewritten.
    ///
    /// Pulled out so the rule can be tested directly: it is one boolean that
    /// decides between an instant operation and one that reads and writes every
    /// byte, and it is invisible from the outside — both paths produce the same
    /// files.
    /// </summary>
    internal static bool CanRename(string source, string target, bool move)
        => move && Volumes.Same(source, target);

    /// <summary>
    /// Removes a partly-written target after a failed or cancelled copy.
    ///
    /// Swallows everything: this runs while another exception is on its way up,
    /// and a failure to tidy must not replace the reason the copy failed with a
    /// less useful one.
    /// </summary>
    private static void Discard(string target)
    {
        try
        {
            if (File.Exists(target))
            {
                ClearReadOnly(target);
                File.Delete(target);
            }
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed("file-ops", ex);
        }
    }

    /// <summary>
    /// The name a copy is written under until it is whole: beside the target,
    /// so the final rename never crosses a volume, and marked so a listing can
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
        // ran; a crash or a pulled cable meant the delete never ran. And an
        // Overwrite truncated the ORIGINAL before a single byte of the
        // replacement had been read, so a source that failed at 60% — a stick
        // pulled, a share gone, a disk full — left neither file. The comment
        // that used to sit in the catch said as much: "worse on Replace: the
        // original was already gone."
        //
        // A rename on one volume is atomic, so the target is either the old
        // file or the whole new one, never a partial, and the original is
        // untouched until the replacement exists in full.
        var staging = Staging(target);

        try
        {
            // Scoped so both handles are closed before the metadata is applied:
            // a timestamp set on an open file is overwritten when the stream
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
            // already complete in every respect, dates and attributes included.
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
                        target = Deduplicate(target, isDirectory: false, inPlace: false);
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

            // Windows refuses to rename over a read-only file.
            if (replace && File.Exists(target)) ClearReadOnly(target);

            // A name that appears between the question above and this rename
            // fails it, and the item is then reported as a problem rather
            // than asked about again: Windows settles the collision inside
            // the rename, and nothing here can hold that moment open to test
            // an answer for it.
            File.Move(from, target, overwrite: replace);

            return target;
        }
    }

    internal enum ItemKind { File, Directory, Link }

    /// <summary>
    /// <paramref name="IsRoot"/> marks one of the paths the user actually
    /// selected, as opposed to something found underneath one. Only those are
    /// worth recording as landing sites: the undo works per selection, not per
    /// file.
    /// </summary>
    private readonly record struct PlannedItem(
        string Source, string Target, long Length, ItemKind Kind, bool IsRoot);

    /// <summary>
    /// Something that can be put back.
    ///
    /// **Undoing returns what would redo it**, rather than redo being a second
    /// list kept in step by hand. Each of these already knows its own inverse —
    /// a rename back is a rename, a move back is a move with the pair the other
    /// way round — so asking the action itself is the one place that cannot
    /// drift out of agreement with what was actually done.
    ///
    /// Null where there is no honest inverse. Restoring something from the bin
    /// is the case: putting it back would mean trashing it again, and the
    /// original trash entry is gone, so the redo would not be the same act.
    /// </summary>
    private interface IUndoable
    {
        ValueTask<IUndoable?> UndoAsync(CancellationToken ct);

        /// <summary>What this would take back, for the menu row and the status
        /// line. Named by the action itself for the same reason its inverse is:
        /// it is the one place that cannot drift out of agreement with what was
        /// actually done.</summary>
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
        // "creating" rather than "create of": this one reads as a thing that
        // happened rather than as a batch, because it always is exactly one.
        public string Describe => "creating " + PathRules.LeafName(created);

        public async ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            if (!File.Exists(created) && !Directory.Exists(created)) return null;

            await trash([created]).Completion.ConfigureAwait(false);

            return null;
        }
    }

    /// <summary>
    /// Several steps taken back as one.
    ///
    /// **In reverse, and that is not a detail.** A plain renumber is drained
    /// from the far end of its chain — img003 to img004 first — so undoing in
    /// the order the renames happened puts img004 back to img003 before the old
    /// img002 has left that name, and File.Move refuses with "Cannot create a
    /// file when that file already exists". Measured by walking the list
    /// forward and running <c>A_group_of_renames_is_one_undo_step</c>.
    ///
    /// A swap refuses one step later rather than at the first. It is performed
    /// as a to a staging name, b to a, the staged file to b, so going forward
    /// reaches the staging move first and skips it silently — measured, the
    /// staging name is vacant by then (the parked file has gone on to b), so
    /// <see cref="UndoRename"/> takes neither of its branches and moves
    /// nothing. The refusal is the step after: the file now called a is asked
    /// to go back to b, which the parked file is sitting on.
    ///
    /// The list is therefore always walked from its end, in both directions:
    /// the inverses come back in the order the undo ran them, which is already
    /// the reverse of the order a redo has to run them in, so they are kept
    /// exactly as they arrive rather than being turned round again.
    ///
    /// **One name that will not come back does not cost the other thirty-nine.**
    /// The per-file history this replaces lost only the press it was on:
    /// measured on three independent renames with one old name re-taken in the
    /// meantime, the ungrouped stack put two of the three files back and still
    /// offered a redo, while a composite that let the IOException out left
    /// `names=c.txt,x.txt,y.txt,z.txt undo=- redo=-` — <see cref="UndoAsync"/>
    /// pops before it awaits, so the batch was gone as well.
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
                // **A partial result is not one name failing.** It carries the
                // two halves of a walk back to the engine, and swallowing it
                // here would drop both. Inert today, because UndoGroup.Add is
                // only ever reached from RenameAsync; here so it stays true.
                catch (PartlyUndone) { throw; }
                catch (IOException) { /* this one name, and only this one */ }
            }

            if (back.Count == 0) return null;

            // A batch of one is not a batch. Unwrapped, the inverse carries its
            // own name — the name the file has now — where a composite would go
            // on offering the name the batch was given, which after the undo is
            // a name nothing is called any more.
            return back.Count == 1 ? back[0] : new UndoBatch(describe, back);
        }
    }

    /// <summary>
    /// The open group. Renames land here instead of on the stack until it is
    /// disposed, and then the whole lot goes on as one step.
    /// </summary>
    private sealed class UndoGroup(WindowsFileOperations owner) : IUndoGroup
    {
        private readonly List<IUndoable> _steps = [];

        public string Description { get; set; } = "";

        /// <summary>
        /// The redo goes here rather than waiting for <see cref="Dispose"/>: a
        /// rename that has only joined a group has departed from the history
        /// just as much as one that went straight on the stack. Measured
        /// without this line — a rename, an undo, then a group performing two
        /// renames — RedoDescription still answered "rename of old.txt" with
        /// both of the group's files already renamed on disk, and the engine is
        /// one instance for the whole application while the dialog is modal
        /// only to its own window.
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
            // caller gave rather than the step's own. A batch rename that stops
            // on the rename after the staging move that breaks a swap leaves
            // exactly that one move behind, and unwrapping it made the parked
            // file's machine name the whole Undo row — measured as
            // "rename of .vaktari-rename-0123456789abcdef". A lone rename still
            // gets its own name back on the way out, in UndoBatch.
            owner.Remember(new UndoBatch(Description, _steps));
        }
    }

    private sealed class UndoRename(string current, string original) : IUndoable
    {
        // Named by where it is NOW, which is the name on screen — the one the
        // person is looking at when they wonder what Ctrl+Z will do.
        public string Describe => UndoNames.Of("rename", [current]);

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            if (Directory.Exists(current)) RenameDirectory(current, original);
            else if (File.Exists(current)) File.Move(current, original, overwrite: false);
            else return ValueTask.FromResult<IUndoable?>(null);

            // The same rename, the other way round.
            return ValueTask.FromResult<IUndoable?>(new UndoRename(original, current));
        }
    }

    /// <summary>
    /// Takes where things landed rather than where they were sent.
    ///
    /// Reconstructing the landing site as destination + name is wrong the
    /// moment a conflict was resolved: after KeepBoth on `readme.txt` into a
    /// folder that already had one, that arithmetic names the file that was
    /// already there — so the undo moved a bystander back to the source and
    /// left the user's own file sitting under `readme - Copy.txt`.
    /// </summary>
    /// <summary>
    /// What an undo or a redo did in part and could not finish.
    ///
    /// **A part-way failure used to lose the step from both stacks.** The engine
    /// popped before it ran, so an exception out of the walk took Ctrl+Z and
    /// Ctrl+Y away together while the disk had already changed — measured on a
    /// read-only file at the source, and on a file standing where a moved
    /// subfolder went. This carries the two halves back to the engine instead:
    /// <see cref="Done"/> is what went, ready to go the other way, and
    /// <see cref="Left"/> is what did not, ready to be asked again.
    ///
    /// **An IOException on purpose, with the HResult left where the base class
    /// puts it.** <c>Failures.Describe</c> reaches IOException by its message,
    /// so the sentence below is what the pane says; copying a leaf's
    /// SharingViolation onto it would replace the whole sentence with "something
    /// else has that file open". <c>UndoBatch</c> catches IOException to swallow
    /// one name, and has a guard above that catch so it can never swallow this.
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
    /// whatever it is: a file, a folder, or a link of any kind, renamed and never
    /// followed.</summary>
    private sealed record Travel(string From, string To) : Step;

    /// <summary>Make a real folder at <paramref name="Path"/> if nothing is
    /// there, wearing the attributes captured from the folder it stands for.
    /// Its inverse is <see cref="RemoveIfEmpty"/>.</summary>
    private sealed record MakeFolder(string Path, FileAttributes Attributes) : Step;

    /// <summary>Remove <paramref name="Path"/> only if it is a real folder with
    /// nothing in it — never recursive, so it can never take away something this
    /// walk did not put there. Its inverse is <see cref="MakeFolder"/>.</summary>
    private sealed record RemoveIfEmpty(string Path) : Step;

    /// <summary>
    /// The undo of a move, as an ordered list of steps that knows how to run
    /// itself backwards.
    ///
    /// **An undo puts back what the move put there, and never replaces,
    /// overwrites or deletes anything else.** The walk this replaces did the
    /// opposite in four measured ways: it copied back with
    /// <c>File.Copy(overwrite: true)</c>, so a file written at the source since
    /// was destroyed without a word; a read-only file there, or a file standing
    /// where a moved subfolder went, stopped it part-way with both stacks lost;
    /// and a redo after it had merged into a re-created source folder moved that
    /// whole folder, taking a file nobody had moved.
    ///
    /// Every name the walk lands on is decided by the kernel in one call — a
    /// rename that will not replace — and never by asking whether the name is
    /// free and renaming afterwards, which answers for the moment before the one
    /// that matters.
    /// </summary>
    private sealed class UndoMove : IUndoable
    {
        /// <summary>ERROR_FILE_EXISTS and ERROR_ALREADY_EXISTS, the two ways
        /// Windows says the name is taken.</summary>
        private const int FileExists = unchecked((int)0x80070050);

        private const int AlreadyExists = unchecked((int)0x800700B7);

        private readonly IReadOnlyList<Step> _steps;

        private readonly string _describe;

        private readonly bool _forward;

        private readonly Action<string, string>? _beforeRenaming;

        /// <summary>What a move records: the pairs it landed, read backwards.</summary>
        public UndoMove(
            IReadOnlyList<(string Source, string Target)> moved, Action<string, string>? beforeRenaming = null)
            : this(
                [.. moved.Select(m => (Step)new Travel(m.Target, m.Source))],
                UndoNames.Of("move", [.. moved.Select(m => m.Target)]),
                forward: false,
                beforeRenaming)
        {
        }

        private UndoMove(
            IReadOnlyList<Step> steps, string describe, bool forward, Action<string, string>? beforeRenaming)
        {
            _steps = steps;
            _describe = describe;
            _forward = forward;
            _beforeRenaming = beforeRenaming;
        }

        /// <summary>
        /// The name of the roots this holds part of, carried rather than
        /// recomputed. Recomputed from the steps it would read "move of 5 items"
        /// after a merge walk, name a child file, or — since
        /// <c>UndoNames.Of(verb, 0)</c> answers "move of 0 items" — say "0 items"
        /// for a folder with nothing in it.
        /// </summary>
        public string Describe => _describe;

        public ValueTask<IUndoable?> UndoAsync(CancellationToken ct)
        {
            var inverses = new List<Step>();
            var left = new List<Step>();

            // What could not go, each with its own reason. One list rather than
            // two, because a name and a reason held apart get paired by position
            // and the sentence then blames one entry for another's refusal.
            var blocked = new List<(string Name, string? Why)>();

            // What was left behind but is not waiting to be tried again: a
            // folder that would not go holds nothing this walk put there.
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

            // Reversed: the log of what was done, read backwards, is what takes
            // it the other way.
            inverses.Reverse();

            var done = inverses.Count > 0
                ? new UndoMove(inverses, _describe, !_forward, _beforeRenaming)
                : null;

            // **A note is not nothing.** Everything asked for went, but something
            // was left standing that the walk could not take away, and saying so
            // is the whole of what is owed for it. Dropping it here would leave a
            // folder behind with the press reporting plain success.
            if (left.Count == 0 && notes.Count == 0) return ValueTask.FromResult<IUndoable?>(done);

            throw new PartlyUndone(
                Sentence(blocked, notes),
                done,
                left.Count > 0 ? new UndoMove(left, _describe, _forward, _beforeRenaming) : null);
        }

        /// <summary>
        /// One entry, from where it stands to where it belongs.
        ///
        /// The order is the whole of the rule: ask the disk what is at
        /// <c>From</c> without following it, try the rename that will not
        /// replace, and only once the kernel has said the name is taken ask what
        /// is standing there.
        /// </summary>
        private void RunTravel(
            Travel travel, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked, List<string> notes)
        {
            FileAttributes attributes;

            try
            {
                // An lstat, and one question rather than two: it says both
                // whether anything is there and what kind it is, for the entry
                // itself rather than for whatever it points at. Measured
                // 2026-09-17, because the obvious claim about Exists is wrong
                // and worth not repeating: a DANGLING junction answers
                // Directory.Exists TRUE and File.Exists false, reading back
                // [Directory, ReparsePoint] — so Exists would not miss it. What
                // Exists cannot do is tell a link from the folder it names, and
                // that is what decides which rename below is the right one.
                attributes = File.GetAttributes(travel.From);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone since the move — deleted, or moved again by hand. Nothing
                // done, so nothing to take the other way.
                return;
            }
            catch (Exception e)
            {
                Remaining(travel, e, left, blocked);
                return;
            }

            var directory = (attributes & FileAttributes.Directory) != 0;
            var link = (attributes & FileAttributes.ReparsePoint) != 0;
            var realFolder = directory && !link;

            // Every ancestor this walk has to make is a recorded step of its
            // own, so the reversed log removes it again once the child leaves.
            if (!Ancestors(travel.To, inverses, left, blocked)) { left.Add(travel); return; }

            switch (TryRename(travel.From, travel.To, directory, out var refusal))
            {
                case Renamed.Done:
                    inverses.Add(new Travel(travel.To, travel.From));
                    return;

                case Renamed.Taken:
                    Taken(travel, realFolder, inverses, left, blocked, notes);
                    return;

                default:
                    // **A folder rename is refused for reasons that are not
                    // worth telling apart.** Across roots Directory.Move throws
                    // before touching the disk; through a junction onto another
                    // volume it answers Access denied, which is also what a
                    // folder holding an open file answers. All of them take the
                    // walk, which reports per child what it could not do.
                    if (realFolder) Merge(travel, made: true, inverses, left, blocked, notes);
                    else Remaining(travel, refusal, left, blocked);
                    return;
            }
        }

        /// <summary>The name was taken, so ask what is standing there — and only
        /// now, because before the rename the answer would have been about a
        /// different moment.</summary>
        private void Taken(
            Travel travel, bool realFolder, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked, List<string> notes)
        {
            var standing = Directory.Exists(travel.To) && !IsLink(travel.To);

            if (realFolder && standing)
            {
                Merge(travel, made: false, inverses, left, blocked, notes);
                return;
            }

            // **A link of the same kind pointing at the same place is already
            // put back.** A link's whole content is where it points, and this
            // application would reproduce it from the same text, so removing the
            // one that travelled loses nothing. Anything else standing at the
            // name is left alone and the step waits.
            if (!realFolder && IsLink(travel.From) && IsLink(travel.To) && SameLink(travel.From, travel.To))
            {
                var wore = Attributes(travel.From);

                try
                {
                    // DeleteLink refuses a ReadOnly junction, so the mark comes
                    // off first — and goes back on if the removal is refused
                    // anyway, because a link that stays must stay as it was. The
                    // emptied-folder removal below keeps the same rule.
                    ClearReadOnly(travel.From);
                    DeleteLink(travel.From);
                    inverses.Add(new Travel(travel.To, travel.From));
                }
                catch (Exception e)
                {
                    try { File.SetAttributes(travel.From, wore); } catch (Exception) { /* it kept its own */ }

                    Remaining(travel, e, left, blocked);
                }

                return;
            }

            Remaining(travel, refusal: null, left, blocked);
        }

        /// <summary>
        /// One level of children, each a step of its own, and then the folder
        /// they came out of.
        ///
        /// <paramref name="made"/> says the folder at <c>To</c> is this walk's
        /// own work and has to be recorded, so the reversed log takes it away
        /// again; otherwise it was already standing and is somebody else's.
        /// </summary>
        private void Merge(
            Travel travel, bool made, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked, List<string> notes)
        {
            // Kept apart from the caller's, because if this folder goes back
            // whole the whole lot is replaced by one step.
            var mine = new List<Step>();

            // **Made, not asked for.** The caller asks for the folder because the
            // rename did not happen; whether the name was actually free is what
            // RunMakeFolder answers, and only that may key the collapse below.
            // Read off the parameter instead, a cross-volume undo collapsed onto
            // a folder somebody had re-created and its redo carried their file to
            // the other volume — the measured fault this walk exists to end,
            // arriving again by the one road the tests did not cover.
            var created = false;

            if (made)
            {
                switch (RunMakeFolder(new MakeFolder(travel.To, Attributes(travel.From)), mine, left, blocked))
                {
                    case Made.Created: created = true; break;
                    case Made.AlreadyThere: break;
                    default: left.Add(travel); return;
                }
            }

            FileSystemInfo[] children;

            try
            {
                // Without following: a link among them is renamed as itself.
                children = new DirectoryInfo(travel.From).GetFileSystemInfos();
            }
            catch (Exception e)
            {
                // **The failure is the whole folder's, not one child's.** A
                // folder that will not list has told us nothing about what is
                // inside it, so the step waits entire.
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
                // Something is still inside, so the folder cannot go yet. The
                // retry carries the children and then this, in that order.
                inverses.AddRange(mine);
                left.Add(new RemoveIfEmpty(travel.From));
                return;
            }

            RunRemoveIfEmpty(new RemoveIfEmpty(travel.From), mine, notes);

            // **The folder goes back whole, so it comes forward whole.** Read off
            // the disk rather than off this bookkeeping: the name was free before
            // the step ran — this walk made the folder standing there — and
            // nothing is left at the source. Then the inverse is the one rename,
            // not the list of children, and a file saved into the folder between
            // the undo and the redo travels with it instead of being left behind
            // in a folder of the same name.
            //
            // Keyed this way and not on "it went by one rename", so the arm that
            // had to walk agrees with the arm that did not; that agreement is the
            // whole point, and a person cannot see which arm ran.
            if (created && !Directory.Exists(travel.From))
            {
                inverses.Add(new Travel(travel.To, travel.From));
                return;
            }

            inverses.AddRange(mine);
        }

        /// <summary>Every folder above <paramref name="to"/> that has to be made,
        /// deepest last, each recorded. False when anything but a real folder
        /// holds one of the names.</summary>
        private static bool Ancestors(
            string to, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked)
        {
            if (PathRules.Parent(to) is not { } parent) return true;

            var missing = new List<string>();

            for (var at = parent; at is not null; at = PathRules.Parent(at))
            {
                if (Directory.Exists(at) && !IsLink(at)) break;

                // Anything else at the name — a file, or a link of any kind — is
                // collected the same way and refused by RunMakeFolder, which is
                // the one place that decides whether a name may be made into a
                // folder. Refusing it a second time here would be a line no test
                // could redden, since the other would catch the case first.
                missing.Add(at);
            }

            missing.Reverse();

            foreach (var at in missing)
                if (RunMakeFolder(new MakeFolder(at, FileAttributes.Directory), inverses, left, blocked) == Made.Refused)
                    return false;

            return true;
        }

        /// <summary>What a <see cref="MakeFolder"/> step turned out to be.</summary>
        private enum Made
        {
            /// <summary>This walk made it, and recorded the step that takes it away.</summary>
            Created,

            /// <summary>A real folder was already there. It is somebody else's.</summary>
            AlreadyThere,

            /// <summary>Something that is not a folder holds the name, or it could
            /// not be made.</summary>
            Refused,
        }

        private static Made RunMakeFolder(
            MakeFolder make, List<Step> inverses, List<Step> left, List<(string Name, string? Why)> blocked)
        {
            // **Already standing is not the same as made, and the difference
            // decides whether a folder may travel back whole.** Reported rather
            // than folded into a yes, because a caller that reads "yes" as "the
            // name was free" collapses an inverse it had no right to — measured:
            // a cross-volume undo then carried a bystander file to the other
            // volume on the redo and deleted the folder the person had made.
            if (Directory.Exists(make.Path) && !IsLink(make.Path)) return Made.AlreadyThere;

            if (File.Exists(make.Path) || IsLink(make.Path))
            {
                left.Add(make);
                blocked.Add((PathRules.LeafName(make.Path), null));
                return Made.Refused;
            }

            try
            {
                Directory.CreateDirectory(make.Path);

                if (make.Attributes != FileAttributes.Directory)
                    File.SetAttributes(make.Path, make.Attributes);

                inverses.Add(new RemoveIfEmpty(make.Path));
                return Made.Created;
            }
            catch (Exception e)
            {
                left.Add(make);
                blocked.Add((PathRules.LeafName(make.Path), e.Message));
                return Made.Refused;
            }
        }

        /// <summary>
        /// The emptied folder, taken away — and only if it really is empty.
        ///
        /// **A folder that will not go is a note, never a remaining step.** It
        /// holds nothing this walk put there, so there is nothing to try again;
        /// saying where it is is the whole of what is owed.
        /// </summary>
        private static void RunRemoveIfEmpty(RemoveIfEmpty remove, List<Step> inverses, List<string> notes)
        {
            if (!Directory.Exists(remove.Path) || IsLink(remove.Path)) return;

            var attributes = Attributes(remove.Path);

            try
            {
                // **Cleared before the attempt, not in reaction to it.** An empty
                // ReadOnly folder refuses Directory.Delete with the same generic
                // refusal everything else gives, so there is nothing to react to.
                ClearReadOnly(remove.Path);

                Directory.Delete(remove.Path, recursive: false);
                inverses.Add(new MakeFolder(remove.Path, attributes));
            }
            catch (Exception e)
            {
                try { File.SetAttributes(remove.Path, attributes); } catch (Exception) { /* it kept its own */ }

                // **The name is composed here, because the message does not
                // always carry it.** Measured on Windows: Directory.Delete on a
                // folder that will not go says "The directory is not empty." and
                // nothing else, so the note read "everything went back, but The
                // directory is not empty" and named no folder at all — while the
                // summary above promises that saying where it is is the whole of
                // what is owed. Other refusals do name the path, which is why
                // this was easy to miss.
                //
                // **No mutation reddens this line**, and that is worth saying
                // rather than dressing up: reaching it needs the removal to fail
                // with a message that omits the path, and the walk only asks
                // after every child has gone — so the folder is empty unless
                // something outside puts a file in it mid-walk, which no seam
                // here can arrange. A Linux test written for it passed with the
                // line reverted, because the refusal there names the path.
                notes.Add($"{PathRules.LeafName(remove.Path)} was left at "
                          + $"{PathRules.Parent(remove.Path) ?? remove.Path}: {e.Message}");
            }
        }

        private static void Remaining(
            Travel travel, Exception? refusal, List<Step> left, List<(string Name, string? Why)> blocked)
        {
            left.Add(travel);
            blocked.Add((PathRules.LeafName(travel.From), refusal?.Message));
        }

        private enum Renamed { Done, Taken, Refused }

        private Renamed TryRename(string from, string to, bool directory, out Exception? refusal)
        {
            refusal = null;

            try
            {
                _beforeRenaming?.Invoke(from, to);

                // Neither replaces: Directory.Move has no overwrite at all, and
                // File.Move without one refuses a taken name. A link is renamed
                // as itself by both, and the Directory attribute picks between
                // them — measured, Directory.Move renames a dangling junction
                // and File.Move throws FileNotFoundException at one, because it
                // asks File.Exists first.
                if (directory) Directory.Move(from, to);
                else File.Move(from, to);

                return Renamed.Done;
            }
            catch (IOException e) when (e.HResult == FileExists || e.HResult == AlreadyExists)
            {
                refusal = e;
                return Renamed.Taken;
            }
            catch (Exception e)
            {
                refusal = e;
                return Renamed.Refused;
            }
        }

        /// <summary>
        /// Whether two links would be written the same way by this application.
        ///
        /// Not "do they point at the same file" — that is a question about the
        /// disk, and this one is about the entry. CopyLink hands LinkTarget to
        /// Native.CreateJunction, which runs Path.GetFullPath and trims a
        /// trailing separator before it writes; measured, so a trailing
        /// separator and a "…\..\…" segment are the same entry and an 8.3
        /// component is not. PathRules.Same is that comparison.
        /// </summary>
        private static bool SameLink(string from, string to)
        {
            if (ReparseTags.Of(from) is not { } here || ReparseTags.Of(to) is not { } there) return false;

            if (here != there) return false;

            var mine = new DirectoryInfo(from).LinkTarget ?? new FileInfo(from).LinkTarget;
            var theirs = new DirectoryInfo(to).LinkTarget ?? new FileInfo(to).LinkTarget;

            return mine is not null && theirs is not null && PathRules.Same(mine, theirs);
        }

        private static FileAttributes Attributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (Exception) { return FileAttributes.Directory; }
        }

        /// <summary>
        /// What could not be done, and where it still is.
        ///
        /// **The reason belongs to the name it came from.** Held in two lists and
        /// paired by position, a name would be given somebody else's refusal —
        /// and the two lists do not even grow together, since a folder that will
        /// not go adds a reason and no name at all.
        /// </summary>
        private string Sentence(List<(string Name, string? Why)> blocked, List<string> notes)
        {
            var way = _forward ? "go forward" : "go back";

            if (blocked.Count == 0)
                return notes.Count > 0
                    ? $"everything went {(_forward ? "forward" : "back")}, but {notes[0]}"
                    : $"something could not {way}";

            var named = string.Join(", ", blocked.Take(3).Select(b => b.Name));

            if (blocked.Count > 3) named += $" and {blocked.Count - 3} more";

            // The first reason there is, from the entry that gave it, rather than
            // the first reason anything gave.
            var why = blocked.FirstOrDefault(b => b.Why is not null).Why;

            return why is not null
                ? $"{named} could not {way}: {why}"
                : $"{named} could not {way}, because something of that name is there now";
        }
    }
}
