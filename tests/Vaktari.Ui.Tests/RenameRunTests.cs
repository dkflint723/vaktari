using Vaktari.Core.FileSystem;
using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Renaming a run of files with Tab.
///
/// **It cost three keystrokes each — Enter, arrow, F2.** Explorer answers Tab,
/// which is how anybody who has tidied a folder of photographs does it. The
/// arrow was the worst of the three: a rename can re-sort the folder, so "the
/// row under the one just finished" is not the file that was under it a moment
/// ago.
/// </summary>
public sealed class RenameRunTests
{
    private static FileEntry Row(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    private static readonly FileEntry[] Rows = [Row("a.txt"), Row("b.txt"), Row("c.txt")];

    private static string? Next(string from, int step)
        => RenameRun.Next(Rows, Path.Combine(Path.GetTempPath(), from), step)?.Name;

    [Fact]
    public void Tab_goes_to_the_next_row()
        => Assert.Equal("b.txt", Next("a.txt", 1));

    [Fact]
    public void And_shift_tab_to_the_one_before()
        => Assert.Equal("a.txt", Next("b.txt", -1));

    /// <summary>
    /// **Null rather than wrapping.** A run has a beginning and an end, and
    /// wrapping from the last file back to the first would re-open a name that
    /// has just been settled — with the bar looking exactly as it does mid-run,
    /// so the only sign would be the name in it.
    /// </summary>
    [Fact]
    public void The_run_stops_at_the_end()
        => Assert.Null(Next("c.txt", 1));

    [Fact]
    public void And_at_the_beginning()
        => Assert.Null(Next("a.txt", -1));

    /// <summary>
    /// **Matched on the path, not the entry.** The entry the prompt opened with
    /// is the one from before the rename, and differs from the listing's own row
    /// in every field the rename touched — so comparing entries would find
    /// nothing and the run would stop after one file.
    /// </summary>
    [Fact]
    public void A_row_is_found_by_where_it_is_rather_than_what_it_was()
    {
        var stale = new FileEntry(
            "a.txt", Path.Combine(Path.GetTempPath(), "a.txt"),
            Length: 999, DateTimeOffset.UnixEpoch.AddYears(3), EntryFlags.ReadOnly);

        Assert.NotEqual(Rows[0], stale);
        Assert.Equal("b.txt", RenameRun.Next(Rows, stale.FullPath, 1)?.Name);
    }

    /// <summary>
    /// **By the platform's own rule for "the same path", not by string.** The
    /// path the prompt was opened with and the one the listing holds come from
    /// different places, and on Windows they can differ in case alone — where
    /// an ordinal comparison finds nothing and the run stops after one file.
    /// </summary>
    [Fact]
    public void A_path_that_differs_only_in_case_is_the_same_row_on_windows()
    {
        var shouted = Path.Combine(Path.GetTempPath(), "A.TXT").ToUpperInvariant();

        Assert.Equal(
            PathRules.Same(shouted, Rows[0].FullPath) ? "b.txt" : null,
            RenameRun.Next(Rows, shouted, 1)?.Name);
    }

    /// <summary>A file that is no longer in the listing has no neighbour.</summary>
    [Fact]
    public void A_row_that_has_gone_ends_the_run()
        => Assert.Null(Next("vanished.txt", 1));

    /// <summary>And an empty listing has none either.</summary>
    [Fact]
    public void An_empty_listing_ends_it_too()
        => Assert.Null(RenameRun.Next([], "anything", 1));
}
