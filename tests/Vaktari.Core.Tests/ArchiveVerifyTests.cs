using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The three Stage A review fixes the author could not revert-check: the
/// cancellation token inside the deep folder walk, the parent-only stat, and
/// the planner keyed by (parent, segment). Each is pinned by what it costs or
/// what it lets happen after a cancel, since none changes WHAT lands.
/// </summary>
public sealed class ArchiveVerifyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-verify").FullName;

    public void Dispose()
    {
        try { Archives.DeleteTree(_root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    private string Dir(string name) => Directory.CreateDirectory(At(name)).FullName;

    private string Zip(string name, params ZipBytes.Entry[] entries)
    {
        File.WriteAllBytes(At(name), ZipBytes.Build(entries));
        return At(name);
    }

    private sealed class Counting(CancellationTokenSource cancel, Func<int, string, bool> cancelWhen) : IExtractionObserver
    {
        public int Creates { get; private set; }
        public int Writes { get; private set; }

        public void BeforeCreate(string path)
        {
            if (cancelWhen(++Creates, path)) cancel.Cancel();
        }

        public void WhileWriting(string temporary, string final) => Writes++;

        public void BeforeLanding(string target) { }
    }

    /// <summary>
    /// **A cancel reaches the folder walk between two folders**, not after
    /// the whole chain is made. Four hundred folders deep, cancelled as the
    /// fifth is about to be created: at most one more is asked for.
    /// </summary>
    [Fact]
    public void A_cancel_during_a_deep_folder_chain_stops_before_the_next_folder()
    {
        var archive = Zip("deep.zip",
            new ZipBytes.Entry(string.Concat(Enumerable.Repeat("a/", 400)) + "f.txt") { Data = "x"u8.ToArray() });

        using var cancel = new CancellationTokenSource();
        var hooks = new Counting(cancel, (n, _) => n == 5);
        var into = Dir("out");

        Assert.ThrowsAny<OperationCanceledException>(
            () => Archives.Extract(archive, into, null, cancel.Token, ArchiveRoom.Real, hooks));

        Assert.True(hooks.Creates <= 6, $"{hooks.Creates} folders asked for after a cancel at the fifth");
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    /// <summary>
    /// **A cancel arriving as a file is about to be created stops the chain
    /// check**, before the file is written. The file is empty, so no copy
    /// loop runs to notice the cancel instead.
    /// </summary>
    [Fact]
    public void A_cancel_as_a_deep_file_is_created_writes_nothing()
    {
        var archive = Zip("deep.zip",
            new ZipBytes.Entry(string.Concat(Enumerable.Repeat("a/", 50)) + "empty.txt") { Data = [] });

        using var cancel = new CancellationTokenSource();
        var hooks = new Counting(cancel, (_, path) => path.EndsWith("empty.txt", StringComparison.Ordinal));
        var into = Dir("out");

        Assert.ThrowsAny<OperationCanceledException>(
            () => Archives.Extract(archive, into, null, cancel.Token, ArchiveRoom.Real, hooks));

        Assert.Equal(0, hooks.Writes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    /// <summary>
    /// **Planning a deep path costs its depth, not its depth squared.** Keyed
    /// by the whole raw prefix, every call built and hashed a string per
    /// folder as long as the path to it: about 16 MB for one file 500 folders
    /// of 60 characters deep. Keyed by (parent, segment), a path already
    /// planned allocates next to nothing.
    /// </summary>
    [Fact]
    public void Planning_a_deep_path_again_allocates_next_to_nothing()
    {
        var planner = new LandingPlanner(windowsRules: true);
        var segment = new string('s', 60);
        string[] path = [.. Enumerable.Repeat(segment, 500), "f.txt"];

        Assert.NotNull(planner.FileNode(path));

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20; i++) Assert.NotNull(planner.FileNode(path));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 2 * 1024 * 1024, $"{allocated:N0} bytes for 20 plans of one deep path");
    }

    /// <summary>
    /// **A hard link is asked about before it is copied, like any file.**
    /// A sized entry is never watched while it is written — the running check
    /// is for streams that declare nothing — so only the question asked per
    /// entry can stop it: 3 MiB and twenty links to it, 63 MiB in all, onto
    /// a disk with 30 MiB free that fills as it is written.
    /// </summary>
    [Fact]
    public void Each_hard_link_copy_is_asked_about_before_it_is_written()
    {
        const long MiB = 1024 * 1024;
        var archive = ArchiveTestData.Tar(At("links.tar"), t =>
        {
            t.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "one.bin")
            {
                DataStream = new MemoryStream(new byte[3 * MiB]),
            });

            for (var i = 0; i < 20; i++)
                t.WriteEntry(ArchiveTestData.Link_(System.Formats.Tar.TarEntryType.HardLink, $"copy{i}.bin", "one.bin"));
        });

        var into = Dir("out");
        var room = new ArchiveRoom(_ => 30 * MiB - Written(into), _ => null);

        Assert.Throws<ArchiveRefusedException>(() => Archives.Extract(archive, into, null, default, room, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    private static long Written(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    /// <summary>
    /// **A 7z is held to its own CRC by us, because nothing else checks
    /// it.** A stored (Copy) entry with one byte changed decodes without
    /// complaint, so only the CRC the archive recorded can say it is wrong.
    /// The fixture test with a damaged PPMd byte cannot tell: the decoder
    /// fails on its own.
    /// </summary>
    [Fact]
    public void A_stored_7z_entry_whose_bytes_changed_is_damage()
    {
        var content = Enumerable.Range(0, 100).Select(i => (byte)(i * 31 + 7)).ToArray();
        var path = At("stored.7z");

        // Undamaged, it extracts: the hand-made archive is a real one.
        File.WriteAllBytes(path, StoredSevenZip("data.bin", content));

        var clean = Archives.Extract(path, Dir("clean"), null, default, ArchiveRoom.Real, null);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(clean.Landed, "data.bin")));

        var bytes = StoredSevenZip("data.bin", content);

        bytes[32 + 40] ^= 0x55;
        File.WriteAllBytes(path, bytes);

        var into = Dir("out");

        Assert.Throws<ArchiveDamagedException>(() => Archives.Extract(path, into, null, default, ArchiveRoom.Real, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    /// <summary>
    /// A 7z of one entry under the Copy method, which SharpCompress's writer
    /// will not make: the signature header, the bytes as they are, and a
    /// plain header naming one pack stream, one Copy folder, the entry's CRC
    /// and its name. Sizes under 128 so every number is one byte.
    /// </summary>
    private static byte[] StoredSevenZip(string name, byte[] content)
    {
        Assert.True(content.Length < 0x80);

        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        var crc = ZipDirectory.Crc32(content);
        var header = new List<byte>
        {
            0x01,                                           // Header
            0x04,                                           // MainStreamsInfo
            0x06, 0x00, 0x01, 0x09, (byte)content.Length, 0x00, // PackInfo: pos 0, 1 stream, its size
            0x07, 0x0B, 0x01, 0x00,                         // UnPackInfo: Folder, 1, not external
            0x01, 0x01, 0x00,                               // 1 coder: id size 1, id 00 (Copy)
            0x0C, (byte)content.Length, 0x00,               // CodersUnPackSize
            0x08, 0x0A, 0x01,                               // SubStreamsInfo: CRC, all defined
        };

        header.AddRange(BitConverter.GetBytes(crc));
        header.AddRange([0x00, 0x00]);                      // end SubStreamsInfo, end MainStreamsInfo
        header.AddRange([0x05, 0x01, 0x11, (byte)(nameBytes.Length + 1), 0x00]); // FilesInfo: 1 file, Name
        header.AddRange(nameBytes);
        header.AddRange([0x00, 0x00]);                      // end FilesInfo, end Header

        var next = header.ToArray();
        var start = new byte[20];

        BitConverter.GetBytes((long)content.Length).CopyTo(start, 0);
        BitConverter.GetBytes((long)next.Length).CopyTo(start, 8);
        BitConverter.GetBytes(ZipDirectory.Crc32(next)).CopyTo(start, 16);

        return [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, .. BitConverter.GetBytes(ZipDirectory.Crc32(start)), .. start, .. content, .. next];
    }

    /// <summary>
    /// **Each new folder's parent is stat'ed once**, not the whole chain
    /// above it. Checking the chain for every folder made a deep entry
    /// quadratic in stats: 511 folders of 55 characters is about 130,000 stats
    /// of paths up to 28,000 characters long, against 511.
    /// </summary>
    /// Windows only: Linux caps a landing path at 4,000 bytes, which this
    /// chain is far past.
    [WindowsFact]
    public void A_chain_five_hundred_folders_deep_is_made_in_linear_time()
    {
        var segment = new string('p', 55);
        var archive = Zip("deep.zip",
            new ZipBytes.Entry(string.Concat(Enumerable.Repeat(segment + "/", 511)) + "f") { Data = "x"u8.ToArray() });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var done = Archives.Extract(archive, Dir("out"), null, default, ArchiveRoom.Real, null);

        Assert.Equal(1, done.Files);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"{watch.Elapsed}");
    }
}
