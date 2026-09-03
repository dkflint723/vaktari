namespace Vaktari.Core.FileSystem;

/// <summary>
/// The handful of questions this application asks about a path's *shape* —
/// is it the root, what is its parent, what is its leaf, are these two the same
/// place — answered without assuming the separator is <c>/</c>.
///
/// **Why this exists.** Fifteen places spelled these questions out inline as
/// <c>TrimEnd('/')</c>, <c>== "/"</c> and <c>Split('/')</c>. Every one is correct
/// on Linux and wrong on Windows, and the worst of them —
/// <c>if (current == "/") break;</c> walking up to the root — would not have
/// terminated at all on <c>C:\</c>. Fifteen scattered bugs are fifteen chances to
/// fix fourteen of them.
///
/// **Behaviour on Linux is unchanged, deliberately.** This is a refactor that
/// unblocks a port, not a change of behaviour, and every method below was checked
/// against what the inline code already did.
///
/// **What this is NOT.** It does not canonicalise, resolve symlinks, or touch the
/// filesystem — it is pure string shape. Anything that needs the disk belongs on
/// <see cref="IFileSystemProvider"/>.
/// </summary>
public static class PathRules
{
    /// <summary>
    /// How two paths are compared for identity.
    ///
    /// **Ordinal on Linux, case-insensitive on Windows** — not a style choice:
    /// <c>/Home</c> and <c>/home</c> are two different directories on ext4 and
    /// the same one on NTFS, so comparing them the same way on both platforms is
    /// wrong on one of them.
    /// </summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// The same rule as <see cref="Comparison"/>, for the places that need a
    /// comparer rather than an enum — a dictionary keyed by path, chiefly.
    ///
    /// Derived from it rather than written out again, because two statements of
    /// one rule are two things to keep in step and this is the rule the whole
    /// file exists to state once.
    /// </summary>
    public static StringComparer Comparer => StringComparer.FromComparison(Comparison);

    /// <summary>
    /// Reduces the two separator spellings to one, because <b>Windows accepts
    /// both</b>: <c>C:\Users</c> and <c>C:/Users</c> are one folder, and every
    /// comparison below is a string comparison that would otherwise call them
    /// two.
    ///
    /// **A no-op on Linux**, where both constants are <c>/</c> — and that is not
    /// merely a happy accident but the reason this goes through the platform's
    /// own constants rather than a literal. <c>\</c> is a legal *filename*
    /// character on Linux, so rewriting it there would rename files.
    /// </summary>
    private static string Unify(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    /// <summary>
    /// True for <c>/</c>, and on Windows for <c>C:\</c> and a UNC share root.
    ///
    /// Asks the framework rather than comparing to a literal:
    /// <see cref="Path.GetPathRoot(string)"/> already knows what a root looks
    /// like on the platform it is running on, and a path equal to its own root
    /// IS the root.
    /// </summary>
    public static bool IsRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // Unified first, or the comparison fails on spelling alone: on Windows
        // GetPathRoot("/") answers "\", which is the same root written the other
        // way, and a raw comparison would call "/" not-a-root.
        var unified = Unify(path);
        var root = Path.GetPathRoot(unified);

        return !string.IsNullOrEmpty(root) && string.Equals(root, unified, Comparison);
    }

    /// <summary>
    /// Removes a trailing separator so two spellings of one folder compare equal
    /// — <c>/home/flint</c> and <c>/home/flint/</c> are the same place.
    ///
    /// **A root keeps its separator**, because <c>/</c> and <c>C:\</c> ARE the
    /// trailing separator; trimming it would leave <c>""</c> and <c>C:</c>, and
    /// on Windows <c>C:</c> means "the current directory on drive C", which is a
    /// different place entirely.
    /// </summary>
    /// <summary>
    /// Splits a leaf name into the part a suffix goes after, and the extension
    /// that follows it.
    ///
    /// **A folder name is atomic, and so is a dotfile.** `my.project` has no
    /// `.project` extension to preserve - Explorer copies it to
    /// `my.project - Copy`, not `my - Copy.project` - and `.bashrc` is a name
    /// beginning with a dot rather than a bare extension, so restoring a second
    /// one produced ` (1).bashrc` with nothing in front of it.
    ///
    /// Here rather than in either caller because both the copy path and the
    /// restore path need the same answer, and they had drifted: one knew about
    /// folders and the other did not.
    /// </summary>
    public static (string Stem, string Extension) SplitLeaf(string leaf, bool isDirectory)
    {
        if (isDirectory || leaf.Length == 0) return (leaf, "");

        // A leading dot belongs to the name. Only a LATER dot starts an
        // extension, which is the same rule FileEntry.Extension applies.
        var dot = leaf.LastIndexOf('.');

        return dot <= 0 ? (leaf, "") : (leaf[..dot], leaf[dot..]);
    }

