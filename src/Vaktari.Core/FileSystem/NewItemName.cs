namespace Vaktari.Core.FileSystem;

/// <summary>
/// A free name for something the user has just asked Vaktari to make.
///
/// **Three creates carried three copies of the rule, and all three spelled it
/// differently from the rest of the application.** New folder, new file and
/// new-from-template each had their own numbering loop, and each produced
/// "New folder 2" — a space and a bare digit. Every other name Vaktari
/// invents is parenthesised: a conflict kept both is "report (2).txt" from
/// WindowsFileOperations.Deduplicate and "report (1).txt" from
/// XdgTrash.Deduplicate, and Explorer's own answer to this very gesture is
/// "New folder (2)". So the one name a user sees most often was written in a
/// style neither desktop, and no other part of this program, uses.
///
/// One rule in one place, because three copies is how they drifted apart.
/// </summary>
public static class NewItemName
{
    /// <summary>
    /// <paramref name="stem"/> plus <paramref name="extension"/> inside
    /// <paramref name="directory"/>, numbered until nothing is there.
    ///
    /// From two, for the reason WindowsFileOperations.Deduplicate gives: the
    /// thing already sitting at the plain name is the first.
    /// </summary>
    public static string Free(string directory, string stem, string extension)
    {
        var candidate = Path.Combine(directory, stem + extension);

        for (var n = 2; Taken(candidate); n++)
            candidate = Path.Combine(directory, $"{stem} ({n}){extension}");

        return candidate;
    }

    /// <summary>
    /// **A file counts, not only a folder.** New folder asked
    /// <see cref="Directory.Exists"/> alone, so a FILE sitting at the name was
    /// invisible to the check: it went on to create a directory at a path it
    /// had just been told was free, System.IO refused, and the gesture made
    /// nothing at all while the status bar showed an IO error. The two loops
    /// beside it had always asked both.
    /// </summary>
    private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);
}
