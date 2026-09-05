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

    // ---- the extension the listing may be told to leave off ------------------

    /// <summary>
    /// **Two extensions were hidden and no others could ever be.** .lnk and
    /// .desktop were the whole list, both because the platform's own shell
    /// hides them, and nothing in the application or in settings.json could ask
    /// for Explorer's "File name extensions" unticked.
    ///
    /// The preference is PASSED here rather than set on the static, so this
    /// class touches no shared state: FileKind.HideExtensions is per-process,
    /// and xUnit runs the classes in this assembly in parallel.
    /// </summary>
    [Theory]
    [InlineData("notes.txt", "notes")]
    [InlineData("archive.tar.gz", "archive.tar")]
    [InlineData("Report.FINAL.DOCX", "Report.FINAL")]
    // A leading dot begins a name rather than an extension, so there is nothing
    // to take off and a row cannot be emptied.
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("Makefile", "Makefile")]
    // One character either side of the dot is still an extension.
    [InlineData("a.b", "a")]
    // **"..foo" was cut down to a single dot** — a legal name on both
    // platforms, drawn as the current-directory entry. The last dot decides the
    // extension and only index 0 is refused, so the leading-dot reasoning above
    // covers a name whose ONLY dot leads and no other.
    [InlineData("..foo", "..foo")]
    [InlineData("...foo", "...foo")]
    // And the stem being merely SHORT is fine — it is being nothing but dots
    // that is not.
    [InlineData(".x.foo", ".x")]
    public void An_extension_comes_off_only_when_the_listing_asks(string name, string shown)
    {
        var entry = File(name);

        Assert.Equal(shown, FileKind.DisplayName(entry, hideExtension: true));

        // And the other way answers the whole name, which is what every row
        // drew before there was a switch.
        Assert.Same(entry.Name, FileKind.DisplayName(entry, hideExtension: false));
    }

    /// <summary>
    /// A folder called "src.old" is a folder called "src.old", and the type
    /// column says "Folder" whatever the name looks like.
    ///
    /// **The CONDITION has two independent mechanisms; the VALUE has one.**
    /// <c>DisplayName</c> returns at the top for a directory, and
    /// <c>FileEntry.Extension</c> is <c>default</c> for one as well — so
    /// swapping that early return's <c>entry.IsDirectory</c> for
    /// <c>entry.IsSymlink</c> leaves this green, because the empty extension
    /// then fails the length test one line further down. Measured, both ways.
    ///
    /// Not a guard, then: the killing mutation is on the value rather than on
    /// the test, and it is <c>return entry.Name ?? ""</c> to <c>return ""</c>
    /// in that same early return, which reddens this with
    /// "Expected: src.old, Actual: ". Measured.
    ///
    /// Kept for the day somebody removes the second of the two conditions — an
    /// Extension that starts answering for directories would otherwise turn
    /// every folder with a dot in its name into a truncated one, silently, in
    /// all three layouts.
    /// </summary>
    [Fact]
    public void A_folder_keeps_its_whole_name_however_it_is_spelled()
        => Assert.Equal(
            "src.old", FileKind.DisplayName(File("src.old", directory: true), hideExtension: true));

    /// <summary>
    /// **Nothing may allocate per row**, which is the rule the whole method is
    /// written around — and it still holds for the rows that keep their name
    /// with the preference switched on.
    /// </summary>
    [Fact]
    public void A_name_with_nothing_to_take_off_is_still_handed_straight_back()
    {
        var entry = File("Makefile");

        Assert.Same(entry.Name, FileKind.DisplayName(entry, hideExtension: true));
    }

    /// <summary>
    /// **A program could be drawn as the document beside it.** With extensions
    /// hidden, report.exe and report.pdf in one folder both answered the single
    /// word "report" — measured — and no other part of the row closed the gap:
    /// the Type column has no initialiser behind it and exists in one layout of
    /// three, the name tooltip is gated on a separate preference, and an .exe
    /// supplies its own icon. So the suffix that says a row STARTS something is
    /// the one this preference never takes off, and the two rows stay two
    /// different words.
    /// </summary>
    [Theory]
    [InlineData("report.exe", "report.exe")]
    [InlineData("setup.MSI", "setup.MSI")]
    [InlineData("build.ps1", "build.ps1")]
    [InlineData("install.sh", "install.sh")]
    [InlineData("Ember.AppImage", "Ember.AppImage")]
    [InlineData("keys.reg", "keys.reg")]
    // Not a program, and this is the row it must not be confusable with.
    [InlineData("report.pdf", "report")]
    // The famous shape: hiding ".exe" here would draw it as the PDF it is not.
    [InlineData("invoice.pdf.exe", "invoice.pdf.exe")]
    public void The_suffix_that_says_a_row_runs_is_never_hidden(string name, string shown)
        => Assert.Equal(shown, FileKind.DisplayName(File(name), hideExtension: true));

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
