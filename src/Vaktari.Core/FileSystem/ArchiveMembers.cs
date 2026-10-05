using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Vaktari.Core.FileSystem;

// **Where a gzip stream ends.** The decoder here reads member after member and
// ends after the last one that finished cleanly with its own check passed.
// What follows it is trailing data, not part of the archive, and is left
// unread, as gzip(1) leaves it with at most a warning. Bytes that START with
// the gzip signature are not trailing data but another member, which must be
// whole — so a second member cut short is damage, not something to drop. A
// failure anywhere inside a member is damage. A bare .gz and a .tar.gz are
// read through the same decoder, so the rule is the same for both.
//
// This file held a decoder each for xz, lzip, bzip2 and zstd as well, under
// the same rule. They went when Extract all narrowed to zip and tar.gz; the
// gzip one is unchanged, including the 0.11.1 fixes for a cut file and for an
// empty member.
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
