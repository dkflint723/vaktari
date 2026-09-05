using System.Collections;
using System.Diagnostics;
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

    [Theory]
    [InlineData("holiday.zip", true)]
    [InlineData("HOLIDAY.ZIP", true)]
    [InlineData("holiday.7z", false)]
    [InlineData("holiday.tar.gz", false)]
    [InlineData("holiday.rar", false)]
    [InlineData("holiday", false)]
    [InlineData("zip", false)]
    public void Only_a_zip_can_be_extracted(string name, bool offered)
        => Assert.Equal(offered, Archives.CanExtract(name));

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

        Junction(At("trip", "shortcut"), At("elsewhere"));

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

        Junction(At("here", "shortcut"), At("elsewhere"));

        var names = NamesIn(Archives.Compress([At("here", "shortcut")], At("here")));

        Assert.Equal(["shortcut/", "shortcut/report.txt"], names);
    }

    /// <summary>
    /// Made by the platform's own tool rather than by anything under test —
    /// setup sharing an implementation with its subject passes just as happily
    /// when both are wrong. The same reason, and the same command, as the
    /// Windows suite's TempTree.
    /// </summary>
    private static void Junction(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        mklink.WaitForExit();

        if (!Directory.Exists(path))
            throw new InvalidOperationException(
                $"could not make a junction at '{path}': {mklink.StandardError.ReadToEnd().Trim()}");
    }

    // ---- extracting --------------------------------------------------------

    /// <summary>Builds an archive with exactly the entries asked for, including
    /// ones no honest writer would produce.</summary>
    private string Zip(string name, params (string Entry, string Content)[] entries)
    {
        var path = At(name);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    [Fact]
    public void An_archive_unpacks_into_a_folder_named_after_it()
    {
        var archive = Zip("trip.zip", ("top.txt", "one"), ("day one/photo.txt", "two"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(At("trip"), done.Folder);
        Assert.Equal(2, done.Files);
        Assert.Equal(0, done.Refused);

        Assert.Equal("one", File.ReadAllText(At("trip", "top.txt")));
        Assert.Equal("two", File.ReadAllText(At("trip", "day one", "photo.txt")));
    }

    /// <summary>
    /// **An entry is free to call itself ..\..\somewhere.** The rule is not
    /// that the name looks harmless but that the resolved path is genuinely
    /// underneath the folder being unpacked into.
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
        Assert.Equal(2, done.Refused);

        Assert.True(File.Exists(Path.Combine(done.Folder, "innocent.txt")));
        Assert.False(File.Exists(At("into", "escaped.txt")));
        Assert.False(File.Exists(At("escaped.txt")));
    }

    /// <summary>
    /// **The count is of files that came out, not of entries that went past.**
    /// A zip is free to hold two entries under one name, and the second lands
    /// on the first — the folder is made fresh at a free name a moment earlier,
    /// so nothing else can be at the path. Measured before the check: this
    /// archive reported two files into a folder holding one.
    /// </summary>
    [Fact]
    public void Two_entries_under_one_name_are_counted_as_the_one_file_they_leave()
    {
        var archive = Zip("twins.zip", ("notes.txt", "first"), ("notes.txt", "second"));

        var done = Archives.Extract(archive, _root);

        Assert.Equal(1, done.Files);

        Assert.Equal(
            ["notes.txt"],
            Directory.EnumerateFileSystemEntries(done.Folder)
                .Select(Path.GetFileName).OfType<string>().ToArray());

        Assert.Equal("second", File.ReadAllText(Path.Combine(done.Folder, "notes.txt")));
    }

    [Fact]
    public void A_second_extraction_lands_in_a_numbered_folder()
    {
        var archive = Zip("trip.zip", ("top.txt", "one"));

        Archives.Extract(archive, _root);

        Assert.Equal(At("trip (2)"), Archives.Extract(archive, _root).Folder);
    }

    /// <summary>
    /// **A folder holding half an archive looks like one holding all of it.**
    /// The folder is this class's own, made a moment earlier at a free name, so
    /// removing it cannot take anything that was already there.
    ///
    /// **And it is refused in words rather than in the runtime's.** The menu
    /// row decides by extension, so a file named .zip that is not one is the
    /// failure this verb meets most often — a download that arrived as an error
    /// page, a renamed .rar, a file that stopped halfway. Measured before the
    /// sentence existed: the message was "End of Central Directory record could
    /// not be found.", which reached the status bar verbatim.
    /// </summary>
    [Fact]
    public void An_extraction_that_fails_leaves_no_folder_behind()
    {
        var notAnArchive = Write("trip.zip", "this is not a zip at all");

        var refused = Assert.ThrowsAny<InvalidDataException>(
            () => Archives.Extract(notAnArchive, _root));

        Assert.Equal("trip.zip is not a zip file, or is damaged", refused.Message);

        Assert.False(Directory.Exists(At("trip")));
    }

    /// <summary>
    /// The same rule from the other side: an archive that fails PARTWAY, once
    /// files are already on disk, leaves nothing behind either.
    ///
    /// Staged with an archive naming one thing twice, once as a folder and once
    /// as a file — the folder is made first and writing the file onto it is
    /// refused by the filesystem. A separate case from the one above because
    /// the failure arrives after the archive has been opened and read, which is
    /// the catch that clears up after everything except an unreadable zip.
    /// </summary>
    [Fact]
    public void An_extraction_that_fails_partway_leaves_no_folder_behind()
    {
        var archive = Zip("odd.zip", ("clash/", ""), ("clash", "onto its own folder"));

        Assert.ThrowsAny<Exception>(() => Archives.Extract(archive, _root));

        Assert.False(Directory.Exists(At("odd")));
    }

    /// <summary>An archive and the folder it unpacks to make a round trip
    /// without losing the shape in between — including a folder with nothing in
    /// it, which survives only because both ends handle an entry that is a name
    /// ending in a separator and holds no bytes.</summary>
    [Fact]
    public void A_folder_survives_being_compressed_and_extracted_again()
    {
        Write("trip/top.txt", "one");
        Write("trip/day one/photo.txt", "two");
        Dir("trip/nothing here");

        var made = Archives.Compress([At("trip")], _root);
        var done = Archives.Extract(made, Dir("back"));

        Assert.Equal("one", File.ReadAllText(Path.Combine(done.Folder, "trip", "top.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(done.Folder, "trip", "day one", "photo.txt")));

        Assert.True(Directory.Exists(Path.Combine(done.Folder, "trip", "nothing here")));
    }
}
