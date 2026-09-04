using Vaktari.Core.Settings;

namespace Vaktari.Core.FileSystem;

/// <summary>What a sweep did. Reported rather than silent, because the whole
/// feature is the application deleting files with nobody watching.</summary>
public sealed record TrashSweepResult
{
    public int Removed { get; init; }

    public long BytesFreed { get; init; }

    /// <summary>
    /// Entries left alone because their deletion date could not be read.
    /// **Unreadable is never treated as old.** A malformed or missing
    /// <c>.trashinfo</c> means we do not know when something was deleted, and
    /// "do not know" must not become "delete it".
    /// </summary>
    public int Skipped { get; init; }

    /// <summary>
    /// The size limit is exceeded and the policy said to warn rather than
    /// delete. The caller surfaces this; nothing was removed for it.
    /// </summary>
    public bool OverLimit { get; init; }

    public static readonly TrashSweepResult Nothing = new();
}

/// <summary>
/// Expiry and size limits for the trash.
///
/// Platform-specific because the trash itself is: freedesktop's spec on Linux,
/// the recycle bin on Windows. Separate from <see cref="IFileOperations"/>
/// because moving one file to the trash and unattended bulk deletion are very
/// different risks, and a caller should have to reach for this one deliberately.
/// </summary>
public interface ITrashMaintenance
{
    /// <summary>
    /// Applies the policy. Does nothing at all when neither expiry nor a size
    /// limit is enabled — the disabled state is not "sweep with defaults".
    /// </summary>
    ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct);

    /// <summary>
    /// What is currently in the trash, newest first.
    ///
    /// Needed because the trash CANNOT be browsed as an ordinary folder: the
    /// payload directory holds deduplicated names with no memory of where
    /// anything came from, and the original path lives in a sidecar. A listing
    /// built by enumerating that directory could show you a file but never
    /// restore it.
    /// </summary>
    IReadOnlyList<TrashedItem> List();

    /// <summary>
    /// Whether the bin is holding anything at all.
    ///
    /// Separate from <see cref="List"/> because the sidebar asks this every
    /// time it rebuilds, and listing is not cheap: it walks every volume's bin
    /// and reads a sidecar per item to recover where each one came from. "Is
    /// there anything" needs none of that, and both platforms can answer it
    /// without opening a single sidecar.
    ///
    /// Defaulted to the expensive answer so an implementation that has no
    /// cheaper route is still correct rather than absent.
    /// </summary>
    bool HasAny() => List().Count > 0;

    /// <summary>
    /// Just the keys, in no particular order.
    ///
    /// Separate from <see cref="List"/> for the reason <see cref="HasAny"/> is.
    /// Undoing a delete needs the difference between the keys before the trash
    /// call and the keys after it, and nothing else about any item — but List
    /// opens a sidecar per item to recover an original path, a size and a date,
    /// and the difference then throws all three away. On Windows that read is
    /// the whole cost of a listing: 107 entries measured at 13.9 ms through
    /// List and 0.27 ms through this.
    ///
    /// Defaulted to the expensive answer, so an implementation with no cheaper
    /// route is correct rather than absent.
    ///
    /// **An implementation may answer MORE keys than <see cref="List"/> has
    /// items, and the Windows one does.** A metadata file whose payload has
    /// gone holds a key but lists as nothing; the bin this was measured against
    /// answered 114 keys to 107 items. So this is for taking a DIFFERENCE
    /// across a call, where such a key stands on both sides and cancels. It is
    /// not a cheap spelling of List, and a caller that takes one end from each
    /// reads every one of those leftovers as something that just arrived.
    ///
    /// The answer is also lazy on both routes and walks again on every
    /// enumeration, unlike List — materialise it once.
    /// </summary>
    IEnumerable<string> Keys() => List().Select(item => item.TrashName);

    /// <summary>
    /// Puts one item back where it came from, returning the path it landed at —
    /// which is NOT always the original: if something has since taken that name
    /// it restores alongside rather than clobbering.
    /// </summary>
    string Restore(string trashName);

    /// <summary>
    /// Destroys one item, permanently.
    ///
    /// **A confirmed yes was refused.** Shift+Delete on a bin row showed the
    /// permanent-delete prompt, took the answer, and then declined — because
    /// the only routes out of the bin were Restore and Empty, and a bin row
    /// carries the path the file USED to occupy, which the file operations
    /// cannot act on. Both references delete just the items you picked; here
    /// the choice was one file or all of them.
    ///
    /// Keyed by trash name, like <see cref="Restore"/>, because that is what
    /// identifies an item INSIDE the trash — two items can share an original
    /// path, and one of them being destroyed must not depend on which the
    /// caller happened to look up first.
    /// </summary>
    void Delete(string trashName);

    /// <summary>
    /// Deletes everything, permanently. Deliberately separate from
    /// <see cref="SweepAsync"/>, which applies a policy and stops at the
    /// allowance — this one has no policy to obey and no stopping condition,
    /// which is exactly why it must be its own method rather than a flag.
    /// </summary>
    ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct);
}

/// <summary>
/// One trashed item. <paramref name="TrashName"/> is the key inside the trash,
/// which is what Restore takes; <paramref name="OriginalPath"/> is where it
/// came from and is what makes the listing meaningful.
/// </summary>
public sealed record TrashedItem(
    string TrashName,
    string OriginalPath,
    string Payload,
    DateTimeOffset Deleted,
    long Size,
    bool IsDirectory);
