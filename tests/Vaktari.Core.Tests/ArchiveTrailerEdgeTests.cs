using System.Buffers.Binary;
using System.IO.Compression;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Rules of the member readers that ArchiveTrailerTests reaches only by way of
/// the runtime's and SharpCompress's own writers, and so did not pin (RC QA,
/// round 2: a mutation of each stayed green).
///
/// - A gzip header with a reserved flag set is refused.
/// - A gzip member ends where its deflate data ends, wherever that falls
///   relative to the chunks the inflater reads — at the start of its last
///   chunk or straddling the edge between its last two.
/// - The end-of-file marker is handed over only once the file has given its
///   last byte, including when that last read returns a single byte (RC QA,
///   round 3: GZipStream reads its input 128 KiB at a time, so only a file of
///   one byte more than a multiple of that ends that way).
/// - A zstd frame header is sized by its own descriptor: the single-segment
///   flag drops the window byte, and each content-size flag gives its field's
///   width. The writers the other tests use set neither, so frames here are
///   written by hand from raw and RLE blocks.
/// </summary>
public sealed class ArchiveTrailerEdgeTests
{
    private static byte[] Decode(byte[] file, ArchiveFormat format)
    {
        using var input = new MemoryStream(file);
        using var decoded = ArchiveReader.Decompress(input, format);
        var output = new MemoryStream();
        decoded.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Random_(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Gz(byte[] data, CompressionLevel level)
    {
        var ms = new MemoryStream();
        using (var z = new GZipStream(ms, level, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ---- gzip -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    public void A_gzip_header_with_a_reserved_flag_is_damage(byte flag)
    {
        var data = Random_(2000, 1);
        var file = Gz(data, CompressionLevel.Optimal);

        Assert.Equal(data, Decode(file, ArchiveFormat.Gz));

        file[3] |= flag;

        Assert.ThrowsAny<InvalidDataException>(() => Decode(file, ArchiveFormat.Gz));
    }

    /// <summary>Lengths around the inflater's chunk edges, stored and deflated,
    /// each followed by a second member: every one decodes whole, and one byte
    /// cut off the end is always damage.</summary>
    public static TheoryData<int, CompressionLevel> Edges()
    {
        var data = new TheoryData<int, CompressionLevel>();

        foreach (var start in new[] { 0, 4050, 8150, 16340, 32730, 65500 })
            foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Optimal })
                data.Add(start, level);

        return data;
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void A_gzip_trailer_is_found_across_every_chunk_edge(int start, CompressionLevel level)
    {
        for (var n = start; n < start + 120; n++)
        {
            var first = Random_(n, n);
            var second = Random_(333, n + 7);
            byte[] file = [.. Gz(first, level), .. Gz(second, level)];

            Assert.Equal([.. first, .. second], Decode(file, ArchiveFormat.Gz));
            Assert.ThrowsAny<InvalidDataException>(() => Decode(file[..^1], ArchiveFormat.Gz));
        }
    }

    /// <summary>A stored .gz whose length is one, two or seventeen bytes more
    /// than a multiple of 128 KiB — GZipStream's input read — so its last read
    /// returns that few bytes: it decodes whole, and cut by one byte it is
    /// damage.</summary>
    [Theory]
    [InlineData(131072 + 1)]
    [InlineData(131072 + 2)]
    [InlineData(131072 + 17)]
    [InlineData(262144 + 1)]
    public void A_gz_whose_last_input_read_is_a_few_bytes_decodes_whole(int length)
    {
        byte[]? file = null, data = null;

        for (var n = length - 80; n < length && file is null; n++)
        {
            var candidate = Random_(n, n);
            var made = Gz(candidate, CompressionLevel.NoCompression);

            if (made.Length == length) (file, data) = (made, candidate);
        }

        Assert.True(file is not null, $"no stored .gz of exactly {length} bytes was found");
        Assert.Equal(data, Decode(file, ArchiveFormat.Gz));
        Assert.ThrowsAny<InvalidDataException>(() => Decode(file[..^1], ArchiveFormat.Gz));
    }

    // ---- zstd -----------------------------------------------------------------------------

    private static byte[] Frame(byte descriptor, byte[] headerTail, params byte[][] blocks)
        => [0x28, 0xB5, 0x2F, 0xFD, descriptor, .. headerTail, .. blocks.SelectMany(b => b)];

    /// <summary>A block header: last flag, type (0 raw, 1 RLE), size.</summary>
    private static byte[] Block(int type, bool last, int size, byte[] body)
    {
        var value = (last ? 1 : 0) | (type << 1) | (size << 3);

        return [(byte)value, (byte)(value >> 8), (byte)(value >> 16), .. body];
    }

    private static readonly byte[] Raw = Random_(3000, 5);

    public static TheoryData<string> Frames() => ["window", "single-1", "single-2", "single-4", "window-8"];

    private static byte[] FrameFor(string shape)
    {
        var raw = Block(0, false, Raw.Length, Raw);
        var rle = Block(1, true, 700, [0x41]);
        var total = Raw.Length + 700;

        return shape switch
        {
            // no content size, a window byte (2^17)
            "window" => Frame(0x00, [0x38], raw, rle),
            // single segment: no window byte, content size in 1, 2 (minus 256) or 4 bytes
            "single-1" => Frame(0x20, [0xFF], Block(0, false, 200, Raw[..200]), Block(1, true, 55, [0x41])),
            "single-2" => Frame(0x60, [(byte)(total - 256), (byte)((total - 256) >> 8)], raw, rle),
            "single-4" => Frame(0xA0, BitConverter.GetBytes((uint)total), raw, rle),
            // a window byte and an 8-byte content size
            "window-8" => Frame(0xC0, [0x38, .. BitConverter.GetBytes((ulong)total)], raw, rle),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static byte[] Expected(string shape)
        => shape == "single-1"
            ? [.. Raw[..200], .. Enumerable.Repeat((byte)0x41, 55)]
            : [.. Raw, .. Enumerable.Repeat((byte)0x41, 700)];

    /// <summary>Each header shape, alone, followed by another frame, and
    /// followed by trailing bytes: the frame walk must size the header exactly,
    /// or the second frame is missed or the trailing bytes are fed to the
    /// decoder.</summary>
    [Theory]
    [MemberData(nameof(Frames))]
    public void Every_zstd_frame_header_shape_is_walked(string shape)
    {
        var frame = FrameFor(shape);
        var expected = Expected(shape);

        Assert.Equal(expected, Decode(frame, ArchiveFormat.Zst));
        Assert.Equal([.. expected, .. expected], Decode([.. frame, .. frame], ArchiveFormat.Zst));
        Assert.Equal(expected, Decode([.. frame, .. new byte[16]], ArchiveFormat.Zst));
        Assert.ThrowsAny<Exception>(() => Decode(frame[..^1], ArchiveFormat.Zst));
    }
}
