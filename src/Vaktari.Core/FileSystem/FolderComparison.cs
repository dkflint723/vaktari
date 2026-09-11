namespace Vaktari.Core.FileSystem;

/// <summary>How a row compares with the row of the same name on the other side.</summary>
public enum CompareMark
{
    /// <summary>Nothing of that name on the other side.</summary>
    OnlyHere,

    /// <summary>Both sides have it, and this one was changed later.</summary>
    NewerHere,

    /// <summary>Both sides have it, and the other one was changed later.</summary>
    OlderHere,

    /// <summary>
    /// Both sides have the name, but not the same thing: a file against a
    /// folder, or two files changed at the same moment at different sizes.
    /// </summary>
    Differs,
}

/// <summary>
/// Two folders' listings compared name by name, one level deep: each side's
/// marks, keyed by the full path that side lists.
///
/// A row with no mark looks the same on both sides — or is a folder on both,
/// whose insides one level cannot see. Names match the way the platform
/// matches them, so "Report.txt" and "report.txt" are one name on Windows and
/// two on Linux, as they are to everything else there.
/// </summary>
public sealed record FolderComparison(
    IReadOnlyDictionary<string, CompareMark> Left,
    IReadOnlyDictionary<string, CompareMark> Right)
{
    /// <summary>Nothing compared, and nothing marked.</summary>
    public static FolderComparison None { get; } = new(
        new Dictionary<string, CompareMark>(StringComparer.Ordinal),
        new Dictionary<string, CompareMark>(StringComparer.Ordinal));

    public static FolderComparison Between(IEnumerable<FileEntry> left, IEnumerable<FileEntry> right)
    {
        var leftByName = ByName(left);
        var rightByName = ByName(right);

        var leftMarks = new Dictionary<string, CompareMark>(StringComparer.Ordinal);
        var rightMarks = new Dictionary<string, CompareMark>(StringComparer.Ordinal);

        foreach (var (name, here) in leftByName)
        {
            if (!rightByName.TryGetValue(name, out var there))
            {
                leftMarks[here.FullPath] = CompareMark.OnlyHere;
                continue;
            }

            var (mine, theirs) = Judge(here, there);

            if (mine is { } m) leftMarks[here.FullPath] = m;
            if (theirs is { } t) rightMarks[there.FullPath] = t;
        }

        foreach (var (name, there) in rightByName)
            if (!leftByName.ContainsKey(name))
                rightMarks[there.FullPath] = CompareMark.OnlyHere;

        return new FolderComparison(leftMarks, rightMarks);
    }

    /// <summary>Entries by name, the platform's way. A folder cannot list one
    /// name twice under the platform's own rule, so the first wins only in a
    /// listing that did.</summary>
    private static Dictionary<string, FileEntry> ByName(IEnumerable<FileEntry> entries)
    {
        var byName = new Dictionary<string, FileEntry>(PathRules.Comparer);

        foreach (var entry in entries) byName.TryAdd(entry.Name, entry);

        return byName;
    }

    private static (CompareMark? Here, CompareMark? There) Judge(FileEntry here, FileEntry there)
    {
        if (here.IsDirectory != there.IsDirectory) return (CompareMark.Differs, CompareMark.Differs);

        if (here.IsDirectory) return (null, null);

        return FileSameness.Judge(here.Length, here.LastWriteTime, there.Length, there.LastWriteTime) switch
        {
            Sameness.Same => (null, null),
            Sameness.SameTime => (CompareMark.Differs, CompareMark.Differs),
            Sameness.FirstNewer => (CompareMark.NewerHere, CompareMark.OlderHere),
            _ => (CompareMark.OlderHere, CompareMark.NewerHere),
        };
    }
}