    public static string Normalise(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        var unified = Unify(path);

        if (IsRoot(unified)) return unified;

        // One separator to trim, because Unify already removed the other.
        var trimmed = unified.TrimEnd(Path.DirectorySeparatorChar);

        // Trimming can expose a root that the trailing separators were hiding,
        // e.g. "C:\\\\" or "//".
        return trimmed.Length == 0 ? unified[..1] : trimmed;
    }

    /// <summary>
    /// The containing folder, or null at a root — and null rather than empty
    /// <b>on purpose</b>.
    ///
    /// <see cref="Path.GetDirectoryName(string)"/> returns an EMPTY STRING for a
    /// bare name with no separator, not null. That difference already caused a
    /// live bug: the Up button reported a parent for the virtual path
    /// <c>vaktari:recent-files</c>, enabled itself, and then did nothing when
    /// pressed. Callers should be able to write <c>Parent(p) is { } up</c> and
    /// trust it.
    /// </summary>
    public static string? Parent(string? path)
    {
        if (string.IsNullOrEmpty(path) || IsRoot(path)) return null;

        var parent = Path.GetDirectoryName(Normalise(path));

        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    /// <summary>
    /// The name to show for a path. A root has no file name, so it shows as
    /// itself — <c>/</c> rather than blank.
    /// </summary>
    public static string LeafName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        var name = Path.GetFileName(Normalise(path));

        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// Whether two paths name the same place, ignoring a trailing separator,
    /// honouring the platform's case rules, and — on Windows — treating the two
    /// separator spellings as one.
    ///
    /// All three matter here, and the third was the one originally missed:
    /// this drives place highlighting and duplicate-tab detection, so calling
    /// <c>C:\Users</c> and <c>C:/Users</c> different folders opened a second tab
    /// on a folder already open.
    /// </summary>
    public static bool Same(string? a, string? b)
        => string.Equals(Normalise(a), Normalise(b), Comparison);

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> or lies
    /// anywhere inside it.
    ///
    /// **Three separate places claimed to prevent a folder being copied into
    /// itself and all three tested equality.** Equality catches dropping A onto
    /// A and misses dropping A into A/sub, which is the case that actually goes
    /// wrong: the destination is inside the thing being read, so the copy walks
    /// into its own output.
    ///
    /// The prefix has to end at a separator, or "/media/one" would claim
    /// "/media/onetwo" — the same trap <see cref="Volumes.MountFor"/> documents.
    /// </summary>
    public static bool Contains(string? root, string? candidate)
    {
        var top = Normalise(root);
        var inner = Normalise(candidate);

        if (top.Length == 0 || inner.Length == 0) return false;

        if (string.Equals(top, inner, Comparison)) return true;

        if (!inner.StartsWith(top, Comparison)) return false;

        // A drive root keeps its separator, so it already ends at one.
        return top.EndsWith(Path.DirectorySeparatorChar)
               || (inner.Length > top.Length
                   && inner[top.Length] == Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Every ancestor from the root down to <paramref name="path"/> itself, which
    /// is what the column strip walks.
    ///
    /// Written here rather than inline because the loop that did it inline
    /// terminated on <c>current == "/"</c> and would have spun forever on a
    /// Windows path — the one site in the application that turned a wrong
    /// assumption into a hang rather than a wrong answer.
    /// </summary>
    public static IReadOnlyList<string> Ancestors(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];

        var levels = new List<string>();

        for (var current = Normalise(path); !string.IsNullOrEmpty(current);)
        {
            levels.Add(current);

            if (IsRoot(current)) break;

            // Parent returns null at a root and for a rootless bare name, so this
            // cannot loop: every step is strictly shorter than the last.
            if (Parent(current) is not { } up) break;

            current = up;
        }

        levels.Reverse();

        // A relative or virtual path has no root to prepend, and forcing one in
        // would fabricate a place that does not exist.
        if (levels.Count > 0 && !IsRoot(levels[0])
            && Path.GetPathRoot(levels[0]) is { Length: > 0 } root)
            levels.Insert(0, root);

        return levels;
    }
}
