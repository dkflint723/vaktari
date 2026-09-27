using System.IO.Compression;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Our own reading of a zip's central directory: the source of truth for
/// sizes, CRCs, flags and link bits, and the zip-bomb check.
/// </summary>
public sealed class ZipDirectoryTests
{
    private static ZipDirectory Read(byte[] zip) => ZipDirectory.Read(new MemoryStream(zip));

    [Fact]
    public void An_ordinary_zip_reads_whole()
    {
        var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("folder/");
            using var w = new StreamWriter(zip.CreateEntry("folder/a.txt").Open());
            w.Write(new string('a', 5000));
        }

        var dir = Read(ms.ToArray());

        Assert.Equal(["folder/", "folder/a.txt"], dir.Records.Select(r => r.Name));
        Assert.Equal(5000, dir.Records[1].UncompressedSize);
        Assert.Equal(ZipBytes.Crc(Encoding.ASCII.GetBytes(new string('a', 5000))), dir.Records[1].Crc);
        Assert.False(dir.Overlapping);
        Assert.False(dir.AnyEncrypted);
        Assert.Equal(32, dir.Digest.Length);
    }

    /// <summary>A directory of 65,536 entries needs Zip64's record for its
    /// count alone.</summary>
    [Fact]
    public void A_zip64_directory_is_read()
    {
        var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < 65_536; i++) zip.CreateEntry($"e{i}", CompressionLevel.NoCompression);

        var dir = Read(ms.ToArray());

        Assert.Equal(65_536, dir.Records.Count);
        Assert.False(dir.Overlapping);
    }

    [Fact]
    public void Fifty_records_pointing_at_one_entry_overlap()
    {
        var entries = new List<ZipBytes.Entry> { new("0.bin") { Data = new byte[1000], Method = 8 } };

        for (var i = 1; i < 50; i++)
            entries.Add(new ZipBytes.Entry($"{i}.bin") { Data = new byte[1000], Method = 8, SharesWith = 0 });

        Assert.True(Read(ZipBytes.Build([.. entries])).Overlapping);
    }

    [Fact]
    public void A_record_running_past_its_data_overlaps()
    {
        var zip = ZipBytes.Build(new ZipBytes.Entry("a.txt") { Data = "short"u8.ToArray() });

        // The compressed size at offset 20 of the central record claims far
        // more than the file holds.
        var central = IndexOf(zip, [0x50, 0x4B, 0x01, 0x02]);

        BitConverter.GetBytes(100_000u).CopyTo(zip, central + 20);

        Assert.True(Read(zip).Overlapping);
    }

    /// <summary>
    /// **A Zip64 size of 0xFFFF…FF read as a long is −1**, which shrank the
    /// declared total and slipped an entry past the room check. A 64-bit
    /// field past long.MaxValue is damage.
    /// </summary>
    [Fact]
    public void A_zip64_size_past_the_largest_long_is_damage()
    {
        var zip = ZipBytes.Build(new ZipBytes.Entry("huge.bin") { Data = "x"u8.ToArray(), Size64 = -1 });

        Assert.Throws<InvalidDataException>(() => Read(zip));
    }

    [Fact]
    public void A_unix_symlink_is_known_by_its_mode()
    {
        var dir = Read(ZipBytes.Build(new ZipBytes.Entry("link") { Data = "target"u8.ToArray(), MadeBy = 0x031E, External = 0xA1FFu << 16 }));

        Assert.Equal(3, dir.Records[0].Host);
        Assert.Equal(0xA1FF, dir.Records[0].UnixMode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_AES_version_is_read(ushort version)
    {
        var dir = Read(ZipBytes.Build(new ZipBytes.Entry("s.txt") { Data = "x"u8.ToArray(), Flags = 1, Method = 99, Extra = ZipBytes.AesExtra(version) }));

        Assert.Equal(version, dir.Records[0].AesVersion);
        Assert.True(dir.AnyEncrypted);
    }

    /// <summary>
    /// **SharpCompress reports the LOCAL header's name** when the two disagree
    /// (E-16): one archive can show one name to a listing and extract as
    /// another. Refused as damaged.
    /// </summary>
    [Fact]
    public void A_local_header_naming_another_file_is_damage()
    {
        var zip = ZipBytes.Build(new ZipBytes.Entry("central.txt") { Data = "x"u8.ToArray(), LocalName = "local!.txt"u8.ToArray() });

        Assert.Throws<InvalidDataException>(() => Read(zip));
    }

    [Fact]
    public void The_unicode_path_extra_is_believed_only_for_its_own_name()
    {
        var name = "plain.txt"u8.ToArray();
        var good = UnicodePath(name, "unié.txt", ZipBytes.Crc(name));
        var stale = UnicodePath(name, "other.txt", 0x12345678);

        Assert.Equal("unié.txt", Read(ZipBytes.Build(new ZipBytes.Entry("plain.txt") { Extra = good })).Records[0].Name);
        Assert.Equal("plain.txt", Read(ZipBytes.Build(new ZipBytes.Entry("plain.txt") { Extra = stale })).Records[0].Name);
    }

    [Fact]
    public void Not_a_zip_has_no_directory()
        => Assert.Throws<InvalidDataException>(() => Read("this is not a zip at all"u8.ToArray()));

    private static byte[] UnicodePath(byte[] nameBytes, string unicode, uint crc)
    {
        var u = Encoding.UTF8.GetBytes(unicode);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write((ushort)0x7075);
        w.Write((ushort)(5 + u.Length));
        w.Write((byte)1);
        w.Write(crc);
        w.Write(u);

        return ms.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;

        return -1;
    }
}
