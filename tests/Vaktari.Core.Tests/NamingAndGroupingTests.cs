using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Two labelling rules an audit caught disagreeing with themselves.
/// </summary>
public class NamingAndGroupingTests
{
    private static FileEntry File(string name) =>
        new(name, "/f/" + name, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    private static FileEntry Folder(string name) =>
        new(name, "/f/" + name, 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

    /// <summary>
    /// **Group-by-Kind sliced a character off every extension.** FileEntry
    /// .Extension is already dot-free, and the label sliced it again: .txt
    /// grouped under "XT", .cs under "S". The guard hid the rest — a one-letter
    /// extension had nothing left after the slice, so every .c file landed in
    /// "No extension".
    /// </summary>
    [Theory]
    [InlineData("report.txt", "TXT")]
    [InlineData("main.c", "C")]
    [InlineData("Program.cs", "CS")]
    [InlineData("archive.tar.gz", "GZ")]
    public void Kind_groups_by_the_whole_extension(string name, string expected)
        => Assert.Equal(expected, Grouping.Label(File(name), GroupMode.Kind, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("README")]
    [InlineData(".gitignore")]
    public void A_name_with_no_extension_says_so(string name)
        => Assert.Equal(
            "No extension", Grouping.Label(File(name), GroupMode.Kind, DateTimeOffset.UtcNow));

    [Fact]
    public void Folders_group_as_folders()
        => Assert.Equal(
            "Folders", Grouping.Label(Folder("photos"), GroupMode.Kind, DateTimeOffset.UtcNow));

    /// <summary>
    /// **A folder name is atomic, and so is a dotfile.** Splitting either on
    /// the last dot produced "my (1).photos" for a folder called "my.photos",
    /// and " (1).bashrc" — a suffix with nothing in front of it — for a second
    /// ".bashrc".
    /// </summary>
    [Theory]
    [InlineData("notes.txt", false, "notes", ".txt")]
    [InlineData("archive.tar.gz", false, "archive.tar", ".gz")]
    [InlineData(".bashrc", false, ".bashrc", "")]
    [InlineData("README", false, "README", "")]
    [InlineData("my.photos", true, "my.photos", "")]
    [InlineData("photos", true, "photos", "")]
    public void A_leaf_splits_where_the_suffix_should_go(
        string leaf, bool isDirectory, string stem, string extension)
    {
        var split = PathRules.SplitLeaf(leaf, isDirectory);

        Assert.Equal(stem, split.Stem);
        Assert.Equal(extension, split.Extension);
    }
}
