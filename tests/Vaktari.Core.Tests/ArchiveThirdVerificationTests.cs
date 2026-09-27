using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The third independent verification of archive Stage A. The two product
/// fixes and three smaller ones from the second verification (commit 56c2a81)
/// re-passed every probe; the hunt for a deeper hole found none. This pins the
/// one gap the hunt did surface: the Zip64 guard that the locator's shift and
/// the directory's shift are the same is exercised by no other test, though
/// the record-beside-the-locator choice it backs up is.
/// </summary>
public sealed class ArchiveThirdVerificationTests
{
    /// <summary>
    /// A Zip64 archive behind a prefix whose locator names an offset that does
    /// not match where the directory says the bytes were shifted to: the two
    /// readers would part ways, so it is refused rather than read. Here the
    /// locator's written offset is a lie (0) while a real Zip64 end record
    /// sits beside it, so the shift read from the locator's own position and
    /// the shift the directory implies disagree.
    /// </summary>
    [Fact]
    public void A_zip64_locator_that_disagrees_with_the_directory_shift_is_refused()
    {
        var data = new byte[] { (byte)'g', (byte)'o', (byte)'o', (byte)'d' };
        var crc = ZipDirectory.Crc32(data);

        var local = Local("a.txt", data, crc);
        var central = Central("a.txt", crc, (uint)data.Length, cdOffsetRelative: 0);
        byte[] prefix = new byte[128];

        byte[] bytes =
        [
            .. prefix,
            .. local,
            .. central,
            .. Zip64End(cdSize: central.Length, cdOffsetRelative: local.Length),
            .. Zip64Locator(writtenOffset: 0),   // a lie: the record is beside, not at 0
            .. End(),
        ];

        var thrown = Assert.Throws<InvalidDataException>(() => ZipDirectory.Read(new MemoryStream(bytes)));

        Assert.Equal("zip64 locator and directory disagree", thrown.Message);
    }

    /// <summary>An honest Zip64 archive behind the same prefix — the locator's
    /// offset counts from the start of the zip, so the two shifts agree — is
    /// read, and its one entry decodes. The refusal above is the disagreement,
    /// not the prefix.</summary>
    [Fact]
    public void An_honest_zip64_behind_a_prefix_is_read()
    {
        var data = new byte[] { (byte)'g', (byte)'o', (byte)'o', (byte)'d' };
        var crc = ZipDirectory.Crc32(data);

        var local = Local("a.txt", data, crc);
        var central = Central("a.txt", crc, (uint)data.Length, cdOffsetRelative: 0);
        byte[] prefix = new byte[128];
        long z64Relative = local.Length + central.Length;

        byte[] bytes =
        [
            .. prefix,
            .. local,
            .. central,
            .. Zip64End(cdSize: central.Length, cdOffsetRelative: local.Length),
            .. Zip64Locator(writtenOffset: z64Relative),   // honest: counted from the zip's own start
            .. End(),
        ];

        var directory = ZipDirectory.Read(new MemoryStream(bytes));

        Assert.Equal(128, directory.Shift);
        var record = directory.Records.Single();
        Assert.Equal("a.txt", record.Name);
        Assert.Equal(data, bytes.AsSpan((int)record.DataOffset, (int)record.CompressedSize).ToArray());
    }

    private static byte[] Local(string name, byte[] data, uint crc)
    {
        var n = System.Text.Encoding.UTF8.GetBytes(name);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // version, flags, method, time, date
        w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length);
        w.Write((ushort)n.Length); w.Write((ushort)0); w.Write(n); w.Write(data);
        return ms.ToArray();
    }

    private static byte[] Central(string name, uint crc, uint size, long cdOffsetRelative)
    {
        var n = System.Text.Encoding.UTF8.GetBytes(name);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // made by, needed, flags, method, time, date
        w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(crc); w.Write(size); w.Write(size); w.Write((ushort)n.Length); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)cdOffsetRelative); w.Write(n);
        return ms.ToArray();
    }

    private static byte[] Zip64End(long cdSize, long cdOffsetRelative)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x06064b50u); w.Write(44L); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write(1L); w.Write(1L); w.Write(cdSize); w.Write(cdOffsetRelative);
        return ms.ToArray();
    }

    private static byte[] Zip64Locator(long writtenOffset)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x07064b50u); w.Write(0u); w.Write(writtenOffset); w.Write(1u);
        return ms.ToArray();
    }

    private static byte[] End()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)0);
        return ms.ToArray();
    }
}
