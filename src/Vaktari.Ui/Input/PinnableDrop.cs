using Avalonia.Input;

namespace Vaktari.Ui.Input;

/// <summary>
/// What a drop onto the sidebar's own ground — the blank strip under the
/// sections, the gaps between them, a section heading — can become. Its rows
/// are not this: each of those keeps whatever it already answered.
///
/// **A place is a folder and nothing else.** The provider stores a path and the
/// row opens it, so pinning a file would put a row in the sidebar that cannot
/// be opened; and pinning the folder a file happens to live in is a different
/// thing from what was dragged, which is the kind of helpfulness nobody asked
/// for and nobody can undo without noticing it first. So the files in a drop
/// are counted and left alone, and a drop that is only files is refused before
/// the button is released rather than swallowed after it.
/// </summary>
/// <param name="Folders">The folders among the dropped paths, in the order the
/// drop carried them.</param>
/// <param name="Files">How many of the dropped paths were not folders.</param>
public readonly record struct PinPlan(IReadOnlyList<string> Folders, int Files)
{
    /// <summary>Whether there is anything here to pin.</summary>
    public bool Any => Folders.Count > 0;

    /// <summary>
    /// What the cursor says over the panel.
    ///
    /// Link rather than Copy or Move, because a place is a pointer at a folder:
    /// nothing is duplicated and nothing leaves where it was, and a cursor that
    /// promised either would be describing a different gesture.
    ///
    /// None when there is nothing to pin, so a drag carrying only files is
    /// turned away while it can still be steered somewhere else — the toolkit
    /// delivers a drop only where the drag-over said yes, which is what makes
    /// this the refusal rather than a message afterwards.
    /// </summary>
    public DragDropEffects Effect => Any ? DragDropEffects.Link : DragDropEffects.None;

    /// <summary>
    /// What to say afterwards. Said even when the answer is "nothing", because
    /// the whole finding was a drop that did nothing and explained nothing.
    ///
    /// **<paramref name="already"/> is the number this cannot work out for
    /// itself, and leaving it out made the line a lie.** Dropping a folder the
    /// panel ALREADY shows — Downloads, or a drive root — reported "pinned 1
    /// folder(s) to places" and changed nothing: measured in a real window,
    /// the rows before and after were identical, because the provider drops a
    /// pin whose path is already a built-in place from the list it renders
    /// while still writing it to places.json. That is the likeliest drop of
    /// all, and it claimed success for a row that does not exist and that no
    /// "Remove from places" can ever reach.
    ///
    /// So the count of folders that were already there is passed in from the
    /// caller — which is the only thing that can see the rendered panel — and
    /// said out loud rather than counted as pinned.
    /// </summary>
    /// <param name="already">How many of <see cref="Folders"/> the sidebar was
    /// already showing, and so were not pinned.</param>
    public string Report(int already)
    {
        var pinned = Folders.Count - already;

        // Nothing about the drop could be a place at all, which the head says
        // on its own — a "1 file(s) cannot be a place" after it would be the
        // same sentence twice.
        if (pinned == 0 && already == 0) return "only a folder can be a place";

        var clauses = new List<string>
        {
            pinned > 0
                ? $"pinned {pinned} folder(s) to places"
                : $"{already} folder(s) already in places",
        };

        if (pinned > 0 && already > 0) clauses.Add($"{already} already there");
        if (Files > 0) clauses.Add($"{Files} file(s) cannot be a place");

        return string.Join(" — ", clauses);
    }
}

/// <summary>
/// Sorts a drop's paths into the folders that can be pinned and the files that
/// cannot.
/// </summary>
public static class PinnableDrop
{
    /// <summary>
    /// Reads the disk, because a path is not self-describing: a drop carries
    /// names, and only the filesystem knows which of them are directories.
    /// A path that has gone away by the time this runs counts as a file, which
    /// is the safe way round — it is not pinned.
    /// </summary>
    public static PinPlan For(IReadOnlyList<string> dropped)
    {
        var folders = new List<string>();
        var files = 0;

        foreach (var path in dropped)
        {
            if (Directory.Exists(path)) folders.Add(path);
            else files++;
        }

        return new PinPlan(folders, files);
    }
}
