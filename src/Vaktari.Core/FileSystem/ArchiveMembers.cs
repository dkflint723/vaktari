using System.Buffers.Binary;
using System.IO.Compression;
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
/// A .gz file of one or more members, each checked against its own trailer.
///
/// **A .gz cut short landed as the part that arrived** (RC QA: a 460 KB
/// .txt.gz cut at 40 places, and all 40 extracted with wrong bytes and no word
/// said). The runtime's <see cref="GZipStream"/> checks a member's CRC-32 and
/// length when it reaches them, but at the end of its input it simply ends —
/// so a file that stops before its trailer was never checked at all. Here each
/// member's header is read by hand, its deflate data goes through the
/// runtime's <see cref="DeflateStream"/>, and its eight-byte trailer must be
/// there and must hold the CRC-32 and the length (mod 2^32) of exactly what
/// that member decoded. Missing, short or different is damage. A .tar.gz cut
/// inside its first few hundred bytes no longer reads as a tar and is tried
/// as a bare .gz, and the same check refuses it there.
///
/// **Where the deflate data ends is found from the trailer.** DeflateStream
/// reads its input in chunks and neither says how much of the last one it
/// used nor puts the rest back (measured on .NET 10: the input stays where the
/// last chunk left it). It only asks for more once it has used everything it
/// was given, so the data ends inside the last two chunks it read, and the
/// trailer is the first eight bytes from there that match what was decoded.
/// For compressed bytes to match instead, 64 bits would have to agree by
/// chance.
///
/// Chosen over SharpCompress's gzip reader, measured: it checks the trailer,
/// but it failed on the second member of a multi-member file and decoded
/// forty times slower than the runtime's inflater.
/// </summary>
internal sealed class GzipMembers(Stream compressed) : Stream
{
    private DeflateStream? _deflate;
    private Watched? _watched;
    private long _dataStart;
    private uint _crc;
    private long _length;
    private bool _started;
    private bool _ended;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (!_ended && buffer.Length > 0)
        {
            if (_deflate is null)
            {
                if (_started && !AnotherMember())
                {
                    _ended = true;
                    break;
                }

                _started = true;
                Begin();
            }

            var n = _deflate!.Read(buffer);

            if (n > 0)
            {
                _crc = Crc32.Update(_crc, buffer[..n]);
                _length += n;

                return n;
            }

            _deflate.Dispose();
            _deflate = null;

            CheckTrailer();
        }

        return 0;
    }

    /// <summary>Reads a member's header and starts its deflate data.</summary>
    private void Begin()
    {
        Span<byte> head = stackalloc byte[10];

        Need(head);

        if (head[0] != 0x1F || head[1] != 0x8B) throw new InvalidDataException("not a gzip member");
        if (head[2] != 8) throw new InvalidDataException("gzip member is not deflate");

        var flags = head[3];

        if ((flags & 0xE0) != 0) throw new InvalidDataException("gzip header sets reserved flags");

        var headerCrc = Crc32.Update(0, head);

        if ((flags & 0x04) != 0)
        {
            Span<byte> size = stackalloc byte[2];
            Need(size);

            var extra = new byte[BinaryPrimitives.ReadUInt16LittleEndian(size)];
            Need(extra);

            headerCrc = Crc32.Update(Crc32.Update(headerCrc, size), extra);
        }

        if ((flags & 0x08) != 0) headerCrc = PastZero(headerCrc);
        if ((flags & 0x10) != 0) headerCrc = PastZero(headerCrc);

        if ((flags & 0x02) != 0)
        {
            Span<byte> check = stackalloc byte[2];
            Need(check);

            if (BinaryPrimitives.ReadUInt16LittleEndian(check) != (ushort)headerCrc)
                throw new InvalidDataException("gzip header CRC does not match");
        }

        _dataStart = compressed.Position;
        _watched = new Watched(compressed);
        _deflate = new DeflateStream(_watched, CompressionMode.Decompress, leaveOpen: true);
        _crc = 0;
        _length = 0;
    }

    /// <summary>
    /// Finds the member's trailer and checks it, leaving the input just past
    /// it. See the class summary for why it is searched for.
    /// </summary>
    private void CheckTrailer()
    {
        var end = compressed.Position;
        var from = Math.Max(_dataStart, _watched!.Previous >= 0 ? _watched.Previous : _watched.Last);
        var window = new byte[(int)(end - from) + 8];

        compressed.Position = from;

        var got = compressed.ReadAtLeast(window, window.Length, throwOnEndOfStream: false);

        Span<byte> expected = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, _crc);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[4..], unchecked((uint)_length));

        var at = window.AsSpan(0, got).IndexOf(expected);

        if (at < 0) throw new InvalidDataException("gzip member's CRC-32 and length are missing or do not match what it decoded");

        compressed.Position = from + at + 8;
    }

    /// <summary>Whether another member follows; if not, what is left is trailing data.</summary>
    private bool AnotherMember()
    {
        Span<byte> magic = stackalloc byte[2];

        var got = compressed.ReadAtLeast(magic, 2, throwOnEndOfStream: false);

        compressed.Position -= got;

        return got == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
    }

    private void Need(Span<byte> field)
    {
        if (compressed.ReadAtLeast(field, field.Length, throwOnEndOfStream: false) < field.Length)
            throw new InvalidDataException("gzip header is cut short");
    }

    /// <summary>Past a zero-terminated name or comment, adding it to the header's CRC.</summary>
    private uint PastZero(uint crc)
    {
        Span<byte> one = stackalloc byte[1];

        do
        {
            Need(one);
            crc = Crc32.Update(crc, one);
        }
        while (one[0] != 0);

        return crc;
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
        if (disposing) _deflate?.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>The input as DeflateStream sees it, remembering where its
    /// last two reads started.</summary>
    private sealed class Watched(Stream inner) : Stream
    {
        public long Previous { get; private set; } = -1;
        public long Last { get; private set; } = -1;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var at = inner.Position;
            var n = inner.Read(buffer);

            if (n > 0)
            {
                Previous = Last;
                Last = at;
            }

            return n;
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

/// <summary>CRC-32 as gzip and zip use it (IEEE, reflected), eight bytes at a
/// time — the runtime has no public one outside a package.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[8 * 256];

        for (uint i = 0; i < 256; i++)
        {
            var c = i;

            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

            table[i] = c;
        }

        for (var i = 0; i < 256; i++)
            for (var k = 1; k < 8; k++)
                table[(k * 256) + i] = (table[((k - 1) * 256) + i] >> 8) ^ table[table[((k - 1) * 256) + i] & 0xFF];

        return table;
    }

    /// <summary>The CRC of what <paramref name="crc"/> covered followed by
    /// <paramref name="data"/>; start from 0.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Table;
        crc = ~crc;

        while (data.Length >= 8)
        {
            var one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

            crc = t[(7 * 256) + (one & 0xFF)] ^ t[(6 * 256) + ((one >> 8) & 0xFF)]
                ^ t[(5 * 256) + ((one >> 16) & 0xFF)] ^ t[(4 * 256) + (one >> 24)]
                ^ t[(3 * 256) + (two & 0xFF)] ^ t[(2 * 256) + ((two >> 8) & 0xFF)]
                ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];

            data = data[8..];
        }

        foreach (var b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return ~crc;
    }
}
