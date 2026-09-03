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

    /// <summary>
    /// **Every file used to read "&lt;EXT&gt; file".** Explorer says
    /// "Application" and "Text Document"; this said "EXE file" and "TXT file" —
    /// the extension the column sits beside, spelled louder. A Type column
    /// whose every value can be read off the Name column is a column of
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData("photo.png", "PNG image")]
    [InlineData("notes.txt", "Text document")]
    [InlineData("report.DOCX", "Word document")]
    [InlineData("setup.exe", "Application")]
    public void A_file_is_named_by_what_it_is(string name, string expected)
        => Assert.Equal(expected, FileKind.Describe(File(name)));

    /// <summary>
    /// And one nobody has named falls through to the extension, which says
    /// exactly as much as it did before — the list stops well short of
    /// guessing at every suffix in the world.
    /// </summary>
    [Theory]
    [InlineData("data.xyz", "XYZ file")]
    [InlineData("thing.qqq", "QQQ file")]
    public void An_unfamiliar_one_still_falls_back_to_its_extension(
        string name, string expected)
        => Assert.Equal(expected, FileKind.Describe(File(name)));

    /// <summary>The last dot decides, the same as everywhere else in this
    /// codebase — archive.tar.gz is a gzip archive, not a tar one.</summary>
    [Fact]
    public void The_last_dot_decides()
        => Assert.Equal("Gzip archive", FileKind.Describe(File("archive.tar.gz")));

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
    /// **A named kind sorts and groups with its own sort.** The Type column,
    /// the Kind sort and the Kind grouping all key on this one phrase, so
    /// naming a kind moves it: .exe files file under Application rather than
    /// under E, between .dll and .gif. That is the point of the column, and it
    /// is why the table is here rather than in the view.
    /// </summary>
    [Fact]
    public void Files_of_one_kind_answer_with_one_phrase_whatever_their_extension()
    {
        var jpg = FileKind.Describe(File("a.jpg"));
        var jpeg = FileKind.Describe(File("b.jpeg"));

        Assert.Equal("JPEG image", jpg);
        Assert.Equal(jpg, jpeg);
    }

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

        Assert.Equal("PNG image", second);
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

    // ---- a link is not the thing it points at -------------------------------

    /// <summary>
    /// **The Symlink flag was set correctly by both providers and read by
    /// nothing.** A symlinked folder, a junction and a mount point were drawn
    /// exactly like the real thing in every layout — and deleting a link is a
    /// very different act from deleting what it points at.
    ///
    /// A folder, because a folder has no extension to lose. A symlink to a file
    /// keeps its own type: "PNG file" says more than "Link" would.
    /// </summary>
    [Fact]
    public void A_symlinked_folder_says_it_is_a_link()
    {
        var link = new FileEntry(
            "shared", Path.Combine(Path.GetTempPath(), "shared"), 0,
            DateTimeOffset.UnixEpoch, EntryFlags.Directory | EntryFlags.Symlink);

        Assert.Equal("Folder link", FileKind.Describe(link));
    }

    [Fact]
    public void A_real_folder_still_says_Folder()
        => Assert.Equal("Folder", FileKind.Describe(File("things", directory: true)));

    /// <summary>A symlinked file keeps the type its extension gives it, which
    /// is the more useful of the two facts.</summary>
    [Fact]
    public void A_symlinked_file_keeps_its_own_type()
    {
        var link = new FileEntry(
            "photo.png", Path.Combine(Path.GetTempPath(), "photo.png"), 1,
            DateTimeOffset.UnixEpoch, EntryFlags.Symlink);

        Assert.Equal("PNG image", FileKind.Describe(link));
    }
}
