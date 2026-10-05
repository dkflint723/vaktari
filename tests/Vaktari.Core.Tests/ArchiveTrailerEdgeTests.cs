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
}
