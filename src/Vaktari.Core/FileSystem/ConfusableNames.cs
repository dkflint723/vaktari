namespace Vaktari.Core.FileSystem;

/// <summary>
/// Names in one folder that the eye cannot tell apart.
///
/// **Two files really can sit side by side looking identically named.**
/// "Ember Setup 0.1.0 .exe" and "Ember Setup 0.1.0.exe" differ by one space
/// before the extension: legal, distinct to the filesystem, and invisible in
/// any listing — including Explorer's. Somebody looking at those two rows has
/// no way to know why there are two, which of them is which, or whether their
/// file manager has just done something wrong.
///
/// So the rows say so. This is not a convention borrowed from anywhere; it is
/// an answer to a question a listing could not otherwise be asked.
/// </summary>
public static class ConfusableNames
{
    /// <summary>
    /// The paths whose names collide with another once the invisible
    /// differences are set aside.
    ///
    /// **Whitespace and case**, and nothing cleverer. Unicode has whole
    /// families of characters that look alike, and chasing those would flag
    /// names that are merely unusual — a Cyrillic folder name is not a
    /// mistake. What is worth flagging is a difference somebody could not see
    /// even knowing to look.
    ///
    /// The Name asked for is whatever the row DRAWS, which is not always the
    /// file name — a launcher draws its Name= key. Two of those can be exactly
    /// equal, where two file names in one directory cannot, and an exact match
    /// flattens to itself and collides like any other.
    /// </summary>
    public static IReadOnlySet<string> Among(IEnumerable<(string FullPath, string Name)> entries)
    {
        var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (path, name) in entries)
        {
            var key = Flatten(name);

            if (key.Length == 0) continue;

            if (!seen.TryGetValue(key, out var paths)) seen[key] = paths = [];

            paths.Add(path);
        }

        // **Ordinal, deliberately.** Case-insensitively, the two paths this is
        // built to mark — "notes.txt" and "NOTES.TXT", which can coexist on a
        // case-sensitive filesystem — collapse into one entry and neither row
        // is flagged. The paths come from the same enumeration the rows bind,
        // so they match exactly; it is the FLATTENING that ignores case, which
        // is where ignoring it belongs.
        var confusable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var paths in seen.Values)
        {
            // One name that flattens to something is just a name.
            if (paths.Count < 2) continue;

            foreach (var path in paths) confusable.Add(path);
        }

        return confusable;
    }

    /// <summary>
    /// A name with everything invisible taken out, so two that look the same
    /// land on the same key.
    /// </summary>
    private static string Flatten(string name)
    {
        var built = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c)) continue;

            built.Append(char.ToLowerInvariant(c));
        }

        return built.ToString();
    }
}
