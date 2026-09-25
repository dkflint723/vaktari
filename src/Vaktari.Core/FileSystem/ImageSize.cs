using System.Buffers.Binary;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// An image's pixel dimensions, read from its header without decoding it.
///
/// **Why not just decode and look.** Decoding to find out whether a picture is
/// worth decoding is backwards, and the file this exists for is a 32 pixel
/// favicon: the answer is a few bytes in, and a 40 megapixel photo would cost
/// hundreds of megabytes to answer the same question.
///
/// **Deliberately partial.** PNG, JPEG, GIF and BMP cover essentially every
/// thumbnail source here, and anything else returns null — which callers must
/// read as "unknown", never as "small". Guessing a size for a format we cannot
/// parse would suppress thumbnails for it entirely.
/// </summary>
public static class ImageSize
{
    public static (int Width, int Height)? TryRead(string path)
    {
        try
        {
            // A picture kept online has its header in the part that is not on
            // the disk, and reading 32 bytes of it fetches the file —
            // measured, see OnlineOnly. Unknown, which callers already read
            // as unknown.
            if (OnlineOnly.Is(path)) return null;

            using var stream = File.OpenRead(path);

            Span<byte> head = stackalloc byte[32];
            var read = stream.ReadAtLeast(head, 32, throwOnEndOfStream: false);
            if (read < 16) return null;

            // PNG: an 8 byte signature, then IHDR whose first two fields are
            // width and height as big-endian 32 bit integers.
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                return (
                    (int)BinaryPrimitives.ReadUInt32BigEndian(head[16..20]),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..24]));

            // GIF: "GIF87a"/"GIF89a", then width and height little-endian 16 bit.
            if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
                return (
                    BinaryPrimitives.ReadUInt16LittleEndian(head[6..8]),
                    BinaryPrimitives.ReadUInt16LittleEndian(head[8..10]));

            // BMP: "BM", then a header whose width and height are little-endian
            // 32 bit at offset 18. Height is signed — a negative one means the
            // rows are stored top-down, not that the image is inside out.
            if (head[0] == 'B' && head[1] == 'M')
                return (
                    BinaryPrimitives.ReadInt32LittleEndian(head[18..22]),
                    Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..26])));

            if (head[0] == 0xFF && head[1] == 0xD8) return Jpeg(stream);

            return null;
        }
        catch
        {
            // Unreadable is unknown, which is the safe answer here.
            return null;
        }
    }

    /// <summary>
    /// JPEG keeps its size in a start-of-frame marker, which sits after a
    /// variable number of other segments, so the file has to be walked.
    ///
    /// The frame markers are C0-CF EXCEPT C4, C8 and CC, which are Huffman
    /// tables and arithmetic-coding conditioning — reading those as a frame
    /// gives a plausible but wrong answer, which is worse than none.
    /// </summary>
    private static (int, int)? Jpeg(FileStream stream)
    {
        stream.Position = 2;

        Span<byte> pair = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[7];

        while (true)
        {
            // Markers are 0xFF followed by a type; padding 0xFF bytes are legal
            // between segments and must be skipped rather than counted.
            int marker;
            do
            {
                if (stream.Read(pair[..1]) != 1) return null;
            }
            while (pair[0] != 0xFF);

            do
            {
                if (stream.Read(pair[..1]) != 1) return null;
                marker = pair[0];
            }
            while (marker == 0xFF);

            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;

            if (stream.Read(pair) != 2) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(pair);
            if (length < 2) return null;

            var isFrame = marker >= 0xC0 && marker <= 0xCF
                          && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (isFrame)
            {
                if (stream.Read(frame[..5]) != 5) return null;

                // precision, then height then width — height first, unlike
                // every other format here.
                return (
                    BinaryPrimitives.ReadUInt16BigEndian(frame[3..5]),
                    BinaryPrimitives.ReadUInt16BigEndian(frame[1..3]));
            }

            stream.Position += length - 2;
        }
    }
}
