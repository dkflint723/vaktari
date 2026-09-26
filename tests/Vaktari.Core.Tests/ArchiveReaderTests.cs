using System.Formats.Tar;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// One pass over one archive: what each format's entries are, how damage and
/// passwords are reported, and that a cancellation is a cancellation.
/// </summary>
public sealed class ArchiveReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-reader").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    private static List<ArchiveEntryInfo> List(string path)
    {
        using var pass = ArchiveReader.Open(path, CancellationToken.None);

        return [.. pass.Items().Select(i => i.Info)];
    }

    [Theory]
    [InlineData("7z-solid-lzma2.7z")]
    [InlineData("7z-bcj2.7z")]
    [InlineData("7z-ppmd.7z")]
    [InlineData("zip-deflate64.zip")]
    [InlineData("zip-ppmd.zip")]
    [InlineData("tree.tar.gz")]
    [InlineData("tree.tar.bz2")]
    [InlineData("tree.tar.xz")]
    [InlineData("tree.tar.zst")]
    [InlineData("tree.tar.lz")]
    public void The_fixture_tree_lists_as_four_files_and_two_folders(string fixture)
    {
        var items = List(ArchiveTestData.Fixture(fixture));

        Assert.Equal(4, items.Count(i => i.Kind == ArchiveEntryKind.File));
        Assert.Equal(2, items.Count(i => i.Kind == ArchiveEntryKind.Folder));
        Assert.Equal(21390, items.Single(i => i.RawKey.EndsWith("c.txt", StringComparison.Ordinal)).Size);
    }

    /// <summary>
    /// **SharpCompress hands RAR keys over with the platform's own
    /// separator** (E-23, measured on both): <c>exe\test.exe</c> on Windows,
    /// <c>exe/test.exe</c> under Linux, from the same archive. Either way the
    /// key cuts into the same two segments.
    /// </summary>
    [Theory]
    [InlineData("rar4.rar")]
    [InlineData("rar5-solid.rar")]
    public void A_rar_names_its_folders_with_the_platform_separator(string fixture)
    {
        var items = List(ArchiveTestData.Fixture(fixture));
        var exe = items.Single(i => i.Size == 45056);

        Assert.Equal($"exe{Path.DirectorySeparatorChar}test.exe", exe.RawKey);
        Assert.Equal(["exe", "test.exe"], ArchiveKeys.Split(exe.RawKey, ArchiveFormat.Rar, out _)!);
        Assert.False(exe.CrcIsOurs);
        Assert.Contains(items, i => i.RawKey == "тест.txt");
    }

    /// <summary>7z checks no CRC of its own (E-25), so ours is asked for.</summary>
    [Fact]
    public void A_7z_file_entry_asks_for_our_CRC()
        => Assert.All(
            List(ArchiveTestData.Fixture("7z-ppmd.7z")).Where(i => i.Kind == ArchiveEntryKind.File),
            i => Assert.True(i.CrcIsOurs));

    [Theory]
    [InlineData("7z-mhe.7z")]
    [InlineData("rar4-hp.rar")]
    [InlineData("rar5-hp.rar")]
    public void A_header_encrypted_archive_needs_its_password_to_list(string fixture)
        => Assert.Throws<ArchivePasswordRequiredException>(
            () => ArchiveReader.Open(ArchiveTestData.Fixture(fixture), CancellationToken.None));

    [Theory]
    [InlineData("7z-p.7z")]
    [InlineData("zip-zipcrypto.zip")]
    [InlineData("zip-aes256.zip")]
    [InlineData("rar4-p.rar")]
    [InlineData("rar5-p.rar")]
    public void A_file_encrypted_archive_lists_and_says_it_is_encrypted(string fixture)
    {
        using var pass = ArchiveReader.Open(ArchiveTestData.Fixture(fixture), CancellationToken.None);

        Assert.True(pass.AnyEncrypted);
    }

    /// <summary>
    /// Every tar entry type lands in its kind: links as links, devices and
    /// pipes as special, long names whole.
    /// </summary>
    [Fact]
    public void Tar_entry_types_have_their_kinds()
    {
        var longName = string.Join('/', Enumerable.Repeat("very-long-folder-name", 10)) + "/file.txt";

        var tar = ArchiveTestData.Tar(At("kinds.tar"), t =>
        {
            t.WriteEntry(ArchiveTestData.File_("plain.txt", "x", UnixFileMode.SetUser | UnixFileMode.UserRead | UnixFileMode.UserExecute));
            t.WriteEntry(ArchiveTestData.Link_(TarEntryType.SymbolicLink, "sym", "plain.txt"));
            t.WriteEntry(ArchiveTestData.Link_(TarEntryType.HardLink, "hard", "plain.txt"));
            t.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "fifo"));
            t.WriteEntry(new PaxTarEntry(TarEntryType.CharacterDevice, "chr"));
            t.WriteEntry(new PaxTarEntry(TarEntryType.BlockDevice, "blk"));
            t.WriteEntry(ArchiveTestData.File_(longName, "deep"));
        });

        var gnu = At("gnu.tar");

        using (var file = File.Create(gnu))
        using (var w = new TarWriter(file, TarEntryFormat.Gnu))
            w.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, longName) { DataStream = new MemoryStream("g"u8.ToArray()) });

        var items = List(tar);

        Assert.Equal(
            [ArchiveEntryKind.File, ArchiveEntryKind.SymbolicLink, ArchiveEntryKind.HardLink,
             ArchiveEntryKind.Special, ArchiveEntryKind.Special, ArchiveEntryKind.Special, ArchiveEntryKind.File],
            items.Select(i => i.Kind));

        Assert.Equal("plain.txt", items[2].LinkTarget);
        Assert.Equal(0x940, items[0].UnixMode);
        Assert.Equal(longName, items[6].RawKey);
        Assert.Equal(longName, List(gnu).Single().RawKey);
    }

    /// <summary>Nothing a tar says becomes an attribute: it has no Windows
    /// attributes to say, and its mode is not one.</summary>
    [Fact]
    public void A_tar_entry_carries_no_windows_attributes()
    {
        var tar = ArchiveTestData.Tar(At("ro.tar"), t => t.WriteEntry(ArchiveTestData.File_("r.txt", "x", UnixFileMode.UserRead)));

        Assert.Null(List(tar).Single().WindowsAttributes);
    }

    [Fact]
    public void A_pax_global_header_is_not_an_entry()
    {
        var generated = ArchiveTestData.Tar(At("global.tar"), t =>
        {
            t.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "hello" }));
            t.WriteEntry(ArchiveTestData.File_("only.txt", "x"));
        });

        Assert.Equal(["only.txt"], List(generated).Select(i => i.RawKey));
        Assert.Equal(["small.txt"], List(ArchiveTestData.Fixture("paxglobal.tar")).Select(i => i.RawKey));
    }

    [Fact]
    public void A_gnu_sparse_tar_is_refused()
        => Assert.Throws<ArchiveRefusedException>(() => List(ArchiveTestData.Fixture("sparse-gnu.tar")));

    /// <summary>PAX 0.1 sparse arrives as an ordinary file whose data is the
    /// map (refutations 2); writing it would write the map.</summary>
    [Fact]
    public void A_pax_sparse_member_is_special()
        => Assert.Equal(ArchiveEntryKind.Special, List(ArchiveTestData.Fixture("sparse-pax01.tar")).Single().Kind);

    /// <summary>Pinned as a Known limit: the BCL reader gives a non-UTF-8 tar
    /// name as U+FFFD, with no raw bytes to do better from.</summary>
    [Fact]
    public void A_latin1_tar_name_arrives_with_a_replacement_character()
        => Assert.Contains("caf�", List(ArchiveTestData.Fixture("latin1-gnu.tar")).Select(i => i.RawKey));

    /// <summary>
    /// **A stream truncated part-way reads its early entries and then fails**
    /// (E-14): <c>EndOfStreamException</c> for gzip, <c>DataErrorException</c>
    /// for xz — each reported as damage after the entries that came whole.
    /// </summary>
    [Theory]
    [InlineData("tree.tar.gz")]
    [InlineData("tree.tar.xz")]
    public void A_truncated_stream_is_damaged_after_what_it_read(string fixture)
    {
        var bytes = File.ReadAllBytes(ArchiveTestData.Fixture(fixture));

        File.WriteAllBytes(At(fixture), bytes[..(bytes.Length * 3 / 4)]);

        using var pass = ArchiveReader.Open(At(fixture), CancellationToken.None);

        var damaged = Assert.Throws<ArchiveDamagedException>(() =>
        {
            foreach (var item in pass.Items())
            {
                using var data = item.OpenData();
                data.CopyTo(Stream.Null);
            }
        });

        Assert.True(damaged.EntriesBefore > 0, $"damaged after {damaged.EntriesBefore}");
        Assert.StartsWith($"{fixture} is damaged after", damaged.Message);
    }

    /// <summary>A zip, 7z or RAR keeps its directory at the end, so a
    /// truncated one fails before any entry (E-14).</summary>
    [Theory]
    [InlineData("zip-lzma.zip", "zip")]
    [InlineData("7z-ppmd.7z", "7z")]
    [InlineData("rar5.rar", "RAR")]
    public void A_truncated_archive_with_its_directory_at_the_end_is_refused_up_front(string fixture, string word)
    {
        var bytes = File.ReadAllBytes(ArchiveTestData.Fixture(fixture));

        File.WriteAllBytes(At(fixture), bytes[..(bytes.Length / 2)]);

        var refused = Assert.Throws<InvalidDataException>(() => ArchiveReader.Open(At(fixture), CancellationToken.None));

        Assert.Equal($"{fixture} is not a {word} file, or is damaged", refused.Message);
    }

    [Fact]
    public void A_gz_holding_a_tar_reads_as_a_tar()
    {
        var path = ArchiveTestData.Tar(At("x.gz"), t => t.WriteEntry(ArchiveTestData.File_("in.txt", "x")),
            ArchiveTestData.Compressor(ArchiveFormat.Gz));

        using var pass = ArchiveReader.Open(path, CancellationToken.None);

        Assert.Equal(ArchiveFormat.TarGz, pass.Format);
    }

    [Theory]
    [InlineData(ArchiveFormat.Gz)]
    [InlineData(ArchiveFormat.Bz2)]
    [InlineData(ArchiveFormat.Zst)]
    [InlineData(ArchiveFormat.Lz)]
    public void A_bare_stream_is_one_file_named_by_the_stem(ArchiveFormat format)
    {
        var path = ArchiveTestData.Bare(At("notes.txt.x"), format, Encoding.UTF8.GetBytes("hello"));
        var named = At("notes.txt" + format switch
        {
            ArchiveFormat.Gz => ".gz", ArchiveFormat.Bz2 => ".bz2", ArchiveFormat.Zst => ".zst", _ => ".lz",
        });

        File.Move(path, named);

        using var pass = ArchiveReader.Open(named, CancellationToken.None);
        var item = pass.Items().Single();

        Assert.Equal("notes.txt", item.Info.RawKey);
        Assert.Null(item.Info.Size);

        using var data = item.OpenData();
        Assert.Equal("hello", new StreamReader(data).ReadToEnd());
    }

    /// <summary>
    /// **LZMA2 reports a cancellation arriving through its input as
    /// "Data Error"** (E-7). Asked of the token first, it is still a
    /// cancellation — not an archive somebody is told is damaged.
    /// </summary>
    [Fact]
    public void Cancelling_mid_decode_of_a_7z_is_a_cancellation_not_damage()
    {
        var data = new byte[16 << 20];
        uint x = 1;

        for (var i = 0; i < data.Length; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            data[i] = (byte)('a' + (x & 0x0F));
        }

        var path = ArchiveTestData.SevenZip(At("big.7z"), ("big.bin", data));

        using var cts = new CancellationTokenSource();
        using var pass = ArchiveReader.Open(path, cts.Token);

        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var item in pass.Items())
            {
                using var stream = item.OpenData();
                var buffer = new byte[81920];

                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                        watch.Restart();
                    }
                }
            }
        });

        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }
}
