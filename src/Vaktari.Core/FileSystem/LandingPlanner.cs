namespace Vaktari.Core.FileSystem;

/// <summary>
/// Decides the name every entry of one archive lands under, in archive order.
///
/// **One naming truth.** The same planner will decide the names a listing of
/// the archive shows, so what is listed and what lands on disk cannot
/// disagree — the disagreement a reviewer found in the draft, where a listing
/// showed the last of two same-named entries and extraction kept the first.
///
/// **Every copy is kept; the first keeps its name.** An archive is free to
/// hold two entries called <c>notes.txt</c>. Measured before this existed: the
/// second was written over the first and the count said two files had
/// arrived. The maintainer's decision is that nothing an archive holds is lost
/// silently, so the second lands as <c>notes (2).txt</c>, in the house style
/// <see cref="NewItemName.Free"/> uses.
///
/// **A folder is identified by its raw prefix, not by its landed name.**
/// <c>Docs/</c> and <c>docs/</c> are two folders to the archive; under
/// Windows rules they would land on one folder and merge two trees into one,
/// so the second is numbered. The same folder named twice (<c>a/</c>, then
/// <c>a/b.txt</c>) is the same raw prefix and is reused. A folder whose name
/// an earlier FILE already took is numbered too — there is no other way both
/// can exist.
/// </summary>
internal sealed class LandingPlanner(bool windowsRules)
{
    /// <summary>One landed name, and where it sits.</summary>
    internal sealed class Node
    {
        public required string Name { get; set; }
        public required bool IsFolder { get; init; }
        public Node? Parent { get; init; }

        /// <summary>Counted in <see cref="Renamed"/> already.</summary>
        public bool Counted { get; set; }

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    private readonly Node _root = new() { Name = "", IsFolder = true };

    /// <summary>Raw folder prefix (segments joined with <c>/</c>) to the
    /// node it landed as. Ordinal: the archive's own distinction.</summary>
    private readonly Dictionary<string, Node> _folders = new(StringComparer.Ordinal);

    /// <summary>How many landed names differ from what the archive said,
    /// whether by replaced characters or by a number.</summary>
    public int Renamed { get; private set; }

    public bool WindowsRules => windowsRules;

    /// <summary>The landing path of a folder, or null when a segment is not
    /// a name at all.</summary>
    public string[]? PlaceFolder(string[] segments) => FolderNode(segments) is { } node ? Segments(node) : null;

    /// <summary>The landing path of a file.</summary>
    public string[]? PlaceFile(string[] segments) => FileNode(segments) is { } node ? Segments(node) : null;

    internal Node? FolderNode(ReadOnlySpan<string> segments)
    {
        var node = _root;
        var prefix = "";

        for (var i = 0; i < segments.Length; i++)
        {
            prefix = i == 0 ? segments[0] : prefix + "/" + segments[i];

            if (_folders.TryGetValue(prefix, out var known))
            {
                node = known;
                continue;
            }

            if (ArchiveNames.Land(segments[i], windowsRules) is not { } landed) return null;

            node = Claim(node, landed.Name, isFolder: true, landed.Changed);
            _folders[prefix] = node;
        }

        return node;
    }

    internal Node? FileNode(ReadOnlySpan<string> segments)
    {
        if (segments.Length == 0) return null;

        if (FolderNode(segments[..^1]) is not { } parent) return null;

        if (ArchiveNames.Land(segments[^1], windowsRules) is not { } landed) return null;

        return Claim(parent, landed.Name, isFolder: false, landed.Changed);
    }

    /// <summary>
    /// Gives a node the next free number, because the disk held something at
    /// its name that the plan could not know about — an 8.3 alias, a folder
    /// that is case-sensitive, a name taken between planning and writing.
    /// Descendants follow, because they hang off the node.
    /// </summary>
    internal string Renumber(Node node, Func<string, bool> takenOnDisk)
    {
        var parent = node.Parent ?? throw new InvalidOperationException("the root is not renumbered");
        var original = node.Name;

        parent.Children.Remove(Key(original));

        string candidate;
        var n = 1;

        do candidate = ArchiveNames.Numbered(original, ++n, node.IsFolder);
        while (parent.Children.ContainsKey(Key(candidate)) || takenOnDisk(candidate));

        node.Name = candidate;
        parent.Children[Key(candidate)] = node;

        if (!node.Counted)
        {
            node.Counted = true;
            Renamed++;
        }

        return candidate;
    }

    internal static string[] Segments(Node node)
    {
        var stack = new List<string>();

        for (var at = node; at.Parent is not null; at = at.Parent) stack.Add(at.Name);

        stack.Reverse();

        return [.. stack];
    }

    private Node Claim(Node parent, string name, bool isFolder, bool changed)
    {
        var candidate = name;
        var n = 1;

        while (parent.Children.ContainsKey(Key(candidate)))
            candidate = ArchiveNames.Numbered(name, ++n, isFolder);

        var node = new Node { Name = candidate, IsFolder = isFolder, Parent = parent };

        parent.Children[Key(candidate)] = node;

        if (changed || n > 1)
        {
            node.Counted = true;
            Renamed++;
        }

        return node;
    }

    private string Key(string name) => ArchiveNames.CollisionKey(name, windowsRules);
}
