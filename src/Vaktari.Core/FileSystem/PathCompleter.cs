namespace Vaktari.Core.FileSystem;

/// <summary>
/// Tab-completion for a typed path.
///
/// Follows the shell convention people already have in their fingers: the first
/// press extends as far as every candidate agrees, and further presses cycle
/// through them. Completing straight to the first match would be faster to
/// write and worse to use — it moves the text somewhere you did not ask for
/// while you are still typing.
///
/// Holds the cycle position, so it is per-input-box rather than static.
/// </summary>
public sealed class PathCompleter
{
    private string _lastResult = "";
    private List<string> _matches = [];
    private int _index = -1;

    // The directory and fragment the candidates were built FOR. Cycling must
    // stay anchored to these rather than re-reading the text, because each
    // completion appends a trailing "/" and re-splitting would then treat the
    // folder just offered as the one to search — so the next candidate landed
    // INSIDE it instead of replacing it, and Tab produced
    // /home/flint/ → /home/flint/Desktop/ → /home/flint/Desktop/Documents/.
    private string _directory = "";
    private string _partial = "";

    /// <summary>Forgets the cycle; call when the user types something new.</summary>
    public void Reset()
    {
        _lastResult = "";
        _matches = [];
        _index = -1;
        _directory = "";
        _partial = "";
    }

    /// <summary>
    /// The next completion for <paramref name="text"/>, or null when there is
    /// nothing to add.
    /// </summary>
    public string? Complete(string text)
    {
        // Rebuild when the user has typed something new, and also when the last
        // offer was UNAMBIGUOUS — one candidate means the trailing "/" has taken
        // us inside it, so the next Tab should complete in there. With several
        // candidates the text still ends in a folder we are choosing BETWEEN,
        // so the cycle continues instead.
        if (text != _lastResult || _matches.Count <= 1) Rebuild(text);

        if (_matches.Count == 0) return null;

        if (_matches.Count == 1)
        {
            _index = 0;
            return Remember(Join(_directory, _matches[0]));
        }

        // Extend to the shared prefix first, and only start cycling once there
        // is nothing left that every candidate agrees on.
        var shared = CommonPrefix(_matches);

        if (_index < 0 && shared.Length > _partial.Length)
            return Remember(Join(_directory, shared), cycling: false);

        _index = (_index + 1) % _matches.Count;
        return Remember(Join(_directory, _matches[_index]));
    }

    private string Remember(string result, bool cycling = true)
    {
        _lastResult = result;
        if (!cycling) _index = -1;
        return result;
    }

    /// <summary>
    /// Every folder Tab would offer for <paramref name="text"/>, as full paths
    /// in the order Tab hands them out, at most <paramref name="limit"/> of
    /// them.
    ///
    /// **The candidate list was private, so the box could show one candidate at
    /// a time and only to somebody who already knew to press Tab.** Everything
    /// a dropdown needs was worked out on the first press and thrown away on
    /// the last.
    ///
    /// Static, and it enumerates again rather than handing out the cycle's own
    /// list. A cycle in progress is anchored to the directory and fragment it
    /// was built for, while the caller here asks on every keystroke: handing
    /// out the live list would show a stale offer the moment another letter was
    /// typed, and rebuilding it to answer would reset <c>_index</c> — so Tab
    /// would stop advancing and re-offer the first candidate forever. The cost
    /// is one directory read per keystroke, which is the read Tab already does
    /// per press.
    /// </summary>
    public static IReadOnlyList<string> Suggestions(string text, int limit)
    {
        var (directory, partial) = Split(text);

        return Candidates(directory, partial)
            .Take(limit)
            .Select(name => Join(directory, name))
            .ToList();
    }

    private void Rebuild(string text)
    {
        _index = -1;

        var (directory, partial) = Split(text);

        _directory = directory;
        _partial = partial;
        _matches = Candidates(directory, partial);
    }

    /// <summary>
    /// The child folders of <paramref name="directory"/> that
    /// <paramref name="partial"/> could grow into, in the order they are
    /// offered.
    ///
    /// One place, shared by the cycle and by the dropdown, so what Tab does and
    /// what the list shows cannot drift apart — otherwise each would carry its
    /// own copy of the hidden-folder rule and of the ordering.
    /// </summary>
    private static List<string> Candidates(string directory, string partial)
    {
        // Moved here unchanged from Rebuild, and a fast path rather than a
        // rule: MEASURED, deleting it leaves every test green, because
        // EnumerateDirectories("") throws and the catch below answers with the
        // same empty list. It is kept because that is the answer without an
        // exception, on a method the box now calls once per keystroke.
        if (directory.Length == 0) return [];

        try
        {
            return Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))

                // A leading dot has to be asked for explicitly, or completing in
                // a home directory is mostly configuration folders.
                .Where(name => partial.StartsWith('.') || !name.StartsWith('.'))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Unreadable or missing directory: nothing to offer.
            return [];
        }
    }

    /// <summary>
    /// Both spellings. Windows accepts either and people type either — a path
    /// pasted from a shell script arrives with forward slashes, one copied from
    /// Explorer with backslashes — and reading only one of them made Tab do
    /// nothing at all for every ordinary Windows path.
    /// </summary>
    private static readonly char[] Separators = ['/', '\\'];

    private static bool IsSeparator(char c) => c is '/' or '\\';

    /// <summary>Splits into the folder to search and the fragment to match.</summary>
    private static (string Directory, string Partial) Split(string text)
    {
        var path = Expand(text);

        if (path.Length == 0) return ("", "");

        // A trailing separator means "inside this folder", not "a folder whose
        // name is empty" — so everything in it is a candidate.
        if (IsSeparator(path[^1])) return (path, "");

        var slash = path.LastIndexOfAny(Separators);
        if (slash < 0) return ("", path);

        var directory = slash == 0 ? path[..1] : path[..slash];

        // **"C:" is not a folder.** A bare drive letter means wherever this
        // process happens to be on that drive, so completing in it would offer
        // the contents of somewhere nobody named. PathVariables makes the same
        // point about typing one.
        if (directory.Length == 2 && directory[1] == ':' && char.IsLetter(directory[0]))
            directory += Path.DirectorySeparatorChar;

        return (directory, path[(slash + 1)..]);
    }

    /// <summary>
    /// Rejoins in the spelling the directory already uses, so completing a path
    /// typed with backslashes does not hand back a mix of the two.
    /// </summary>
    private static string Join(string directory, string name)
    {
        var separator = Style(directory);

        var joined = IsSeparator(directory[^1])
            ? directory + name
            : directory + separator + name;

        // Trailing separator, so the next Tab completes inside it rather than
        // re-matching the folder just chosen.
        return joined + separator;
    }

    private static char Style(string directory)
    {
        var at = directory.LastIndexOfAny(Separators);

        return at >= 0 ? directory[at] : Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// The same expansion the path bar navigates with.
    ///
    /// **It used to be a second, poorer copy that knew only about `~`**, so
    /// `%LOCALAPPDATA%\GOG.com` completed nothing while typing it and pressing
    /// Enter went to exactly the right place — the box appearing to not
    /// understand a path it understood perfectly well. One expander, so the two
    /// cannot drift apart again.
    /// </summary>
    private static string Expand(string text) => PathVariables.Expand(text);

    private static string CommonPrefix(List<string> values)
    {
        if (values.Count == 0) return "";

        var prefix = values[0];

        foreach (var value in values.Skip(1))
        {
            var length = 0;

            while (length < prefix.Length && length < value.Length
                   && char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(value[length]))
                length++;

            prefix = prefix[..length];
            if (prefix.Length == 0) break;
        }

        return prefix;
    }
}
