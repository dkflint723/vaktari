using Avalonia;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Input;

/// <summary>
/// What rides beside the pointer while a drag is in flight, and where it sits.
///
/// **A drag in flight said what it would land in and never what it was
/// carrying.** The destination half is answered — the folder row under the
/// pointer takes a ring, a place takes a wash — but the source half was the OS
/// cursor and nothing else, so once the pointer had left the rows it started on
/// (over the sidebar, over the other pane, over a folder two levels down) the
/// gesture had no subject: whether the hand held the one file or the eleven
/// could only be learnt by letting go.
///
/// It is drawn rather than handed to the toolkit because there is nowhere to
/// hand it: <c>DragDrop.DoDragDropAsync</c> takes a trigger, a payload and a
/// set of effects, and has no drag-image parameter at all. So the label is a
/// control inside this window, moved by the drag-over handler.
///
/// **The one is named; the many are counted**, which is the rule
/// <see cref="UndoNames"/> already settled on for the same question in the undo
/// row: one name is the useful thing when there is one, and past that a list is
/// unreadable and the count is what a person actually wants.
/// </summary>
public static class DragGhost
{
    /// <summary>
    /// How far off the pointer the label sits, on both axes.
    ///
    /// Not zero, and that is the whole of the rule: centred on the pointer it
    /// would cover the row being aimed at, which is the one thing a drag has to
    /// keep showing.
    /// </summary>
    public const double Gap = 14;

    /// <summary>
    /// What the label says.
    /// </summary>
    /// <param name="carried">The paths the drag carries. It is empty for a drag
    /// this application did not start, because the caller does not read a
    /// foreign drag's payload at all — it hands this an empty list rather than
    /// asking. The empty string is the answer for that, and the caller draws
    /// nothing rather than a label claiming zero.</param>
    public static string Label(IReadOnlyList<string> carried) => carried.Count switch
    {
        0 => "",
        1 => Named(carried[0]),
        var many => $"{many:N0} items",
    };

    /// <summary>
    /// The name the row it was dragged out of is showing.
    ///
    /// **The ghost said "Firefox.lnk" over a row reading "Firefox".** Every
    /// name cell in the window draws <see cref="FileKind.DisplayName"/>, which
    /// hides a Windows shortcut's extension and swaps a .desktop launcher's
    /// file name for its Name=; this drew <see cref="PathRules.LeafName"/>
    /// instead. Measured before the fix, in a headless window with a real
    /// drag raised over it: row 'Firefox', ghost 'Firefox.lnk' — and on the
    /// launcher side row 'Konsole', ghost 'org.kde.konsole.desktop', which
    /// share not one character. A ghost that no longer says what is being
    /// dragged is the fault the extension-keeping trim was written to prevent,
    /// arriving by the other door.
    ///
    /// The rule is ASKED FOR rather than copied, so what a listing decides to
    /// show reaches the label with it.
    /// </summary>
    private static string Named(string path)
    {
        var leaf = PathRules.LeafName(path);
        var extension = FileEntry.ExtensionOf(leaf);

        // **The stat is behind this line because it costs 28 µs and this runs
        // on every drag-over.** Measured on this machine, 200,000 warm calls
        // each: Directory.Exists is 28.3 µs for an existing file, 30.8 µs for a
        // folder and 18.5 µs for a path that is not there — a filter-driver
        // tax a networked path pays many times over, on the thread that is
        // meant to be following the pointer. Nothing but these two extensions
        // can make FileKind answer differently from the leaf name, so nothing
        // else is worth one.
        if (!FileKind.IsShortcut(extension) && !FileKind.IsLauncher(extension)) return OneLine(leaf);

        // A folder called "Notes.lnk" is not a shortcut and keeps every
        // character, which FileKind reads off the flags rather than off the
        // extension — so the flags have to be right for it to be asked at all.
        var flags = (IsFolder ?? Directory.Exists)(path)
            ? EntryFlags.Directory
            : EntryFlags.None;

        return OneLine(FileKind.DisplayName(new FileEntry(leaf, path, 0, default, flags)));
    }

    /// <summary>
    /// Whether a path is a folder. Null in the application, where the answer is
    /// <see cref="Directory.Exists"/>.
    ///
    /// A seam because it is a machine fact, and because skipping the ask is
    /// the one part of <see cref="Named"/> that changes no answer — a test
    /// that COUNTS the asks is the only thing that can tell the guard above
    /// from its absence.
    /// </summary>
    internal static Func<string, bool>? IsFolder { get; set; }

    /// <summary>
    /// One line, whatever the file is called.
    ///
    /// **The label was bounded in width and not in height, and a name may
    /// contain newlines.** Only "/" and NUL are illegal in a Linux file name,
    /// and a launcher's Name= is a line out of a file anyone can write, so this
    /// is content somebody else chose. Measured through the window's own
    /// DragGhostText and DragGhostBox: a forty-line name asked for 136 × 532
    /// against a one-line 124 × 22, and <see cref="Spot"/> then put that slab
    /// at (614, 0) in a 1200 × 1000 window — a column down the whole of it,
    /// no longer riding the pointer at all.
    ///
    /// Dropped rather than folded into spaces, which is what
    /// <see cref="Vaktari.Core.Places.PlaceNames.Clean"/> settled on for the
    /// sidebar's version of this same hazard. Not that method, though: it also
    /// trims and answers "" for a label that was all whitespace, and both of
    /// those would make the ghost disagree with the row it came out of.
    ///
    /// Unconditional, with no fast path for the names that have nothing to
    /// drop. One copy of one file name per drag-over is beneath notice beside
    /// the list DroppedFileReader.Offered already builds for the same event —
    /// and a branch that only saves an allocation cannot be told from its own
    /// absence by any test, which is a worse thing to have in the file.
    /// </summary>
    private static string OneLine(string name)
        => new(name.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>
    /// Where to put it, in the coordinates of the layer it is drawn on.
    ///
    /// **Flipped at an edge rather than clamped to it.** Clamping puts the
    /// label's far edge on the window's, which walks the pointer INTO the
    /// label — and the label is the one thing that must never sit over the row
    /// being aimed at. Flipping to the other side of the pointer keeps the gap
    /// on whichever side there is room for.
    /// </summary>
    /// <param name="room">The layer's own size. A layer that has not been laid
    /// out reports nothing, and clamping against nothing would park the ghost
    /// in the corner for the whole drag, so a zero axis is left alone.</param>
    public static Point Spot(Point pointer, Size ghost, Size room)
    {
        var x = pointer.X + Gap;
        var y = pointer.Y + Gap;

        if (room.Width > 0 && x + ghost.Width > room.Width) x = pointer.X - Gap - ghost.Width;
        if (room.Height > 0 && y + ghost.Height > room.Height) y = pointer.Y - Gap - ghost.Height;

        // A window narrower than the label leaves nowhere on either side; the
        // near corner is the least bad of those, and it is still inside.
        return new Point(Math.Max(0, x), Math.Max(0, y));
    }
}
