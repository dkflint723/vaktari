using System.Buffers.Binary;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// An .xz file made of several streams, read as one.
///
/// **A concatenated .xz landed with its first stream only, silently**
/// (review of Stage A: <c>lzma.compress(a) + lzma.compress(b)</c> extracted as
/// <c>a</c>). The format allows any number of streams, each optionally
/// followed by null padding in multiples of four bytes, and
/// <see cref="SharpCompress.Compressors.Xz.XZStream"/> stops at the end of
/// the first. Measured: it leaves the file exactly at that end, so the next
/// stream can be started where it stopped. Anything after the padding that is
/// not another stream is damage, never ignored.
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

        if (b < 0)
        {
            if (zeros % 4 != 0) throw new InvalidDataException("xz stream padding is not a multiple of four");
            return false;
        }

        if (zeros % 4 != 0) throw new InvalidDataException("xz stream padding is not a multiple of four");

        Span<byte> head = stackalloc byte[6];
        head[0] = (byte)b;

        if (compressed.ReadAtLeast(head[1..], 5, throwOnEndOfStream: false) < 5 || !head.SequenceEqual(Magic))
            throw new InvalidDataException("data after the end of the xz stream");

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
        var end = compressed.Length;
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
