using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **What a row shows about itself is read from itself.** A plain "…\report "
/// is opened as "…\report", so every fact read by that spelling was the
/// neighbour's (seventh review round, the hunt): the watcher gave the row the
/// neighbour's length and its Hidden flag, which hid the row outright; the
/// properties window showed the neighbour's size and attributes and measured
/// the neighbour folder; a folder row counted the neighbour's items; and the
/// search walked a folder "sub " as "sub", returning sub's files under the
/// wrong name and then again under their own.
///
/// Each is read through the spelling that reaches the entry — "\\?\" where the
/// plain one folds — and the neighbour here is always made bigger, hidden,
/// read-only or fuller than the row, so an answer that came from it cannot pass
/// for the row's own. All in a temporary folder, the trailing names made
/// through "\\?\".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingNameReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-readfold").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }

            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private const string Own = "12345";
    private const string Theirs = "a much longer neighbour, forty bytes long";

    /// <summary>"name" as the row, beside a hidden, read-only neighbour that is
    /// eight times its size.</summary>
    private string Beside(string name)
    {
        var neighbour = Path.Combine(_root, name.TrimEnd(' ', '.'));
        File.WriteAllText(neighbour, Theirs);
        File.SetAttributes(neighbour, FileAttributes.Hidden | FileAttributes.ReadOnly);

        var row = Path.Combine(_root, name);
        File.WriteAllText(@"\\?\" + row, Own);
        return row;
    }

    /// <summary>A folder "album " holding one file, beside an "album" holding three.</summary>
    private string AlbumBeside()
    {
        var neighbour = Directory.CreateDirectory(Path.Combine(_root, "album")).FullName;
        for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(neighbour, $"n{i}.bin"), new string('n', 1000));

        var row = Path.Combine(_root, "album ");
        Directory.CreateDirectory(@"\\?\" + row);
        File.WriteAllText(@"\\?\" + Path.Combine(row, "own.txt"), "abc");
        return row;
    }

    // ---- the watcher's stat ----------------------------------------------------

    [WindowsTheory]
    [InlineData("report ")]
    [InlineData("report.")]
    public async Task The_watcher_reads_a_folded_row_as_itself(string name)
    {
        var row = Beside(name);

        var entry = await new WindowsFileSystemProvider().GetEntryAsync(row, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(name, entry.Value.Name);
        Assert.Equal(row, entry.Value.FullPath);
        Assert.Equal(Own.Length, entry.Value.Length);
        Assert.False(entry.Value.Flags.HasFlag(EntryFlags.Hidden), "the neighbour's Hidden flag would hide the row");
    }

    /// <summary>A row whose own file has gone is gone, whatever stands beside it.</summary>
    [WindowsFact]
    public async Task A_folded_row_that_has_gone_is_not_answered_for_by_its_neighbour()
    {
        var row = Beside("report ");
        File.Delete(@"\\?\" + row);

        Assert.Null(await new WindowsFileSystemProvider().GetEntryAsync(row, CancellationToken.None));
    }

    // ---- the properties window ------------------------------------------------------

    [WindowsFact]
    public async Task The_properties_window_reads_a_folded_row_as_itself()
    {
        var row = Beside("report ");
        var provider = new WindowsPropertiesProvider();

        var details = await provider.GetAsync(row, CancellationToken.None);

        Assert.Equal("report ", details.Name);
        Assert.Equal(Own.Length, details.Size);

        var attributes = Assert.Single(details.Groups).Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("no", attributes["Hidden"]);
        Assert.Equal("no", attributes["Read-only"]);

        var shared = Assert.Single(await provider.GetSharedAsync([row], CancellationToken.None))
            .Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("no", shared["Hidden"]);
    }

    [WindowsFact]
    public async Task Measuring_a_folded_folder_measures_it_and_not_its_neighbour()
    {
        var row = AlbumBeside();

        var size = await new WindowsPropertiesProvider()
            .MeasureAsync(row, new Progress<SizeProgress>(), CancellationToken.None);

        Assert.Equal(3, size.Bytes);
        Assert.Equal(1, size.Files);
    }

    // ---- a row's inline fact ------------------------------------------------------------

    [WindowsFact]
    public async Task A_folded_row_counts_and_describes_itself()
    {
        var album = AlbumBeside();
        var report = Beside("report ");
        var metadata = new WindowsMetadataProvider();

        Assert.Equal("1 item", await metadata.DescribeAsync(album, isDirectory: true, CancellationToken.None));
        Assert.Null(await metadata.DescribeAccessAsync(report, isDirectory: false, CancellationToken.None));

        // The control: the neighbour describes as what it is.
        Assert.Equal("read-only, hidden", await metadata.DescribeAccessAsync(
            Path.Combine(_root, "report"), isDirectory: false, CancellationToken.None));
    }

    // ---- the search walk ---------------------------------------------------------------

    [WindowsFact]
    public async Task The_search_does_not_walk_a_folded_folder_as_its_neighbour()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "quarterly-secret.txt"), "the neighbour's");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "sub "));

        var query = new SearchQuery { Text = "quarterly-secret", ScopePath = _root, MaxResults = 100 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<string>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry.FullPath);

        Assert.Equal([Path.Combine(_root, "sub", "quarterly-secret.txt")], found);
    }
}
