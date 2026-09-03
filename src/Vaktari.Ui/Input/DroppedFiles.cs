using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Vaktari.Ui.Input;

/// <summary>
/// What a drop is actually carrying, and — when the answer is nothing usable —
/// why.
///
/// **A drop that cannot be taken used to do nothing at all**, which is
/// indistinguishable from a drop that missed the pane, or from a bug. Dragging
/// out of a zip opened in Explorer does exactly that, and reads as the
/// application being unreliable.
/// </summary>
/// <param name="Paths">Real paths, ready to copy or move.</param>
/// <param name="Refusal">Empty when there is nothing to explain.</param>
public readonly record struct DroppedFiles(IReadOnlyList<string> Paths, string Refusal)
{
    public bool Any => Paths.Count > 0;
}

public static class DroppedFileReader
{
    /// <summary>
    /// Reads a drop.
    ///
    /// **Two lines of Avalonia, then a decision.** The decision is where the
    /// behaviour lives and it is separated deliberately: Avalonia's storage
    /// items cannot be implemented outside the framework, so anything that
    /// takes one cannot be tested, and burying the reasoning inside such a
    /// method would put it out of reach along with them.
    /// </summary>
    public static DroppedFiles Read(IDataTransfer data, string destination, bool copying)
    {
        return Decide(Offered(data), [.. data.Formats.Select(f => f.Identifier)], destination, copying);
    }

    /// <summary>The local paths a drop carries, before anything is decided about
    /// them — which is what the copy-or-move rule needs to see.</summary>
    public static IReadOnlyList<string> Offered(IDataTransfer data) =>
        [.. (data.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()];

    /// <summary>
    /// What a drop of these paths, offered in these formats, means here.
    /// </summary>
    /// <param name="offered">Local paths the drop carried, which may be none
    /// even when it carried files.</param>
    /// <param name="formats">Format identifiers, which is how a drop carrying
    /// files with no paths is told from one carrying nothing.</param>
    /// <param name="copying">Whether the drag means copy. **A file dropped into
    /// the folder it already lives in is a no-op when moving and a duplicate
    /// when copying** — Ctrl+drag onto the current folder is how Explorer makes
    /// a second copy, and filtering those paths out regardless meant the
    /// gesture did nothing at all.</param>
    internal static DroppedFiles Decide(
        IReadOnlyList<string> offered, IReadOnlyList<string> formats, string destination, bool copying)
    {
        // A folder cannot be put inside itself, nor inside one of its own
        // subfolders, whichever key is held: the plan is built by walking the
        // source and the destination is inside what is being walked, so a copy
        // feeds itself its own output and a move dismantles the tree it is
        // halfway through reading.
        //
        // **The whole drop is refused, rather than the one path that offends.**
        // This filtered the offender out and let the rest through, which is
        // invisible with one item selected and destructive with several:
        // dragging A, B and C onto A dropped A from the list and moved B and C
        // INTO A, so half a selection was swallowed by the other half. A
        // six-pixel twitch over a selected folder was enough to start it, and
        // the cursor showed an ordinary move right up to the release, because
        // DragOver asks this same question. The engine refuses this case by
        // name and never saw it, because the source it looks for had already
        // been removed here.
        //
        // **And asked of `offered`, above the already-here filter**, which is
        // what the MOVE arm needed: that filter strips the destination itself
        // as a path going nowhere, so a containment check made after it sees
        // only B and C and finds nothing wrong with either. The copy arm keeps
        // every path it is given, so there it is the refusing rather than the
        // ordering that does the work — both halves are needed, for different
        // arms.
        //
        // The same shape as ShellViewModel.TransferToOther, which refuses the
        // whole transfer rather than the one folder that cannot go.
        //
        // **Ctrl+Shift is refused along with them**, and a shortcut to A inside
        // A would have been harmless. This is told copy-or-move and never that
        // the intent was "create shortcut here", so telling the two apart is a
        // change to what the handlers ask rather than to what is decided here.
        if (offered.Any(p => Core.FileSystem.PathRules.Contains(p, destination)))
            return new DroppedFiles([], copying
                ? "a folder cannot be copied into itself"
                : "a folder cannot be moved into itself");

        // **PathRules.Same, not ==.** On Windows these compared case-sensitively,
        // so dropping a file back into the folder it already lives in was only
        // recognised when the destination happened to be spelled exactly the
        // way the path was - a breadcrumb reading one case and a listing
        // another was enough to let a pointless self-move go ahead. Same also
        // absorbs a trailing separator and both separator spellings, which is
        // why it is what the rest of the application compares paths with.
        var usable = copying
            ? offered
            : offered
                .Where(p => !Core.FileSystem.PathRules.Same(p, destination)
                            && !Core.FileSystem.PathRules.Same(
                                Path.GetDirectoryName(p), destination))
                .ToList();

        if (usable.Count > 0) return new DroppedFiles(usable, "");

        // Only a move arrives here empty-handed with paths in the drop:
        // copying keeps every path it is given, and the one refusal a copy has
        // is the containment one above. **This branch answered the copy case
        // too**, with the folder-into-itself wording, and it was the only place
        // that refusal was ever produced — which is why the containment check
        // could strip a path and still look like it had refused something.
        if (offered.Count > 0) return new DroppedFiles(usable, "that is already here");

        if (HasVirtualFiles(formats))
            return new DroppedFiles(usable,
                "those files are inside an archive and have no location on disk yet — "
                + "extract them first, or drag them from a program that unpacks as it copies");

        return new DroppedFiles(usable, formats.Count == 0
            ? "that drop carried nothing"
            : "there are no files in that");
    }

    /// <summary>
    /// **Files that exist only inside another program.** Windows offers these
    /// as a descriptor plus one stream per item rather than as paths, which is
    /// how Explorer presents the contents of a zip.
    ///
    /// Vaktari cannot take them, and that is a limit of what a drop handler is
    /// given rather than a decision: reading the contents needs the native data
    /// object, one stream at a time, by index, and Avalonia exposes formats and
    /// bytes but not the object. Recognising the case is what turns a drop that
    /// silently does nothing into one that says why.
    /// </summary>
    private static bool HasVirtualFiles(IReadOnlyList<string> formats) =>
        formats.Any(f =>
            f.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase)
            || f.Contains("FileContents", StringComparison.OrdinalIgnoreCase));
}
