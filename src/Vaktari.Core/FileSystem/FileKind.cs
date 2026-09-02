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
        // **A link was drawn as the thing it points at, everywhere.** The
        // Symlink flag has been set correctly by both providers since they were
        // written and read by nothing at all — no binding, no converter, no
        // view-model property — so a symlinked folder, a junction and a mount
        // point were indistinguishable from the real thing in every layout, and
        // deleting one is a very different act from deleting the other.
        //
        // A FOLDER only, here. A symlink to a file already carries its own
        // type — "PNG file" says more than "Link" would — and losing that to
        // say "this is a link" trades one fact for another. A folder has no
        // extension to lose, so this is free. The emblem is what marks the
        // rest, and that is a drawing rather than a word.
        if (entry.IsDirectory) return entry.IsSymlink ? "Folder link" : "Folder";

        var extension = entry.Extension;

        if (extension.Length == 0) return "File";

        // **Explorer never says "LNK file", and never shows the extension
        // either.** lnkfile carries NeverShowExt in the registry, so Desktop and
        // the Start Menu — folders that are nothing but shortcuts — listed here
        // as a wall of "Chrome.lnk / LNK file" rows and everywhere else on the
        // machine as "Chrome / Shortcut". The sidebar already agreed with
        // Explorer, pinning "Sync.lnk" as "Sync", and the shortcut writer's own
        // doc comment claimed the listing hid the extension. Only the listing
        // disagreed.
        //
        // Windows only. On Linux a .lnk is an opaque file from another
        // operating system that nothing here can follow, so calling it a
        // shortcut would promise a hop that does not exist.
        if (OperatingSystem.IsWindows() && IsShortcut(extension)) return "Shortcut";

        // Bounded on purpose. A name may legally end in a dot and a hundred
        // characters, and caching one entry per such name would make the cache
        // grow with the listing rather than with the kinds in it.
        if (extension.Length > 12) return "File";

        return Phrases.GetOrAdd(extension.ToString(), ext => ext.ToUpperInvariant() + " file");
    }

    /// <summary>
    /// The Windows shortcut extension, in the shape
    /// <see cref="FileEntry.Extension"/> hands it out — without its dot.
    ///
    /// Public and platform-blind so the fact lives in one place: the properties
    /// provider asks it too, and two copies would drift into a listing that
    /// says "Shortcut" beside a properties window that says "LNK file".
    /// </summary>
    public static bool IsShortcut(ReadOnlySpan<char> extension)
        => extension.Equals("lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name a listing shows: the entry's own, except for a Windows
    /// shortcut, whose extension Explorer never displays.
    ///
    /// Hands back the SAME string instance whenever there is nothing to hide,
    /// which is every row but a handful. This runs once per visible row per
    /// bind, and a substring allocated per row while scrolling is the one cost
    /// this codebase does not pay.
    ///
    /// Cannot return empty: Extension is default when the dot sits at index 0,
    /// so a file literally named ".lnk" is a name rather than an extension and
    /// keeps every character.
    /// </summary>
    public static string DisplayName(FileEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Name) || entry.IsDirectory) return entry.Name ?? "";

        var extension = entry.Extension;

        return OperatingSystem.IsWindows() && IsShortcut(extension)
            ? entry.Name[..^(extension.Length + 1)]
            : entry.Name;
    }
}
