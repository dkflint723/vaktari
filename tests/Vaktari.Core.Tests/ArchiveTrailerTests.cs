using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Where a compressed stream ends, and what is checked there (0.11.1 RC QA).
///
/// **A .gz cut short landed as the part that arrived.** The runtime's
/// GZipStream ends at the end of its input without reading the CRC-32 and
/// length a member ends with, and nothing else looked: a 460 KB .txt.gz cut at
/// 40 places extracted 40 times with the wrong bytes, and a .tar.gz cut inside
/// its first hundred bytes landed as a partial bare "tree.tar". Every gzip
/// member's trailer must now be there and match.
///
/// **And harmless bytes after the last stream refused a compressed tar.**
/// Once the tar reader read to the end of its compression, zero padding or
/// junk after a .tar.bz2 or .tar.zst, and junk after a .tar.xz, became
/// "damaged" — they had landed before, and GNU tar extracts them. Each decoder
/// now ends after its last clean stream and leaves trailing bytes alone, for a
/// bare stream as for a tar; bytes that begin with the format's own signature
/// are another stream and must be whole; a failure inside a stream is still
/// damage.
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

    // ---- 2. trailing bytes after the last stream ---------------------------------------

    /// <summary>What QA appended: zero padding of four sizes, and two kinds of junk.</summary>
    private static byte[] Tail(string tail) => tail switch
    {
        "zeros-3" => new byte[3],
        "zeros-4" => new byte[4],
        "zeros-512" => new byte[512],
        "zeros-10240" => new byte[10240],
        "garbage" => "garbage!"u8.ToArray(),
        "pk" => "PK\u0003\u0004 junk after"u8.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(tail)),
    };

    /// <summary>Honest archives of every compressor, compressed tars and
    /// bare streams, with the file they are written as.</summary>
    private static byte[] Honest(string name) => Fixture(name);

    public static TheoryData<string, string> Trailed()
    {
        var data = new TheoryData<string, string>();

        foreach (var name in new[] { "tree.tar.gz", "bare.txt.gz" })
        {
            foreach (var tail in new[] { "zeros-3", "zeros-4", "zeros-512", "zeros-10240", "garbage", "pk" })
                data.Add(name, tail);
        }

        return data;
    }

    /// <summary>
    /// **Trailing bytes are not part of the archive.** The .tar.bz2,
    /// .tar.zst and .tar.xz cases were refused as damaged in the RC and had
    /// landed before it; the bare streams and lzip had refused them all along.
    /// Each lands exactly as the same archive without the tail.
    /// </summary>
    [Theory]
    [MemberData(nameof(Trailed))]
    public void Bytes_after_the_last_stream_are_left_alone(string name, string tail)
    {
        var honest = Honest(name);

        Assert.Equal(Landed(name, honest), Landed(name, [.. honest, .. Tail(tail)]));
    }

    /// <summary>Where gzip keeps the check that ends its stream, counted from
    /// the end: a byte there, flipped, fails it.</summary>
    public static TheoryData<string, int> Checks() => new()
    {
        { "tree.tar.gz", 8 },
        { "bare.txt.gz", 8 },
    };

    /// <summary>A tail after the stream does not make a failed check at its
    /// end pass: the check is read before the tail is left alone.</summary>
    [Theory]
    [MemberData(nameof(Checks))]
    public void A_failed_check_before_trailing_bytes_is_still_damage(string name, int fromEnd)
    {
        var bytes = Honest(name);
        bytes[^fromEnd] ^= 0xFF;

        AssertDamaged(name, [.. bytes, .. Tail("zeros-512")]);
    }

    /// <summary>And a byte changed in the middle of the stream, with a tail
    /// after it, is damage.</summary>
    [Theory]
    [InlineData("tree.tar.gz")]
    public void A_changed_byte_inside_the_stream_is_still_damage(string name)
    {
        var bytes = Honest(name);
        bytes[bytes.Length / 2] ^= 0x01;

        AssertDamaged(name, [.. bytes, .. Tail("garbage")]);
    }

    /// <summary>
    /// **Bytes that begin with the format's own signature are another stream**,
    /// and one cut short is damage — never trailing data to drop. Without this
    /// a multi-member file cut in its last member would land without it.
    /// </summary>
    [Theory]
    [InlineData("bare.txt.gz")]
    public void A_second_stream_cut_short_is_damage(string name)
    {
        var one = Honest(name);

        AssertDamaged(name, [.. one, .. one[..(one.Length / 2)]]);
    }

    // ---- 3. members as gzip(1) writes them, and where each one ends -------------------

    /// <summary>
    /// An empty member exactly as <c>gzip -n</c> writes it (Fedora 44, gzip
    /// 1.13: <c>printf '' | gzip -n</c>), and as zlib and Python's gzip module
    /// do: the header, the deflate data <c>03 00</c> — one empty final block —
    /// and a trailer of eight zero bytes. The runtime's GZipStream writes
    /// nothing at all for an empty input, so a test that compresses an empty
    /// array with it has no empty member in it.
    /// </summary>
    private static readonly byte[] GnuEmpty =
        [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];

    public static TheoryData<string> EmptyPlacements() => ["before", "between", "after", "twice before", "only"];

    /// <summary>The members <paramref name="placement"/> names, with
    /// <paramref name="data"/> split in two where there are two.</summary>
    private static (byte[] File, byte[] Content) WithEmpty(string placement, byte[] data)
    {
        var half = data.Length / 2 + 11;
        var whole = Compress(ArchiveFormat.Gz, data);

        return placement switch
        {
            "before" => ([.. GnuEmpty, .. whole], data),
            "between" => ([.. Compress(ArchiveFormat.Gz, data[..half]), .. GnuEmpty, .. Compress(ArchiveFormat.Gz, data[half..])], data),
            "after" => ([.. whole, .. GnuEmpty], data),
            "twice before" => ([.. GnuEmpty, .. GnuEmpty, .. whole], data),
            "only" => (GnuEmpty, []),
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };
    }

    /// <summary>
    /// **An empty member hid every member after it** (RC QA round 2: gzip
    /// wrote <c>cat empty.gz data.gz</c> as 108,894 bytes, and it landed as an
    /// empty file with no word said). The trailer search found the eight zero
    /// bytes one byte early, starting at the <c>00</c> of <c>03 00</c>, and the
    /// next member's <c>1F 8B</c> was then read one byte late.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptyPlacements))]
    public void An_empty_member_as_gzip_writes_it_hides_nothing(string placement)
    {
        var (file, content) = WithEmpty(placement, Text[..20000]);

        Assert.Equal(Convert.ToHexString(content), Landed("empty.txt.gz", file)[""]);
    }

    /// <summary>The same in a .tar.gz: the tar is the members' content, and
    /// lands whole.</summary>
    [Theory]
    [InlineData("before")]
    [InlineData("between")]
    [InlineData("after")]
    [InlineData("twice before")]
    public void An_empty_member_as_gzip_writes_it_hides_nothing_in_a_tar_gz(string placement)
    {
        var tar = new MemoryStream();

        using (var z = new GZipStream(new MemoryStream(Fixture("tree.tar.gz")), CompressionMode.Decompress)) z.CopyTo(tar);

        var (file, _) = WithEmpty(placement, tar.ToArray());

        Assert.Equal(Landed("tree.tar.gz", Fixture("tree.tar.gz")), Landed("tree.tar.gz", file));
    }

    /// <summary>
    /// **Every cut of a file with an empty member in it, one byte at a time.**
    /// A cut that falls exactly between two members leaves a shorter file
    /// that is whole in every way gzip can tell, and lands as the members
    /// before it; every other cut is damage. This is the rule the trailer
    /// search could not keep: it needs each member's end found exactly.
    /// </summary>
    [Fact]
    public void Every_cut_is_damage_except_one_between_members()
    {
        byte[] first = Compress(ArchiveFormat.Gz, Text[..700]), last = Compress(ArchiveFormat.Gz, Text[700..1500]);
        byte[] file = [.. first, .. GnuEmpty, .. last];
        var boundaries = new Dictionary<int, byte[]>
        {
            [first.Length] = Text[..700],
            [first.Length + GnuEmpty.Length] = Text[..700],
        };
        var wrong = new List<string>();

        for (var cut = 1; cut < file.Length; cut++)
        {
            var name = $"cut{cut}.txt.gz";
            File.WriteAllBytes(At(name), file[..cut]);
            var into = Directory.CreateDirectory(At("out-cut" + cut)).FullName;

            try
            {
                var done = Archives.Extract(At(name), into);
                var landed = File.ReadAllBytes(done.Landed);

                if (!boundaries.TryGetValue(cut, out var expected) || !landed.AsSpan().SequenceEqual(expected))
                    wrong.Add($"cut at {cut} of {file.Length} landed {landed.Length} bytes");
            }
            // Too short to be told as gzip at all is refused as not one.
            catch (Exception e) when (e is ArchiveDamagedException or InvalidDataException)
            {
                if (boundaries.ContainsKey(cut)) wrong.Add($"cut at {cut}, between members, was refused");
                if (Directory.EnumerateFileSystemEntries(into).Any()) wrong.Add($"cut at {cut} left something behind");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// **A member whose stored data holds its own CRC and length** (RC QA
    /// round 2). Its content P is 3,000 bytes, then eight bytes equal to P's
    /// own CRC-32 and length (the CRC forced by four free bytes after them),
    /// then a whole gzip member M2, then 500 more bytes, all in one stored
    /// block. The trailer search took the first eight as the trailer and
    /// decoded M2 as a member of the file. gzip -d and the runtime's
    /// GZipStream, reading member by member, give P and nothing else, and so
    /// must Extract all.
    /// </summary>
    [Fact]
    public void A_member_whose_data_holds_its_own_trailer_decodes_as_gzip_does()
    {
        var a = new byte[3000];
        new Random(1).NextBytes(a);
        var z = new byte[500];
        new Random(2).NextBytes(z);
        var hidden = Compress(ArchiveFormat.Gz, "SMUGGLED: read only by a reader that guesses where a member ends\n"u8.ToArray());
        var len = a.Length + 8 + 4 + hidden.Length + z.Length;

        byte[] p = [.. a, .. new byte[12], .. hidden, .. z];
        const uint target = 0x5EC0DE01;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(a.Length), target);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(a.Length + 4), (uint)len);
        ForceCrc(p, a.Length + 8, target);

        Assert.Equal(target, ZipDirectory.Crc32(p));

        byte[] trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, target);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), (uint)len);
        byte[] file =
        [
            0x1F, 0x8B, 0x08, 0x00, 0, 0, 0, 0, 0, 0x03,
            0x01, (byte)len, (byte)(len >> 8), (byte)~len, (byte)(~len >> 8), .. p,
            .. trailer,
        ];

        var runtime = new MemoryStream();

        using (var g = new GZipStream(new MemoryStream(file), CompressionMode.Decompress)) g.CopyTo(runtime);

        Assert.Equal(Convert.ToHexString(p), Convert.ToHexString(runtime.ToArray()));
        Assert.Equal(Convert.ToHexString(p), Landed("fake.txt.gz", file)[""]);

        // With its real trailer gone, nothing inside it can stand in for one.
        file.AsSpan(file.Length - 8).Clear();
        AssertDamaged("fake.txt.gz", file);
    }

    /// <summary>
    /// Sets the four bytes at <paramref name="at"/> so the CRC-32 of
    /// <paramref name="p"/> is <paramref name="target"/>. CRC-32 is linear
    /// over GF(2), so each of the 32 bits flips a fixed pattern of the CRC and
    /// the bits are found by elimination.
    /// </summary>
    private static void ForceCrc(byte[] p, int at, uint target)
    {
        p.AsSpan(at, 4).Clear();

        var start = ZipDirectory.Crc32(p);
        var rows = new ulong[32];

        for (var bit = 0; bit < 32; bit++)
        {
            p[at + (bit / 8)] ^= (byte)(1 << (bit % 8));
            var column = ZipDirectory.Crc32(p) ^ start;
            p[at + (bit / 8)] ^= (byte)(1 << (bit % 8));

            for (var r = 0; r < 32; r++)
                if (((column >> r) & 1) != 0) rows[r] |= 1UL << bit;
        }

        var want = start ^ target;

        for (var r = 0; r < 32; r++)
            if (((want >> r) & 1) != 0) rows[r] |= 1UL << 32;

        var pivots = new int[32];
        var rank = 0;

        for (var c = 0; c < 32; c++)
        {
            pivots[c] = -1;

            var found = Enumerable.Range(rank, 32 - rank).FirstOrDefault(r => ((rows[r] >> c) & 1) != 0, -1);

            if (found < 0) continue;

            (rows[rank], rows[found]) = (rows[found], rows[rank]);

            for (var r = 0; r < 32; r++)
                if (r != rank && ((rows[r] >> c) & 1) != 0) rows[r] ^= rows[rank];

            pivots[c] = rank++;
        }

        for (var c = 0; c < 32; c++)
            if (pivots[c] >= 0 && ((rows[pivots[c]] >> 32) & 1) != 0) p[at + (c / 8)] ^= (byte)(1 << (c % 8));
    }
}
