using System.IO.Compression;
using System.Text;

namespace Vaktari.Core.Tests;

/// <summary>
/// Zips written byte by byte, for the archives no honest writer produces:
/// arbitrary name bytes and flags, a central directory that disagrees with
/// its local headers, sizes and CRCs that lie, records that share data, Unix
/// modes that make a link.
///
/// **By hand rather than through a library**, because every library that
/// writes zips writes them correctly — which is exactly the property these
/// tests need to be without.
/// </summary>
internal static class ZipBytes
{
    internal sealed record Entry(string Name)
    {
        /// <summary>The name as bytes, instead of <see cref="Name"/> in ASCII.</summary>
        public byte[]? NameBytes { get; init; }

        /// <summary>What the local header calls it, when that should differ.</summary>
        public byte[]? LocalName { get; init; }

        public byte[] Data { get; init; } = [];

        /// <summary>8 deflates <see cref="Data"/>; 0 stores it.</summary>
        public ushort Method { get; init; }

        public ushort Flags { get; init; }

        /// <summary>The recorded CRC, when it should not be the real one.</summary>
        public uint? Crc { get; init; }

        /// <summary>The recorded uncompressed size, when it should lie.</summary>
        public uint? Size { get; init; }

        /// <summary>A Zip64 uncompressed size, recorded in the 0x0001 extra
        /// of both headers — a size a 32-bit field cannot hold.</summary>
        public long? Size64 { get; init; }

        public ushort MadeBy { get; init; } = 20;
        public uint External { get; init; }
        public byte[] Extra { get; init; } = [];

        /// <summary>Point this record at an earlier entry's local header,
        /// writing no local header of its own.</summary>
        public int? SharesWith { get; init; }

        internal byte[] Bytes => NameBytes ?? Encoding.ASCII.GetBytes(Name);
    }

    internal static byte[] Build(params Entry[] entries)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var offsets = new long[entries.Length];
        var packed = new byte[entries.Length][];

        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];

            packed[i] = e.Method == 8 ? Deflate(e.Data) : e.Data;

            if (e.SharesWith is { } other)
            {
                offsets[i] = offsets[other];
                continue;
            }

            offsets[i] = ms.Position;

            var name = e.LocalName ?? e.Bytes;
            var localExtra = Zip64(e, packed[i]);

            w.Write(0x04034b50u);
            w.Write((ushort)20);
            w.Write(e.Flags);
            w.Write(e.Method);
            w.Write((ushort)0);
            w.Write((ushort)0x21);
            w.Write(e.Crc ?? Crc(e.Data));
            w.Write(e.Size64 is null ? (uint)packed[i].Length : 0xFFFFFFFF);
            w.Write(e.Size64 is null ? e.Size ?? (uint)e.Data.Length : 0xFFFFFFFF);
            w.Write((ushort)name.Length);
            w.Write((ushort)localExtra.Length);
            w.Write(name);
            w.Write(localExtra);
            w.Write(packed[i]);
        }

        var start = ms.Position;

        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var name = e.Bytes;
            var extra = Zip64(e, packed[i]).Concat(e.Extra).ToArray();

            w.Write(0x02014b50u);
            w.Write(e.MadeBy);
            w.Write((ushort)20);
            w.Write(e.Flags);
            w.Write(e.Method);
            w.Write((ushort)0);
            w.Write((ushort)0x21);
            w.Write(e.Crc ?? Crc(e.Data));
            w.Write(e.Size64 is null ? (uint)packed[i].Length : 0xFFFFFFFF);
            w.Write(e.Size64 is null ? e.Size ?? (uint)e.Data.Length : 0xFFFFFFFF);
            w.Write((ushort)name.Length);
            w.Write((ushort)extra.Length);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(e.External);
            w.Write((uint)offsets[i]);
            w.Write(name);
            w.Write(extra);
        }

        var length = ms.Position - start;

        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)entries.Length);
        w.Write((ushort)entries.Length);
        w.Write((uint)length);
        w.Write((uint)start);
        w.Write((ushort)0);

        return ms.ToArray();
    }

    private static byte[] Zip64(Entry e, byte[] packed)
    {
        if (e.Size64 is not { } size) return [];

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write((ushort)0x0001);
        w.Write((ushort)16);
        w.Write(size);
        w.Write((long)packed.Length);

        return ms.ToArray();
    }

    /// <summary>The WinZip AES extra: vendor version, "AE", strength 3
    /// (256-bit), and the real method.</summary>
    internal static byte[] AesExtra(ushort version)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write((ushort)0x9901);
        w.Write((ushort)7);
        w.Write(version);
        w.Write((byte)'A');
        w.Write((byte)'E');
        w.Write((byte)3);
        w.Write((ushort)8);

        return ms.ToArray();
    }

    internal static uint Crc(byte[] data)
    {
        var crc = new SharpCompress.Compressors.Deflate.CRC32();

        crc.SlurpBlock(data, 0, data.Length);

        return (uint)crc.Crc32Result;
    }

    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();

        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);

        return ms.ToArray();
    }
}
