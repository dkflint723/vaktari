namespace Vaktari.Core.FileSystem;

/// <summary>
/// Whether Windows can be asked about this path by name at all.
///
/// **A file whose name ends in a space or a dot is reachable only through the
/// extended prefix**, and the .NET path layer strips those characters before
/// the call. So asking for "report " gets you "report": it exists, it opens,
/// it reads — and it is a different file. Deleting "report " deletes "report"
/// and leaves "report " standing. Measured on this machine, .NET 10:
///
///     File.Exists(@"…\report ")     -> True        (it is answering for "report")
///     File.ReadAllText(@"…\report ") -> contents of "report"
///     File.Delete(@"…\report ")      -> "report" is gone, "report " remains
///
/// Such names are legal on NTFS and arrive routinely — from WSL, from a Linux
/// SMB client, from git, from anything that did not go through the Win32 path
/// rules. <see cref="FileNames"/> already stops Vaktari from CREATING one, and
/// says in its own summary that "a name read from disk is used exactly as it
/// is". The BCL does not honour that promise, and the listing shows the true
/// name — so the row a person clicks and the file the operation hits are two
/// different files, silently.
///
/// **Refusing is what Explorer does**, right down to acting as though the item
/// were not there, and it is the only answer that cannot destroy the wrong
/// file. Reaching such a name properly means an extended-prefix path threaded
/// through every call in the engine, and one missed call site is a deletion of
/// something the user never named — so the guard comes first and the reach can
/// come later.
///
/// Windows only. On a freedesktop filesystem a trailing space is an ordinary
/// character, nothing normalises it away, and Dolphin handles these names
/// without comment — so this must never refuse anything there.
/// </summary>
public static class ReachablePath
{
    /// <summary>
    /// Why this path cannot be acted on, or null when it can.
    ///
    /// A sentence rather than a code, for the same reason
    /// <see cref="FileNames.Refuse"/> gives one: it is shown to the person who
    /// clicked the row, and it has to say which character is the problem
    /// because the name looks perfectly ordinary on screen.
    /// </summary>
    public static string? Refuse(string? path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(path)) return null;

        // Already extended: whoever built it took responsibility for the rules.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;

        foreach (var segment in path.Split('\\', '/'))
        {
            // "." and ".." end in a dot and are ordinary path syntax, not names.
            if (segment is "" or "." or "..") continue;

            var last = segment[^1];

            if (last == ' ')
                return $"\"{segment}\" ends with a space, and Windows cannot open it by name "
                       + "— acting on it would hit a different file.";

            if (last == '.')
                return $"\"{segment}\" ends with a dot, and Windows cannot open it by name "
                       + "— acting on it would hit a different file.";
        }

        return null;
    }

    /// <summary>Convenience for the many call sites that only branch on it.</summary>
    public static bool IsReachable(string? path) => Refuse(path) is null;
}
