using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The rows a duplicates listing is built from, and which of them are spare.
///
/// **The whole safety property lives in Extras.** The listing shows every copy
/// so the person can choose which to keep, and the one route that selects in
/// bulk offers every copy BUT one of each set — so selecting everything it
/// gives and pressing Delete can never take the last copy of anything. These
/// hold it to that.
///
/// Driven from a report rather than a real tree: what the scan finds is
/// <see cref="DuplicateFinder"/>'s own tests' business, and what is interesting
/// here is which rows survive and what they carry.
/// </summary>
public sealed class DuplicateListingTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-dup-listing").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A real file, because the builder reads the date and the hidden bit off
    /// the disk rather than taking them from the report.
    ///
    /// **Concealed as the platform conceals things.** Setting the Hidden
    /// attribute does nothing at all on Linux — the name decides there — so a
    /// fixture that only set the attribute made a perfectly visible file and
    /// the two tests about hidden copies measured nothing. .NET reports
    /// FileAttributes.Hidden for a dot-name on Unix, which is what the code
    /// under test reads.
    /// </summary>
    private string File_(string name, int bytes = 8, bool hidden = false)
    {
        if (hidden && !OperatingSystem.IsWindows())
        {
            var directory = Path.GetDirectoryName(name);
            var leaf = "." + Path.GetFileName(name);

            name = string.IsNullOrEmpty(directory) ? leaf : Path.Combine(directory, leaf);
        }

        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);

        if (hidden && OperatingSystem.IsWindows())
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        Assert.Equal(hidden, (new FileInfo(path).Attributes & FileAttributes.Hidden) != 0);

        return path;
    }

    private static DuplicateReport Report(int unreadable, params (long Length, string[] Paths)[] sets)
        => new([.. sets.Select(s => new DuplicateSet(s.Length, s.Paths))], unreadable);

    [Fact]
    public void Every_copy_is_a_row_and_all_but_one_are_spare()
    {
        var a = File_("a.txt");
        var b = File_("b.txt");

        var built = DuplicateListing.Build(Report(0, (8, [a, b])), includeHidden: false);

        Assert.Equal([a, b], built.Rows.Select(r => r.FullPath));

        // One of the two is not offered, and it is the same one every time.
        Assert.Equal([b], built.Extras);
    }

    [Fact]
    public void A_set_of_three_leaves_one_behind()
    {
        var a = File_("a.txt");
        var b = File_("b.txt");
        var c = File_("c.txt");

        var built = DuplicateListing.Build(Report(0, (8, [c, a, b])), includeHidden: false);

        Assert.Equal(3, built.Rows.Count);
        Assert.Equal([b, c], built.Extras);
        Assert.DoesNotContain(a, built.Extras);
    }

    /// <summary>
    /// **The one property this listing exists to keep.** Whatever the sets, a
    /// member of each survives selecting everything offered.
    /// </summary>
    [Fact]
    public void Nothing_offered_in_bulk_would_empty_a_set()
    {
        var sets = new[]
        {
            (8L, new[] { File_("one/a.txt"), File_("one/b.txt") }),
            (8L, new[] { File_("two/a.txt"), File_("two/b.txt"), File_("two/c.txt") }),
        };

        var built = DuplicateListing.Build(Report(0, sets), includeHidden: false);

        foreach (var (_, paths) in sets)
            Assert.Contains(paths, path => !built.Extras.Contains(path));
    }

    [Fact]
    public void What_the_rows_give_back_is_every_copy_but_one_of_each()
    {
        var built = DuplicateListing.Build(
            Report(0,
                (1000, [File_("one/a.bin", 1000), File_("one/b.bin", 1000)]),
                (10, [File_("two/a.bin", 10), File_("two/b.bin", 10), File_("two/c.bin", 10)])),
            includeHidden: false);

        Assert.Equal(2, built.Summary.Sets);
        Assert.Equal(5, built.Summary.Files);

        // One copy of the first set and two of the second.
        Assert.Equal(1000 + 20, built.Summary.Reclaimable);
    }

    /// <summary>What could not be read has nowhere else to go, as the measured
    /// folder's count has nowhere but its band.</summary>
    [Fact]
    public void What_could_not_be_read_is_carried()
    {
        var built = DuplicateListing.Build(
            Report(3, (8, [File_("a.txt"), File_("b.txt")])), includeHidden: false);

        Assert.Equal(3, built.Summary.Unreadable);
    }

    [Fact]
    public void A_hidden_copy_is_dropped_with_its_whole_set()
    {
        var shown = File_("shown.txt");
        var hidden = File_("hidden.txt", hidden: true);

        var built = DuplicateListing.Build(Report(0, (8, [shown, hidden])), includeHidden: false);

        // One member left is no set at all: a row calling itself a copy with
        // nothing on screen it is a copy of invites deleting the only one you
        // can see.
        Assert.Empty(built.Rows);
        Assert.Empty(built.Extras);
        Assert.Equal(0, built.Summary.Sets);
        Assert.Equal(0, built.Summary.Reclaimable);
    }

    [Fact]
    public void A_hidden_copy_is_shown_and_counted_when_hidden_files_are()
    {
        var shown = File_("a-shown.txt");
        var hidden = File_("b-hidden.txt", hidden: true);

        var built = DuplicateListing.Build(Report(0, (8, [shown, hidden])), includeHidden: true);

        Assert.Equal(2, built.Rows.Count);
        Assert.Equal(8, built.Summary.Reclaimable);
        Assert.Contains(built.Rows, r => r.FullPath == hidden && (r.Flags & EntryFlags.Hidden) != 0);
    }

    /// <summary>
    /// A file deleted between the scan and the listing stops being a copy of
    /// anything, and takes its set with it when it leaves one member.
    /// </summary>
    [Fact]
    public void A_copy_that_has_gone_since_the_scan_is_not_a_row()
    {
        var kept = File_("kept.txt");
        var gone = Path.Combine(_root, "gone.txt");

        var built = DuplicateListing.Build(Report(0, (8, [kept, gone])), includeHidden: false);

        Assert.Empty(built.Rows);
    }

    /// <summary>
    /// **The date is filled in here and nowhere else among the virtual
    /// listings.** It is how a person decides which copy to keep, so it is
    /// worth the one look the space listing refuses.
    /// </summary>
    [Fact]
    public void A_row_carries_the_date_the_file_was_written()
    {
        var a = File_("a.txt");
        var b = File_("b.txt");

        var written = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, written);

        var built = DuplicateListing.Build(Report(0, (8, [a, b])), includeHidden: false);

        var row = Assert.Single(built.Rows, r => r.FullPath == a);

        Assert.Equal(written, row.LastWriteTime.UtcDateTime);
    }

    /// <summary>A copy is a file: nothing here is ever drawn as a folder, which
    /// would make Enter navigate into it.</summary>
    [Fact]
    public void No_row_is_a_folder()
    {
        var built = DuplicateListing.Build(
            Report(0, (8, [File_("a.txt"), File_("b.txt")])), includeHidden: false);

        Assert.All(built.Rows, row => Assert.False(row.IsDirectory));
    }

    [Fact]
    public void A_report_of_nothing_is_an_empty_listing()
    {
        var built = DuplicateListing.Build(Report(0), includeHidden: false);

        Assert.Empty(built.Rows);
        Assert.Empty(built.Extras);
        Assert.Equal(new Copies(0, 0, 0, 0), built.Summary);
    }
}
