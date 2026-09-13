using System.Text.Json;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.Settings;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The records a search left behind in the per-folder view store.
///
/// **Every search anybody ran left a record that nothing could read again.**
/// The pane remembered a search's view under the search's own path, and that
/// path carries the query, the folder, the scope and the case — so each
/// distinct search became a key of its own, while the one key all searches were
/// meant to share, <see cref="VirtualPaths.SearchViewKey"/>, was declared and
/// never used. The pane now keeps one view for every search, which leaves the
/// records already written unreachable for good; they are dropped when the
/// store is read, and the file loses them at its next write. The settings
/// dialog counted each of them as a folder remembered.
/// </summary>
public sealed class SearchViewRecordsPrunedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-search-views-" + Guid.NewGuid().ToString("N")[..8]);

    public SearchViewRecordsPrunedTests() => Directory.CreateDirectory(_root);

    /// <summary>Only the folder this class made.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string File_ => Path.Combine(_root, "folder-views.json");

    private JsonFolderViewStore Store() => new(_root);

    private static FolderViewState Grid => new() { View = ViewMode.Grid };

    /// <summary>A store file as an earlier version left it.</summary>
    private void Seed(Dictionary<string, FolderViewState> folders)
    {
        using var stream = File.Create(File_);

        JsonSerializer.Serialize(
            stream, new FolderViewFile { Folders = folders }, FolderViewJsonContext.Default.FolderViewFile);
    }

    /// <summary>
    /// **A record kept for one search is dropped when the store is read.** The
    /// shared search record and every folder's own record are kept.
    /// </summary>
    [Fact]
    public void A_record_kept_for_one_search_is_dropped_when_the_store_is_read()
    {
        var folder = Path.GetTempPath();
        var one = VirtualPaths.Search("alpha", folder, scoped: true);
        var other = VirtualPaths.Search("beta", folder, scoped: false, matchCase: true);
        var ordinary = Path.Combine(_root, "somewhere");

        Seed(new(StringComparer.Ordinal)
        {
            [ordinary] = Grid,
            [one] = Grid,
            [other] = Grid,
            [VirtualPaths.SearchViewKey] = Grid,
        });

        var store = Store();

        Assert.Equal(2, store.Remembered);

        Assert.NotNull(store.Read(ordinary));
        Assert.NotNull(store.Read(VirtualPaths.SearchViewKey));

        Assert.Null(store.Read(one));
        Assert.Null(store.Read(other));
    }

    /// <summary>
    /// And the file itself loses them at the next write, rather than carrying
    /// them for ever and dropping them again on every launch — a store with
    /// nothing changed does not write at all, so dropping has to count as a
    /// change.
    /// </summary>
    [Fact]
    public void The_dropped_search_records_leave_the_file_at_the_next_write()
    {
        var one = VirtualPaths.Search("alpha", Path.GetTempPath(), scoped: true);

        Seed(new(StringComparer.Ordinal)
        {
            [Path.Combine(_root, "somewhere")] = Grid,
            [one] = Grid,
        });

        // The key as the file holds it, raw. Escaping it first compared against
        // text the file never contains, and the assertion passed before the
        // change as well as after — measured, which is how it was caught.
        Assert.Contains(one, File.ReadAllText(File_));

        Store().Flush();

        var written = File.ReadAllText(File_);

        Assert.DoesNotContain(one, written);
        Assert.Contains("somewhere", written);
    }
}
