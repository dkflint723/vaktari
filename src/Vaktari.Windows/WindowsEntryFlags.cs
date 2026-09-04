using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The flags a Windows row carries, worked out in one place for every way a
/// row can arrive: the listing's enumeration, the watcher's single-entry
/// lookup, and the search walk.
///
/// **A shortcut carried no mark anywhere.** Only FileAttributes.ReparsePoint
/// set the Symlink flag, so a symbolic link and a junction drew the listing's
/// link emblem and a .lnk — the one indirection a Windows desktop is actually
/// full of — drew nothing. Desktop and the Start Menu are folders of nothing
/// but shortcuts, and every row in them was drawn exactly like the thing it
/// points at: the same program glyph an .exe gets, and with the desktop's own
/// icons switched on, the target's icon with nothing added to it. The word
/// beside them already said "Shortcut", the properties window said "Shortcut",
/// and <c>LinkEmblem</c>'s own description says it is the emblem for "a
/// shortcut, a symlink or a junction". Only the picture disagreed, because no
/// attribute marks a .lnk and nothing read the name.
///
/// **And the same five lines were written three times.** Each path computed
/// its own set from the same attributes, agreeing only by hand — which is how
/// a rule taught to one of them ends up drawing an arrow in the listing and
/// none on the same file half a second later through the watcher, on the same
/// row, in the same folder. Three copies is three chances to teach two of
/// them; one copy is none.
///
/// The flag is called Symlink and a .lnk is not one, which is the honest
/// objection. What the flag means to everything that reads it is "this is an
/// indirection rather than the thing itself" — the enum's own note says the UI
/// asks nothing finer, and by that question a shortcut is one.
/// </summary>
internal static class WindowsEntryFlags
{
    /// <summary>
    /// <paramref name="name"/> is the entry's own name rather than its path:
    /// it is what an enumeration has to hand without a second stat, and it is
    /// all the shortcut rule needs.
    /// </summary>
    internal static EntryFlags For(
        ReadOnlySpan<char> name, FileAttributes attributes, bool isDirectory)
    {
        var flags = EntryFlags.None;

        if (isDirectory)
            flags |= EntryFlags.Directory;

        // An attribute, not a leading dot. A file named ".gitignore" is an
        // ordinary visible file here, which is the whole difference from Linux.
        if ((attributes & FileAttributes.Hidden) != 0)
            flags |= EntryFlags.Hidden;

        if ((attributes & FileAttributes.System) != 0)
            flags |= EntryFlags.System;

        // Covers symbolic links, junctions and mount points alike. The UI only
        // asks "is this an indirection", and telling them apart needs the
        // reparse tag, which costs another call per entry.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            flags |= EntryFlags.Symlink;

        // And the one the attributes never say. A shortcut is an ordinary file
        // holding a path, so it is read off the name instead — through the same
        // predicate the Type column and the properties window ask, so a row
        // cannot be drawn as a link while the words beside it say otherwise.
        //
        // A folder called "things.lnk" is a folder: an extension is a fact
        // about a file, and FileKind refuses it for a directory too.
        if (!isDirectory && FileKind.IsShortcut(FileEntry.ExtensionOf(name)))
            flags |= EntryFlags.Symlink;

        if ((attributes & FileAttributes.ReadOnly) != 0)
            flags |= EntryFlags.ReadOnly;

        return flags;
    }
}
