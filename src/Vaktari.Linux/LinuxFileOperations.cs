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
        var handle = new OperationHandle();

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
                    var name = XdgTrash.Trash(path);
                    restored.Add((name, path));
                    handle.ItemFinished();
                }

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
        var handle = new OperationHandle();

        _ = Task.Run(async () =>
        {
            try
            {
                handle.Begin(paths.Count, totalBytes: 0);

                foreach (var path in paths)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    handle.ItemStarted(path);

                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else File.Delete(path);

                    handle.ItemFinished();
                }

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/'))
            throw new ArgumentException("A name cannot be empty or contain a separator.", nameof(newName));

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
    private void Remember(IUndoable action)
    {
        _redo.Clear();
        _undo.Push(action);
    }

    public bool CanRedo => !_redo.IsEmpty;

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

    private IOperationHandle Run(
        IReadOnlyList<string> sources, string destination,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict, bool move)
    {
        var handle = new OperationHandle();

        _ = Task.Run(async () =>
        {
            try
            {
                // Enumerating first means the progress bar is honest from the
                // start rather than discovering the total as it goes.
                var plan = BuildPlan(sources, destination, handle.Token);
                handle.Begin(plan.Count, plan.Sum(p => p.Length));

                var created = new List<string>();

                // Where each named item actually landed, and where a renamed
                // folder sends its contents. Both exist because a target is a
                // guess until the conflict at it has been settled.
                var landings = new List<(string Source, string Target)>();
                var redirects = new List<(string From, string To)>();

                foreach (var item in plan)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    var target = Redirect(item.Target, redirects);

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        switch (await onConflict(new FileConflict(item.Source, target)).ConfigureAwait(false))
                        {
                            case ConflictResolution.Skip:
                                // Nothing landed, so nothing is recorded — an
                                // undo that "put back" a file the user asked to
                                // leave alone would move the bystander sitting
                                // at that name instead.
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
                    else
                    {
                        await CopyFileAsync(item.Source, target, handle).ConfigureAwait(false);
                        if (move) File.Delete(item.Source);
                    }

                    created.Add(target);

                    // Only the items the user named, and the place they really
                    // went — not destination + name, which is true only when
                    // nothing was renamed or skipped along the way.
                    if (item.IsRoot) landings.Add((item.Source, target));

                    handle.ItemFinished();
                }

                // Directories are removed only after their contents moved, so a
                // cancelled move never deletes a folder it hasn't emptied.
                if (move)
                {
                    // A link to a directory is not a directory to empty and
                    // remove - Directory.Exists says yes to both.
                    foreach (var source in sources.Where(p => Directory.Exists(p) && !IsLink(p)).Reverse())
                        if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any())
                            Directory.Delete(source);
                }

                // Copies are not undoable: undoing one means deleting files,
                // and an undo that deletes is not a safe default.
                if (move && landings.Count > 0)
                    Remember(new UndoMove(landings));

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    private static List<PlannedItem> BuildPlan(
        IReadOnlyList<string> sources, string destination, CancellationToken ct)
    {
        var plan = new List<PlannedItem>();

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source);
            var name = Path.GetFileName(full);

            // **Before the directory test, because a link to a directory
            // answers to both.** SearchOption.AllDirectories follows symlinks,
            // so a linked folder was descended into and its TARGET's contents
            // copied out - and on a move, deleted from the real folder
            // afterwards. Copying a home directory holding a link to a photo
            // library duplicated the library; moving one emptied it.
            if (IsLink(full))
            {
                plan.Add(new PlannedItem(
                    full, Path.Combine(destination, name), 0,
                    IsDirectory: false, IsRoot: true, IsLink: true));
            }
            else if (Directory.Exists(full))
            {
                var root = Path.Combine(destination, name);
                plan.Add(new PlannedItem(full, root, 0, IsDirectory: true, IsRoot: true));

                foreach (var (path, isDirectory, isLink, length) in Descend(full, ct))
                    plan.Add(new PlannedItem(
                        path, Path.Combine(root, Path.GetRelativePath(full, path)),
                        length, isDirectory, IsRoot: false, IsLink: isLink));
            }
            else if (File.Exists(full))
            {
                plan.Add(new PlannedItem(
                    full, Path.Combine(destination, name), new FileInfo(full).Length,
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
    private static IEnumerable<(string Path, bool IsDirectory, bool IsLink, long Length)> Descend(
        string root, CancellationToken ct)
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

    private static async Task CopyFileAsync(string source, string target, OperationHandle handle)
    {
        var buffer = new byte[BufferSize];

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var output = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        int read;
        while ((read = await input.ReadAsync(buffer, handle.Token).ConfigureAwait(false)) > 0)
        {
            await handle.WaitIfPausedAsync().ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), handle.Token).ConfigureAwait(false);
            handle.BytesCopied(read);
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
    }

    private sealed class UndoRename(string current, string original) : IUndoable
    {
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
