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
    /// all the shortcut rule needs. <paramref name="tag"/> is the reparse tag
    /// of an entry wearing the ReparsePoint attribute, as <see cref="TagFor"/>
    /// reads it, and null for one that does not wear it or whose tag could not
    /// be read.
    /// </summary>
    internal static EntryFlags For(
        ReadOnlySpan<char> name, FileAttributes attributes, bool isDirectory, uint? tag)
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

        // **Every reparse point drew the link emblem.** The attribute alone
        // used to set the flag, to cover symbolic links, junctions and mount
        // points alike without the second call the tag costs — and it covered
        // an app execution alias with them, and whatever else a filter marks
        // for its own purposes, since none of those stands for another name.
        // Measured: a file under a third-party tag drew the arrow while the
        // walk said it was no link, and the aliases every Store app leaves
        // under WindowsApps — tag 0x8000001B, all of them — were a folder of
        // arrows. So the walk's own question is asked, of the tag TagFor read.
        //
        // Not among them, and unchanged here: a cloud placeholder and a
        // compressed file. Their filters hide the attribute from a process in
        // the default mode, which is where .NET starts — measured against a
        // Proton Drive sync root, and against a file compact.exe had just
        // compressed, which answered Archive and "not a reparse point".
        if (SafeWalk.IsLink(attributes, tag))
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

    /// <summary>
    /// The tag for <see cref="For"/>, read only for an entry wearing the
    /// attribute. **This is the second look the enumeration used to avoid**,
    /// and its price was measured before it was paid: 19-24 us per marked row
    /// — the entry opened without being followed, and asked — against under a
    /// microsecond to enumerate it, and nothing at all for an unmarked row,
    /// which is every row in almost every folder. A folder of a thousand
    /// marked entries pays about twenty milliseconds to stop drawing a
    /// thousand arrows.
    /// </summary>
    internal static uint? TagFor(string path, FileAttributes attributes)
        => (attributes & FileAttributes.ReparsePoint) != 0 ? ReparseTags.Of(path) : null;
}
