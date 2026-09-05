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
    /// Everywhere this operation reads or writes: the sources, and the
    /// destination when there is one.
    ///
    /// **Nothing could ask a running operation where its bytes were going.** A
    /// handle carried an id, a state, a progress count and a problem list, so
    /// the eject command had no way to tell a copy onto the stick from a copy
    /// on the other side of the machine — and it went ahead with both. Only the
    /// engine that started the work knows the paths, which is why they are
    /// recorded here rather than worked out later.
    ///
    /// Whole paths, not the volume they sit on: deciding what a drive owns is
    /// <see cref="PathRules.Contains"/>'s job, and a handle that pre-computed a
    /// mount point would have to be right about it on two platforms.
    ///
    /// Empty is legal and means "nowhere in particular" — a handle built by
    /// something that is not one of the file engines.
    /// </summary>
    IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Items that failed while the rest of the batch went through. Empty on a
    /// clean run. A non-empty list on a COMPLETED operation is the normal way
    /// to report "eleven of twelve" — it is not a failure of the operation.
    /// </summary>
    IReadOnlyList<ItemProblem> Problems { get; }

    void Pause();
    void Resume();
    void Cancel();

    /// <summary>
    /// Whether stopping this half way is a thing that can be asked for.
    ///
    /// **Both buttons were offered for an operation that is one blocking
    /// call.** Windows recycles a whole batch through a single synchronous
    /// SHFileOperation, so there is no loop between items to check a gate in
    /// and no token the shell will read: pressing Pause set a flag nothing
    /// would ever look at, and pressing Cancel cancelled a token nobody was
    /// passing. The buttons did nothing and gave no sign of it, which reads as
    /// the application being broken rather than as the operation being
    /// uninterruptible.
    ///
    /// Default true, because every engine written in this codebase awaits the
    /// gate between items and inside the byte loop. It is the operation that
    /// hands its work to somebody else's API that has to say otherwise.
    /// </summary>
    bool CanPause => true;

    /// <inheritdoc cref="CanPause"/>
    bool CanCancel => true;

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

    /// <summary>
    /// Runs again the items this operation could not do, and nothing else.
    /// Null when there is nothing worth trying again.
    ///
    /// **Deliberately not the conflict prompt's shape.** A clash can be asked
    /// about before anything happens, so a question in the middle of a copy
    /// costs only the time taken to answer it. A failure cannot: the whole
    /// reason "something else has that file open" is worth offering at all is
    /// that somebody is going to go and CLOSE that program, and a modal that
    /// stops item three of five thousand while they do it leaves the other four
    /// thousand nine hundred and ninety-seven undone. Both engines carry long
    /// comments about having deliberately removed exactly that stop.
    ///
    /// So the batch never pauses. It finishes, the bar names what was left
    /// behind, and this is how the person says "I have closed it now".
    /// </summary>
    RetryOffer? Retry { get; }
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

    /// <summary>
    /// Makes something that was just created undoable.
    ///
    /// New folder, new file and new-from-template write straight to the
    /// filesystem rather than through a copy or a move, so the undo history
    /// never heard about them and Ctrl+Z right after Ctrl+Shift+N did nothing.
    /// The undo puts the new item in the bin, which is recoverable and is what
    /// Explorer does.
    /// </summary>
    void RecordCreation(string path);

    bool CanUndo { get; }
    ValueTask UndoAsync(CancellationToken ct);

    /// <summary>
    /// What the next undo would take back — "copy of readme.txt", "move of 12
    /// items" — or null when there is nothing to take back.
    ///
    /// **Ctrl+Z said nothing about what it was going to undo**, and there was
    /// no Undo row in any menu to say it either, so after a copy, a rename and
    /// a delete in quick succession the only way to find out which one came
    /// back was to press it and look.
    ///
    /// Asked of the engine rather than remembered by a pane: one history is
    /// shared by every pane and every tab, so an undo pressed in one window
    /// takes back what another one did, and a pane that kept its own idea of
    /// the top of the stack would name the wrong thing.
    /// </summary>
    string? UndoDescription { get; }

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

    /// <summary>What the next redo would put back, or null when there is
    /// nothing to put back.</summary>
    string? RedoDescription { get; }

    /// <summary>
    /// Gathers every rename performed until the group is disposed into ONE undo
    /// step. Null from a provider that keeps no history to gather.
    ///
    /// **A batch rename was one undo step per file.** Renumbering forty
    /// photographs pushed forty entries, so taking the dialog back meant forty
    /// presses of Ctrl+Z, each naming a single file — and a swap pushed MORE
    /// entries than there were files, because breaking a cycle costs a staging
    /// rename and that landed on the stack too. The dialog performs one act, so
    /// the history should hold one act.
    ///
    /// **Renames only, deliberately.** Copy, move and trash all record from
    /// inside the Task.Run that carries out the work, so a group that caught
    /// everything would swallow a copy that merely happened to finish while it
    /// was open — a copy started beforehand goes on running in its own task.
    /// <see cref="RenameAsync"/> records on the thread that called it, so what
    /// joins the group is exactly what the caller asked for.
    /// </summary>
    IUndoGroup? BeginRenameGroup();
}

/// <summary>
/// Several operations being gathered into one undo step, closed by disposing
/// it.
///
/// The steps inside are taken back in the reverse of the order they were
/// performed, which is the only order that works: a renumber is drained from
/// the far end of its chain, so going forward puts img004 back to img003 before
/// the old img002 has left that name. A swap refuses one step later — going
/// forward reaches the staging move, finds the staging name vacant because the
/// parked file has already gone on to b, skips it, and then asks the file now
/// called a to go back to b, which the parked file is sitting on.
///
/// A name that will not come back — taken again in the meantime — is skipped
/// rather than abandoning the rest of the batch, because the per-file history
/// this replaces lost only the press it was on.
/// </summary>
public interface IUndoGroup : IDisposable
{
    /// <summary>
    /// What the finished step is called in the Undo row — this and nothing
    /// else, however many renames the group ended up holding.
    ///
    /// Settable, and set as the work proceeds rather than when the group opens,
    /// because a batch that stops halfway has to be named for what it actually
    /// did: only the renames that went through are in the group, and a name
    /// fixed up front would offer "rename of 40 items" for the three that
    /// landed.
    /// </summary>
    string Description { get; set; }
}
