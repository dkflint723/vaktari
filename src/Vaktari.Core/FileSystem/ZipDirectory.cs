using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Common;

namespace Vaktari.Core.FileSystem;

/// <summary>One central-directory record, with where its data really is.</summary>
internal sealed record ZipRecord(
    long LocalOffset,
    long DataOffset,
    long CompressedSize,
    long UncompressedSize,
    uint Crc,
    ushort Flags,
    ushort Method,
    ushort MadeBy,
    uint ExternalAttributes,
    byte[] NameBytes,
    int AesVersion)
{
    /// <summary>The name, decoded by the house rule (<see cref="Cp437Names"/>),
    /// or by the Info-ZIP Unicode Path extra when it vouches for these very
    /// bytes.</summary>
    public string Name { get; init; } = "";

    public bool Encrypted => (Flags & 1) != 0;

    /// <summary>The host that made the entry: 3 is Unix.</summary>
    public int Host => MadeBy >> 8;

    /// <summary>The Unix mode, file type included, of a Unix-made entry.</summary>
    public int? UnixMode => Host == 3 ? (int)(ExternalAttributes >> 16) : null;
}

/// <summary>
/// A zip's central directory, read by us, and the source of truth for every
/// size, CRC, flag and link bit Extract all acts on.
///
/// **SharpCompress reads a zip by the central directory's offsets and takes
/// everything else from the local header.** Measured (E-16, E-28): its
/// <c>Entries</c> come in central-directory order and one-to-one with it —
/// a local entry the directory does not name is never seen — but when the
/// two headers disagree it reports the LOCAL name and size. An archive can say
/// one thing to a listing and another to an extraction; this reads the
/// directory, and a local header whose name bytes differ from its directory
/// record makes the archive damaged rather than ambiguous.
///
/// **SharpCompress checks no CRC on a zip** (E-25: a stored entry and a
/// deflated one with a wrong CRC both read without complaint), so ours is the
/// only check, and it needs the directory's CRC. AE-2 entries record CRC 0 by
/// design and are authenticated by an HMAC instead; that is Stage D, and they
/// are refused before any of this matters.
///
/// **SharpCompress exposes the raw external attributes but not who made
/// them** (E-27: <c>Attrib</c> is 0xA1FF0000 for a Unix symlink, and
/// <c>LinkTarget</c> is empty), so a link is recognised here, from the
/// made-by host and the mode's file type.
///
/// **The overlap check is the zip bomb check.** A "non-recursive" zip bomb
/// points hundreds of directory records at one compressed stream, each
/// inflating to the whole of it. Every record's range — local header, name,
/// extra and data — must be disjoint from every other's and must end inside
/// the file.
/// </summary>
internal sealed class ZipDirectory
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;

    public required IReadOnlyList<ZipRecord> Records { get; init; }

    /// <summary>Two records share bytes, or one runs past the end.</summary>
    public required bool Overlapping { get; init; }

    public bool AnyEncrypted => Records.Any(r => r.Encrypted);

    /// <summary>SHA-256 of the central directory's bytes — part of an
    /// archive's identity in Stage C.</summary>
    public required byte[] Digest { get; init; }

    /// <summary>
    /// Reads the directory of a seekable stream.
    /// </summary>
    /// <exception cref="InvalidDataException">No end record, or a directory
    /// that is not where its end record says, or a local header that is not
    /// one, or one that names a different file than its record.</exception>
    public static ZipDirectory Read(Stream s)
    {
        var length = s.Length;

        // The end record is 22 bytes and may be followed by a comment of up to
        // 65,535, so it is somewhere in the last 65,557.
        var tailLength = (int)Math.Min(length, 22 + 65_535);
        var tail = new byte[tailLength];

        s.Position = length - tailLength;
        s.ReadExactly(tail);

        var at = -1;

        for (var i = tailLength - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature)
            {
                at = i;
                break;
            }

        if (at < 0) throw new InvalidDataException("no end of central directory record");

        var endOffset = length - tailLength + at;
        var end = tail.AsSpan(at);

        long count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var cdEnd = endOffset;

        if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            // Zip64: a locator sits right before the end record and points at
            // the Zip64 end record, which carries the real numbers.
            if (endOffset < 20) throw new InvalidDataException("zip64 locator missing");

            var locator = ReadAt(s, endOffset - 20, 20);

            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                throw new InvalidDataException("zip64 locator missing");

            var z64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            var z64 = ReadAt(s, z64Offset, 56);

            if (BinaryPrimitives.ReadUInt32LittleEndian(z64) != Zip64EndSignature)
                throw new InvalidDataException("zip64 end record missing");

            count = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(32));
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(48));
            cdEnd = z64Offset;
        }

        // **Bytes in front of the archive** — a self-extracting stub — move
        // everything; the directory then sits later than its end record says
        // by exactly that much, and every offset in it shifts with it.
        var shift = cdEnd - cdSize - cdOffset;

        if (shift < 0 || cdSize > cdEnd) throw new InvalidDataException("central directory out of place");

        var directory = ReadAt(s, cdOffset + shift, checked((int)cdSize));
        var records = new List<ZipRecord>((int)Math.Min(count, 1 << 20));
        var pos = 0;
        var disagrees = false;

        for (long n = 0; n < count; n++)
        {
            if (pos + 46 > directory.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(pos)) != CentralSignature)
                throw new InvalidDataException("central directory record damaged");

            var r = directory.AsSpan(pos);
            var madeBy = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(r[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(r[10..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(r[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(r[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(r[32..]);
            var external = BinaryPrimitives.ReadUInt32LittleEndian(r[38..]);
            long local = BinaryPrimitives.ReadUInt32LittleEndian(r[42..]);

            if (pos + 46 + nameLength + extraLength + commentLength > directory.Length)
                throw new InvalidDataException("central directory record damaged");

            var nameBytes = r.Slice(46, nameLength).ToArray();
            var extra = r.Slice(46 + nameLength, extraLength);
            var aes = 0;
            string? unicode = null;

            foreach (var (id, data) in Extras(extra))
            {
                if (id == 0x0001)
                {
                    // Zip64: only the fields that overflowed are present, in
                    // this order.
                    var z = data;
                    if (uncompressed == 0xFFFFFFFF && z.Length >= 8) { uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(z); z = z[8..]; }
                    if (compressed == 0xFFFFFFFF && z.Length >= 8) { compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(z); z = z[8..]; }
                    if (local == 0xFFFFFFFF && z.Length >= 8) local = (long)BinaryPrimitives.ReadUInt64LittleEndian(z);
                }
                else if (id == 0x9901 && data.Length >= 7)
                {
                    aes = BinaryPrimitives.ReadUInt16LittleEndian(data);
                }
                else if (id == 0x7075 && data.Length > 5 && data[0] == 1
                         && BinaryPrimitives.ReadUInt32LittleEndian(data[1..]) == Crc32(nameBytes))
                {
                    try { unicode = new UTF8Encoding(false, true).GetString(data[5..]); }
                    catch (DecoderFallbackException) { unicode = null; }
                }
            }

            var name = unicode ?? Cp437Names.Decode(
                nameBytes, 0, nameBytes.Length, (flags & 0x800) != 0 ? EncodingType.UTF8 : EncodingType.Default);

            local += shift;

            var header = ReadAt(s, local, 30, "local header outside the file");

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature)
                throw new InvalidDataException("local header missing");

            var localName = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var localExtra = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));

            // Noted, and thrown only after the overlap check below: a bomb's
            // records all share one local header, so every record but one
            // disagrees with it, and the refusal that names the bomb is the
            // useful one.
            if (!ReadAt(s, local + 30, localName, "local header outside the file").AsSpan().SequenceEqual(nameBytes))
                disagrees = true;

            records.Add(new ZipRecord(
                local, local + 30 + localName + localExtra, compressed, uncompressed, crc,
                flags, method, madeBy, external, nameBytes, aes) { Name = name });

            pos += 46 + nameLength + extraLength + commentLength;
        }

        var overlapping = Overlaps(records, cdOffset + shift);

        if (disagrees && !overlapping)
            throw new InvalidDataException("local header names a different file than the central directory");

        return new ZipDirectory
        {
            Records = records,
            Overlapping = overlapping,
            Digest = SHA256.HashData(directory),
        };
    }

    /// <summary>
    /// Whether any two records' byte ranges meet, or any runs into the
    /// central directory. A data descriptor, when one follows, is not
    /// counted: it is at most 24 bytes and lies after the data, so leaving
    /// it out can only make this more lenient toward honest archives.
    /// </summary>
    private static bool Overlaps(List<ZipRecord> records, long directoryStart)
    {
        var ranges = records
            .Select(r => (Start: r.LocalOffset, End: r.DataOffset + r.CompressedSize))
            .OrderBy(r => r.Start)
            .ToList();

        for (var i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].End > directoryStart || ranges[i].End < ranges[i].Start) return true;
            if (i > 0 && ranges[i].Start < ranges[i - 1].End) return true;
        }

        return false;
    }

    private static IEnumerable<(ushort Id, byte[] Data)> Extras(ReadOnlySpan<byte> extra)
    {
        var found = new List<(ushort, byte[])>();

        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);

            if (4 + size > extra.Length) break;

            found.Add((id, extra.Slice(4, size).ToArray()));
            extra = extra[(4 + size)..];
        }

        return found;
    }

    private static byte[] ReadAt(Stream s, long offset, int count, string? outside = null)
    {
        if (offset < 0 || offset + count > s.Length)
            throw new InvalidDataException(outside ?? "zip structure outside the file");

        var buffer = new byte[count];

        s.Position = offset;
        s.ReadExactly(buffer);

        return buffer;
    }

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = new SharpCompress.Compressors.Deflate.CRC32();
        var array = bytes.ToArray();

        crc.SlurpBlock(array, 0, array.Length);

        return (uint)crc.Crc32Result;
    }
}
