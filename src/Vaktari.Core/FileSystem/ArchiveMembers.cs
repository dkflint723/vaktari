using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace Vaktari.Core.FileSystem;

// **Where a compressed stream ends, for every decoder in this file.** Each one
// reads stream after stream (gzip and lzip members, bzip2 and xz streams, zstd
// frames) and ends after the last one that finished cleanly with its own check
// passed. What follows it is trailing data, not part of the archive, and is
// left unread: gzip, bzip2 and lzip ignore it with at most a warning, and GNU
// tar extracts a padded .tar.bz2; xz and zstd are held to the same rule, so
// there is one rule rather than five. Bytes that START with the format's own
// signature are not trailing data but another stream, which must be whole —
// so a second member cut short is damage, not something to drop. A failure
// anywhere inside a stream is damage. A bare stream and a compressed tar are
// read through the same decoders, so the rule is the same for both.

/// <summary>
/// An .xz file made of several streams, read as one.
///
/// **A concatenated .xz landed with its first stream only, silently**
/// (review of Stage A: <c>lzma.compress(a) + lzma.compress(b)</c> extracted as
/// <c>a</c>). The format allows any number of streams, each optionally
/// followed by null padding in multiples of four bytes, and
/// <see cref="SharpCompress.Compressors.Xz.XZStream"/> stops at the end of
/// the first. Measured: it leaves the file exactly at that end, so the next
/// stream can be started where it stopped. Padding before another stream must
/// be in fours; anything after the last stream that is not another is
/// trailing data (see the note at the top of this file).
/// </summary>
internal sealed class XzMembers(Stream compressed) : Stream
{
    private static ReadOnlySpan<byte> Magic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private Stream _current = new SharpCompress.Compressors.Xz.XZStream(new ArchiveReader.Unowned(compressed));
    private bool _ended;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (!_ended && buffer.Length > 0)
        {
            var n = _current.Read(buffer);

            if (n > 0) return n;

            if (!NextStream()) _ended = true;
        }

