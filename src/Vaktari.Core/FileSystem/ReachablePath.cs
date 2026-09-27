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

        // **A NUL ends the path for whatever reads it next**: the shell's
        // double-NUL list stopped "W:\0" at "W:" and deleted that drive's
        // folder (seventh review round). No name holds one.
        if (path.Contains('\0')) return Nul;

        if (Unopenable(path) is not { } segment) return null;

        return segment[^1] == ' '
            ? $"\"{segment}\" ends with a space, and Windows cannot open it by name "
              + "— acting on it would hit a different file."
            : $"\"{segment}\" ends with a dot, and Windows cannot open it by name "
              + "— acting on it would hit a different file.";
    }

    /// <summary>
    /// Why a copy or move cannot land at this path, or null when it can: the
    /// same rule as <see cref="Refuse"/>, asked of where an item will be
    /// WRITTEN — the destination joined with the item's own name, and so on
    /// down a folder's contents.
    ///
    /// **Only the destination folder was asked, never the name joined to it.**
    /// A source reached through "\\?\" or "\??\" keeps "report " as its own
    /// name, and the copy then wrote it into a plainly spelled destination as
    /// "report": Win32 folded the target. When the destination already held
    /// "report", the conflict was raised for "dst\report " and Overwrite
    /// replaced the destination's own "report" — a file nobody named — and a
    /// move then removed the source (seventh review round). A folder's child
    /// "x..." landed as "x" the same way.
    ///
    /// **Refused rather than landed under another name.** A plain destination
    /// cannot hold the name as written, and any name chosen instead is a name
    /// the person did not choose, which may belong to a file already there —
    /// the very clash the prompt would then misname. A destination opened
    /// through "\\?\" holds the name as it is, and is not refused.
    /// </summary>
    public static string? RefuseLanding(string? target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(target)) return null;

        if (target.Contains('\0')) return Nul;

        if (Unopenable(target) is not { } segment) return null;

        var kept = segment.TrimEnd(' ', '.');
        var why = segment[^1] == ' ' ? "ends with a space" : "ends with a dot";

        return kept.Length == 0
            ? $"\"{segment}\" {why}, and a folder opened by its ordinary name cannot take that name "
              + "— Windows would drop it, so it is not copied or moved there."
            : $"\"{segment}\" {why}, and a folder opened by its ordinary name cannot take that name "
              + $"— it would land as \"{kept}\", which is another name, so it is not copied or moved there.";
    }

    /// <summary>
    /// Why this path cannot be handed to another program — in a drag, or on
    /// the clipboard as a file — or null when it can.
    ///
    /// **A drag out of a plainly opened pane carried the neighbour.** The
    /// payload was built through the storage provider, a FileInfo underneath,
    /// which folds "…\report " to "…\report": a Shift-drop moved "report",
    /// which nobody dragged, and a Ctrl-drop copied it, both reported
    /// Completed; the bin row reads the same payload (seventh review round,
    /// 7-D). The clipboard's file list is built the same way, and is what
    /// Explorer pastes from.
    ///
    /// **Asked of the name with any "\\?\" taken off**, which is stricter than
    /// <see cref="Refuse"/>: what the receiver does with "\\?\…\report " is
    /// its own business — Explorer, or anything that strips the prefix, would
    /// open "report" — so a name Win32 would fold is never handed on, however
    /// it is spelled here.
    /// </summary>
    public static string? RefuseHandedOut(string? path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(path)) return null;

        var plain = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
                  : path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
                  : path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..]
                  : path;

        if (Refuse(plain) is not { } why) return null;

        return Unopenable(plain) is { } segment
            ? $"\"{segment}\" cannot be handed to another program — its name ends with "
              + (segment[^1] == ' ' ? "a space" : "a dot")
              + ", and whatever opens it by name would open "
              + (segment.TrimEnd(' ', '.') is { Length: > 0 } kept ? $"\"{kept}\"" : "something else")
              + " instead."
            : why;
    }

    private const string Nul ="a path with a NUL character in it names nothing Windows can open "
                               + "— acting on it would hit whatever comes before the NUL.";

    /// <summary>
    /// The first name in the path that Win32 would open without its trailing
    /// space or dot, or null when there is none — or when the path is a
    /// literal "\\?\" or "\??\" one, which Win32 opens as written.
    /// </summary>
    private static string? Unopenable(string path)
    {
        // **Only a literal "\\?\" or "\??\" is opened as written** — exact
        // backslashes, as .NET's PathInternal.IsExtended asks — so only there
        // is "report " a name of its own. "\\.\" was let through as well, and
        // Win32 folds it like any other path: with "report", "report " and
        // "report." in one folder, Delete, Move, Copy and Trash of
        // "\\.\C:\…\report " all acted on "report" (sixth review round). So
        // every other spelling — "\\.\", "//./", "//?/", "\\?/", "/\?\" and
        // the rest — has its names read here as a plain path's are.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return null;

        var segments = path.Split('\\', '/');

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            // "." and ".." end in a dot and are ordinary path syntax, not names.
            if (segment is "" or "." or "..") continue;

            if (segment[^1] is not (' ' or '.')) continue;

            // **Read as Win32 will open it: a name the next step takes away is
            // never opened.** Win32 folds "\\.\X:\x\...\.." to x before it
            // opens anything — measured, and every verb acts on x alone — so
            // "..." there names nothing to hit. Only a ".." right after it
            // (past "." and doubled separators, which the fold drops) is
            // excused; anything further is refused, which errs the safe way.
            if (segments.Skip(i + 1).FirstOrDefault(next => next is not ("" or ".")) == "..") continue;

            return segment;
        }

        return null;
    }
    /// <summary>Convenience for the many call sites that only branch on it.</summary>
    public static bool IsReachable(string? path) => Refuse(path) is null;
}
