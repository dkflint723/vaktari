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

    /// <summary>Its name ends in a space or a dot and this side reaches it
    /// through "\\?\", but the other side is opened by its ordinary name and
    /// cannot hold it. See <see cref="ReachablePath.RefuseLanding"/>.</summary>
    NameTheOtherSideCannotTake,
}

/// <summary>
/// A marked row copying across leaves out, and why. <paramref name="Inside"/>
/// is set when the row is a folder left out over a name further down: that
/// item's path below the folder, as listed. Null when the row's own name is
/// the reason.
/// </summary>
public readonly record struct Withheld(string Path, WithheldBecause Because, string? Inside = null);

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

    /// <summary>
    /// The plan for one side's marks, copying into
    /// <paramref name="destination"/>.
    ///
    /// Reads the disk only for a marked folder that <see cref="MustLookInside"/>
    /// says could hold a name the engine refuses; nothing at all on Linux, or
    /// between two sides both opened through "\\?\". Throws
    /// <see cref="OperationCanceledException"/> when
    /// <paramref name="cancel"/> is cancelled during such a walk.
    /// </summary>
    public static CopyAcrossPlan From(
        IReadOnlyDictionary<string, CompareMark> marks, string destination, CancellationToken cancel = default)
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

            // **Nor one this side reaches and the other cannot hold.** A side
            // opened through "\\?\" lists "report " as itself, so the rule
            // above lets it through — and the engine then refuses the whole
            // copy into a plainly spelled side, where it would land as
            // "report" (0.11.1 path-safety check). Asked as the engine asks
            // it: the destination joined with the name.
            else if (ReachablePath.RefuseLanding(Path.Combine(destination, PathRules.LeafName(path))) is not null)
                withheld.Add(new Withheld(path, WithheldBecause.NameTheOtherSideCannotTake));

            // **Nor a folder holding such a name further down.** The engine
            // asks both rules of every item it plans, a folder's contents
            // included, and refuses the WHOLE copy over the first that fails:
            // "report " inside a marked folder of a "\\?\" side kept the plain
            // file beside that folder from going too, and the person heard
            // only afterwards (batch-0.11.2 QA, probe-copy-across-nested).
            // Asked here as the engine asks it, so only that folder stays.
            else if (MustLookInside(path, destination) && LookInside(path, destination, cancel) is { } below)
                withheld.Add(below);

            else if (mark == CompareMark.OnlyHere) missing.Add(path);

            else replacing.Add(path);
        }

        return new CopyAcrossPlan(destination, missing, replacing, withheld);
    }

    /// <summary>
    /// Whether a folder at <paramref name="source"/> could hold a name the
    /// engine refuses to copy into <paramref name="destination"/> — and so has
    /// to be walked before it is offered. Answered from the two spellings
    /// alone, without the disk.
    ///
    /// **Never on Linux**, where neither rule refuses anything. **Never between
    /// two literal "\\?\" (or "\??\") spellings**: a name below such a source
    /// is read as itself, and lands as itself in such a destination. Any other
    /// pair can: a plainly spelled source reads "report " as "report", and a
    /// plainly spelled destination writes it so.
    /// </summary>
    public static bool MustLookInside(string source, string destination)
        => OperatingSystem.IsWindows() && !(Literal(source) && Literal(destination));

    private static bool Literal(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>
    /// The first item down a marked folder that the engine would refuse, as
    /// the row to leave out — or null when there is none, or the row is not a
    /// folder.
    ///
    /// **Walked as the engine walks it** (WindowsFileOperations.Descend): the
    /// row resolved as the engine resolves a root, each child as the listing
    /// names it joined to its folder, a link planned as itself and never
    /// followed, and each item asked <see cref="ReachablePath.Refuse"/> of
    /// where it is read and <see cref="ReachablePath.RefuseLanding"/> of where
    /// it will be written — the destination, the folder's own name, and the
    /// path below it. A folder that cannot be read is passed over: the engine
    /// does not refuse over one, it says which it could not read.
    ///
    /// Stops at the first such name, so a folder full of them costs no more
    /// than the walk to the first.
    /// </summary>
    private static Withheld? LookInside(string path, string destination, CancellationToken cancel)
    {
        var root = Path.GetFullPath(path);
        var top = new DirectoryInfo(root);

        // A file is planned as itself, and so is a link to a folder.
        if (!top.Exists || (top.Attributes & FileAttributes.ReparsePoint) != 0) return null;

        var target = Path.Combine(destination, PathRules.LeafName(root));
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();

            var folder = pending.Pop();

            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    var below = entry.FullName[root.Length..].TrimStart('\\', '/');

                    if (ReachablePath.Refuse(entry.FullName) is not null)
                        return new Withheld(path, WithheldBecause.NameWindowsCannotOpen, below);

                    if (ReachablePath.RefuseLanding(Path.Combine(target, below)) is not null)
                        return new Withheld(path, WithheldBecause.NameTheOtherSideCannotTake, below);

                    if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                        pending.Push(entry.FullName);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Read by the engine again, which says what it could not.
            }
        }

        return null;
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
