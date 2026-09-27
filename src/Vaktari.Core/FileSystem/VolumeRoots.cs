namespace Vaktari.Core.FileSystem;

/// <summary>
/// The paths no operation may move, bin or delete: the root of a volume.
///
/// **Shift+Delete on a drive in This PC deleted the drive's contents.** The
/// row was hidden from the menu but its key was not, and nothing below the key
/// asked what it was being handed: the prompt named "D:\", a yes went to
/// DeleteChosen, and WindowsFileOperations.Delete cleared the read-only bits of
/// the whole tree and walked it deleting files, until Directory.Delete refused
/// the root at the very end. Ctrl+X then Ctrl+V on a drive moved every file on
/// it. It shipped in 0.11.0.
///
/// **Asked twice on purpose.** The pane refuses before anything is queued, so
/// the person gets a sentence rather than a failed operation; the engines ask
/// again as their first act, so a route nobody has thought of yet — a drop, a
/// script of keys, a caller added next year — still cannot reach the walk. The
/// two are independent guards of one rule and each is revert-checked alone.
///
/// A root is <see cref="PathRules.IsRoot"/> — "C:\", "/", a UNC share root —
/// or, on Linux, any mount point: a USB stick is a directory under /media, and
/// removing it would empty the stick.
/// </summary>
public static class VolumeRoots
{
    /// <summary>The sentence a refusal says, in the pane and in a failed
    /// operation alike.</summary>
    public const string Refusal = "a drive cannot be copied, moved, renamed or deleted — only what is on it";

    /// <summary>
    /// The mount points to compare against, or null for the machine's own.
    /// A seam for tests, which must never have a guard's absence tried on a
    /// real volume; null in the application.
    /// </summary>
    public static Func<IReadOnlyList<string>>? MountPointsOverride { get; set; }

    /// <summary>Whether this path is the root of a volume.</summary>
    public static bool IsVolumeRoot(string? path) => IsVolumeRootIn(path, MountPoints());

    /// <summary>The refusal if any of these paths is a volume's root, or null.
    ///
    /// **The mount table is read once per call, not once per path.** It was
    /// read per path: a thousand selected files read /proc/mounts a thousand
    /// times, in the pane on the UI thread and again in the engine.</summary>
    public static string? Refuse(IEnumerable<string> paths)
    {
        IReadOnlyList<string>? points = null;

        foreach (var path in paths)
            if (IsVolumeRootIn(path, points ??= MountPoints())) return Refusal;

        return null;
    }

    /// <summary>
    /// Whether a path is a root, however it is spelled.
    ///
    /// **Five spellings of a root got past both guards**, measured on the
    /// review's probe against the real engine: "\\server\share\" (the ordinary
    /// trailing-backslash form), "\\?\UNC\server\share\", "Z:\\", "Z:\." and
    /// "Z:\x\..". PathRules.IsRoot compares a path with its own root as TEXT,
    /// so each of them read as a folder: Delete walked on, Trash handed "Z:\"
    /// to the recycler, and Move landed the root on itself.
    ///
    /// So the path is asked as written first — "Z:" must be, because
    /// Path.GetFullPath("Z:") resolves to Z:'s CURRENT folder, which is not
    /// its root — then with its trailing separators taken off, which is what
    /// "\\server\share\" and "\\?\UNC\server\share\" needed (GetPathRoot
    /// answers either without its last backslash), and then resolved, which
    /// folds "." and ".." away and collapses a doubled separator. A mount point
    /// is compared the same way on both sides, so "/media/me/STICK/.",
    /// "//media/me/STICK" and "/media/me/./STICK" all name the stick.
    ///
    /// **The trimmed spelling is asked twice, and each time hides the
    /// other's absence**: once as written and once resolved, because
    /// resolving a path that is only trailing separators away from a root
    /// gives the same path back. The first stands for a path GetFullPath
    /// refuses. Revert-checked as a pair.
    /// </summary>
    private static bool IsVolumeRootIn(string? path, IReadOnlyList<string> points)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (PathRules.IsRoot(path)) return true;

        var spellings = new List<string> { PathRules.Normalise(path) };

        if (Resolved(path) is { } full)
        {
            spellings.Add(full);
            spellings.Add(PathRules.Normalise(full));
        }

        if (spellings.Any(PathRules.IsRoot)) return true;

        if (points.Count == 0) return false;

        var canonical = Canonical(path);

        return points.Any(point => string.Equals(Canonical(point), canonical, StringComparison.Ordinal));
    }

    /// <summary>The path with "." and ".." folded away, or null where it
    /// cannot be resolved.</summary>
    private static string? Resolved(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>A path as a mount point is compared: resolved, and without a
    /// trailing separator.</summary>
    private static string Canonical(string path)
        => PathRules.Normalise(Resolved(path) ?? path);

    /// <summary>
    /// The mount points to compare against: the seam's, or on Linux the
    /// machine's own, read at most once every two seconds — the pane asks as a
    /// key is pressed and the engine asks again as it starts, and a mount that
    /// appears between the two within that time is a drive that was not on
    /// screen when the key was pressed. On Windows none: a volume mounted in a
    /// folder is rare, and the fallback that lists them asks every drive
    /// letter, a dead mapped drive included.
    /// </summary>
    private static IReadOnlyList<string> MountPoints()
        => MountPointsOverride?.Invoke() ?? (OperatingSystem.IsLinux() ? CachedMountTable() : []);

    /// <summary>What reads the mount table. A seam, so the cache in front of it
    /// can be counted; Volumes.MountPoints in the application.</summary>
    internal static Func<IReadOnlyList<string>> ReadMountTable { get; set; } = Volumes.MountPoints;

    private static readonly object CacheGate = new();
    private static IReadOnlyList<string>? _cached;
    private static long _cachedAt;

    /// <summary>The mount table, read again only once it is two seconds old.</summary>
    internal static IReadOnlyList<string> CachedMountTable()
    {
        lock (CacheGate)
        {
            var now = Environment.TickCount64;

            if (_cached is null || now - _cachedAt > 2000)
            {
                _cached = ReadMountTable();
                _cachedAt = now;
            }

            return _cached;
        }
    }

    /// <summary>Drops the cached table, so the next ask reads it again.</summary>
    internal static void ForgetMountTable()
    {
        lock (CacheGate) _cached = null;
    }

    /// <summary>
    /// An operation that has already failed with the refusal, for an engine to
    /// hand back instead of starting — or null when none of the paths is a root.
    /// </summary>
    public static IOperationHandle? RefusedOperation(IReadOnlyList<string> paths, OperationKind kind)
    {
        if (Refuse(paths) is not { } why) return null;

        var handle = new OperationHandle { Paths = paths, Kind = kind };

        handle.Failed(new IOException(why));

        return handle;
    }
}
