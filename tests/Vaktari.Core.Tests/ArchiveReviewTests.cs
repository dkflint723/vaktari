using System.Formats.Tar;
using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What the adversarial review of Stage A found, one test per finding, each
/// staged the way the review's probe staged it.
/// </summary>
public sealed class ArchiveReviewTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-review").FullName;

    public void Dispose()
    {
        // Not Directory.Delete: a revert-check that lets a deep tree be made
        // must fail its test, not crash the host in the clean-up.
        try { Archives.DeleteTree(_root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Dir(string name) => Directory.CreateDirectory(At(name)).FullName;

    private static string[] Tree(string folder)
        => [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)];

    private static Archives.Extraction Extract(
        string archive, string into, ArchiveRoom? room = null, IExtractionObserver? hooks = null, OperationHandle? handle = null)
        => Archives.Extract(archive, into, handle, default, room ?? ArchiveRoom.Real, hooks);

    private sealed class Hooks : IExtractionObserver
    {
        public Action<string>? Landing { get; init; }
        public Action<string>? Before { get; init; }

        public void BeforeCreate(string path) => Before?.Invoke(path);
        public void WhileWriting(string temporary, string final) { }
        public void BeforeLanding(string target) => Landing?.Invoke(target);
    }

    private string Zip(string name, params ZipBytes.Entry[] entries)
    {
        File.WriteAllBytes(At(name), ZipBytes.Build(entries));
        return At(name);
    }

    // ---- 1. deep nesting ----------------------------------------------------

    /// <summary>
    /// **Ten thousand folders deep in a few kilobytes.** Eight thousand
    /// overflowed the stack and killed the process; this is counted and the
    /// rest of the archive extracts, quickly.
    /// </summary>
    [Fact]
    public void An_entry_ten_thousand_folders_deep_is_counted_and_the_rest_extracts()
    {
        var deep = string.Concat(Enumerable.Repeat("a/", 10_000)) + "f.txt";
        var archive = Zip("deep.zip",
            new ZipBytes.Entry(deep) { Data = "x"u8.ToArray() },
            new ZipBytes.Entry("ok.txt") { Data = "fine"u8.ToArray() });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var done = Extract(archive, Dir("out"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");
        Assert.Equal(1, done.LeftOut.Unwritable);
        Assert.Equal(["ok.txt"], Tree(done.Landed));
        Assert.Equal(["deep", "deep/ok.txt"], Tree(At("out")));
    }

    /// <summary>
    /// **Deleting a deep tree must not recurse either.** The runtime's own
    /// recursive delete overflowed the stack on a tree ten thousand levels
    /// deep; the discard and the sweep use an iterative one. Ten thousand
    /// levels take minutes to make and remove on Windows, so this measures it
    /// cheaply: 1,500 levels, deleted with all but a sliver of a 256 KiB
    /// stack already used — room an iterative delete fits in and the
    /// runtime's recursion does not. Windows only, because Linux will not
    /// make a path that long in the first place.
    /// </summary>
    [WindowsFact]
    public void A_tree_thousands_of_levels_deep_is_deleted_without_recursion()
    {
        // An abandoned working folder, 1,500 levels deep, swept away the way
        // any discard removes one.
        var into = Dir("out");
        var top = Path.Combine(into, ".vaktari-extracting-deepdeepdeep");
        var path = Path.Combine([top, .. Enumerable.Repeat("d", 1_500)]);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "f.txt"), "bottom");
        Directory.SetLastWriteTimeUtc(top, DateTime.UtcNow.AddHours(-2));

        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try { OnAShortStack(() => Archives.Sweep(into)); }
            catch (Exception e) { failed = e; }
        }, maxStackSize: 256 * 1024);

        thread.Start();
        thread.Join();

        Assert.Null(failed);
        Assert.False(Directory.Exists(top));
    }

    /// <summary>Runs <paramref name="then"/> with 200 KB of the stack
    /// already taken.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void OnAShortStack(Action then)
    {
        Span<byte> taken = stackalloc byte[200 * 1024];

        taken[^1] = 1;
        then();
        GC.KeepAlive(taken[0]);
    }

    /// <summary>
    /// A discard never goes through a link. A folder swapped for a link to
    /// somewhere else during a run that then fails is removed as a link;
    /// what it pointed at is untouched.
    /// </summary>
    [Fact]
    public void Discarding_a_failed_run_does_not_follow_a_link_inside_it()
    {
        var outside = Dir("outside");

        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not the archive's");

        var archive = Zip("fails.zip",
            new ZipBytes.Entry("sub/a.txt") { Data = "a"u8.ToArray() },
            new ZipBytes.Entry("sub/b.txt") { Data = "b"u8.ToArray() },
            new ZipBytes.Entry("z.txt") { Data = "wrong"u8.ToArray(), Crc = 0xDEADBEEF });

        Assert.Throws<ArchiveDamagedException>(() => Extract(archive, Dir("out"), hooks: new Hooks
        {
            Before = path =>
            {
                if (!path.EndsWith("b.txt", StringComparison.Ordinal)) return;

                var sub = Path.GetDirectoryName(path)!;

                Directory.Move(sub, sub + "-moved");
                TestLinks.FolderLink(sub, outside);
            },
        }));

        Assert.Equal("not the archive's", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>Too long a path, however few folders: nothing of it is
    /// created, not the folders it would have reached before failing.</summary>
    [Fact]
    public void An_entry_whose_path_is_too_long_creates_nothing()
    {
        var segment = new string('s', 250);
        var longPath = string.Join('/', Enumerable.Repeat(segment, 140)) + "/f.txt";
        var archive = Zip("long.zip",
            new ZipBytes.Entry(longPath) { Data = "x"u8.ToArray() },
            new ZipBytes.Entry("ok.txt") { Data = "fine"u8.ToArray() });

        var done = Extract(archive, Dir("out"));

        Assert.Equal(1, done.LeftOut.Unwritable);
        Assert.Equal(["ok.txt"], Tree(done.Landed));
    }

    /// <summary>Deep, but not absurdly: made by the loop, not the stack.</summary>
    [Fact]
    public void An_entry_three_hundred_folders_deep_extracts()
    {
        var deep = string.Concat(Enumerable.Repeat("d/", 300)) + "f.txt";
        var archive = Zip("deep.zip",
            new ZipBytes.Entry(deep) { Data = "deep"u8.ToArray() },
            new ZipBytes.Entry("ok.txt") { Data = "fine"u8.ToArray() });

        var done = Extract(archive, Dir("out"));

        Assert.Equal(2, done.Files);
        Assert.Equal("deep", File.ReadAllText(Path.Combine([done.Landed, .. Enumerable.Repeat("d", 300), "f.txt"])));
    }

    // ---- 2. room while a tar is written ----------------------------------

    /// <summary>
    /// **Every tar entry declares its size, and the running floor ran only
    /// for entries that did not.** 70 MiB of zeros in a tar.gz, with the
    /// disk falling to 100 MiB free once writing starts: stopped, and
    /// nothing left.
    /// </summary>
    [Fact]
    public void The_running_floor_holds_for_a_tar()
    {
        var archive = ArchiveTestData.Tar(At("zeros.tar.gz"), t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "zeros.bin")
        {
            DataStream = new MemoryStream(new byte[70 * MiB]),
        }), ArchiveTestData.Compressor(ArchiveFormat.TarGz));

        // Up front: plenty. Before the entry: plenty. While writing: not.
        var asked = 0;
        var room = new ArchiveRoom(_ => ++asked <= 2 ? 10L << 30 : 100 * MiB, _ => null);

        var stopped = Assert.Throws<ArchiveRefusedException>(() => Extract(archive, Dir("out"), room));

        Assert.StartsWith("stopped before zeros.tar.gz filled ", stopped.Message);
        Assert.True(asked > 2, "the floor was never checked while writing");
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>A tar entry whose own declared size would leave less than
    /// the floor is refused before a byte of it is written.</summary>
    [Fact]
    public void A_tar_entry_that_would_leave_less_than_the_floor_is_refused_up_front()
    {
        var archive = ArchiveTestData.Tar(At("big.tar.gz"), t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "big.bin")
        {
            DataStream = new MemoryStream(new byte[8 * MiB]),
        }), ArchiveTestData.Compressor(ArchiveFormat.TarGz));

        var room = new ArchiveRoom(_ => 200 * MiB, _ => null);

        Assert.Throws<ArchiveRefusedException>(() => Extract(archive, Dir("out"), room));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>
    /// **Hard links multiplied the output unchecked**: a 1.6 MB tar holding a
    /// 1 MB file and 400 links to it wrote 420 MB. Each copy is counted
    /// against the room like any other file.
    /// </summary>
    [Fact]
    public void Hard_link_copies_are_counted_against_the_room()
    {
        var archive = ArchiveTestData.Tar(At("links.tar"), t =>
        {
            t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "one.bin") { DataStream = new MemoryStream(new byte[MiB]) });

            for (var i = 0; i < 400; i++) t.WriteEntry(ArchiveTestData.Link_(TarEntryType.HardLink, $"copy{i}.bin", "one.bin"));
        });

        var room = new ArchiveRoom(_ => 330 * MiB - Written(At("out")), _ => null);

        Assert.Throws<ArchiveRefusedException>(() => Extract(archive, Dir("out"), room));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    private static long Written(string folder)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

    // ---- 3. times Windows cannot hold ------------------------------------

    [Fact]
    public void A_time_before_1601_is_left_alone_rather_than_failing_the_run()
    {
        var archive = ArchiveTestData.Tar(At("old.tar"), t =>
        {
            var e = ArchiveTestData.File_("ancient.txt", "from before");
            e.ModificationTime = DateTimeOffset.FromUnixTimeSeconds(-12_000_000_000);
            t.WriteEntry(e);
            t.WriteEntry(ArchiveTestData.File_("recent.txt", "now"));
        });

        var done = Extract(archive, Dir("out"));

        Assert.Equal(2, done.Files);
        Assert.Equal("from before", File.ReadAllText(Path.Combine(done.Landed, "ancient.txt")));
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(done.Landed, "ancient.txt")).Year >= 1601);
    }

    // ---- 4. concatenated streams -------------------------------------------

    [Fact]
    public void Every_stream_of_a_concatenated_xz_arrives()
    {
        var done = Extract(ArchiveTestData.Fixture("concat.txt.xz"), Dir("out"));

        var text = File.ReadAllText(done.Landed);

        Assert.StartsWith("first stream line 0001", text);
        Assert.EndsWith("second stream line 0300\n", text);
    }

    /// <summary>Null padding between streams, in fours, is part of the
    /// format; the oracle's own decoder stops at it, so it is pinned here.</summary>
    [Fact]
    public void Padding_between_xz_streams_is_skipped()
    {
        var one = File.ReadAllBytes(ArchiveTestData.Fixture("bare.txt.xz"));

        File.WriteAllBytes(At("twice.txt.xz"), [.. one, 0, 0, 0, 0, .. one]);

        var done = Extract(At("twice.txt.xz"), Dir("out"));

        Assert.Equal("Vaktari archive fixture\nVaktari archive fixture\n", File.ReadAllText(done.Landed));
    }

    [Fact]
    public void Bytes_after_an_xz_stream_that_are_not_another_are_damage()
    {
        var one = File.ReadAllBytes(ArchiveTestData.Fixture("bare.txt.xz"));

        File.WriteAllBytes(At("junk.txt.xz"), [.. one, .. "not xz at all"u8.ToArray()]);

        Assert.Throws<ArchiveDamagedException>(() => Extract(At("junk.txt.xz"), Dir("out")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    [Fact]
    public void Every_member_of_a_multi_member_lzip_arrives()
    {
        var done = Extract(ArchiveTestData.Fixture("multi.txt.lz"), Dir("out"));

        var text = File.ReadAllText(done.Landed);

        Assert.Equal(16400, text.Length);
        Assert.EndsWith("second member line 0300\n", text);
    }

    [Fact]
    public void An_lzip_that_does_not_divide_into_members_is_damage()
    {
        var bytes = File.ReadAllBytes(ArchiveTestData.Fixture("multi.txt.lz"));

        // The last member's recorded size no longer reaches back to a header.
        BitConverter.GetBytes(40L).CopyTo(bytes, bytes.Length - 8);
        File.WriteAllBytes(At("cut.txt.lz"), bytes);

        Assert.Throws<ArchiveDamagedException>(() => Extract(At("cut.txt.lz"), Dir("out")));
    }

    // ---- 6 and 11. landing the result ---------------------------------------

    [Fact]
    public void A_top_folder_whose_name_is_as_long_as_it_can_be_is_numbered_within_the_limit()
    {
        var name = new string('x', 255);
        var archive = Zip("long.zip", new ZipBytes.Entry(name + "/f.txt") { Data = "x"u8.ToArray() });

        Extract(archive, Dir("out"));

        var second = Extract(archive, At("out"));

        Assert.EndsWith(" (2)", second.Landed);
        Assert.True(Path.GetFileName(second.Landed).Length <= 255);
    }

    [Fact]
    public void A_bare_file_whose_name_is_as_long_as_it_can_be_is_numbered_within_the_limit()
    {
        var archive = ArchiveTestData.Bare(At(new string('e', 252) + ".gz"), ArchiveFormat.Gz, "x"u8.ToArray());

        Extract(archive, Dir("out"));

        var second = Extract(archive, At("out"));

        Assert.EndsWith(" (2)", second.Landed);
        Assert.True(Path.GetFileName(second.Landed).Length <= 255);
    }

    /// <summary>
    /// Something takes the free name between looking and moving — a second
    /// Extract all of the same archive finishing first. The result lands at
    /// the next number rather than the whole run being thrown away.
    /// </summary>
    [Fact]
    public void A_name_taken_while_landing_is_passed_over()
    {
        var archive = ArchiveTestData.Zip(At("trip.zip"), ("a.txt", "a"), ("b.txt", "b"));
        var planted = false;

        var done = Extract(archive, Dir("out"), hooks: new Hooks
        {
            Landing = target =>
            {
                if (planted) return;

                planted = true;
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "theirs.txt"), "the other run's");
            },
        });

        Assert.Equal(At("out", "trip (2)"), done.Landed);
        Assert.Equal(["theirs.txt"], Tree(At("out", "trip")));
        Assert.Equal(["a.txt", "b.txt"], Tree(done.Landed));
    }

    // ---- 8. modes ----------------------------------------------------------

    [PosixFact]
    [SupportedOSPlatform("linux")]
    public void A_unix_zip_entry_with_no_mode_lands_readable()
    {
        var archive = Zip("modes.zip",
            new ZipBytes.Entry("zero.txt") { Data = "z"u8.ToArray(), MadeBy = 0x031E, External = 0x8000u << 16 },
            new ZipBytes.Entry("others.txt") { Data = "o"u8.ToArray(), MadeBy = 0x031E, External = 0x8024u << 16 });

        var done = Extract(archive, Dir("out"));

        Assert.Equal((UnixFileMode)0x1A4, File.GetUnixFileMode(Path.Combine(done.Landed, "zero.txt")));
        Assert.Equal((UnixFileMode)0x1A4, File.GetUnixFileMode(Path.Combine(done.Landed, "others.txt")));
        Assert.Equal("z", File.ReadAllText(Path.Combine(done.Landed, "zero.txt")));
    }

    // ---- 9. progress ---------------------------------------------------------

    /// <summary>A zip holding a folder, a link, Mac metadata and a name that
    /// climbs out ends with every item done and every byte counted.</summary>
    [Fact]
    public void Progress_ends_complete_for_a_zip_with_things_left_out()
    {
        var archive = Zip("mixed.zip",
            new ZipBytes.Entry("docs/") { },
            new ZipBytes.Entry("docs/a.txt") { Data = new byte[5000] },
            new ZipBytes.Entry("link") { Data = "docs/a.txt"u8.ToArray(), MadeBy = 0x031E, External = 0xA1FFu << 16 },
            new ZipBytes.Entry("__MACOSX/._a.txt") { Data = new byte[300] },
            new ZipBytes.Entry("../out.txt") { Data = new byte[70] });

        var last = Watch(archive);

        Assert.Equal(last.ItemsTotal, last.ItemsDone);
        Assert.Equal(4, last.ItemsTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
    }

    [Fact]
    public void Progress_for_a_tar_counts_its_items_as_it_meets_them_and_ends_complete()
    {
        var archive = ArchiveTestData.Fixture("tree.tar.gz");
        var seen = new List<OperationProgress>();
        var handle = new OperationHandle();

        handle.Progressed += (_, p) => seen.Add(p);

        Extract(archive, Dir("out"), handle: handle);

        Assert.DoesNotContain(seen, p => p.ItemsDone > p.ItemsTotal);
        Assert.Equal(4, seen[^1].ItemsDone);
        Assert.Equal(seen[^1].ItemsTotal, seen[^1].ItemsDone);
        Assert.Equal(seen[^1].BytesTotal, seen[^1].BytesDone);
    }

    private OperationProgress Watch(string archive)
    {
        var handle = new OperationHandle();
        var last = default(OperationProgress);

        handle.Progressed += (_, p) => last = p;

        Extract(archive, Dir("out"), handle: handle);

        return last;
    }

    // ---- 10. what a crash left behind ---------------------------------------

    [Fact]
    public void An_abandoned_working_folder_is_swept_by_the_next_extraction_into_the_same_place()
    {
        var into = Dir("out");
        var stale = Directory.CreateDirectory(Path.Combine(into, ".vaktari-extracting-0123456789ab")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(into, ".vaktari-extracting-fedcba987654")).FullName;

        File.WriteAllText(Path.Combine(stale, "half.txt"), "half an archive");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        Extract(ArchiveTestData.Zip(At("a.zip"), ("a.txt", "a"), ("b.txt", "b")), into);

        Assert.False(Directory.Exists(stale), "the abandoned folder is still there");
        Assert.True(Directory.Exists(fresh), "a folder changed a moment ago was taken for abandoned");
    }

    /// <summary>Held by its lock, it is somebody's run however old it looks.</summary>
    [Fact]
    public void A_working_folder_whose_lock_is_held_is_left_alone()
    {
        var into = Dir("out");
        var live = Directory.CreateDirectory(Path.Combine(into, ".vaktari-extracting-aaaaaaaaaaaa")).FullName;

        Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddHours(-2));

        using (new FileStream(live + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            Assert.Equal(0, Archives.Sweep(into));

        Assert.True(Directory.Exists(live));
    }

    [WindowsFact]
    public void What_lands_is_not_hidden_though_the_working_folder_was()
    {
        var done = Extract(ArchiveTestData.Zip(At("a.zip"), ("a.txt", "a"), ("b.txt", "b")), Dir("out"));

        Assert.Equal(0, (int)(File.GetAttributes(done.Landed) & FileAttributes.Hidden));
    }

    // ---- 12. the mark --------------------------------------------------------

    [WindowsFact]
    public void A_mark_too_large_to_copy_still_marks_everything_as_from_the_internet()
    {
        var archive = ArchiveTestData.Zip(At("web.zip"), ("a.txt", "a"), ("b.txt", "b"));

        File.WriteAllText(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n" + new string(';', 70 * 1024));

        var done = Extract(archive, Dir("out"));

        Assert.Equal(ZoneMarks.Internet, File.ReadAllText(Path.Combine(done.Landed, "a.txt") + ":Zone.Identifier"));
    }

    // ---- 14. zips that do not start with a local header --------------------

    [Theory]
    [InlineData("PK00")]
    [InlineData("64 bytes of something")]
    public void A_zip_with_bytes_before_it_extracts_as_the_runtime_reader_did(string prefix)
    {
        var path = At("marked.zip");

        using (var file = File.Create(path))
        {
            file.Write(prefix == "PK00" ? "PK00"u8.ToArray() : new byte[64]);

            using var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);

            foreach (var name in new[] { "a.txt", "b.txt" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(name);
            }
        }

        var done = Extract(path, Dir("out"));

        Assert.Equal(["a.txt", "b.txt"], Tree(done.Landed));
    }

    // ---- 15. the disk the archive is on ------------------------------------

    /// <summary>
    /// **An unplugged stick is not a damaged archive.** A read of the archive
    /// file itself failing part-way came out as "damaged after N entries";
    /// it is its own sentence now, and its own exception, which a copy loop
    /// cannot mistake for one entry's failure.
    /// </summary>
    [Fact]
    public void A_read_of_the_archive_file_failing_is_not_damage()
    {
        var path = ArchiveTestData.Zip(At("stick.zip"), ("a.txt", new string('a', 100_000)), ("b.txt", "b"));
        var faulty = new Faulty(File.OpenRead(path));

        using var pass = new ArchivePass(path, "stick.zip", ArchiveFormat.Zip,
            new CancellableCountingStream(faulty, CancellationToken.None, fillReads: true), CancellationToken.None);

        pass.Load();
        faulty.Armed = true;

        var failed = Assert.Throws<ArchiveUnreadableException>(() =>
        {
            foreach (var item in pass.Items())
            {
                using var data = item.OpenData();
                data.CopyTo(Stream.Null);
            }
        });

        Assert.Equal("stick.zip could not be read — nothing was extracted", failed.Message);
        Assert.Equal(unchecked((int)0x80070015), failed.HResult);
    }

    private sealed class Faulty(Stream inner) : Stream
    {
        public bool Armed { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
            => Armed ? throw new IOException("The device is not ready.", unchecked((int)0x80070015)) : inner.Read(buffer);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ---- 16 and 17. names, AE-2, a damaged 7z -------------------------------

    [Fact]
    public void A_tar_name_that_lost_bytes_counts_as_renamed()
    {
        var done = Extract(ArchiveTestData.Fixture("latin1-gnu.tar"), Dir("out"));

        Assert.Equal(1, done.Renamed);
    }

    /// <summary>AE-2 records CRC 0 by design, so our CRC must not be asked
    /// of it; AE-1 records a real one, so it must.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Only_an_ae1_entry_is_held_to_its_CRC(ushort version, bool held)
    {
        var path = Zip("aes.zip", new ZipBytes.Entry("s.txt") { Data = "x"u8.ToArray(), Flags = 1, Method = 99, Extra = ZipBytes.AesExtra(version) });

        using var pass = ArchiveReader.Open(path, CancellationToken.None);

        Assert.Equal(held, pass.Items().Single().Info.CrcIsOurs);
    }

    [Fact]
    public void A_7z_with_a_damaged_byte_is_refused_and_leaves_nothing()
    {
        var bytes = File.ReadAllBytes(ArchiveTestData.Fixture("7z-ppmd.7z"));

        bytes[200] ^= 0x55;
        File.WriteAllBytes(At("hurt.7z"), bytes);

        Assert.ThrowsAny<IOException>(() => Extract(At("hurt.7z"), Dir("out")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }
}
