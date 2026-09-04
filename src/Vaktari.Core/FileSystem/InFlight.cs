namespace Vaktari.Core.FileSystem;

/// <summary>
/// Which of the operations still going would be cut off by taking a drive away.
///
/// **The eject command asked nobody.** It walked every tab off the drive and
/// called the ejector with a copy still in flight, and the two platforms then
/// fail differently — neither of them well.
///
/// <c>WindowsEjector.Quiesce</c> issues FSCTL_DISMOUNT_VOLUME even when the
/// lock never came, and says so in its own comment; <c>Remove</c> then returns
/// Ejected on CR_SUCCESS without consulting whether the volumes went down
/// cleanly. <c>UdisksEjector</c> stops at the other end: a "busy" complaint
/// from <c>udisksctl unmount</c> becomes InUse and returns, so nothing is
/// unmounted and the sentence blames a program the person cannot find, which
/// is Vaktari. Vaktari knows what it is writing and where; it just never
/// looked.
///
/// Here rather than in the shell because the decision is a state test and a
/// path comparison over a list of handles, and nothing about it needs a window
/// — which is also what makes it answerable from a plain test.
/// </summary>
public static class InFlight
{
    /// <summary>
    /// The operations with work left to do that read or write something on
    /// <paramref name="root"/>, or anywhere inside it.
    ///
    /// **State is tested as well as the paths**, because a finished handle
    /// stays in the shell's list for a moment: the shell drops it on a
    /// continuation posted to the UI thread, so between the last byte and that
    /// post running the handle is both completed and still listed. Refusing an
    /// eject on account of a copy that has already finished would be a veto
    /// nobody could act on.
    ///
    /// **One volume, and an eject is not per-volume.** This asks about the
    /// place's own path. WindowsEjector.Eject builds SiblingsOf(letter,
    /// probed) — every Removable-or-Fixed letter sharing the device number —
    /// and quiesces all of them, so on a two-partition stick ejecting E:
    /// force-dismounts F: as well and a copy onto F: walks past this guard.
    /// Widening it means asking Volumes which device is behind a place, which
    /// is a second thing to get right; it is named here so the next reader is
    /// not surprised by it.
    /// </summary>
    public static IReadOnlyList<IOperationHandle> On(
        IEnumerable<IOperationHandle> handles, string? root)
    {
        if (string.IsNullOrEmpty(root)) return [];

        return
        [
            .. handles.Where(h => Unfinished(h.State)
                                  && h.Paths.Any(p => PathRules.Contains(root, p))),
        ];
    }

    /// <summary>
    /// Whether an operation still owes the drive something.
    ///
    /// Paused counts. The bytes not yet written are still owed, and resuming
    /// after the volume has gone writes them nowhere — a pause is a reason to
    /// wait, not a reason to consider the transfer over.
    ///
    /// Public because a CLOSING WINDOW asks the same question of the same list:
    /// whether anything it is holding still owes work, before it takes the lot
    /// away with it. Two spellings of that would be one too many, and the note
    /// above about a finished handle lingering in the shell's list applies
    /// exactly as much there.
    /// </summary>
    public static bool Unfinished(OperationState state)
        => state is OperationState.Queued
                 or OperationState.Running
                 or OperationState.Paused;
}
