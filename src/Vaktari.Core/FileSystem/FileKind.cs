using System.Collections.Concurrent;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// What kind of thing a row is, in one short phrase, computed synchronously.
///
/// **The platforms already have a Kind, and neither can be a column.** Windows
/// asks the shell and Linux asks shared-mime-info; both are per-file and async,
/// so a listing of two hundred thousand rows would be two hundred thousand
/// round trips to fill a column that scrolls past in a second. They also
/// disagree in shape — "PNG file" against "image/png" — so a column fed by
/// whichever platform is running would say something different on each.
///
/// This is the cheap answer instead: the extension, spelled the way Explorer
/// spells it when it has nothing better. It agrees with the Kind sort and the
/// Kind grouping beside it, because all three key on the same extension.
/// </summary>
public static class FileKind
{
    /// <summary>
    /// One string per extension, shared by every row that has it. A listing is
    /// mostly a handful of extensions repeated, so the alternative is tens of
    /// thousands of identical strings — allocated while scrolling, which is the
    /// one place this must not do that.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Phrases = new(StringComparer.OrdinalIgnoreCase);

    public static string Describe(FileEntry entry)
    {
        if (entry.IsDirectory) return "Folder";

        var extension = entry.Extension;

        if (extension.Length == 0) return "File";

        // Bounded on purpose. A name may legally end in a dot and a hundred
        // characters, and caching one entry per such name would make the cache
        // grow with the listing rather than with the kinds in it.
        if (extension.Length > 12) return "File";

        return Phrases.GetOrAdd(extension.ToString(), ext => ext.ToUpperInvariant() + " file");
    }
}
