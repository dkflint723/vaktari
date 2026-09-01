using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The type column's text.
///
/// **Neither platform's real answer can fill a column.** Windows asks the shell
/// and Linux asks shared-mime-info; both are per-file and asynchronous, so a
/// listing of two hundred thousand rows would be two hundred thousand round
/// trips to fill something that scrolls past in a second — and they disagree in
/// shape besides, so the column would read differently on each.
/// </summary>
public sealed class FileKindTests
{
    private static FileEntry File(string name, bool directory = false)
        => new(name, "/tmp/" + name, 0, DateTimeOffset.UnixEpoch,
               directory ? EntryFlags.Directory : EntryFlags.None);

    [Fact]
    public void A_folder_says_so()
        => Assert.Equal("Folder", FileKind.Describe(File("Documents", directory: true)));

    [Theory]
    [InlineData("photo.png", "PNG file")]
    [InlineData("notes.txt", "TXT file")]
    [InlineData("report.DOCX", "DOCX file")]
    public void A_file_is_named_by_its_extension(string name, string expected)
        => Assert.Equal(expected, FileKind.Describe(File(name)));

    /// <summary>The last dot decides, the same as everywhere else in this
    /// codebase — archive.tar.gz is a GZ file.</summary>
    [Fact]
    public void The_last_dot_decides()
        => Assert.Equal("GZ file", FileKind.Describe(File("archive.tar.gz")));

    [Theory]
    [InlineData("README")]
    [InlineData("trailing.")]
    public void Something_with_no_extension_is_just_a_file(string name)
        => Assert.Equal("File", FileKind.Describe(File(name)));

    /// <summary>
    /// A leading dot begins a name rather than an extension, which is what
    /// FileEntry.Extension already says and what every rename in this codebase
    /// assumes.
    /// </summary>
    [Fact]
    public void A_dotfile_is_a_file_not_a_gitignore_file()
        => Assert.Equal("File", FileKind.Describe(File(".gitignore")));

    /// <summary>
    /// **The cache must not grow with the listing.** A name may legally end in
    /// a dot and a hundred characters, and one cache entry per such name would
    /// make a folder of junk names cost memory per row rather than per kind.
    /// </summary>
    [Fact]
    public void An_absurd_extension_is_not_worth_remembering()
        => Assert.Equal("File", FileKind.Describe(File("x." + new string('z', 40))));

    /// <summary>
    /// One string shared by every row of a kind. A listing is mostly a handful
    /// of extensions repeated, so the alternative is an allocation per row —
    /// while scrolling, which is the one place that matters.
    /// </summary>
    [Fact]
    public void Two_files_of_a_kind_share_one_string()
    {
        var first = FileKind.Describe(File("a.png"));
        var second = FileKind.Describe(File("b.PNG"));

        Assert.Equal("PNG file", second);
        Assert.Same(first, second);
    }
}
