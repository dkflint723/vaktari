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
    public const string Refusal = "a drive cannot be moved, renamed or deleted — only what is on it";

    /// <summary>
    /// The mount points to compare against, or null for the machine's own.
    /// A seam for tests, which must never have a guard's absence tried on a
    /// real volume; null in the application.
    /// </summary>
    public static Func<IReadOnlyList<string>>? MountPointsOverride { get; set; }

    /// <summary>Whether this path is the root of a volume.</summary>
    public static bool IsVolumeRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (PathRules.IsRoot(path)) return true;

        // Only where mount points are a directory tree's own business. On
        // Windows a volume mounted in a folder is rare, and the fallback that
        // lists them asks every drive letter — a dead mapped drive included —
        // on each call.
        var points = MountPointsOverride?.Invoke()
                     ?? (OperatingSystem.IsLinux() ? Volumes.MountPoints() : []);

        if (points.Count == 0) return false;

        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;

        return points.Any(point => string.Equals(
            point.Length > 1 ? point.TrimEnd('/') : point, trimmed, StringComparison.Ordinal));
    }

    /// <summary>The refusal if any of these paths is a volume's root, or null.</summary>
    public static string? Refuse(IEnumerable<string> paths)
        => paths.Any(IsVolumeRoot) ? Refusal : null;

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
