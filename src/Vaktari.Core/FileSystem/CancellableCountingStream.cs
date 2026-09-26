namespace Vaktari.Core.FileSystem;

/// <summary>
/// The archive file as every decoder sees it: cancellable on every read,
/// counted, and honest about where it is.
///
/// **A decoder reading a large solid block has no cancellation of its own.**
/// SharpCompress's readers take no token, so the only place a Cancel can reach
/// them is the stream they pull bytes from, and it has to be checked on EVERY
/// read — a 16 MiB solid 7z is one entry stream with nothing between its
/// buffers for the caller to check. Measured (E-7): the exception thrown here
/// comes out of the PPMd, BCJ2 and solid RAR decoders unchanged, and out of
/// LZMA and LZMA2 as <c>DataErrorException: Data Error</c> — the decoder
/// catches it and reports corruption. That is why the reader asks the token,
/// not the exception type, whether a failure was a cancellation.
///
/// **Reads are filled, and the position is the truth**, the two contracts
/// <see cref="IconThemeArchive.ConcatStream"/> learned the xz decoder relies
/// on: it misreads short reads as a corrupt block, and it works out block
/// padding from <see cref="Position"/>.
///
/// **Seeking passes through**, because zip, 7z and RAR are read by random
/// access and seek by the offsets their directories give.
/// </summary>
internal sealed class CancellableCountingStream(Stream inner, CancellationToken token, bool fillReads) : Stream
{
    private long _read;

    /// <summary>Every byte handed out so far, however the stream was moved
    /// about — progress for the formats that declare no sizes.</summary>
    public long BytesRead => Interlocked.Read(ref _read);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();

        var total = 0;

        do
        {
            var n = inner.Read(buffer[total..]);

            if (n <= 0) break;

            total += n;
        }
        while (fillReads && total < buffer.Length);

        Interlocked.Add(ref _read, total);

        return total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.CanSeek ? inner.Position : BytesRead;
        set => inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();

        base.Dispose(disposing);
    }
}
