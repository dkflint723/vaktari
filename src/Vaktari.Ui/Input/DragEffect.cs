namespace Vaktari.Ui.Input;

/// <summary>What a drag means, modifiers considered.</summary>
public enum DragIntent { Copy, Move, Link }

/// <summary>
/// Copy or move, when nothing was held down.
///
/// **Windows decides by volume, and Vaktari decided by origin.** Explorer moves
/// within a drive and copies between drives — the reasoning being that a move
/// inside a volume is a rename of an entry and effectively free, while one
/// across volumes is a copy and a delete, which is slow and destroys the
/// original. Dragging to a place on another disk therefore did something
/// materially different from what Windows would have done, without saying so.
///
/// Holding a key still wins outright, as it does everywhere.
///
/// **Explorer has two gestures for "create shortcut here" and this had one.**
/// Ctrl+Shift was read; Alt was not a parameter at all, so Alt+drag — the
/// one-key spelling of the two — arrived with no modifier set and fell through
/// to the volume rule, which MOVED the file within a drive. The gesture that is
/// supposed to leave the original where it is was the gesture most likely to
/// take it away.
/// </summary>
public static class DragEffect
{
    public static DragIntent For(
        bool control, bool shift, bool alt, bool internalDrag,
        IReadOnlyList<string> sources, string destination)
    {
        // Explorer's two spellings of "create shortcut here", read before every
        // single modifier below — a chord is not a pair of fallbacks, and Alt
        // is not a key that falls through to what the other two would have said.
        //
        // **Alt held WITH one of the others is a decision, not a measurement.**
        // What is known is the shape of the four gestures Explorer documents:
        // Ctrl+Shift or Alt means link, Ctrl alone copy, Shift alone move. What
        // the shell does with Ctrl+Alt was not checked from here — running it
        // is the only way to find out and this machine does not run the shell —
        // so `alt || (control && shift)` reads alt as the deliberate key and
        // lets it decide. Pinned by
        // AltDragShortcutTests.Alt_outranks_the_modifiers_held_with_it, which
        // is the record of the choice; if the shell is ever measured saying
        // otherwise, that test is the one line to change.
        if (alt || (control && shift)) return DragIntent.Link;

        if (control) return DragIntent.Copy;
        if (shift) return DragIntent.Move;

        // From another application, a move would mean taking somebody else's
        // file away on a plain drag. Copying is the safe reading and what every
        // desktop does.
        if (!internalDrag) return DragIntent.Copy;

        if (sources.Count == 0) return DragIntent.Copy;

        // **Read once, asked many times.** SameVolume used to read the whole
        // mount table on every call, and this asks it once per file — so a
        // plain drag of a 200-file selection was 200 mount-table reads for
        // every drag-over event, and drag-over fires continuously while the
        // pointer moves.
        var mounts = Vaktari.Core.FileSystem.Volumes.MountPoints();

        return sources.All(s => Vaktari.Core.FileSystem.Volumes.Same(s, destination, mounts))
            ? DragIntent.Move
            : DragIntent.Copy;
    }


    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
