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
/// </summary>
public static class DragEffect
{
    public static DragIntent For(
        bool control, bool shift, bool internalDrag, IReadOnlyList<string> sources, string destination)
    {
        // Both together is Explorer's "create shortcut here", and it has to be
        // read before either alone — a chord is not a pair of fallbacks.
        if (control && shift) return DragIntent.Link;

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
