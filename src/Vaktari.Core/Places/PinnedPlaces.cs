namespace Vaktari.Core.Places;

/// <summary>
/// What a pinned path is, for the two providers that turn pins into rows.
///
/// **A pin was a folder and nothing else.** Both providers asked
/// <c>Directory.Exists</c> to decide whether a pinned row could be opened and
/// drew every pin with the bookmark glyph — so a search, which the panes can
/// open as a listing of its own, could not be kept: pinned, it would have
/// been a dimmed bookmark named after the tail of an internal scheme. The
/// scheme is decided in the Ui and the rows are built here, below it, so the
/// one fact the providers need — "this pin is a search" — lives at the lowest
/// level that reads it, and the Ui's own constant is read from here rather
/// than restated.
/// </summary>
public static class PinnedPlaces
{
    /// <summary>The scheme a search listing's path begins with. The Ui's
    /// <c>VirtualPaths.SearchPrefix</c> is this constant.</summary>
    public const string SearchPrefix = "vaktari:search:";

    public static bool IsSearch(string path)
        => path.StartsWith(SearchPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a pinned row can be opened: a folder while it exists, and a
    /// search always — the folder it looks under is asked when it runs, and
    /// a search whose folder has gone is still a question worth asking.
    /// </summary>
    public static bool IsAvailable(string path)
        => IsSearch(path) || Directory.Exists(path);

    /// <summary>The glyph a pinned row draws: a magnifier for a search, the
    /// bookmark for a folder.</summary>
    public static string Icon(string path)
        => IsSearch(path) ? "search" : "bookmark";
}
