namespace Vaktari.Core.Places;

/// <summary>
/// Tidying the label somebody types for a pinned place.
///
/// **Deliberately not <see cref="FileSystem.FileNames"/>.** A place label is a
/// caption, not a filename: a slash, a colon and "CON" are all perfectly good
/// text for a sidebar row, and refusing them here would be refusing something
/// harmless because it happens to be illegal somewhere else. Nothing is written
/// to disk under this name — the pin keeps its path.
///
/// What does have to go is anything that would wreck the row: a pasted newline
/// or tab turns one line into a shape the sidebar cannot draw, and leading or
/// trailing space is invisible and would make two labels look identical.
/// </summary>
public static class PlaceNames
{
    /// <summary>The label to store, or "" when nothing usable was typed — in
    /// which case the caller refuses rather than storing a blank row.</summary>
    public static string Clean(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return "";

        var kept = new System.Text.StringBuilder(typed.Length);

        foreach (var c in typed)
            if (!char.IsControl(c))
                kept.Append(c);

        return kept.ToString().Trim();
    }
}