        return 0;
    }

    /// <summary>Starts the next stream, or says there is none.</summary>
    private bool NextStream()
    {
        var zeros = 0;
        int b;

        while ((b = compressed.ReadByte()) == 0) zeros++;

        if (b < 0) return false;

        Span<byte> head = stackalloc byte[6];
        head[0] = (byte)b;

        if (compressed.ReadAtLeast(head[1..], 5, throwOnEndOfStream: false) < 5 || !head.SequenceEqual(Magic))
            return false;

        if (zeros % 4 != 0) throw new InvalidDataException("xz stream padding is not a multiple of four");

        compressed.Seek(-6, SeekOrigin.Current);

        _current.Dispose();
        _current = new SharpCompress.Compressors.Xz.XZStream(new ArchiveReader.Unowned(compressed));

        return true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>
/// An .lz file made of several members — what plzip writes — read as one.
///
/// **SharpCompress's <c>LZipStream</c> decodes one member and then reads the
/// next member's header as compressed data**: measured, "Data Error" on every
/// multi-member file (review of Stage A). The members are found the way the
/// lzip tools find them, from the END: each member's trailer ends with its own
/// size, so the last eight bytes of the file name where the last member
/// starts, and so on back to the first. Each member is then decoded inside its
/// own bounds. A walk that does not land exactly on the start of the file, on
/// an <c>LZIP</c> header each time, is damage.
///
/// **Trailing data is stepped over first, the way the lzip tools' own index
/// does it**: when the last eight bytes do not name a member, the walk looks
/// back for the last place one ends, and what follows it is trailing data —
/// unless it starts <c>LZIP</c>, which makes it a member cut short.
/// </summary>
internal sealed class LzipMembers(Stream compressed) : Stream
{
    private Queue<(long Start, long Size)>? _members;
    private Stream? _current;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;

        _members ??= Walk();

        while (true)
        {
            if (_current is null)
            {
                if (!_members.TryDequeue(out var member)) return 0;

                _current = SharpCompress.Compressors.LZMA.LZipStream.Create(
                    new Bounded(compressed, member.Start, member.Size), SharpCompressionMode.Decompress, leaveOpen: false);
            }

            var n = _current.Read(buffer);

            if (n > 0) return n;

            _current.Dispose();
            _current = null;
        }
    }

    private Queue<(long, long)> Walk()
    {
        var found = new Stack<(long, long)>();
        var end = EndOfMembers(compressed.Length);
        Span<byte> field = stackalloc byte[8];

        while (end > 0)
        {
            // Header 6 bytes, trailer 20: nothing shorter is a member.
            if (end < 26) throw new InvalidDataException("lzip file does not divide into members");

            compressed.Position = end - 8;
            compressed.ReadExactly(field);

            var size = BinaryPrimitives.ReadInt64LittleEndian(field);

            if (size < 26 || size > end) throw new InvalidDataException("lzip member size is out of range");

            var start = end - size;

            compressed.Position = start;
            compressed.ReadExactly(field[..4]);

            if (!field[..4].SequenceEqual("LZIP"u8)) throw new InvalidDataException("lzip member does not start with its header");

            found.Push((start, size));
            end = start;
        }

        return new Queue<(long, long)>(found);
    }

    /// <summary>
    /// Where the last member ends: the end of the file, or the last place
    /// before it whose eight bytes name a member that starts <c>LZIP</c>.
    /// </summary>
    private long EndOfMembers(long length)
    {
        var window = new byte[64 * 1024];
        long from = 0, to = 0;

        for (var end = length; end >= 26; end--)
        {
            if (end - 8 < from || end > to)
            {
                to = end;
                from = Math.Max(0, to - window.Length);
                compressed.Position = from;
                compressed.ReadExactly(window.AsSpan(0, (int)(to - from)));
            }

            var size = BinaryPrimitives.ReadInt64LittleEndian(window.AsSpan((int)(end - 8 - from), 8));

            if (size < 26 || size > end || !StartsLzip(end - size)) continue;

            if (end < length && StartsLzip(end))
                throw new InvalidDataException("an lzip member after the last whole one is cut short or damaged");

            return end;
        }

        throw new InvalidDataException("lzip file does not divide into members");
    }

    private bool StartsLzip(long at)
    {
        Span<byte> magic = stackalloc byte[4];

        compressed.Position = at;

        return compressed.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && magic.SequenceEqual("LZIP"u8);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>One member's bytes, and nothing past them.</summary>
    private sealed class Bounded(Stream inner, long start, long size) : Stream
    {
        private long _at;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var left = size - _at;

            if (left <= 0 || buffer.Length == 0) return 0;

            if (inner.Position != start + _at) inner.Position = start + _at;

            var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);

            _at += n;

            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A .gz file of one or more members, decoded by the runtime's
/// <see cref="GZipStream"/>, with a file cut short refused.
///
/// **A .gz cut short landed as the part that arrived** (RC QA: a 460 KB
/// .txt.gz cut at 40 places, and all 40 extracted with wrong bytes and no word
/// said). GZipStream does everything else a gzip reader must, inside zlib: it
/// parses each header (a reserved flag or a wrong header CRC is an error),
/// checks each member's CRC-32 and length against its trailer, starts another
/// member only where the bytes after one begin 1F 8B, and leaves anything else
/// after the last member alone. And because zlib itself says where each
/// member's deflate data ends, it is exact: an empty member as gzip(1) and
/// zlib write it (<c>03 00</c> and eight zero bytes), and a stored member that
/// carries eight bytes equal to its own CRC and length, decode as gzip -d
/// decodes them. What it does not do is complain when its input simply runs
/// out: at the end of the file it ends, trailer or not.
///
/// **So the end of the file is marked.** When GZipStream asks for more input
/// after the last byte of the file, it is handed one more whole member, made
/// here, whose content is sixteen random bytes. If the file ended cleanly —
/// its last member whole and checked — GZipStream reads that member as the
/// next one and decodes it, and the sixteen bytes are exactly what comes out
/// after the file's own data. If the file was cut inside a member, header or
/// trailer, the marker's bytes are read as the rest of that member instead,
/// and what comes out is an error, or anything but those sixteen bytes. The
/// bytes are new for every file, so no file can be made to produce them. If
/// GZipStream never asks, the file ended cleanly and bytes it leaves alone
/// follow.
///
/// **Chosen over finding each member's end by hand** (RC QA round 2: the
/// search for a trailer took eight zero bytes one byte early in an empty
/// member, and dropped every member after it). The runtime's own
/// System.IO.Compression.UseStrictValidation switch refuses a cut file too,
/// but it is read once per process and changes how zips are read as well.
/// This also restores the speed of the runtime's reader: the CRC-32 is zlib's.
/// </summary>
internal sealed class GzipMembers : Stream
{
    private const int MarkerLength = 16;

    private readonly Input _input;
    private readonly GZipStream _gzip;
    private readonly byte[] _marker = RandomNumberGenerator.GetBytes(MarkerLength);
    private bool _ended;

    public GzipMembers(Stream compressed)
    {
        var member = new MemoryStream();

        using (var z = new GZipStream(member, CompressionLevel.Optimal, leaveOpen: true)) z.Write(_marker);

        _input = new Input(compressed, member.ToArray());
        _gzip = new GZipStream(_input, CompressionMode.Decompress);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_ended || buffer.IsEmpty) return 0;

        // GZipStream reads input only when it has nothing left to give, so a
        // Read that asked for the marker returned nothing from the file.
        var n = _gzip.Read(buffer);

        if (!_input.MarkerGiven)
        {
            if (n > 0) return n;

            _ended = true;
            return 0;
        }

        _ended = true;

        Span<byte> after = stackalloc byte[MarkerLength + 1];
        var got = Math.Min(n, after.Length);

        buffer[..got].CopyTo(after);

        while (got < after.Length && (n = _gzip.Read(after[got..])) > 0) got += n;

        if (got != MarkerLength || !after[..got].SequenceEqual(_marker))
            throw new InvalidDataException("gzip data ends before its last member does");

        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gzip.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>The file, then the marker member once, then nothing. Leaves
    /// the file open.</summary>
    private sealed class Input(Stream inner, byte[] marker) : Stream
    {
        private int _given;

        public bool MarkerGiven { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;

            if (!MarkerGiven)
            {
                var n = inner.Read(buffer);

                if (n > 0) return n;

                MarkerGiven = true;
            }

            var take = Math.Min(buffer.Length, marker.Length - _given);

            marker.AsSpan(_given, take).CopyTo(buffer);
            _given += take;

            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A .bz2 file of one or more streams — what pbzip2 writes — read as one.
///
/// **SharpCompress's own concatenated mode refuses whatever follows the last
/// stream**, zero padding included, which made a padded .tar.bz2 "damaged"
/// once the tar reader read to the end of its compression (RC QA; GNU tar
/// extracts it with a warning). Measured: one stream decoded on its own
/// leaves the input exactly at its end, so the next is started only when the
/// next bytes are a bzip2 signature, and anything else is trailing data.
/// </summary>
internal sealed class Bzip2Members(Stream compressed) : Stream
{
    private Stream _current = Open(compressed);
    private bool _ended;

    private static Stream Open(Stream compressed) => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
        compressed, SharpCompressionMode.Decompress, decompressConcatenated: false, leaveOpen: true);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (!_ended && buffer.Length > 0)
        {
            var n = _current.Read(buffer);

            if (n > 0) return n;

            if (!AnotherStream()) _ended = true;
        }

        return 0;
    }

    private bool AnotherStream()
    {
        Span<byte> head = stackalloc byte[4];

        var got = compressed.ReadAtLeast(head, 4, throwOnEndOfStream: false);

        compressed.Position -= got;

        if (got < 4 || !head[..3].SequenceEqual("BZh"u8) || head[3] is < (byte)'1' or > (byte)'9') return false;

        _current.Dispose();
        _current = Open(compressed);

        return true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>
/// A .zst file of one or more frames, handed to the decoder only as far as
/// its frames go.
///
/// **The decoder refuses whatever follows the last frame** ("Unknown frame
/// descriptor", for zero padding too), and it reads its input in chunks, so it
/// cannot be stopped at a frame's end afterwards. The frames are found by
/// their structure instead, as the decoder reaches them: a frame header, then
/// blocks whose three-byte headers say how long each is and which is last,
/// then the checksum if the frame has one; a skippable frame says its own
/// length. The decoder is given the input up to the end of the last frame and
/// sees end of input there. The walk only marks bounds: every block still
/// passes through the decoder and its checks, and a frame whose blocks run
/// past the end of the file is damage.
/// </summary>
internal sealed class ZstdFrames(Stream compressed) : Stream
{
    private Stream? _decoder;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;

        _decoder ??= new SharpCompress.Compressors.ZStandard.DecompressionStream(new Walked(compressed), leaveOpen: false);

        return _decoder.Read(buffer);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _decoder?.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>The input up to the end of the frames walked so far, walking
    /// one more piece whenever the decoder reaches that end.</summary>
    private sealed class Walked(Stream inner) : Stream
    {
        private enum Next { Frame, Block, Checksum, End }

        private readonly long _length = inner.Length;
        private long _at = inner.Position;
        private long _known = inner.Position;
        private Next _next = Next.Frame;
        private bool _checksum;
        private bool _first = true;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;

            while (_at == _known)
            {
                if (_next == Next.End) return 0;

                Walk();
            }

            if (inner.Position != _at) inner.Position = _at;

            var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _known - _at)]);

            if (n == 0) throw new InvalidDataException("zstd frame is cut short");

            _at += n;

            return n;
        }

        private void Walk()
        {
            switch (_next)
            {
                case Next.Frame:
                {
                    Span<byte> head = stackalloc byte[8];
                    var got = Peek(head);
                    var magic = got >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(head) : 0;

                    if (magic == 0xFD2FB528)
                    {
                        if (got < 5) throw new InvalidDataException("zstd frame header is cut short");

                        var descriptor = head[4];

                        if ((descriptor & 0x08) != 0) throw new InvalidDataException("zstd frame header sets its reserved bit");

                        var single = (descriptor & 0x20) != 0;
                        int[] dictionary = [0, 1, 2, 4];
                        var contentSize = (descriptor >> 6) switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };

                        _checksum = (descriptor & 0x04) != 0;
                        Take(5 + (single ? 0 : 1) + dictionary[descriptor & 3] + contentSize);
                        _next = Next.Block;
                    }
                    else if (got >= 4 && (magic & 0xFFFFFFF0) == 0x184D2A50)
                    {
                        if (got < 8) throw new InvalidDataException("zstd skippable frame is cut short");

                        Take(8L + BinaryPrimitives.ReadUInt32LittleEndian(head[4..]));
                    }
                    else if (_first)
                    {
                        throw new InvalidDataException("not a zstd frame");
                    }
                    else
                    {
                        _next = Next.End;
                    }

                    _first = false;
                    break;
                }

                case Next.Block:
                {
                    Span<byte> head = stackalloc byte[3];

                    if (Peek(head) < 3) throw new InvalidDataException("zstd block header is cut short");

                    var value = head[0] | (head[1] << 8) | (head[2] << 16);
                    var type = (value >> 1) & 3;

                    if (type == 3) throw new InvalidDataException("zstd block has the reserved type");

                    Take(3 + (type == 1 ? 1 : value >> 3));

                    if ((value & 1) != 0) _next = _checksum ? Next.Checksum : Next.Frame;
                    break;
                }

                case Next.Checksum:
                    Take(4);
                    _next = Next.Frame;
                    break;
            }
        }

        private int Peek(Span<byte> into)
        {
            inner.Position = _known;

            return inner.ReadAtLeast(into, into.Length, throwOnEndOfStream: false);
        }

        private void Take(long size)
        {
            if (size > _length - _known) throw new InvalidDataException("zstd frame runs past the end of the file");

            _known += size;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
