namespace Vaktari.Core.FileSystem;

public enum ConflictResolution { Overwrite, Skip, KeepBoth, Cancel }

/// <summary>
/// Something already occupies the place an item is being copied or moved to.
///
/// **Both paths, not just the destination.** Deciding whether to overwrite is a
/// comparison — which is newer, which is larger, are they the same file at all —
/// and a prompt handed only the target can show none of that. It can only ask
/// "replace?" and leave the answer to memory.
/// </summary>
/// <param name="Source">What is arriving.</param>
/// <param name="Target">What is already there.</param>
public readonly record struct FileConflict(string Source, string Target);

public enum OperationState { Queued, Running, Paused, Completed, Failed, Cancelled }

/// <summary>
/// One item an operation could not do, and why — so the rest of the batch can
/// carry on and the person still learns which files were left behind.
/// </summary>
public readonly record struct ItemProblem(string Path, Exception Error);

public readonly record struct OperationProgress(
    long BytesDone,
    long BytesTotal,
    int ItemsDone,
    int ItemsTotal,
    string? CurrentItem);

/// <summary>
/// A running or queued operation. Handles are surfaced by the transfer queue
/// panel — which is why this exists as a type rather than operations being bare
/// awaitable calls. Pause and reorder are impossible to retrofit onto a Task.
/// </summary>
public interface IOperationHandle
{
    Guid Id { get; }
    OperationState State { get; }
    IProgress<OperationProgress> Progress { get; }

    /// <summary>
    /// Items that failed while the rest of the batch went through. Empty on a
    /// clean run. A non-empty list on a COMPLETED operation is the normal way
    /// to report "eleven of twelve" — it is not a failure of the operation.
    /// </summary>
    IReadOnlyList<ItemProblem> Problems { get; }

    void Pause();
    void Resume();
    void Cancel();

    Task Completion { get; }

    /// <summary>
    /// Progress as it happens. On the interface rather than the concrete type
    /// because the shell subscribes to it, and reaching into a platform
    /// assembly for an event is exactly the leak the platform split exists to
    /// prevent.
    /// </summary>
    event EventHandler<OperationProgress>? Progressed;

    /// <summary>
    /// Why the operation failed, when State is Failed. Operations swallow their
    /// exceptions so one bad file cannot tear down the app — but swallowing
    /// them without surfacing this makes a refused delete indistinguishable
    /// from nothing happening at all.
    /// </summary>
    Exception? Error { get; }
}

/// <summary>
/// Mutating operations. On Windows every one of these routes through
/// IFileOperation — recycle bin semantics, collision dialogs, UAC elevation and
/// undo all live there, and a hand-rolled copy loop forfeits the lot. On Linux
/// this is our own engine plus the XDG trash spec.
/// </summary>
public interface IFileOperations
{
    IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict);

    IOperationHandle Move(IReadOnlyList<string> sources, string destination,
        Func<FileConflict, ValueTask<ConflictResolution>> onConflict);

    /// <summary>Moves to recycle bin / XDG trash. Recoverable by the user.</summary>
    IOperationHandle Trash(IReadOnlyList<string> paths);

    /// <summary>Irreversible. Only ever from an explicit, distinct user action.</summary>
    IOperationHandle Delete(IReadOnlyList<string> paths);

    ValueTask RenameAsync(string path, string newName, CancellationToken ct);

    bool CanUndo { get; }
    ValueTask UndoAsync(CancellationToken ct);

    /// <summary>
    /// Puts back what an undo took away — Ctrl+Y, which every editor and file
    /// manager answers and this one did not.
    ///
    /// Emptied by any new operation, because once the history has been departed
    /// from a redo would apply to a state that no longer exists. Not everything
    /// has an honest inverse: restoring from the bin cannot be redone, because
    /// the trash entry it came from is gone and re-trashing would not be the
    /// same act.
    /// </summary>
    bool CanRedo { get; }

    ValueTask RedoAsync(CancellationToken ct);
}
