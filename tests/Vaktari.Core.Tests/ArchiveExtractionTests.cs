using System.Formats.Tar;
using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The one writer, through the observer seam: things planted, thrown and
/// refused exactly where a real disk would do it, and what lands afterwards.
/// </summary>
public sealed class ArchiveExtractionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-extraction").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Dir(string name) => Directory.CreateDirectory(At(name)).FullName;

    private sealed class Hooks : IExtractionObserver
    {
        public Action<string>? Before { get; init; }
        public Action<string, string>? While { get; init; }

        public void BeforeCreate(string path) => Before?.Invoke(path);
        public void WhileWriting(string temporary, string final) => While?.Invoke(temporary, final);

        public Action<string>? Landing { get; init; }

        public void BeforeLanding(string target) => Landing?.Invoke(target);
    }

    private static Archives.Extraction Extract(
        string archive, string into, IExtractionObserver? hooks = null, ArchiveRoom? room = null, OperationHandle? handle = null)
        => Archives.Extract(archive, into, handle, default, room ?? ArchiveRoom.Real, hooks);

    private static string[] Tree(string folder)
        => [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)];

    // ---- never over anything, never at the final name ----------------------

    /// <summary>
    /// Something appears at an entry's name while it is being written; it is
    /// kept, and the entry is numbered beside it.
    /// </summary>
    [Fact]
    public void Nothing_already_at_a_name_is_written_over()
    {
        var archive = ArchiveTestData.Zip(At("a.zip"), ("a.txt", "from the archive"), ("b.txt", "b"));

        var done = Extract(archive, Dir("out"), new Hooks
        {
            While = (_, final) =>
            {
                if (final.EndsWith("a.txt", StringComparison.Ordinal)) File.WriteAllText(final, "planted");
            },
        });

        Assert.Equal("planted", File.ReadAllText(Path.Combine(done.Landed, "a.txt")));
        Assert.Equal("from the archive", File.ReadAllText(Path.Combine(done.Landed, "a (2).txt")));
        Assert.Equal(1, done.Renamed);
    }

    [Fact]
    public void A_file_is_never_at_its_final_name_while_it_is_written()
    {
        var archive = ArchiveTestData.Zip(At("a.zip"), ("a.txt", "x"), ("b.txt", "y"));
        var seen = new List<(bool Temporary, bool Final)>();

        Extract(archive, Dir("out"), new Hooks { While = (t, f) => seen.Add((File.Exists(t), File.Exists(f))) });

        Assert.Equal([(true, false), (true, false)], seen);
    }

    // ---- links on the way down ----------------------------------------------

    /// <summary>
    /// A folder on the way is swapped for a link between planning and
    /// writing; nothing is written through it.
    /// </summary>
    [Fact]
    public void A_link_planted_at_a_parent_is_never_written_through()
    {
        var outside = Dir("outside");
        var archive = ArchiveTestData.Zip(At("a.zip"), ("sub/first.txt", "1"), ("sub/second.txt", "2"), ("top.txt", "t"));

        var done = Extract(archive, Dir("out"), new Hooks
        {
            Before = path =>
            {
                if (!path.EndsWith("second.txt", StringComparison.Ordinal)) return;

                var sub = Path.GetDirectoryName(path)!;

                Directory.Move(sub, sub + "-moved");
                TestLinks.FolderLink(sub, outside);
            },
        });

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Equal(1, done.LeftOut.Unwritable);
        Assert.Equal(2, done.Files);
    }

    // ---- links, hard links, specials ----------------------------------------

    [Fact]
    public void A_symbolic_link_is_counted_and_never_created()
    {
        var tar = ArchiveTestData.Tar(At("l.tar"), t =>
        {
            t.WriteEntry(ArchiveTestData.File_("real.txt", "r"));
            t.WriteEntry(ArchiveTestData.Link_(TarEntryType.SymbolicLink, "escape", "../../outside"));
        });

        var done = Extract(tar, Dir("out"));

        Assert.Equal(1, done.LeftOut.Links);
        Assert.Equal(["real.txt"], Tree(done.Landed));
    }

    [Fact]
    public void A_hard_link_to_a_file_already_landed_becomes_a_copy_of_it()
    {
        var tar = ArchiveTestData.Tar(At("h.tar"), t =>
        {
            t.WriteEntry(ArchiveTestData.File_("docs/real.txt", "the content"));
            t.WriteEntry(ArchiveTestData.Link_(TarEntryType.HardLink, "again.txt", "docs/real.txt"));
        });

        var done = Extract(tar, Dir("out"));

        Assert.Equal("the content", File.ReadAllText(Path.Combine(done.Landed, "again.txt")));
        Assert.Equal(2, done.Files);
        Assert.Equal(0, done.LeftOut.Links);
    }

    [Fact]
    public void A_hard_link_to_nothing_landed_is_counted_as_a_link()
    {
        var tar = ArchiveTestData.Tar(At("h.tar"), t =>
        {
            t.WriteEntry(ArchiveTestData.Link_(TarEntryType.HardLink, "orphan.txt", "never/there.txt"));
            t.WriteEntry(ArchiveTestData.File_("real.txt", "r"));
        });

        var done = Extract(tar, Dir("out"));

        Assert.Equal(1, done.LeftOut.Links);
        Assert.Equal(1, done.Files);
    }

    [Fact]
    public void Devices_and_pipes_are_counted_and_never_created()
    {
        var tar = ArchiveTestData.Tar(At("d.tar"), t =>
        {
            t.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "fifo"));
            t.WriteEntry(new PaxTarEntry(TarEntryType.CharacterDevice, "tty"));
            t.WriteEntry(new PaxTarEntry(TarEntryType.BlockDevice, "sda"));
            t.WriteEntry(ArchiveTestData.File_("real.txt", "r"));
        });

        var done = Extract(tar, Dir("out"));

        Assert.Equal(3, done.LeftOut.Special);
        Assert.Equal(["real.txt"], Tree(done.Landed));
    }

    // ---- modes, attributes, times, the mark ---------------------------------

    [PosixFact]
    [SupportedOSPlatform("linux")]
    public void Modes_are_masked_and_folders_are_opened_after_their_children()
    {
        var tar = ArchiveTestData.Tar(At("m.tar"), t =>
        {
            t.WriteEntry(ArchiveTestData.Folder_("locked", (UnixFileMode)0x16D));
            t.WriteEntry(ArchiveTestData.File_("locked/setuid", "x", (UnixFileMode)0xDFF));
            t.WriteEntry(ArchiveTestData.File_("open", "y", (UnixFileMode)0x1FF));
        });

        var done = Extract(tar, Dir("out"));

        // 0555 folder lands 0755 and still received its child; setuid,
        // setgid, sticky and group/other write are gone from the files.
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(done.Landed, "locked")));
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(done.Landed, "locked", "setuid")));
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(done.Landed, "open")));
    }

    [WindowsFact]
    public void No_windows_attribute_an_archive_carries_is_set()
    {
        var done = Extract(ArchiveTestData.Fixture("7z-attribs.7z"), Dir("out"));

        var attributes = File.GetAttributes(Path.Combine(done.Landed, "readme.txt"));

        Assert.Equal(0, (int)(attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)));
    }

    [Fact]
    public void File_and_folder_times_come_from_the_archive()
    {
        var when = new DateTime(2001, 4, 5, 6, 7, 8, DateTimeKind.Local);
        var archive = ArchiveTestData.DatedZip(At("old.zip"), when, ("docs/", ""), ("docs/a.txt", "a"), ("b.txt", "b"));

        var done = Extract(archive, Dir("out"));

        Assert.Equal(when, File.GetLastWriteTime(Path.Combine(done.Landed, "docs", "a.txt")));
        Assert.Equal(when, File.GetLastWriteTime(Path.Combine(done.Landed, "b.txt")));
        Assert.Equal(when, Directory.GetLastWriteTime(Path.Combine(done.Landed, "docs")));
    }

    /// <summary>
    /// **The mark follows the archive's own, and the time survives it**
    /// (refutations 2, the blocker): writing the stream resets the modified
    /// time, so it has to be written before the time is set.
    /// </summary>
    [WindowsFact]
    public void A_marked_archive_marks_every_file_and_keeps_their_times()
    {
        const string mark = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/a.zip\r\n";

        var when = new DateTime(2001, 4, 5, 6, 7, 8, DateTimeKind.Local);
        var archive = ArchiveTestData.DatedZip(At("web.zip"), when, ("a.txt", "a"), ("sub/b.txt", "b"));

        File.WriteAllText(archive + ":Zone.Identifier", mark);

        var done = Extract(archive, Dir("out"));

        foreach (var file in new[] { Path.Combine(done.Landed, "a.txt"), Path.Combine(done.Landed, "sub", "b.txt") })
        {
            Assert.Equal(mark, File.ReadAllText(file + ":Zone.Identifier"));
            Assert.Equal(when, File.GetLastWriteTime(file));
        }
    }

    [WindowsFact]
    public void An_unmarked_archive_marks_nothing()
    {
        var archive = ArchiveTestData.Zip(At("local.zip"), ("a.txt", "a"), ("b.txt", "b"));

        var done = Extract(archive, Dir("out"));

        Assert.False(File.Exists(Path.Combine(done.Landed, "a.txt") + ":Zone.Identifier"));
    }

    // ---- what one entry's failure costs -------------------------------------

    [Fact]
    public void A_disk_refusing_one_entry_costs_that_entry_alone()
    {
        var archive = ArchiveTestData.Zip(At("a.zip"), ("a.txt", "a"), ("refused.txt", "r"), ("c.txt", "c"));

        var done = Extract(archive, Dir("out"), new Hooks
        {
            Before = path =>
            {
                if (path.EndsWith("refused.txt", StringComparison.Ordinal))
                    throw new IOException("the filesystem will not take that name");
            },
        });

        Assert.Equal(1, done.LeftOut.Unwritable);
        Assert.Equal(["a.txt", "c.txt"], Tree(done.Landed));
    }

    /// <summary>
    /// **A full disk is not one entry's problem.** Windows' two codes and
    /// Linux's ENOSPC (E-38: HResult 28) stop the run, and everything it wrote
    /// is discarded.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    [InlineData(28)]
    [InlineData(122)]
    public void A_full_disk_stops_the_run_and_leaves_nothing(int hresult)
    {
        var archive = ArchiveTestData.Zip(At("a.zip"), ("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));

        var failed = Assert.Throws<IOException>(() => Extract(archive, Dir("out"), new Hooks
        {
            Before = path =>
            {
                if (path.EndsWith("b.txt", StringComparison.Ordinal)) throw new IOException("full", hresult);
            },
        }));

        Assert.Equal(hresult, failed.HResult);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>FAT32 holds nothing over 4 GiB − 1; such an entry is counted,
    /// and the rest still extract. Declared through Zip64 and never read.</summary>
    [Fact]
    public void An_entry_too_large_for_fat32_is_counted_and_the_rest_extract()
    {
        File.WriteAllBytes(At("big.zip"), ZipBytes.Build(
            new ZipBytes.Entry("small.txt") { Data = "s"u8.ToArray() },
            new ZipBytes.Entry("huge.iso") { Data = "not really"u8.ToArray(), Size64 = 5L << 30 }));

        var done = Extract(At("big.zip"), Dir("out"), room: new ArchiveRoom(_ => null, _ => "FAT32"));

        Assert.Equal(1, done.LeftOut.Unwritable);
        Assert.Equal(["small.txt"], Tree(done.Landed));
    }

    [PosixFact]
    public void A_fat_stick_under_linux_gets_windows_names()
    {
        var archive = ArchiveTestData.Zip(At("a.zip"), ("a:b.txt", "x"), ("c.txt", "c"));

        var done = Extract(archive, Dir("out"), room: new ArchiveRoom(_ => null, _ => "vfat"));

        Assert.Equal(["a_b.txt", "c.txt"], Tree(done.Landed));
        Assert.Equal(1, done.Renamed);
    }

    /// <summary>
    /// **A stream declares no size, so it is watched as it is written.** Free
    /// space falls under the floor while a bare .gz is being written: the run
    /// stops before the disk is full, says so, and leaves nothing.
    /// </summary>
    [Fact]
    public void A_stream_that_would_fill_the_disk_stops_at_the_floor()
    {
        var archive = ArchiveTestData.Bare(At("zeros.bin.gz"), ArchiveFormat.Gz, new byte[70 << 20]);
        var asked = 0;
        var room = new ArchiveRoom(_ => ++asked == 1 ? 10L << 30 : 100L << 20, _ => null);

        var stopped = Assert.Throws<ArchiveRefusedException>(() => Extract(archive, Dir("out"), room: room));

        Assert.StartsWith("stopped before zeros.bin.gz filled ", stopped.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    // ---- what the bytes must match -------------------------------------------

    [Fact]
    public void A_zip_entry_whose_bytes_do_not_match_its_CRC_is_damage()
    {
        File.WriteAllBytes(At("bad.zip"), ZipBytes.Build(
            new ZipBytes.Entry("a.txt") { Data = "hello world"u8.ToArray(), Crc = 0xDEADBEEF }));

        Assert.Throws<ArchiveDamagedException>(() => Extract(At("bad.zip"), Dir("out")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>
    /// The directory says ten bytes; the data inflates to sixteen megabytes.
    /// **Stopped as it is written, not afterwards** — not one byte past the
    /// declared ten reaches the disk, which is what the handle's count of
    /// written bytes says.
    /// </summary>
    [Fact]
    public void An_entry_longer_than_it_declared_is_stopped_before_it_is_written()
    {
        File.WriteAllBytes(At("long.zip"), ZipBytes.Build(
            new ZipBytes.Entry("a.bin") { Data = new byte[16 << 20], Method = 8, Size = 10 }));

        var handle = new OperationHandle();
        long written = 0;

        handle.Progressed += (_, p) => written = Math.Max(written, p.BytesDone);

        Assert.Throws<ArchiveDamagedException>(() => Extract(At("long.zip"), Dir("out"), handle: handle));
        Assert.Equal(0, written);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>The other side of the same rule: an entry that ends before
    /// its declared size is damage too — a truncated member, not a small
    /// file.</summary>
    [Fact]
    public void An_entry_shorter_than_it_declared_is_damage()
    {
        File.WriteAllBytes(At("short.zip"), ZipBytes.Build(
            new ZipBytes.Entry("a.bin") { Data = new byte[100], Method = 8, Size = 5000 }));

        Assert.Throws<ArchiveDamagedException>(() => Extract(At("short.zip"), Dir("out")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    // ---- cancelling ---------------------------------------------------------

    /// <summary>
    /// **Checked on every buffer, not only when the archive is read.** A
    /// megabyte of zeros deflates to a kilobyte, so a decoder can go on
    /// producing for a long time without asking its input for anything —
    /// and the input is the only other place a cancellation is seen.
    /// </summary>
    [Fact]
    public void Cancelling_stops_at_the_next_buffer_and_leaves_nothing()
    {
        File.WriteAllBytes(At("zeros.zip"), ZipBytes.Build(
            new ZipBytes.Entry("zeros.bin") { Data = new byte[32 << 20], Method = 8 }));

        var handle = new OperationHandle();
        var afterCancel = 0;

        handle.Progressed += (_, p) =>
        {
            if (p.BytesDone == 0) return;

            if (handle.Token.IsCancellationRequested) afterCancel++;
            else handle.Cancel();
        };

        Assert.ThrowsAny<OperationCanceledException>(() => Extract(At("zeros.zip"), Dir("out"), handle: handle));

        Assert.Equal(0, afterCancel);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("out")));
    }

    /// <summary>
    /// **The run is on a thread of its own, not the pool's** (batch-0.11.2e
    /// QA, round 8). It was a Task.Run given 10 s to reach the pause, and with
    /// the pool held by Core's other classes it had not begun: 2 runs in 5
    /// failed under a pool capped at six workers, 5 in 5 at three. What this
    /// proves is that a paused run holds, which has nothing to do with when
    /// the pool gets round to starting it. The deadlines are generous — a
    /// pass is at once; only a run that never pauses, or never resumes,
    /// waits them out.
    /// </summary>
    [Fact]
    public void Pausing_holds_the_run_until_it_is_resumed()
    {
        var archive = ArchiveTestData.Zip(At("a.zip"), ("a.txt", "a"), ("b.txt", "b"));
        var handle = new OperationHandle();
        using var paused = new ManualResetEventSlim();
        var generous = TimeSpan.FromSeconds(30);

        handle.Progressed += (_, p) =>
        {
            if (p.ItemsDone == 1 && !paused.IsSet)
            {
                handle.Pause();
                paused.Set();
            }
        };

        Archives.Extraction done = default;
        var run = OwnThread.Run(() => done = Extract(archive, Dir("out"), handle: handle));

        Assert.True(paused.Wait(generous), "the run never reached the pause");

        Thread.Sleep(200);

        Assert.False(run.IsFinished, "the run carried on while paused");

        handle.Resume();

        Assert.True(run.Finished(generous), "the run never finished once resumed");
        Assert.Equal(2, done.Files);
    }

    // ---- names the disk has an opinion about ----------------------------------

    /// <summary>
    /// An 8.3 alias is a second name for a file that already landed: the
    /// entry that asks for it is numbered, not written over the first.
    /// A volume with short names turned off has no alias to collide with,
    /// and the test says so rather than passing on the wrong grounds.
    /// </summary>
    [ShortNamesFact]
    public void An_entry_named_like_a_short_alias_is_numbered()
    {
        var archive = ArchiveTestData.Zip(At("alias.zip"), ("longfilename.txt", "the long one"), ("LONGFI~1.TXT", "the short one"));

        var done = Extract(archive, Dir("out"));

        Assert.Equal("the long one", File.ReadAllText(Path.Combine(done.Landed, "longfilename.txt")));
        Assert.Equal(2, done.Files);
        Assert.Equal(1, done.Renamed);
        Assert.Contains(Tree(done.Landed), n => n.StartsWith("LONGFI~1 (2)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_file_written_under_a_replaced_name_says_so()
    {
        var archive = ArchiveTestData.Zip(At("bidi.zip"), ("inv\u202Egpj.exe", "x"), ("ok.txt", "y"));

        var done = Extract(archive, Dir("out"));

        Assert.Equal(["inv_gpj.exe", "ok.txt"], Tree(done.Landed));
        Assert.Equal(1, done.Renamed);
    }
}
