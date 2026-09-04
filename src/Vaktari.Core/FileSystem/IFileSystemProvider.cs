namespace Vaktari.Core.FileSystem;

/// <summary>
/// What a watcher is telling the listing.
///
/// **Lost and Gone are not per-file news**, and that is why they exist. A
/// FileSystemWatcher has a fixed kernel buffer; overflow it — an extraction, a
/// build, a large download — and it silently stops reporting, so the listing
/// went quietly out of date with no way back but F5. And when the watched
/// folder itself is deleted or its share drops, the pane sat on rows for a
/// place that was no longer there.
/// </summary>
public enum ChangeKind
{
    Added,
    Removed,
    Changed,
    Renamed,

    /// <summary>Events were dropped. Whatever is on screen may be wrong;
    /// reload.</summary>
    Lost,

    /// <summary>The watched folder is no longer there.</summary>
    Gone,
}

public readonly record struct FileSystemChange(
    ChangeKind Kind,
    string Path,
    string? OldPath = null);

/// <summary>
/// Named ListingOptions rather than EnumerationOptions deliberately: the
/// providers construct System.IO.Enumeration.FileSystemEnumerable, whose
/// constructor takes a System.IO.EnumerationOptions. Both types would otherwise
/// be in scope in exactly the files that matter.
/// </summary>
public sealed record ListingOptions
{
    public bool IncludeHidden { get; init; }

    // FollowSymlinks was declared here and was the only line in the repository
    // that mentioned it: never set, never read, and no provider consulted it.
    // Removed rather than implemented — a listing that followed links would
    // show a folder's contents twice over, and the recursive walks that DO care
    // now share SafeWalk, which never follows one.

    /// <summary>
    /// How many entries to accumulate before yielding. Small enough that the
    /// first screenful appears immediately, large enough not to thrash the
    /// dispatcher on a huge directory.
    /// </summary>
    public int BatchSize { get; init; } = 500;
}

/// <summary>
/// Everything the UI knows about a filesystem. One implementation per platform;
/// the UI layer never names a concrete one.
/// </summary>
public interface IFileSystemProvider
{
    /// <summary>
    /// Streams entries as they are read. Implementations must not materialise
    /// the full listing first, must not stat entries a second time, and must
    /// observe <paramref name="ct"/> promptly — it fires the moment the user
    /// navigates away, which on a dead SMB host is the difference between a
    /// responsive app and a 30-second hang.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        string path,
        ListingOptions options,
        CancellationToken ct);

    ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct);

    /// <summary>Live change notifications. Dispose to stop watching.</summary>
    IDisposable Watch(string path, Action<FileSystemChange> onChange);

    /// <summary>
    /// Whether the path is reachable, without throwing and without blocking
    /// longer than <paramref name="timeout"/>.
    ///
    /// Called by <c>PaneViewModel.LoadRestoredAsync</c> — the first load of a
    /// tab session restore left standing — to mark that tab dead instead of
    /// leaving it in a listing that never finishes. Nothing else calls it, and
    /// nothing else should: every other navigation is somebody asking for a
    /// folder right now, and the error from the listing itself says more than a
    /// bool can.
    ///
    /// **False means "not now", never "not ever".** A timeout and a folder that
    /// has been deleted both answer false, so a caller cannot tell them apart
    /// and must not word its message as though it could.
    /// </summary>
    ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct);

    string Combine(string basePath, string name);
    string? GetParent(string path);
    bool IsCaseSensitive { get; }
}
