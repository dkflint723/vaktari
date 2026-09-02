namespace Vaktari.Core.FileSystem;

/// <summary>
/// Which volume a path lives on.
///
/// **Path.GetPathRoot is the right answer on Windows and useless on Linux.**
/// There a drive letter or a UNC share IS the volume, so comparing roots
/// separates C: from D: from a share correctly. On Linux every absolute path
/// has the root "/", so the same comparison says every two paths share a
/// volume — which made a plain drag from home to a USB stick or a network mount
/// MOVE the files, when dragging between volumes is exactly the case that
/// should copy and leave the original alone.
///
/// The mount point is the equivalent question there, and it was already being
/// asked: XdgTrashMaintenance works out which mount holds the trash so it can
/// size its allowance. This is that routine, in the one place both callers can
/// reach.
/// </summary>
public static class Volumes
{
    /// <summary>
    /// Whether two paths sit on the same volume.
    ///
    /// **Unknown counts as different**, which errs towards copying — the answer
    /// that leaves the original where it was.
    /// </summary>
    public static bool Same(string a, string b)
    {
        try
        {
            var left = Path.GetFullPath(a);
            var right = Path.GetFullPath(b);

            if (OperatingSystem.IsWindows()) return SameRoot(left, right);

            var leftMount = MountFor(left);
            var rightMount = MountFor(right);

            return leftMount is not null
                   && rightMount is not null
                   && string.Equals(leftMount, rightMount, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException
                                    or NotSupportedException or IOException
                                    or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two paths sit on the same volume, given a mount table already
    /// read.
    ///
    /// **The overload above asks the kernel once per call, and the drag path
    /// calls it twice per file.** A plain drag of a 200-file selection was 400
    /// mount-table reads for every drag-over event — and drag-over fires
    /// continuously while the pointer moves. Read once, ask many times.
    /// </summary>
    public static bool Same(string a, string b, IReadOnlyList<string> mountPoints)
    {
        try
        {
            var left = Path.GetFullPath(a);
            var right = Path.GetFullPath(b);

            if (OperatingSystem.IsWindows()) return SameRoot(left, right);

            var leftMount = MountForIn(mountPoints, left);
            var rightMount = MountForIn(mountPoints, right);

            return leftMount is not null
                   && rightMount is not null
                   && string.Equals(leftMount, rightMount, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException
                                    or NotSupportedException or IOException
                                    or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameRoot(string left, string right)
    {
        var leftRoot = Path.GetPathRoot(left);
        var rightRoot = Path.GetPathRoot(right);

        return !string.IsNullOrEmpty(leftRoot)
               && !string.IsNullOrEmpty(rightRoot)
               && string.Equals(leftRoot, rightRoot, PathRules.Comparison);
    }

    /// <summary>
    /// Every filesystem boundary on the machine.
    ///
    /// **DriveInfo.IsReady is a stat(), and this runs on every drag-over
    /// event.** On Unix IsReady is Directory.Exists(Name), so enumerating the
    /// drives touched every mount point in turn — and a stat on a hung NFS or
    /// sshfs mount does not return. The UI thread froze on a question about
    /// where two files live, which is answerable from a text file.
    ///
    /// Deliberately unfiltered, unlike the sidebar's own mount list: that one
    /// wants volumes a person would call a drive, while this wants every
    /// boundary. /run and /boot/efi really are different filesystems from
    /// /home, and leaving them out would answer the question wrongly.
    ///
    /// A stale mount now appears here, and that is correct — a path under it
    /// does live on it, so source and destination differ and a plain drag
    /// copies, which is the answer that leaves the original alone.
    /// </summary>
    public static IReadOnlyList<string> MountPoints()
        => File.Exists("/proc/mounts")
            ? MountPointsIn(File.ReadLines("/proc/mounts"))
            : [.. DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName)];

    internal static IReadOnlyList<string> MountPointsIn(IEnumerable<string> lines)
    {
        var points = new List<string>();

        foreach (var line in lines)
        {
            var parts = line.Split(' ');

            if (parts.Length < 2) continue;

            points.Add(UnescapeMountField(parts[1]));
        }

        return points;
    }

    /// <summary>
    /// /proc/mounts escapes space, tab, newline and backslash as octal. A mount
    /// point with a space in it is ordinary on a removable disk.
    ///
    /// One left-to-right scan, never chained Replace calls: replacing "\040"
    /// before "\134" would turn a literal backslash-zero-four-zero in a name
    /// into a space.
    /// </summary>
    public static string UnescapeMountField(string field)
    {
        if (field.IndexOf('\\') < 0) return field;

        var built = new System.Text.StringBuilder(field.Length);

        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length)
            {
                var replacement = field.AsSpan(i + 1, 3) switch
                {
                    "040" => ' ',
                    "011" => '\t',
                    "012" => '\n',
                    "134" => '\\',
                    _ => '\0',
                };

                if (replacement != '\0')
                {
                    built.Append(replacement);
                    i += 3;
                    continue;
                }
            }

            built.Append(field[i]);
        }

        return built.ToString();
    }

    /// <summary>
    /// The mount point containing <paramref name="path"/> — the LONGEST mount
    /// whose root prefixes it, since "/" prefixes everything and would
    /// otherwise always win.
    /// </summary>
    public static string? MountFor(string path) => MountForIn(MountPoints(), path);

    public static string? MountForIn(IReadOnlyList<string> mountPoints, string path)
    {
        string? best = null;

        foreach (var root in mountPoints)
        {
            if (root.Length == 0) continue;
            if (!path.StartsWith(root, StringComparison.Ordinal)) continue;

            // A prefix must end at a separator, or "/media" would claim
            // "/mediaserver". The literal '/' rather than the platform
            // separator: these come out of /proc/mounts and are a POSIX notion,
            // and on the Windows fallback a root already ends in its own
            // separator so the length guard never fires.
            if (root.Length > 1
                && path.Length > root.Length
                && path[root.Length] != '/') continue;

            if (best is null || root.Length > best.Length) best = root;
        }

        return best;
    }
}
