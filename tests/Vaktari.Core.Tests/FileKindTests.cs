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

    // ---- a Windows shortcut is not an "LNK file" ----------------------------

    /// <summary>
    /// The predicate both the Type column and the properties window ask.
    /// Platform-blind on purpose, so this one runs on the Linux agent too — the
    /// Windows facts below skip there, and a fix nothing can fail is not a fix.
    /// </summary>
    [Theory]
    [InlineData("lnk", true)]
    [InlineData("LNK", true)]
    [InlineData("Lnk", true)]
    [InlineData("link", false)]
    [InlineData("txt", false)]
    [InlineData("", false)]
    public void The_shortcut_extension_is_recognised_whatever_its_case(string extension, bool is_)
        => Assert.Equal(is_, FileKind.IsShortcut(extension));

    /// <summary>
    /// **Nothing may allocate per row.** DisplayName runs once per visible row
    /// per bind, and a substring for every ordinary file would be an allocation
    /// while scrolling — the one cost this class exists to avoid.
    /// </summary>
    [Fact]
    public void An_ordinary_name_is_handed_straight_back()
    {
        var entry = File("notes.txt");

        Assert.Same(entry.Name, FileKind.DisplayName(entry));
    }

    /// <summary>
    /// **Explorer never says "LNK file" and never shows the extension.**
    /// lnkfile carries NeverShowExt, so Desktop and the Start Menu — folders
    /// that are nothing but shortcuts — read here as a wall of "Chrome.lnk /
    /// LNK file" while every other window on the machine said "Chrome /
    /// Shortcut". The sidebar already agreed with Explorer; only the listing
    /// did not.
    /// </summary>
    [WindowsFact]
    public void A_shortcut_is_called_one_and_loses_its_extension()
    {
        Assert.Equal("Shortcut", FileKind.Describe(File("Chrome.lnk")));
        Assert.Equal("Chrome", FileKind.DisplayName(File("Chrome.lnk")));

        // However it is spelled on disk.
        Assert.Equal("Shortcut", FileKind.Describe(File("Chrome.LNK")));
        Assert.Equal("Chrome", FileKind.DisplayName(File("Chrome.LNK")));
    }

    /// <summary>A file whose whole name is ".lnk" has a name, not an extension,
    /// and hiding it would leave a row with nothing in it.</summary>
    [WindowsFact]
    public void A_file_named_only_lnk_keeps_every_character()
        => Assert.Equal(".lnk", FileKind.DisplayName(File(".lnk")));

    /// <summary>A folder called "things.lnk" is a folder.</summary>
    [WindowsFact]
    public void A_folder_is_untouched_however_it_is_named()
    {
        Assert.Equal("Folder", FileKind.Describe(File("things.lnk", directory: true)));
        Assert.Equal("things.lnk", FileKind.DisplayName(File("things.lnk", directory: true)));
    }

    /// <summary>
    /// On Linux a .lnk is an opaque file from another operating system that
    /// nothing here can follow, so calling it a shortcut would promise a hop
    /// that does not exist.
    /// </summary>
    [PosixFact]
    public void Elsewhere_a_lnk_file_is_just_a_file()
    {
        Assert.Equal("LNK file", FileKind.Describe(File("Chrome.lnk")));
        Assert.Equal("Chrome.lnk", FileKind.DisplayName(File("Chrome.lnk")));
    }
}
