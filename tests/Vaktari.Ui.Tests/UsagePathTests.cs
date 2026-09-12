using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What is using the space in a folder, as somewhere you can be — the path that
/// names it, and the rows it produces.
///
/// **A place has to answer every question the pane asks of a path**, and the two
/// that fail silently rather than loudly are the label, whose final arm is a
/// catch-all, and the view key, which is unbounded unless something collapses
/// it.
/// </summary>
public sealed class UsagePathTests
{
    private static readonly string Measured = OperatingSystem.IsWindows()
        ? @"C:\Users\me\My Documents"
        : "/home/me/My Documents";

    // ---- the path ------------------------------------------------------------

    /// <summary>
    /// **The folder survives the round trip**, separators, colons, spaces and
    /// all. It is escaped for the reason a search's fields are: PathRules
    /// .Normalise runs over every path the pane compares, and it rewrites
    /// separators and trims a trailing one.
    /// </summary>
    [Fact]
    public void The_folder_comes_back_out_as_it_went_in()
        => Assert.Equal(Measured, VirtualPaths.FolderOf(VirtualPaths.Usage(Measured)));

    [Fact]
    public void A_usage_listing_is_a_virtual_listing()
        => Assert.True(VirtualPaths.IsVirtual(VirtualPaths.Usage(Measured)));

    /// <summary>
    /// A real folder is not one of these, which is what keeps every gate that
    /// asks <c>IsRealFolder</c> answering as it did.
    /// </summary>
    [Fact]
    public void A_real_folder_is_not_one()
    {
        Assert.False(VirtualPaths.IsUsage(Measured));
        Assert.False(VirtualPaths.IsVirtual(Measured));
    }

    /// <summary>
    /// The tab is named for the folder it measured. The label's final arm is a
    /// catch-all, so a usage path reaching it would be titled "Recent
    /// locations" — wrong, and silently so.
    /// </summary>
    [Fact]
    public void The_tab_is_named_for_the_folder()
        => Assert.Equal(
            "Space used in My Documents", VirtualPaths.Label(VirtualPaths.Usage(Measured)));

    /// <summary>A drive or a mount root has no leaf to name, so it is shown as
    /// it is spelled rather than as nothing at all.</summary>
    [Fact]
    public void A_root_is_named_as_it_is_spelled()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";

        Assert.Equal($"Space used in {root}", VirtualPaths.Label(VirtualPaths.Usage(root)));
    }

    /// <summary>
    /// One key for every usage listing, so the remembered view is "how I like
    /// these to look" rather than one record per folder anybody ever measured —
    /// an unbounded key in a store kept for the life of the profile.
    /// </summary>
    [Fact]
    public void Every_usage_listing_remembers_its_view_under_one_key()
    {
        Assert.True(VirtualPaths.IsUsage(VirtualPaths.UsageViewKey));

        Assert.NotEqual(VirtualPaths.UsageViewKey, VirtualPaths.Usage(Measured));
    }

    /// <summary>
    /// **A hand-edited or truncated path names a folder that is not there, and
    /// nothing raises.** These strings go into session.json and come back at
    /// startup, so the failure has to be a harmless listing rather than a window
    /// that will not open. Measured: unescaping is lenient and hands a lone "%"
    /// straight back, so there is no malformed case to catch — the path simply
    /// names nothing, which the measurement answers with no rows.
    /// </summary>
    [Fact]
    public void A_malformed_path_names_a_folder_that_is_not_there()
    {
        Assert.Equal("%", VirtualPaths.FolderOf("vaktari:usage:%"));

        Assert.Empty(SpaceUsage.Underneath(
            VirtualPaths.FolderOf("vaktari:usage:%"), progress: null, CancellationToken.None).Rows);
    }

    /// <summary>A path that is not one of these at all has no folder to give.</summary>
    [Fact]
    public void A_path_that_is_not_one_has_no_folder()
        => Assert.Equal("", VirtualPaths.FolderOf(Measured));

    // ---- the rows ------------------------------------------------------------

    private static UsageListing Listing(params UsageRow[] rows)
        => new(rows, new Usage(0, 0, 0, 0));

    private static UsageRow Folder(string name, long bytes, bool concealed = false)
        => new(Path.Combine(Measured, name), IsDirectory: true, IsLink: false,
               new Usage(bytes, 3, 1, 0), IsConcealed: concealed);

    private static UsageRow File(string name, long bytes, bool concealed = false)
        => new(Path.Combine(Measured, name), IsDirectory: false, IsLink: false,
               new Usage(bytes, 1, 0, 0), IsConcealed: concealed);

    /// <summary>
    /// **A folder's row says its length is real.** Everything that draws a row
    /// treats a directory as sizeless, and the flag is the whole of what tells
    /// the size cell, the sort, the bands and the status bar otherwise.
    /// </summary>
    [Fact]
    public void A_measured_folder_carries_its_total_and_says_so()
    {
        var row = Assert.Single(SpaceListing.Build(Listing(Folder("photos", 2048)), includeHidden: false));

        Assert.Equal(2048, row.Length);
        Assert.True(row.IsDirectory);
        Assert.True(row.IsMeasured);
    }

    /// <summary>A file needs no such saying — its own length has always been
    /// read.</summary>
    [Fact]
    public void A_file_is_its_own_length_and_is_not_marked()
    {
        var row = Assert.Single(SpaceListing.Build(Listing(File("notes.txt", 10)), includeHidden: false));

        Assert.Equal(10, row.Length);
        Assert.False(row.IsMeasured);
    }

    /// <summary>
    /// **Hidden rows are dropped here, as the search listing drops them.** The
    /// pane expects them already gone and nothing downstream re-checks it, so a
    /// listing that kept them would show dotfiles beside a folder hiding them
    /// and leave Ctrl+H doing nothing at all.
    /// </summary>
    [Fact]
    public void A_concealed_row_is_left_out_until_hidden_files_are_shown()
    {
        var listing = Listing(File("plain.bin", 10), File(".secret", 90, concealed: true));

        Assert.Equal(
            ["plain.bin"],
            SpaceListing.Build(listing, includeHidden: false).Select(r => r.Name));

        Assert.Equal(
            ["plain.bin", ".secret"],
            SpaceListing.Build(listing, includeHidden: true).Select(r => r.Name));
    }

    /// <summary>And a row that is kept says it is hidden, so it draws the way a
    /// hidden row draws everywhere else.</summary>
    [Fact]
    public void A_concealed_row_that_is_shown_still_says_it_is_hidden()
    {
        var row = Assert.Single(
            SpaceListing.Build(Listing(File(".secret", 90, concealed: true)), includeHidden: true));

        Assert.True(row.IsHidden);
    }

    /// <summary>
    /// A measurement carries no write time, and default is below the Unix epoch
    /// — the "no answer" the date converter already renders as an empty cell,
    /// which is what This PC's drives and the Recent rows give it.
    /// </summary>
    [Fact]
    public void A_row_offers_no_date_it_does_not_have()
        => Assert.Equal(
            default,
            Assert.Single(SpaceListing.Build(Listing(File("notes.txt", 10)), includeHidden: false))
                  .LastWriteTime);
}
