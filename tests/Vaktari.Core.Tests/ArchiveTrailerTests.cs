using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Every gzip member's trailer is read and checked (0.11.1 RC QA).
///
/// **A .gz cut short landed as the part that arrived.** The runtime's
/// GZipStream ends at the end of its input without reading the CRC-32 and
/// length a member ends with, and nothing else looked: a 460 KB .txt.gz cut at
/// 40 places extracted 40 times with the wrong bytes, and a .tar.gz cut inside
/// its first hundred bytes landed as a partial bare "tree.tar". Every gzip
/// member's trailer must now be there and match.
///
/// Every archive is written into the test's own folder; the committed
/// fixtures are only read.
/// </summary>
public sealed class ArchiveTrailerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-trailer").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    /// <summary>What <paramref name="bytes"/>, written as
    /// <paramref name="name"/>, extracts to: each file by its path with its
    /// bytes, or "" for a bare stream's one file.</summary>
    private SortedDictionary<string, string> Landed(string name, byte[] bytes)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var archive = At(tag + "-" + name);
        File.WriteAllBytes(archive, bytes);

        var done = Archives.Extract(archive, Directory.CreateDirectory(At("out-" + tag)).FullName);
        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (done.IsFile)
        {
            tree[""] = Convert.ToHexString(File.ReadAllBytes(done.Landed));
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(done.Landed, "*", SearchOption.AllDirectories))
                tree[Path.GetRelativePath(done.Landed, file).Replace('\\', '/')] = Convert.ToHexString(File.ReadAllBytes(file));
        }

        return tree;
    }

    /// <summary><paramref name="bytes"/> is refused as damaged, in the damage
    /// sentence, with nothing left in the destination.</summary>
    private void AssertDamaged(string name, byte[] bytes)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var archive = At(tag + "-" + name);
        File.WriteAllBytes(archive, bytes);
        var into = Directory.CreateDirectory(At("out-" + tag)).FullName;

        Archives.Extraction? done = null;
        ArchiveDamagedException? damaged = null;

        try { done = Archives.Extract(archive, into); }
        catch (ArchiveDamagedException e) { damaged = e; }

        Assert.True(damaged is not null, $"{name} ({bytes.Length} bytes) landed as {done?.Landed} with no word said");
        Assert.Contains($"{tag}-{name} is damaged", damaged.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(ArchiveTestData.Fixture(name));

    private static byte[] Compress(ArchiveFormat format, byte[] data)
    {
        var ms = new MemoryStream();

        using (var z = ArchiveTestData.Compressor(format)(ms)) z.Write(data);

        return ms.ToArray();
    }

    // ---- 1. every gzip member's trailer ------------------------------------------------

    /// <summary>The QA probe's text: 60,000 numbered lines, about 1.2 MB, 460 KB compressed.</summary>
    private static readonly byte[] Text = Encoding.ASCII.GetBytes(
        string.Concat(Enumerable.Range(0, 60000).Select(i => $"line {i} {(i * 2654435761u) % 1000003}\n")));

    private static readonly Lazy<byte[]> TextGz = new(() => Compress(ArchiveFormat.Gz, Text));

    /// <summary>The probe's 40 cuts, from one byte short of whole back towards the start.</summary>
    public static TheoryData<int> Cuts()
    {
        var length = TextGz.Value.Length;
        var cuts = new TheoryData<int>();

        for (var k = 0; k < 40; k++) cuts.Add(length - 1 - (k * (length / 40)));

        return cuts;
    }

    [Fact]
    public void The_whole_gz_the_cuts_come_from_lands()
    {
        Assert.InRange(TextGz.Value.Length, 100_000, 1_000_000);
        Assert.Equal(Convert.ToHexString(Text), Landed("t.txt.gz", TextGz.Value)[""]);
    }

    /// <summary>
    /// **Every one of these landed with the wrong bytes** (RC QA, Windows and
    /// Fedora). Cut one byte short, the trailer is incomplete; cut further, it
    /// is gone, and so is the deflate data's end.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cuts))]
    public void A_gz_cut_short_anywhere_is_damage(int cut)
        => AssertDamaged("t.txt.gz", TextGz.Value[..cut]);

    /// <summary>Inside the trailer itself: every length that leaves the
    /// deflate data whole but the eight bytes after it short.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_gz_whose_trailer_is_short_is_damage(int missing)
        => AssertDamaged("short.txt.gz", Fixture("bare.txt.gz")[..^missing]);

    /// <summary>
    /// **A .tar.gz cut inside its first hundred bytes no longer reads as a
    /// tar**, and landed as a partial bare "tree.tar" (RC QA). It is tried as
    /// a bare .gz, and the trailer check refuses it there.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(20)]
    [InlineData(37)]
    [InlineData(60)]
    [InlineData(100)]
    public void A_tar_gz_cut_in_its_first_bytes_is_damage_not_a_partial_tar(int cut)
        => AssertDamaged("tree.tar.gz", Fixture("tree.tar.gz")[..cut]);

    [Fact]
    public void A_gz_with_a_changed_CRC_is_damage()
    {
        var bytes = Fixture("bare.txt.gz");
        bytes[^8] ^= 0x01;

        AssertDamaged("crc.txt.gz", bytes);
    }

    [Fact]
    public void A_gz_with_a_changed_length_is_damage()
    {
        var bytes = Fixture("bare.txt.gz");
        bytes[^1] ^= 0x01;

        AssertDamaged("isize.txt.gz", bytes);
    }

    /// <summary>Three members, one of them empty — what pigz, a log appender
    /// and BGZF write — arrive as one file, each checked on its own.</summary>
    [Fact]
    public void Every_member_of_an_honest_multi_member_gz_arrives()
    {
        byte[] bytes =
        [
            .. Compress(ArchiveFormat.Gz, Text[..1000]),
            .. Compress(ArchiveFormat.Gz, []),
            .. Compress(ArchiveFormat.Gz, Text[1000..]),
        ];

        Assert.Equal(Convert.ToHexString(Text), Landed("multi.txt.gz", bytes)[""]);
    }

    [Fact]
    public void A_changed_CRC_in_the_first_of_two_members_is_damage()
    {
        var first = Compress(ArchiveFormat.Gz, Text[..1000]);
        first[^8] ^= 0x01;

        AssertDamaged("first.txt.gz", [.. first, .. Compress(ArchiveFormat.Gz, Text[1000..])]);
    }

    /// <summary>A second member that starts and stops is a member cut short,
    /// not trailing data to be dropped.</summary>
    [Fact]
    public void A_second_member_cut_short_is_damage()
    {
        var second = Compress(ArchiveFormat.Gz, Text[1000..]);

        AssertDamaged("second.txt.gz", [.. Compress(ArchiveFormat.Gz, Text[..1000]), .. second[..(second.Length / 2)]]);
    }

    /// <summary>A member written by hand, with every optional header field
    /// the format has: the extra field, a name, a comment and the header's
    /// own CRC.</summary>
    private static byte[] Member(byte[] data, bool extra, bool name, bool comment, bool headerCrc, bool breakHeaderCrc = false)
    {
        var head = new List<byte> { 0x1F, 0x8B, 8, 0, 0x10, 0x20, 0x30, 0x40, 0, 3 };
        byte flags = 0;

        if (extra)
        {
            flags |= 0x04;
            head.AddRange([6, 0, (byte)'V', (byte)'k', 2, 0, 0xAB, 0xCD]);
        }

        if (name)
        {
            flags |= 0x08;
            head.AddRange([.. "notes été.txt"u8.ToArray(), 0]);
        }

        if (comment)
        {
            flags |= 0x10;
            head.AddRange([.. "written by hand for a test"u8.ToArray(), 0]);
        }

        if (headerCrc) flags |= 0x02;

        head[3] = flags;

        if (headerCrc)
        {
            var crc16 = (ushort)ZipDirectory.Crc32([.. head]);

            if (breakHeaderCrc) crc16 ^= 1;

            head.AddRange([(byte)crc16, (byte)(crc16 >> 8)]);
        }

        var deflated = new MemoryStream();

        using (var z = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);

        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, ZipDirectory.Crc32(data));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), (uint)data.Length);

        return [.. head, .. deflated.ToArray(), .. trailer];
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, true, true)]
    public void A_gz_with_optional_header_fields_lands(bool extra, bool name, bool comment, bool headerCrc)
    {
        byte[] bytes = [.. Member(Text[..5000], extra, name, comment, headerCrc), .. Member(Text[5000..], true, true, true, true)];

        Assert.Equal(Convert.ToHexString(Text), Landed("fields.txt.gz", bytes)[""]);
    }

    [Fact]
    public void A_gz_whose_header_CRC_does_not_match_is_damage()
        => AssertDamaged("hcrc.txt.gz", Member(Text[..5000], true, true, true, true, breakHeaderCrc: true));

    [Fact]
    public void The_CRC_is_the_one_zip_and_gzip_use()
    {
        var bytes = Text[..4099];

        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 1000, 4099 })
            Assert.Equal(ZipDirectory.Crc32(bytes[..length]), Crc32.Update(0, bytes[..length]));

        Assert.Equal(ZipDirectory.Crc32(bytes), Crc32.Update(Crc32.Update(0, bytes[..1001]), bytes[1001..]));
    }
}
