namespace Vaktari.Core.FileSystem;

/// <summary>
/// What an undo is called, in the one place both engines and the menu agree.
///
/// **Ctrl+Z said nothing about what it was going to undo.** There was no Undo
/// row in any menu — the only route was a key — so the whole feature was
/// invisible, and pressing it was a guess: after a copy, a rename and a delete
/// in quick succession, the only way to find out which one came back was to
/// press it and look. Both references name the act in the menu row and put the
/// key beside it.
///
/// **The one is named; the many are counted.** "copy of readme.txt" says
/// exactly what will come back, which is what a person wants when there is one
/// thing; past that a list is unreadable in a menu row and the count is the
/// useful part. The count branch never writes "item(s)" — the status line
/// beside it does, because there a count of one is possible; here it is not,
/// so the plural is always right.
/// </summary>
public static class UndoNames
{
    public static string Of(string verb, int count) => $"{verb} of {count:N0} items";

    public static string Of(string verb, IReadOnlyList<string> paths)
        => paths.Count == 1
            ? $"{verb} of {PathRules.LeafName(paths[0])}"
            : Of(verb, paths.Count);
}
