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

            if (OperatingSystem.IsWindows())
            {
                var leftRoot = Path.GetPathRoot(left);
                var rightRoot = Path.GetPathRoot(right);

                return !string.IsNullOrEmpty(leftRoot)
                       && !string.IsNullOrEmpty(rightRoot)
                       && string.Equals(leftRoot, rightRoot, PathRules.Comparison);
            }

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
    /// The mount point containing <paramref name="path"/> — the LONGEST mount
    /// whose root prefixes it, since "/" prefixes everything and would
    /// otherwise always win.
    /// </summary>
    public static string? MountFor(string path)
    {
        string? best = null;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;

                var root = drive.RootDirectory.FullName;

                if (!path.StartsWith(root, StringComparison.Ordinal)) continue;

                // A prefix must end at a separator, or "/media" would claim
                // "/mediaserver".
                if (root.Length > 1
                    && path.Length > root.Length
                    && path[root.Length] != Path.DirectorySeparatorChar) continue;

                if (best is null || root.Length > best.Length) best = root;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A mount that will not answer is not the one.
            }
        }

        return best;
    }
}
