using System.Collections;
using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Compress to zip, and extract one.
///
/// **Against the real filesystem and real zips**, for the reason the Windows
/// suite's TempTree gives: every hazard here is a disagreement between what the
/// code assumes and what the format or the filesystem actually does — a fresh
/// entry losing its timestamp, a DOS date that cannot go back before 1980, a
/// junction being walked as though it were a folder — and a fake would have
/// agreed with the assumption in all three.
/// </summary>
public sealed class ArchiveVerbTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-archive").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Write(string relative, string content = "content")
    {
        var path = At(relative.Split('/'));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    private string Dir(string relative)
    {
        var path = At(relative.Split('/'));

        Directory.CreateDirectory(path);

        return path;
    }

    private static string[] NamesIn(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);

        return [.. zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal)];
    }

    // ---- which files the verb offers itself for ----------------------------

    [Fact]
    public void Nothing_cannot_be_extracted() => Assert.False(Archives.CanExtract(null));

    // ---- compressing -------------------------------------------------------

    [Fact]
    public void One_file_goes_into_a_zip_named_after_it()
    {
        var source = Write("notes.txt", "hello");

        var made = Archives.Compress([source], _root);

        Assert.Equal(At("notes.zip"), made);
        Assert.Equal(["notes.txt"], NamesIn(made));

        using var zip = ZipFile.OpenRead(made);
        using var reader = new StreamReader(zip.GetEntry("notes.txt")!.Open());

        Assert.Equal("hello", reader.ReadToEnd());
    }

    /// <summary>
    /// **A leading dot begins a name rather than an extension**, the same rule
    /// the rename box follows. Splitting on the last dot regardless turns
    /// ".gitignore" into an archive called ".zip".
    /// </summary>
    [Fact]
    public void A_dotfile_keeps_its_whole_name()
    {
        var made = Archives.Compress([Write(".gitignore", "bin/")], _root);

        Assert.Equal(At(".gitignore.zip"), made);
    }

    /// <summary>A folder called v1.2 has no extension to drop.</summary>
    [Fact]
    public void A_folder_keeps_everything_after_its_dot()
    {
        Write("v1.2/inside.txt");

        var made = Archives.Compress([At("v1.2")], _root);

        Assert.Equal(At("v1.2.zip"), made);
    }

    [Fact]
    public void A_folder_goes_in_under_its_own_name_with_its_tree_inside()
    {
        Write("trip/top.txt");
        Write("trip/day one/photo.jpg");
        Dir("trip/empty");

        var made = Archives.Compress([At("trip")], _root);

        Assert.Equal(
            ["trip/", "trip/day one/", "trip/day one/photo.jpg", "trip/empty/", "trip/top.txt"],
            NamesIn(made));
    }

    /// <summary>Several things at once are named for the folder holding
    /// them.</summary>
    [Fact]
    public void Several_items_are_named_after_the_folder_they_are_in()
    {
        var a = Write("one.txt");
        var b = Write("two.txt");

        var made = Archives.Compress([a, b], _root);

        Assert.Equal(At(Path.GetFileName(_root) + ".zip"), made);
        Assert.Equal(["one.txt", "two.txt"], NamesIn(made));
    }

    /// <summary>
    /// **Two sources sharing a leaf name land on one entry, and the first is
    /// gone.** Measured before the rule existed: these two wrote an archive
    /// holding two entries called notes.txt, and extracting it produced ONE
    /// file holding "second" — with nothing refused, so the counter that exists
    /// to notice an archive losing entries counted nothing.
    ///
    /// Reachable from the pane, which is why it is a rule rather than a note: a
    /// details listing splices an expanded folder's rows in underneath it, so
    /// one selection can hold rows from both of these folders.
    /// </summary>
    [Fact]
    public void Two_folders_worth_of_files_do_not_go_into_one_archive()
    {
        var a = Write("2023/notes.txt", "first");
        var b = Write("2024/notes.txt", "second");

        Assert.True(Archives.CanCompress([a, Write("2023/other.txt")]));
        Assert.False(Archives.CanCompress([a, b]));

        var refused = Assert.ThrowsAny<ArgumentException>(
            () => Archives.Compress([a, b], At("2023")));

        // The whole of the message, because it can reach the status bar through
        // Failures.Describe: measured, an ArgumentException given a paramName
        // prints "(Parameter 'sources')" into its Message.
        Assert.Equal("everything in one archive has to come from one folder", refused.Message);

        Assert.Empty(Directory.EnumerateFiles(At("2023"), "*.zip"));
    }

    [Fact]
    public void A_second_zip_of_the_same_thing_is_numbered_in_parentheses()
    {
        var source = Write("notes.txt");

        Archives.Compress([source], _root);

        Assert.Equal(At("notes (2).zip"), Archives.Compress([source], _root));
    }

    /// <summary>
    /// **A fresh entry does not keep the file's date.** Measured here: with the
    /// assignment removed, a file written with a 2001 timestamp comes out of
    /// the archive dated the moment the archive was built, so every file in a
    /// zip of an old folder claims to be new.
    /// </summary>
    [Fact]
    public void The_files_keep_the_dates_they_had()
    {
        var source = Write("old.txt");
        var when = new DateTime(2001, 4, 5, 6, 7, 8, DateTimeKind.Local);

        File.SetLastWriteTime(source, when);

        using var zip = ZipFile.OpenRead(Archives.Compress([source], _root));

        Assert.Equal(when, zip.GetEntry("old.txt")!.LastWriteTime.DateTime);
    }

    /// <summary>
    /// **The date a zip stores begins in 1980.** Measured here: assigning a
    /// 1970 timestamp to a ZipArchiveEntry raises ArgumentOutOfRangeException,
    /// which without the guard would abandon the whole archive over one odd
    /// file.
    /// </summary>
    [Fact]
    public void A_file_dated_before_the_format_begins_does_not_stop_the_archive()
    {
        var source = Write("ancient.txt", "from before");

        File.SetLastWriteTime(source, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

        var made = Archives.Compress([source], _root);

        Assert.Equal(["ancient.txt"], NamesIn(made));
    }

    /// <summary>
    /// **A compress that dies halfway leaves nothing at the name it was asked
    /// for.** A truncated .zip is indistinguishable from a finished one in a
    /// listing, and the working file it was being written to must not be left
    /// lying in the folder either.
    ///
    /// This separates the CLEAN-UP from its absence; the working name it was
    /// being written under is separated by
    /// <see cref="The_landing_name_is_never_occupied_while_the_archive_is_being_written"/>
    /// below.
    /// </summary>
    [Fact]
    public void A_failed_compress_leaves_no_half_written_archive_behind()
    {
        var source = Write("locked.txt");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Archives.Compress([source], _root));
        }

        Assert.False(File.Exists(At("locked.zip")));

        Assert.Equal(
            ["locked.txt"],
            Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).OfType<string>().ToArray());
    }

    /// <summary>
    /// A source list that looks at the destination folder on its way past the
    /// last item — which is inside the <c>using</c> that holds the archive
    /// open, so it sees the half-written file under whatever name it is being
    /// written at.
    /// </summary>
    private sealed class Peeking(IReadOnlyList<string> items, string destination)
        : IReadOnlyList<string>
    {
        public List<string> Seen { get; } = [];

        public int Count => items.Count;

        public string this[int index] => items[index];

        public IEnumerator<string> GetEnumerator()
        {
            foreach (var item in items) yield return item;

            Seen.AddRange(Directory.EnumerateFileSystemEntries(destination)
                .Select(Path.GetFileName)
                .OfType<string>());
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// **A truncated zip looks exactly like a finished one in a listing**, and
    /// the clean-up above only runs while there is still a process to run it —
    /// a machine that loses power partway through leaves whatever is on disk.
    /// So the name the archive will land at is not occupied until the archive
    /// is complete.
    ///
    /// Staged by watching from inside the write: <see cref="Peeking"/> is the
    /// source list Compress is iterating, so it reads the folder while the
    /// archive is still open. Measured with the working name removed: the
    /// listing then held notes.zip midway, and this is the only test of the
    /// twenty-odd here that noticed.
    /// </summary>
    [Fact]
    public void The_landing_name_is_never_occupied_while_the_archive_is_being_written()
    {
        var sources = new Peeking([Write("notes.txt", "hello")], _root);

        Assert.Equal(At("notes.zip"), Archives.Compress(sources, _root));

        Assert.DoesNotContain("notes.zip", sources.Seen);
        Assert.Contains(sources.Seen, n => n.StartsWith(".vaktari-zipping-", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A junction is walked as though it were a folder by the obvious walk.**
    /// Measured here: swapping <see cref="SafeWalk"/> for
    /// <c>EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)</c>
    /// puts <c>elsewhere/secret.txt</c> in the archive — so compressing a folder
    /// that happens to hold a junction to a photo library archives the photo
    /// library.
    ///
    /// A junction rather than a symbolic link because
    /// <see cref="Directory.CreateSymbolicLink"/> needs Developer Mode or
    /// elevation and this machine has neither, so the test would skip itself on
    /// an ordinary Windows box. The rule is the same for both.
    /// </summary>
    [WindowsFact]
    public void A_link_out_of_the_tree_is_not_followed_into_the_archive()
    {
        Write("elsewhere/secret.txt", "not yours");
        Write("trip/mine.txt", "mine");

        TestLinks.Junction(At("trip", "shortcut"), At("elsewhere"));

        var names = NamesIn(Archives.Compress([At("trip")], _root));

        Assert.Contains("trip/mine.txt", names);
        Assert.DoesNotContain(names, n => n.Contains("secret.txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The other end of the same rule: a link that was PICKED is followed.**
    /// SafeWalk tests each child for a reparse point and pushes the root it was
    /// handed without testing it, so the walk starts inside a selected
    /// junction — measured here, and kept: "compress this shortcut" means the
    /// thing it points at, and a zip holding nothing but an empty folder named
    /// after the link would be the surprising answer.
    ///
    /// The link's own name is what the contents go under, so the archive still
    /// says where they came from.
    /// </summary>
    [WindowsFact]
    public void A_link_that_was_picked_is_archived_as_what_it_points_at()
    {
        Write("elsewhere/report.txt", "the target's own");
        Dir("here");

        TestLinks.Junction(At("here", "shortcut"), At("elsewhere"));

        var names = NamesIn(Archives.Compress([At("here", "shortcut")], At("here")));

        Assert.Equal(["shortcut/", "shortcut/report.txt"], names);
    }

    // ---- extracting --------------------------------------------------------

    /// <summary>Builds an archive with exactly the entries asked for, including
    /// ones no honest writer would produce.</summary>
    private string Zip(string name, params (string Entry, string Content)[] entries)
        => ArchiveTestData.Zip(At(name), entries);

    private string[] Tree(string folder)
        => [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public void An_archive_unpacks_into_a_folder_named_after_it()
    {
        var archive = Zip("trip.zip", ("top.txt", "one"), ("day one/photo.txt", "two"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(At("trip"), done.Landed);
        Assert.False(done.IsFile);
        Assert.Equal(2, done.Files);
        Assert.Equal(0, done.LeftOut.Total);

        Assert.Equal("one", File.ReadAllText(At("trip", "top.txt")));
        Assert.Equal("two", File.ReadAllText(At("trip", "day one", "photo.txt")));
    }

    /// <summary>
    /// **An entry is free to call itself ..\..\somewhere.** The rule is not
    /// that the name looks harmless but that nothing it names can be outside
    /// the folder: a <c>..</c> segment refuses the entry (see ArchiveKeys),
    /// and what is left is counted as an unsafe name.
    /// </summary>
    [Fact]
    public void An_entry_that_climbs_out_of_the_folder_is_refused_and_counted()
    {
        var archive = Zip("hostile.zip",
            ("innocent.txt", "fine"),
            ("../escaped.txt", "should never be written"),
            ("../../escaped-further.txt", "nor this"));

        var done = Archives.Extract(archive, Dir("into"));

        Assert.Equal(1, done.Files);
        Assert.Equal(2, done.LeftOut.Unsafe);

        Assert.True(File.Exists(Path.Combine(done.Landed, "innocent.txt")));
        Assert.False(File.Exists(At("into", "escaped.txt")));
        Assert.False(File.Exists(At("escaped.txt")));
    }

    /// <summary>
    /// **Every copy arrives; the first keeps its name.** A zip is free to hold
    /// two entries under one name. Measured before the planner: the second
    /// was written over the first and the count said two files had arrived.
    ///
    /// This test was <c>Two_entries_under_one_name_are_counted_as_the_one_file_they_leave</c>,
    /// which pinned "last one wins" — the maintainer's decision reversed that
    /// on purpose (nothing an archive holds is lost silently), so its
    /// expectation changed rather than its subject.
    /// </summary>
    [Fact]
    public void Two_entries_under_one_name_both_arrive_the_second_numbered()
    {
        var archive = Zip("twins.zip", ("notes.txt", "first"), ("notes.txt", "second"), ("other.txt", "x"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(3, done.Files);
        Assert.Equal(1, done.Renamed);

        Assert.Equal(["notes (2).txt", "notes.txt", "other.txt"], Tree(done.Landed));
        Assert.Equal("first", File.ReadAllText(Path.Combine(done.Landed, "notes.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(done.Landed, "notes (2).txt")));
    }

    [Fact]
    public void A_second_extraction_lands_in_a_numbered_folder()
    {
        var archive = Zip("trip.zip", ("top.txt", "one"), ("two.txt", "two"));

        Archives.Extract(archive, _root);

        Assert.Equal(At("trip (2)"), Archives.Extract(archive, _root).Landed);
    }

    /// <summary>
    /// **A folder holding half an archive looks like one holding all of it.**
    /// And it is refused in words rather than in the runtime's: measured before
    /// the sentence existed, "End of Central Directory record could not be
    /// found." reached the status bar verbatim.
    /// </summary>
    [Fact]
    public void An_extraction_that_fails_leaves_no_folder_behind()
    {
        var notAnArchive = Write("trip.zip", "this is not a zip at all");

        var refused = Assert.ThrowsAny<InvalidDataException>(
            () => Archives.Extract(notAnArchive, _root));

        Assert.Equal("trip.zip is not a zip file, or is damaged", refused.Message);

        Assert.Equal(["trip.zip"], Tree(_root));
    }

    /// <summary>
    /// The same rule from the other side: an archive that fails PARTWAY, once
    /// files are already on disk, leaves nothing behind either — not the
    /// landing folder and not the working folder it was being written in.
    ///
    /// Staged with a zip whose last entry's bytes do not match its CRC, so the
    /// first entries are written whole before the failure. (It used to be
    /// staged with a folder/file clash, which is now numbered rather than
    /// failed.)
    /// </summary>
    [Fact]
    public void An_extraction_that_fails_partway_leaves_no_folder_behind()
    {
        File.WriteAllBytes(At("odd.zip"), ZipBytes.Build(
            new ZipBytes.Entry("first.txt") { Data = "fine"u8.ToArray() },
            new ZipBytes.Entry("second.txt") { Data = "fine too"u8.ToArray() },
            new ZipBytes.Entry("bad.txt") { Data = "not what the CRC says"u8.ToArray(), Crc = 0xDEADBEEF }));

        var failed = Assert.Throws<ArchiveDamagedException>(() => Archives.Extract(At("odd.zip"), _root));

        Assert.Equal(2, failed.EntriesBefore);
        Assert.Equal(["odd.zip"], Tree(_root));
    }

    /// <summary>An archive and the folder it unpacks to make a round trip
    /// without losing the shape in between — including a folder with nothing in
    /// it — and WITHOUT a second wrapping: a zip of <c>trip</c> holds only
    /// <c>trip/</c>, so it lands as <c>back\trip</c>, not
    /// <c>back\trip\trip</c>.</summary>
    [Fact]
    public void A_folder_survives_being_compressed_and_extracted_again()
    {
        Write("trip/top.txt", "one");
        Write("trip/day one/photo.txt", "two");
        Dir("trip/nothing here");

        var made = Archives.Compress([At("trip")], _root);
        var done = Archives.Extract(made, Dir("back"));

        Assert.Equal(At("back", "trip"), done.Landed);

        Assert.Equal("one", File.ReadAllText(At("back", "trip", "top.txt")));
        Assert.Equal("two", File.ReadAllText(At("back", "trip", "day one", "photo.txt")));

        Assert.True(Directory.Exists(At("back", "trip", "nothing here")));
    }

    // ---- no double wrap -----------------------------------------------------

    [Fact]
    public void An_archive_holding_one_folder_extracts_to_that_folder_not_inside_another()
    {
        var archive = Zip("download.zip", ("project/", ""), ("project/readme.txt", "hi"), ("project/src/a.c", "int"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(At("project"), done.Landed);
        Assert.Equal(["readme.txt", "src", "src/a.c"], Tree(done.Landed));
        Assert.Equal(["download.zip", "project", "project/readme.txt", "project/src", "project/src/a.c"], Tree(_root));
    }

    [Fact]
    public void One_folder_whose_name_is_taken_lands_numbered()
    {
        Write("project/mine.txt", "already here");

        var archive = Zip("download.zip", ("project/readme.txt", "hi"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(At("project (2)"), done.Landed);
        Assert.Equal("already here", File.ReadAllText(At("project", "mine.txt")));
        Assert.Equal(["mine.txt"], Tree(At("project")));
    }

    [Fact]
    public void Loose_files_go_into_a_folder_named_after_the_archive()
    {
        var archive = Zip("bundle.zip", ("a.txt", "a"), ("b.txt", "b"));

        Assert.Equal(At("bundle"), Archives.Extract(archive, _root).Landed);
        Assert.Equal(["a.txt", "b.txt"], Tree(At("bundle")));
    }

    /// <summary>A zip is not a compressed FILE even when it holds one: the
    /// single file still gets a folder, so an archive never scatters anything
    /// loose into the destination.</summary>
    [Fact]
    public void A_single_top_level_file_goes_into_a_folder_named_after_the_archive()
    {
        var archive = Zip("notes.zip", ("notes.txt", "hello"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(At("notes"), done.Landed);
        Assert.False(done.IsFile);
        Assert.Equal("hello", File.ReadAllText(At("notes", "notes.txt")));
    }

    /// <summary>A Mac zips <c>__MACOSX/</c> beside the folder it was asked to
    /// zip; left out, it would make every such zip "two things at the top" and
    /// wrap the one folder in another.</summary>
    [Fact]
    public void Mac_metadata_beside_the_one_folder_does_not_wrap_it()
    {
        var archive = Zip("photos.zip",
            ("photos/a.jpg", "jpeg"),
            ("__MACOSX/photos/._a.jpg", "resource fork"),
            ("photos/._b.jpg", "resource fork"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(At("photos"), done.Landed);
        Assert.Equal(["a.jpg"], Tree(done.Landed));
        Assert.Equal(2, done.LeftOut.MacMetadata);
    }

    /// <summary>
    /// **A compressed single file is the file.** <c>report.txt.gz</c> lands
    /// as <c>report.txt</c> beside it, numbered when that is taken — named by
    /// <see cref="ArchiveFormats.Stem"/>, which knows the whole suffix, and
    /// not by the verb's own stem, which would have taken the last extension
    /// off <c>report.txt</c> as well.
    /// </summary>
    [Fact]
    public void A_compressed_single_file_lands_beside_the_archive_numbered()
    {
        Write("report.txt", "already here");

        var archive = ArchiveTestData.Bare(At("report.txt.gz"), ArchiveFormat.Gz, "the report"u8.ToArray());

        var done = Archives.Extract(archive, _root);

        Assert.True(done.IsFile);
        Assert.Equal(At("report (2).txt"), done.Landed);
        Assert.Equal("the report", File.ReadAllText(done.Landed));
        Assert.Equal("already here", File.ReadAllText(At("report.txt")));
        Assert.Equal(["report (2).txt", "report.txt", "report.txt.gz"], Tree(_root));
    }

    [Fact]
    public void A_gz_holding_a_tar_is_an_archive()
    {
        var archive = ArchiveTestData.Tar(At("src.gz"), tar =>
        {
            tar.WriteEntry(ArchiveTestData.File_("one.txt", "1"));
            tar.WriteEntry(ArchiveTestData.File_("two.txt", "2"));
        }, ArchiveTestData.Compressor(ArchiveFormat.Gz));

        var done = Archives.Extract(archive, _root);

        Assert.False(done.IsFile);
        Assert.Equal(At("src"), done.Landed);
        Assert.Equal(["one.txt", "two.txt"], Tree(done.Landed));
    }

    // ---- every format -------------------------------------------------------

    [Theory]
    [InlineData("holiday.zip", true)]
    [InlineData("HOLIDAY.ZIP", true)]
    [InlineData("holiday.tar", true)]
    [InlineData("holiday.tar.gz", true)]
    [InlineData("holiday.TGZ", true)]
    [InlineData("notes.txt.gz", true)]
    [InlineData("holiday.7z", false)]
    [InlineData("holiday.rar", false)]
    [InlineData("holiday.tar.bz2", false)]
    [InlineData("holiday.tar.xz", false)]
    [InlineData("holiday.tar.zst", false)]
    [InlineData("holiday.tar.lz", false)]
    [InlineData("notes.txt.xz", false)]
    [InlineData("holiday.docx", false)]
    [InlineData("holiday.jar", false)]
    [InlineData("holiday", false)]
    [InlineData("zip", false)]
    public void Every_browsable_format_can_be_extracted(string name, bool offered)
        => Assert.Equal(offered, Archives.CanExtract(name));

    [Theory]
    [InlineData("zip-deflate64.zip")]
    [InlineData("zip-bzip2.zip")]
    [InlineData("zip-lzma.zip")]
    [InlineData("zip-ppmd.zip")]
    [InlineData("tree.tar.gz")]
    public void Each_format_extracts(string fixture)
    {
        var done = Archives.Extract(ArchiveTestData.Fixture(fixture), _root);

        Assert.Equal(At(ArchiveFormats.Stem(fixture)), done.Landed);
        Assert.Equal(["docs", "docs/a.txt", "docs/b.bin", "docs/c.txt", "empty", "readme.txt"], Tree(done.Landed));
        Assert.Equal("Vaktari archive fixture\n", File.ReadAllText(Path.Combine(done.Landed, "readme.txt")));
    }

    [Theory]
    [InlineData("zip-zstd.zip")]
    [InlineData("zip-xz.zip")]
    public void Each_vendored_format_extracts(string fixture)
    {
        var done = Archives.Extract(ArchiveTestData.Fixture(fixture), _root);

        Assert.Contains("тест.txt", Tree(done.Landed));
        Assert.Equal(45056, new FileInfo(Path.Combine(done.Landed, "exe", "test.exe")).Length);
    }

    [Theory]
    [InlineData("zip-zipcrypto.zip")]
    [InlineData("zip-aes256.zip")]
    public void A_password_protected_archive_is_refused_in_words(string fixture)
    {
        var copy = At(fixture);

        File.Copy(ArchiveTestData.Fixture(fixture), copy);

        var refused = Assert.Throws<ArchivePasswordRequiredException>(() => Archives.Extract(copy, _root));

        Assert.Equal($"{fixture} is password-protected — Vaktari cannot extract it yet", refused.Message);
        Assert.Equal([fixture], Tree(_root));
    }

    [Theory]
    [InlineData("photos.part1.rar")]
    [InlineData("photos.part02.rar")]
    [InlineData("photos.r00")]
    [InlineData("photos.7z.001")]
    [InlineData("photos.zip.001")]
    [InlineData("photos.z01")]
    public void A_split_volume_is_refused_in_words(string name)
    {
        var part = Write(name, "a part");

        var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(part, _root));

        Assert.Equal($"{name} is one part of a split archive — Vaktari cannot extract split archives", refused.Message);
    }

    [Fact]
    public void A_sparse_tar_is_refused_in_words()
    {
        var copy = At("sparse-gnu.tar");

        File.Copy(ArchiveTestData.Fixture("sparse-gnu.tar"), copy);

        var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(copy, _root));

        Assert.Equal("sparse-gnu.tar holds a sparse file, which Vaktari cannot extract — nothing was extracted", refused.Message);
        Assert.Equal(["sparse-gnu.tar"], Tree(_root));
    }

    /// <summary>
    /// **The non-recursive zip bomb**: fifty central-directory records, every
    /// one pointing at the same compressed stream. Each would inflate to the
    /// whole of it; refused before anything is created.
    /// </summary>
    [Fact]
    public void Overlapping_entries_are_refused()
    {
        var zeros = new byte[64 * 1024];
        var entries = new List<ZipBytes.Entry> { new("0.bin") { Data = zeros, Method = 8 } };

        for (var i = 1; i < 50; i++)
            entries.Add(new ZipBytes.Entry($"{i}.bin") { Data = zeros, Method = 8, SharesWith = 0, LocalName = "0.bin"u8.ToArray() });

        File.WriteAllBytes(At("bomb.zip"), ZipBytes.Build([.. entries]));

        var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(At("bomb.zip"), _root));

        Assert.Equal("bomb.zip is built to unpack to far more than it holds — nothing was extracted", refused.Message);
        Assert.Equal(["bomb.zip"], Tree(_root));
    }

    [Fact]
    public void More_entries_than_the_cap_are_refused()
    {
        var entries = Enumerable.Range(0, 11).Select(i => ($"{i}.txt", "x")).ToArray();
        var archive = Zip("many.zip", entries);

        var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(
            archive, _root, null, default, ArchiveRoom.Real, observer: null, maxEntries: 10));

        Assert.Equal("many.zip holds more than 10 entries — Vaktari does not extract archives that large", refused.Message);
        Assert.Equal(["many.zip"], Tree(_root));
    }

    /// <summary>A tar declares no count, so it is stopped at the first entry
    /// over the cap, as a stream-level failure, and discarded.</summary>
    [Fact]
    public void A_tar_with_more_entries_than_the_cap_stops_and_leaves_nothing()
    {
        var archive = ArchiveTestData.Tar(At("many.tar"), tar =>
        {
            for (var i = 0; i < 11; i++) tar.WriteEntry(ArchiveTestData.File_($"{i}.txt", "x"));
        });

        Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(
            archive, _root, null, default, ArchiveRoom.Real, observer: null, maxEntries: 10));

        Assert.Equal(["many.tar"], Tree(_root));
    }

    /// <summary>
    /// **A format Vaktari does not extract is refused for what it is, by its
    /// bytes, whatever it is called** (review H2). Before the narrowing these
    /// extracted as what they really were; after it, without the signatures,
    /// a 7z named .zip went to the zip reader and an xz tar named .tar.gz was
    /// called "not a gzip file, or damaged" — an intact file, called broken.
    /// One row per signature, each under a name Extract all does offer. The
    /// first bytes are the format's own; what follows does not matter, since
    /// nothing past the signature is read.
    /// </summary>
    [Theory]
    [InlineData("misnamed.zip", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, "a 7z")]
    [InlineData("misnamed.tar.gz", new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, "an xz")]
    [InlineData("misnamed.gz", new byte[] { 0x42, 0x5A, 0x68, 0x39 }, "a bzip2")]
    [InlineData("misnamed.tar", new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "a zstd")]
    [InlineData("misnamed2.zip", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "a RAR")]
    [InlineData("misnamed2.gz", new byte[] { 0x4C, 0x5A, 0x49, 0x50, 0x01 }, "an lzip")]
    [InlineData("misnamed3.zip", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "a RAR")]
    public void Another_format_under_a_name_vaktari_extracts_is_refused_for_what_it_is(string name, byte[] head, string what)
    {
        File.WriteAllBytes(At(name), [.. head, .. new byte[4096]]);

        var refused = Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(At(name), _root));

        Assert.Equal(
            $"{name} is {what} archive. Vaktari extracts zip and tar.gz archives only — open this one with another app",
            refused.Message);
        Assert.Equal([name], Tree(_root));
    }

    /// <summary>The control: bytes that are no format at all, named .zip,
    /// are still "not a zip file", not "another format".</summary>
    [Fact]
    public void A_file_named_zip_that_is_no_archive_at_all_is_not_a_zip()
    {
        File.WriteAllText(At("page.zip"), "<html>404</html>");

        var refused = Assert.Throws<InvalidDataException>(() => Archives.Extract(At("page.zip"), _root));

        Assert.Equal("page.zip is not a zip file, or is damaged", refused.Message);
    }
}
