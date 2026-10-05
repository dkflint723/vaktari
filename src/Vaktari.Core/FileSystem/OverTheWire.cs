namespace Vaktari.Core.FileSystem;

/// <summary>
/// Whether a folder is reached over a network — the question to ask before
/// doing per-listing work that costs a round trip per call there.
///
/// **Not ThumbnailLoader.IsRemote, which answers a different question by
/// shape.** That one calls anything starting with <c>\\</c> remote, which
/// includes <c>\\?\C:\…</c> — a LOCAL folder reached through the extended
/// prefix, whose git marks 0.11.2 shipped a fix for — and it matches its roots
/// with a bare StartsWith, so a remote "/mnt/nas" made "/mnt/nasty" remote too.
/// For thumbnails that costs a smaller preview limit; for git marks it would
/// cost the marks, silently. So this reads the path the way Windows does:
///
/// - <c>\\server\share\…</c> and <c>\\?\UNC\server\…</c> are remote.
///   **So are <c>\\wsl$\…</c> and <c>\\wsl.localhost\…</c>, by decision**: a
///   WSL distribution is reached through a 9P server, and a git walk and
///   status over it takes seconds on a large tree.
/// - <c>\\?\X:\…</c> and <c>\\.\X:\…</c> are judged by X:, and so is a plain
///   <c>X:\…</c>: remote when X: is a mapped network drive. The drive type is
///   GetDriveType, which reads the DOS device map and never touches the wire
///   (DriveSet's doc says the same), so a mapped letter is known for what it
///   is from the first listing, before the sidebar has found anything.
/// - Anything inside one of <paramref name="roots"/> — the mounts the sidebar
///   discovered (sshfs, gvfs, NFS, CIFS on Linux) — matched at a separator by
///   <see cref="PathRules.Contains"/>.
/// </summary>
public static class OverTheWire
{
    /// <summary>The type of a drive letter ("C"), for tests that need a
    /// network drive this machine does not have. Null in the application.</summary>
    internal static Func<char, DriveType>? DriveTypeOverride { get; set; }

    public static bool IsRemote(string? path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (OperatingSystem.IsWindows() || DriveTypeOverride is not null)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                var rest = path[4..];

                if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return true;

                // \\?\Volume{…}\ and the like: a local device by name.
                return IsLetter(rest) ? IsNetworkDrive(rest[0]) : InsideAny(path, roots);
            }

            // Any other \\name\ is a server: a share, a WebDAV redirector, WSL.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            if (IsLetter(path) && IsNetworkDrive(path[0])) return true;
        }

        return InsideAny(path, roots);
    }

    private static bool IsLetter(string path)
        => path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);

    private static bool IsNetworkDrive(char letter)
    {
        try
        {
            var type = DriveTypeOverride is { } fake
                ? fake(char.ToUpperInvariant(letter))
                : new DriveInfo(letter + ":\\").DriveType;

            return type == DriveType.Network;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool InsideAny(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
            if (PathRules.Contains(root, path)) return true;

        return false;
    }
}
