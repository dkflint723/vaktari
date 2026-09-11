using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Two folders compared name by name, and the one rule for "the same file"
/// that the comparison shares with the file-clash prompt.
///
/// Pure: every entry is built in memory, so these say what the rules are
/// without a disk to disagree with.
/// </summary>
public sealed class FolderComparisonTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static FileEntry File_(string side, string name, long length, DateTimeOffset time)
        => new(name, side + "/" + name, length, time, EntryFlags.None);

    private static FileEntry Folder(string side, string name, DateTimeOffset? time = null)
        => new(name, side + "/" + name, 0, time ?? Noon, EntryFlags.Directory);

    // ---- the one rule ------------------------------------------------------------

    /// <summary>
    /// **A copy on a FAT stick is the same file.** FAT keeps modification
    /// times in two-second steps, so the copy's time is its original's to
    /// within two seconds and no closer.
    /// </summary>
    [Fact]
    public void The_same_size_under_two_seconds_apart_is_the_same_file()
    {
        Assert.Equal(Sameness.Same, FileSameness.Judge(10, Noon, 10, Noon.AddSeconds(1.5)));
        Assert.Equal(Sameness.SameTime, FileSameness.Judge(10, Noon, 11, Noon.AddSeconds(1.5)));
    }

    [Fact]
    public void Two_seconds_apart_is_a_later_change()
    {
        Assert.Equal(Sameness.SecondNewer, FileSameness.Judge(10, Noon, 10, Noon.AddSeconds(2)));
        Assert.Equal(Sameness.FirstNewer, FileSameness.Judge(10, Noon.AddMinutes(5), 10, Noon));
    }

    // ---- the comparison ----------------------------------------------------------

    [Fact]
    public void A_name_on_one_side_is_only_there()
    {
        var result = FolderComparison.Between(
            [File_("left", "a.txt", 1, Noon)],
            [File_("right", "b.txt", 1, Noon)]);

        Assert.Equal(CompareMark.OnlyHere, result.Left["left/a.txt"]);
        Assert.Equal(CompareMark.OnlyHere, result.Right["right/b.txt"]);
    }

    [Fact]
    public void The_side_changed_later_is_newer_and_the_other_older()
    {
        var result = FolderComparison.Between(
            [File_("left", "notes.txt", 5, Noon.AddDays(1))],
            [File_("right", "notes.txt", 9, Noon)]);

        Assert.Equal(CompareMark.NewerHere, result.Left["left/notes.txt"]);
        Assert.Equal(CompareMark.OlderHere, result.Right["right/notes.txt"]);
    }

    /// <summary>The same file on both sides carries no mark at all, which is
    /// what lets a mark mean something.</summary>
    [Fact]
    public void The_same_file_on_both_sides_is_not_marked()
    {
        var result = FolderComparison.Between(
            [File_("left", "photo.jpg", 100, Noon)],
            [File_("right", "photo.jpg", 100, Noon.AddSeconds(1))]);

        Assert.Empty(result.Left);
        Assert.Empty(result.Right);
    }

    [Fact]
    public void Changed_at_the_same_moment_at_different_sizes_differs()
    {
        var result = FolderComparison.Between(
            [File_("left", "log.txt", 100, Noon)],
            [File_("right", "log.txt", 120, Noon)]);

        Assert.Equal(CompareMark.Differs, result.Left["left/log.txt"]);
        Assert.Equal(CompareMark.Differs, result.Right["right/log.txt"]);
    }

    [Fact]
    public void A_file_against_a_folder_of_the_same_name_differs()
    {
        var result = FolderComparison.Between(
            [Folder("left", "build")],
            [File_("right", "build", 3, Noon)]);

        Assert.Equal(CompareMark.Differs, result.Left["left/build"]);
        Assert.Equal(CompareMark.Differs, result.Right["right/build"]);
    }

    /// <summary>One level deep: a folder on both sides is not looked inside,
    /// so it says nothing rather than something it has not checked -- even
    /// when one was changed later, since that time says nothing about what is
    /// inside.</summary>
    [Fact]
    public void A_folder_on_both_sides_is_not_marked()
    {
        var result = FolderComparison.Between(
            [Folder("left", "src", Noon.AddDays(3))],
            [Folder("right", "src", Noon)]);

        Assert.Empty(result.Left);
        Assert.Empty(result.Right);
    }

    [WindowsFact]
    public void On_windows_a_name_matches_whatever_its_case()
    {
        var result = FolderComparison.Between(
            [File_("left", "Report.TXT", 1, Noon)],
            [File_("right", "report.txt", 1, Noon)]);

        Assert.Empty(result.Left);
        Assert.Empty(result.Right);
    }

    [PosixFact]
    public void On_linux_a_name_matches_only_in_its_own_case()
    {
        var result = FolderComparison.Between(
            [File_("left", "Report.TXT", 1, Noon)],
            [File_("right", "report.txt", 1, Noon)]);

        Assert.Equal(CompareMark.OnlyHere, result.Left["left/Report.TXT"]);
        Assert.Equal(CompareMark.OnlyHere, result.Right["right/report.txt"]);
    }
}
