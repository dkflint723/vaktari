namespace Vaktari.Core.FileSystem;

public enum ChangeKind { Added, Removed, Changed, Renamed }

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
    /// longer than <paramref name="timeout"/>. Used by lazy session restore to
    /// mark a tab dead instead of hanging on it.
    /// </summary>
    ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct);

    string Combine(string basePath, string name);
    string? GetParent(string path);
    bool IsCaseSensitive { get; }
}
