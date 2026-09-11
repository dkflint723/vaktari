using System.Collections.Concurrent;

namespace Vaktari.Core.FileSystem;

/// <summary>Why a marked row is not copied across.</summary>
public enum WithheldBecause
{
    /// <summary>The other side is inside it, and a folder cannot be copied
    /// into itself.</summary>
    HoldsTheOtherSide,

    /// <summary>Its name ends in a space or a dot, which Windows cannot open
    /// by name. See <see cref="ReachablePath"/>.</summary>
    NameWindowsCannotOpen,
}

/// <summary>A marked row copying across leaves out, and why.</summary>
public readonly record struct Withheld(string Path, WithheldBecause Because);

/// <summary>
/// What copying the newer and missing files from one side of a comparison to
/// the other is going to do, worked out from that side's marks before anything
/// moves, so the prompt can say it and the copy can hold to it.
///
/// **Only what the other side lacks, and what is newer here.** A row marked
/// older stays where it is, because copying it would put an older file over a
/// newer one; a row marked different stays too, because nothing about a file
/// against a folder, or two files changed at the same moment, says which of
/// the two should win.
/// </summary>
public sealed class CopyAcrossPlan
{
    private readonly ConcurrentQueue<string> _leftAlone = new();

    public CopyAcrossPlan(
        string destination,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> replacing,
        IReadOnlyList<Withheld>? withheld = null)
    {
        Destination = destination;
        Missing = missing;
        Replacing = replacing;
        Withheld = withheld ?? [];
    }

    /// <summary>The folder the prompt named. The copy goes there, whatever the
    /// other side shows by the time the answer comes.</summary>
    public string Destination { get; }

    /// <summary>What the other side lacks.</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>What is newer here, each over the older file of its name
    /// there.</summary>
    public IReadOnlyList<string> Replacing { get; }

    /// <summary>Marked rows left out, and why.</summary>
    public IReadOnlyList<Withheld> Withheld { get; }

    /// <summary>Every path to copy: what the other side lacks, then what is
    /// newer here.</summary>
    public IReadOnlyList<string> Sources => [.. Missing, .. Replacing];

    public int Count => Missing.Count + Replacing.Count;

    /// <summary>
    /// What the copy found changed after the prompt and so left alone, in the
    /// order it met them. Written from the copy's own thread; read once the
    /// copy is done.
    /// </summary>
    public IReadOnlyCollection<string> LeftAlone => _leftAlone;

    /// <summary>The plan for one side's marks, copying into
    /// <paramref name="destination"/>.</summary>
    public static CopyAcrossPlan From(IReadOnlyDictionary<string, CompareMark> marks, string destination)
    {
        var missing = new List<string>();
        var replacing = new List<string>();
        var withheld = new List<Withheld>();

        foreach (var (path, mark) in marks.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (mark is not (CompareMark.OnlyHere or CompareMark.NewerHere)) continue;

            // **A folder the other side is inside cannot be sent across.** One
            // side showing a folder within the other marks that folder "only
            // here", and both engines refuse the whole copy over a folder sent
            // into itself.
            if (PathRules.Contains(path, destination))
                withheld.Add(new Withheld(path, WithheldBecause.HoldsTheOtherSide));

            // **Nor a name Windows cannot open**, over which the engine refuses
            // the whole copy as well. The rest can still go.
            else if (!ReachablePath.IsReachable(path))
                withheld.Add(new Withheld(path, WithheldBecause.NameWindowsCannotOpen));

            else if (mark == CompareMark.OnlyHere) missing.Add(path);

            else replacing.Add(path);
        }

        return new CopyAcrossPlan(destination, missing, replacing, withheld);
    }

    /// <summary>
    /// What to do about something already there when the copy reaches it.
    ///
    /// **Replaced only if the prompt named it, and only if it is still older.**
    /// The listing the plan came from is seconds or minutes old by the time the
    /// copy gets there: a file saved on the other side in the meantime is the
    /// newer one now, and a name that has turned up there since was never
    /// offered for replacing at all. Both are left alone, and kept in
    /// <see cref="LeftAlone"/> so the copy can say so when it is done.
    /// </summary>
    public ConflictResolution Decide(FileConflict conflict)
    {
        // Exactly the string the plan handed over: the engine gives back the
        // path it was given, made full, which a listing's path already is. A
        // spelling it did not give is not a file the prompt named.
        if (!Replacing.Contains(conflict.Source, StringComparer.Ordinal)) return LeaveAlone(conflict);

        var from = new FileInfo(conflict.Source);
        var to = new FileInfo(conflict.Target);

        // A folder has no length to compare, and FileInfo says it does not
        // exist rather than throwing at the question.
        if (!from.Exists || !to.Exists) return LeaveAlone(conflict);

        var judged = FileSameness.Judge(from.Length, from.LastWriteTimeUtc, to.Length, to.LastWriteTimeUtc);

        return judged == Sameness.FirstNewer ? ConflictResolution.Overwrite : LeaveAlone(conflict);
    }

    private ConflictResolution LeaveAlone(FileConflict conflict)
    {
        _leftAlone.Enqueue(conflict.Source);

        return ConflictResolution.Skip;
    }
}
