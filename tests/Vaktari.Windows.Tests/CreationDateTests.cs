using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The creation date a row carries, on all three paths a row can arrive by.
///
/// **Nothing in the window could say when a file was made**, because the entry
/// the listing is built from did not carry it — <see cref="FileEntry"/> had a
/// modified time and no created time, so the Created column was not a column
/// somebody had left switched off, it was a column with no data behind it.
///
/// Three paths, for the reason <see cref="ShortcutLinkFlagTests"/> gives about
/// the link flag: the listing's enumeration, the watcher's single-entry lookup
/// and the search walk each build entries of their own, and a fact taught to
/// one of them and not the others is a file that has a creation date until you
/// rename it, or one that has none until you search for it.
///
/// Real files rather than fakes, for the reason <see cref="TempTree"/> gives:
/// the whole question is what Windows puts in a directory entry.
/// </summary>
[SupportedOSPlatform("windows")]
public class CreationDateTests
{
    private static readonly WindowsFileSystemProvider Listing = new();

    /// <summary>Deliberately not "now": a creation time this far in the past
    /// cannot be confused with the time the test wrote the file, and it is
    /// nowhere near the modified time set beside it.</summary>
    private static readonly DateTime Made =
        new(2019, 5, 4, 3, 2, 1, DateTimeKind.Utc);

    private static readonly DateTime Touched =
        new(2024, 11, 12, 13, 14, 15, DateTimeKind.Utc);

    private static string Dated(TempTree tree, string relative)
    {
        var path = tree.Write(relative);

        File.SetCreationTimeUtc(path, Made);
        File.SetLastWriteTimeUtc(path, Touched);

        return path;
    }

    private static async Task<FileEntry> Enumerated(string folder, string path)
    {
        await foreach (var batch in Listing.EnumerateAsync(
                           folder, new ListingOptions { IncludeHidden = true }, CancellationToken.None))
            foreach (var entry in batch)
                if (PathRules.Same(entry.FullPath, path))
                    return entry;

        throw new InvalidOperationException($"{path} was not enumerated");
    }

    [WindowsFact]
    public async Task A_listed_file_carries_the_date_it_was_made()
    {
        using var tree = new TempTree();

        var folder = tree.Dir("papers");
        var file = Dated(tree, "papers/report.txt");

        var entry = await Enumerated(folder, file);

        Assert.Equal(Made, entry.CreationTime.UtcDateTime);

        // And it is not the modified time wearing a different name, which is
        // the one way this column can be wrong and still look right.
        Assert.Equal(Touched, entry.LastWriteTime.UtcDateTime);
    }

    /// <summary>A folder has one too, and the lookup below reads it through a
    /// FileInfo whose Exists is false for a directory — so it is worth asking
    /// rather than assuming.</summary>
    [WindowsFact]
    public async Task So_does_a_folder()
    {
        using var tree = new TempTree();

        var folder = tree.Dir("papers");
        var inner = tree.Dir("papers/drafts");

        Directory.SetCreationTimeUtc(inner, Made);

        Assert.Equal(Made, (await Enumerated(folder, inner)).CreationTime.UtcDateTime);

        var described = await Listing.GetEntryAsync(inner, CancellationToken.None);

        Assert.Equal(Made, described!.Value.CreationTime.UtcDateTime);
    }

    /// <summary>
    /// The watcher's path. A file that arrives by being created or renamed
    /// while the folder is open is described one entry at a time, and a row
    /// that lost its creation date on the way past the watcher would blank its
    /// own cell the moment you touched it.
    /// </summary>
    [WindowsFact]
    public async Task A_watched_file_carries_it_too()
    {
        using var tree = new TempTree();

        var file = Dated(tree, "papers/report.txt");

        var entry = await Listing.GetEntryAsync(file, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(Made, entry!.Value.CreationTime.UtcDateTime);
    }

    /// <summary>
    /// The search walk's path. Results draw in the same details columns as any
    /// other listing.
    /// </summary>
    [WindowsFact]
    public async Task A_search_result_carries_it_too()
    {
        using var tree = new TempTree();

        tree.Dir("papers");
        Dated(tree, "papers/quarterly-report.txt");

        var query = new SearchQuery
        {
            Text = "quarterly-report",
            ScopePath = tree.Root,
            MaxResults = 100,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<FileEntry>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry);

        Assert.Equal(Made, Assert.Single(found).CreationTime.UtcDateTime);
    }
}
