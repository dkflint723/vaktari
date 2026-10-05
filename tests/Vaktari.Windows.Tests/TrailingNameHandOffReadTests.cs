using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **Reads that only an extension, or nothing at all, kept off the
/// neighbour** (0.11.1 path-safety check, left for 0.11.2):
///
/// - a row's reparse tag was read by its plain spelling, so "x " took "x"'s
///   tag — the listing, the search walk and the disk-usage walk each drew the
///   neighbour's link emblem, or lost the row's own;
/// - a .lnk was read by its plain spelling, so "…\links.\x.lnk" — an extension
///   that matches — opened as "…\links\x.lnk" and went where the neighbour
///   points.
///
/// (A third, a disk image handed to the Virtual Disk Service by its resolved
/// path, went with the Mount verb.)
///
/// **The folders end in a dot, not a space, and that is measured.** Win32
/// takes a trailing dot off every name in a plain path but a trailing space
/// off the last name only: "…\d \f.txt" read the file under "d ", while
/// "…\e.\f.txt" read the one under "e" (batch-0.11.2 notes, fold probe). A
/// folder ending in a space is refused all the same — the rule is the
/// hand-off rule, which does not guess what the receiver strips — but only
/// the dot reaches the neighbour, so only the dot can show the old reads
/// doing it.
///
/// A read reaches the row itself through ReachablePath.Exact. Every neighbour
/// here is made to answer differently from the row, so an answer that came
/// from it cannot pass. All in a temporary folder, the trailing names made
/// through "\\?\".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingNameHandOffReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-handoffread").FullName;

    public TrailingNameHandOffReadTests()
    {
        // Every walk in this assembly wants the platform's answer, and the
        // disk-usage walk asks through this hook — adopted as WindowsPlatform
        // does, and left adopted, as ReparsePointTests does.
        SafeWalk.ReparseTag = ReparseTags.Of;
    }

    public void Dispose()
    {
        try
        {
            // Each file by its true name, the links and tagged files as
            // themselves: File.Delete removes a reparse point, never what it
            // stands for.
            foreach (var f in Directory.GetFiles(Raw(_root), "*", SearchOption.AllDirectories))
                File.Delete(f);

            Directory.Delete(Raw(_root), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private static string Raw(string path) => @"\\?\" + path;

    // ---- the reparse tag -----------------------------------------------------

    /// <summary>
    /// "linked " is a link WSL made, beside an ordinary file "linked" under a
    /// tag that is no link; "tagged " is the reverse, beside a WSL link
    /// "tagged". Read by plain spelling, each row is the other kind.
    /// </summary>
    private string Tagged()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "tags")).FullName;

        Make(Path.Combine(folder, "linked "), link: true);
        Make(Path.Combine(folder, "linked"), link: false);
        Make(Path.Combine(folder, "tagged "), link: false);
        Make(Path.Combine(folder, "tagged"), link: true);

        return folder;

        static void Make(string path, bool link)
        {
            File.WriteAllText(Raw(path), "some content, so it has a size");

            if (link) ReparseFixture.SetWsl(Raw(path), directory: false);
            else ReparseFixture.SetThirdParty(Raw(path), directory: false);
        }
    }

    private static readonly Dictionary<string, bool> Links = new(StringComparer.Ordinal)
    {
        ["linked "] = true,
        ["linked"] = false,
        ["tagged "] = false,
        ["tagged"] = true,
    };

    [WindowsFact]
    public async Task The_listing_reads_each_rows_own_tag()
    {
        var folder = Tagged();
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);

        await foreach (var batch in new WindowsFileSystemProvider().EnumerateAsync(
                           folder, new ListingOptions { IncludeHidden = true }, CancellationToken.None))
            foreach (var entry in batch)
                seen[entry.Name] = entry.IsSymlink;

        Assert.Equal(Links, seen);
    }

    [WindowsFact]
    public async Task The_search_walk_reads_each_rows_own_tag()
    {
        var folder = Tagged();
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);

        var query = new SearchQuery { Text = "ed", ScopePath = folder, MaxResults = 100 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            seen[entry.Name] = entry.IsSymlink;

        Assert.Equal(Links, seen);
    }

    [WindowsFact]
    public void The_disk_usage_walk_reads_each_rows_own_tag()
    {
        var folder = Tagged();

        var rows = SpaceUsage.Underneath(folder, progress: null, CancellationToken.None).Rows
            .ToDictionary(r => Path.GetFileName(r.Path), r => r.IsLink, StringComparer.Ordinal);

        Assert.Equal(Links, rows);
    }

    // ---- the .lnk ------------------------------------------------------------

    /// <summary>"links\x.lnk" pointing at one folder, and "links.\x.lnk"
    /// pointing at another.</summary>
    [WindowsFact]
    public void A_shortcut_under_a_folded_folder_is_read_from_itself()
    {
        var theirs = Directory.CreateDirectory(Path.Combine(_root, "their-target")).FullName;
        var own = Directory.CreateDirectory(Path.Combine(_root, "own-target")).FullName;

        var shortcuts = new WindowsShortcuts();
        var neighbours = Directory.CreateDirectory(Path.Combine(_root, "links")).FullName;
        File.Move(shortcuts.CreateShortcut(theirs, neighbours), Path.Combine(neighbours, "x.lnk"));

        var made = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
        Directory.CreateDirectory(Raw(Path.Combine(_root, "links.")));
        File.Move(Raw(shortcuts.CreateShortcut(own, made)), Raw(Path.Combine(_root, "links.", "x.lnk")));

        Assert.Equal(theirs, shortcuts.TargetOf(Path.Combine(neighbours, "x.lnk")), ignoreCase: true);
        Assert.Equal(own, shortcuts.TargetOf(Path.Combine(_root, "links.", "x.lnk")), ignoreCase: true);
    }

    // ---- a path with no spelling that reaches it (batch-0.11.2 QA) ----------

    /// <summary>
    /// **"\\.\" folds like a plain path, and "\\?\" cannot be put in front of
    /// it**, so ReachablePath.Exact has no spelling to offer and answers null.
    /// The tag and the shortcut are then not read at all: read by the path as
    /// handed over, "\\.\…\tagged " opens "tagged" and "\\.\…\links.\x.lnk"
    /// opens "links\x.lnk" — the neighbour's link and the neighbour's target,
    /// which is the fault this batch fixed for plain paths. The same "\\.\"
    /// spelling of a name that folds nothing is read as before, so the null is
    /// the fold's and not the prefix's.
    /// </summary>
    [WindowsFact]
    public void A_device_spelled_path_that_folds_is_not_read_as_its_neighbour()
    {
        var folder = Tagged();

        Assert.Null(ReparseTags.Of(@"\\.\" + Path.Combine(folder, "tagged ")));
        Assert.NotNull(ReparseTags.Of(@"\\.\" + Path.Combine(folder, "tagged")));

        var theirs = Directory.CreateDirectory(Path.Combine(_root, "their-target")).FullName;
        var own = Directory.CreateDirectory(Path.Combine(_root, "own-target")).FullName;

        var shortcuts = new WindowsShortcuts();
        var neighbours = Directory.CreateDirectory(Path.Combine(_root, "links")).FullName;
        File.Move(shortcuts.CreateShortcut(theirs, neighbours), Path.Combine(neighbours, "x.lnk"));

        var made = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
        Directory.CreateDirectory(Raw(Path.Combine(_root, "links.")));
        File.Move(Raw(shortcuts.CreateShortcut(own, made)), Raw(Path.Combine(_root, "links.", "x.lnk")));

        Assert.Null(shortcuts.TargetOf(@"\\.\" + Path.Combine(_root, "links.", "x.lnk")));
        Assert.Equal(theirs, shortcuts.TargetOf(@"\\.\" + Path.Combine(neighbours, "x.lnk")), ignoreCase: true);
    }
}
