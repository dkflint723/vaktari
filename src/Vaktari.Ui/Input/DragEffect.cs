namespace Vaktari.Ui.Input;

/// <summary>What an unmodified drag means.</summary>
public enum DragIntent { Copy, Move }

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
        if (control) return DragIntent.Copy;
        if (shift) return DragIntent.Move;

        // From another application, a move would mean taking somebody else's
        // file away on a plain drag. Copying is the safe reading and what every
        // desktop does.
        if (!internalDrag) return DragIntent.Copy;

        return sources.Count > 0 && sources.All(s => SameVolume(s, destination))
            ? DragIntent.Move
            : DragIntent.Copy;
    }

    /// <summary>
    /// Whether two paths live on the same volume.
    ///
    /// Unknown counts as different, which errs towards copying — the answer
    /// that leaves the original where it was. A network path has no drive
    /// letter, and treating two unrelated shares as one volume would move files
    /// across a network on a plain drag.
    /// </summary>
    /// <summary>
    /// **Was Path.GetPathRoot, which is a no-op on Linux.** Every absolute path
    /// there has the root "/", so this always answered "same volume" and a
    /// plain drag to a USB stick or a network mount MOVED the files - the one
    /// case that should copy and leave the original where it is. Volumes.Same
    /// keeps the root comparison on Windows, where a drive letter really is the
    /// volume, and compares mount points elsewhere.
    /// </summary>
    private static bool SameVolume(string a, string b)
        => Vaktari.Core.FileSystem.Volumes.Same(a, b);

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
