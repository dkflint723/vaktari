using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;
using SharpZip = SharpCompress.Archives.Zip.ZipArchive;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// The only archive-side user of SharpCompress's readers and of
/// <see cref="System.Formats.Tar"/>: opens one archive for one pass.
///
/// **What each format checks for itself, measured (E-25, E-33)**, which is
/// what decides <see cref="ArchiveEntryInfo.CrcIsOurs"/>:
/// <list type="bullet">
/// <item>zip: nothing. A stored entry and a deflated one with a wrong CRC read
/// without complaint. Ours is the only check.</item>
/// <item>7z: nothing. A byte flipped in a Copy-method entry read without
/// complaint and a wrong CRC; LZMA2 happened to fail on its own. Ours is
/// the only check.</item>
/// <item>RAR4 and RAR5: checked — "file crc mismatch", stored and
/// compressed alike.</item>
/// <item>gzip (the BCL's): CRC-32 and length checked, though it words a
/// mismatch as "unsupported compression method". bzip2, xz, lzip, and zstd
/// when the frame carries a checksum: all checked.</item>
/// </list>
///
/// **How each format is walked.** A tar is one forward pass. 7z, and a solid
/// RAR, go through <c>ExtractAllEntries</c>; a zip and a non-solid RAR cannot
/// (measured: "ExtractAllEntries can only be used on solid archives or 7Zip
/// archives") and are read entry by entry, by random access.
///
/// **Every failure while READING is classified here**, so the writer can tell
/// the archive's fault from the disk's: see <see cref="Classify"/>.
/// </summary>
internal static class ArchiveReader
{
    internal static ArchivePass Open(string path, CancellationToken token)
    {
        var leaf = Path.GetFileName(path);
        var byName = ArchiveFormats.ByName(path);
        var named = byName ?? ArchiveFormat.Zip;

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var stream = new CancellableCountingStream(file, token, fillReads: true);

        try
        {
            var head = new byte[512];
            var got = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            stream.Position = 0;

            // **A zip is known by its END**, the central directory, which is
            // what the runtime's own reader looked for — so a zip with bytes
            // in front of it (a spanning marker other than PK00, a stub)
            // opened before and must open now. A file NAMED .zip that the
            // first bytes do not place is tried as one, and refused in words
            // when it has no directory either.
            if ((ArchiveFormats.Sniff(head.AsSpan(0, got)) ?? (byName == ArchiveFormat.Zip ? ArchiveFormat.Zip : null))
                is not { } format)
                throw new InvalidDataException(ArchiveSentences.NotThisFormat(leaf, named));

            if (ArchiveFormats.IsBare(format) && HoldsTar(path, format))
                format = ArchiveFormats.AsTar(format);

            var pass = new ArchivePass(path, leaf, format, stream, token);

            try
            {
                pass.Load();
            }
            catch
            {
                pass.Dispose();
                throw;
            }

            return pass;
        }
        catch (OperationCanceledException)
        {
            stream.Dispose();
            throw;
        }
        catch (Exception e) when (e is not (ArchivePasswordRequiredException or ArchiveRefusedException
                                        or ArchiveDamagedException or ArchiveUnreadableException))
        {
            stream.Dispose();

            if (token.IsCancellationRequested) throw new OperationCanceledException(token);

            if (stream.SourceFailure is { } io) throw Unreadable(leaf, io);

            if (e is CryptographicException) throw new ArchivePasswordRequiredException(ArchiveSentences.Password(leaf), e);

            // Unreadable up front: a truncated zip has no end record, a
            // truncated 7z "nextHeaderOffset is invalid", a truncated RAR
            // cannot seek to its next header (E-14) — all before any entry.
            throw new InvalidDataException(ArchiveSentences.NotThisFormat(leaf, named), e);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether a compressed stream holds a tar: its first 512 decompressed
    /// bytes, on a stream of its own so the pass starts from the beginning.
    /// </summary>
    private static bool HoldsTar(string path, ArchiveFormat bare)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var decoded = Decompress(file, bare);

            var first = new byte[512];
            var got = decoded.ReadAtLeast(first, first.Length, throwOnEndOfStream: false);

            return got == 512 && ArchiveFormats.LooksLikeTar(first);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Damage shows when the pass reads it, with its own sentence.
            return false;
        }
    }

    /// <summary>
    /// A decoder over <paramref name="compressed"/>, which it does not close.
    ///
    /// **Never asked for zero bytes, and never asked again after it ended.**
    /// Measured on SharpCompress 0.50.4: <c>LZipStream</c> checks its trailer
    /// CRC the first time a read returns nothing, and a read of an EMPTY
    /// buffer returns nothing — so the BCL tar reader, which issues one, made
    /// every .tar.lz fail "LZip CRC mismatch", SharpCompress's own
    /// <c>Tar.tar.lz</c> test archive included. Read directly, the same files
    /// decode cleanly. Every decoder goes through <see cref="Decoded"/>, not
    /// only lzip, because the next one to validate on "end" would fail the
    /// same way.
    /// </summary>
    internal static Stream Decompress(Stream compressed, ArchiveFormat format) => new Decoded(format switch
    {
        ArchiveFormat.Gz or ArchiveFormat.TarGz => new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true),
        ArchiveFormat.Bz2 or ArchiveFormat.TarBz2 => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
            compressed, SharpCompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true),
        ArchiveFormat.Xz or ArchiveFormat.TarXz => new XzMembers(compressed),
        ArchiveFormat.Zst or ArchiveFormat.TarZst => new SharpCompress.Compressors.ZStandard.DecompressionStream(compressed, leaveOpen: true),
        ArchiveFormat.Lz or ArchiveFormat.TarLz => new LzipMembers(compressed),
        _ => new Unowned(compressed),
    });

    /// <summary>See <see cref="Decompress"/>.</summary>
    private sealed class Decoded(Stream inner) : Stream
    {
        private bool _ended;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _ended) return 0;

            var n = inner.Read(buffer);

            if (n == 0) _ended = true;

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

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();

            base.Dispose(disposing);
        }
    }

    internal static ReaderOptions Options => new()
    {
        LeaveStreamOpen = true,
        ArchiveEncoding = new ArchiveEncoding { CustomDecoder = Cp437Names.Decode },
    };

    /// <summary>
    /// Turns whatever reading an archive threw into one of four things:
    /// a cancellation, a refusal in words, a password, or damage — never a
    /// decoder's own exception, whose words mean nothing to anybody.
    ///
    /// **The token is asked first**, because LZMA and LZMA2 report a
    /// cancellation arriving through their input as <c>DataErrorException</c>
    /// (E-7), which would otherwise be told to somebody as a damaged archive
    /// they had only asked to stop.
    /// </summary>
    internal static Exception Classify(Exception e, ArchivePass pass, int entriesBefore)
    {
        if (pass.Token.IsCancellationRequested) return new OperationCanceledException(pass.Token);

        // **The disk the archive is on failed, not the archive** — an
        // unplugged stick, a share that dropped. Asked of the stream, for
        // the token's reason: a decoder may wrap what its input threw.
        if (pass.SourceFailure is { } io) return Unreadable(pass.Leaf, io);

        return e switch
        {
            OperationCanceledException => e,
            ArchivePasswordRequiredException or ArchiveRefusedException or ArchiveDamagedException
                or ArchiveUnreadableException => e,
            CryptographicException => new ArchivePasswordRequiredException(ArchiveSentences.Password(pass.Leaf), e),

            // **GNU sparse members are refused by the BCL reader itself**
            // (refutations 2): NotSupportedException naming 'SparseFile'.
            NotSupportedException when ArchiveFormats.IsTar(pass.Format)
                => new ArchiveRefusedException(ArchiveSentences.Sparse(pass.Leaf), e),

            _ => new ArchiveDamagedException(ArchiveSentences.DamagedAfter(pass.Leaf, entriesBefore), entriesBefore, e),
        };
    }

    private static ArchiveUnreadableException Unreadable(string leaf, IOException io)
        => new(ArchiveSentences.Unreadable(leaf), io) { HResult = io.HResult };

    /// <summary>A stream whose disposal leaves the one underneath alone.</summary>
    internal sealed class Unowned(Stream inner) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>One entry as a pass hands it over. Valid until the next one.</summary>
internal sealed class ArchiveItem(ArchiveEntryInfo info, Func<Stream> open)
{
    public ArchiveEntryInfo Info { get; } = info;

    /// <summary>The entry's bytes, decompressed; every failure while reading
    /// them is already classified.</summary>
    public Stream OpenData() => open();
}

/// <summary>
/// One archive, opened for one operation.
///
/// **Never shared between operations** (R1-9): a SharpCompress archive is a
/// cursor over one stream, and two operations reading it at once read each
/// other's positions.
/// </summary>
internal sealed class ArchivePass : IDisposable
{
    private readonly CancellableCountingStream _stream;
    private IArchive? _archive;
    private List<IArchiveEntry>? _entries;
    private readonly List<IDisposable> _owned = [];

    internal ArchivePass(string path, string leaf, ArchiveFormat format, CancellableCountingStream stream, CancellationToken token)
    {
        Path = path;
        Leaf = leaf;
        Format = format;
        _stream = stream;
        Token = token;
        ArchiveLength = stream.Length;
        _baseline = stream.BytesRead;
    }

    public string Path { get; }
    public string Leaf { get; }
    public ArchiveFormat Format { get; }
    public CancellationToken Token { get; }

    /// <summary>The sum of the sizes the archive declares up front, or null
    /// for a format that declares none before its bytes arrive.</summary>
    public long? DeclaredTotal { get; private set; }

    public int? DeclaredCount { get; private set; }
    public bool AnyEncrypted { get; private set; }
    public ZipDirectory? Directory { get; private set; }
    /// <summary>How far through the archive the pass has read, not counting
    /// the sniff that opened it, and never past its end — a decoder that
    /// seeks back reads some bytes twice.</summary>
    public long CompressedBytesRead => Math.Min(_stream.BytesRead - _baseline, ArchiveLength);

    private readonly long _baseline;

    /// <summary>What reading the archive file itself threw, if it did.</summary>
    public IOException? SourceFailure => _stream.SourceFailure;

    /// <summary>How many entries will each report finishing — every one
    /// that is not a folder — or null where the format does not say.</summary>
    public int? DeclaredItems { get; private set; }
    public long ArchiveLength { get; }

    internal void Load()
    {
        switch (Format)
        {
            case ArchiveFormat.Zip:
            {
                Directory = ZipDirectory.Read(_stream);
                _stream.Position = 0;

                // **Both readings see the same file.** Bytes in front of a zip
                // whose offsets were written without them are what
                // ZipDirectory calls the shift, and it adds it; SharpCompress
                // does not, so the two could read different bytes for one
                // entry — a directory could point one reader at an entry and
                // the other at a different entry's data (verification of
                // Stage A). SharpCompress's offsets cannot be read back to be
                // compared, so it is given the file as the directory sees it
                // instead: from where the zip proper starts. The per-entry
                // names and compressed sizes are then checked against the
                // local headers in ZipDirectory, which is where both readers
                // meet.
                _archive = SharpZip.OpenArchive(
                    Directory.Shift == 0 ? _stream : new From(_stream, Directory.Shift), ArchiveReader.Options);
                _entries = [.. _archive.Entries];

                // SharpCompress walks the same directory (E-28); a count that
                // differs means the two readings of the archive disagree.
                if (_entries.Count != Directory.Records.Count)
                    throw new InvalidDataException("central directory and entries disagree");

                DeclaredTotal = Total(Directory.Records.Select(r => r.UncompressedSize));
                DeclaredCount = Directory.Records.Count;
                DeclaredItems = Directory.Records.Count(r => ZipKind(r) != ArchiveEntryKind.Folder);
                AnyEncrypted = Directory.AnyEncrypted;
                break;
            }

            case ArchiveFormat.SevenZip:
            case ArchiveFormat.Rar:
            {
                _archive = Format == ArchiveFormat.Rar
                    ? RarArchive.OpenArchive(_stream, ArchiveReader.Options)
                    : SevenZipArchive.OpenArchive(_stream, ArchiveReader.Options);

                // **The first touch of a header-encrypted archive's entries
                // throws**, which is what the refusal needs; and it must be
                // the only touch. Measured (E-3r): after IsEncrypted threw on
                // a RAR4 -hp archive, the SAME object answered Entries with an
                // empty list — an archive that "extracted" to nothing.
                _entries = [.. _archive.Entries];

                if (Format == ArchiveFormat.Rar && _archive.Volumes.FirstOrDefault() is RarVolume { IsMultiVolume: true })
                    throw new ArchiveRefusedException(ArchiveSentences.Split(Leaf));

                DeclaredTotal = Total(_entries.Where(e => !e.IsDirectory).Select(e => e.Size));
                DeclaredCount = _entries.Count;
                DeclaredItems = _entries.Count(e => !e.IsDirectory);
                AnyEncrypted = _entries.Any(e => e.IsEncrypted) || _archive.IsEncrypted;
                break;
            }
        }
    }

    /// <summary>A seekable stream seen from <paramref name="start"/> on,
    /// which it does not close.</summary>
    private sealed class From(Stream inner, long start) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length - start;
        public override long Position { get => inner.Position - start; set => inner.Position = value + start; }

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => inner.Seek(start + offset, SeekOrigin.Begin),
            _ => inner.Seek(offset, origin),
        } - start;

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The declared sizes added up, or damage in words.
    ///
    /// **A size past long.MaxValue reads as negative, and a sum of honest
    /// ones can overflow** — LINQ's checked Sum threw, and the refusal read
    /// "x.zip is not a zip file", which is not what is wrong with it (second
    /// verification of Stage A). An archive that declares sizes no disk can
    /// hold is damaged, and says so.
    /// </summary>
    private long Total(IEnumerable<long> sizes)
    {
        long total = 0;

        foreach (var size in sizes)
        {
            if (size < 0 || size > long.MaxValue - total)
                throw new ArchiveDamagedException(ArchiveSentences.ImpossibleSizes(Leaf), 0);

            total += size;
        }

        return total;
    }

    /// <summary>The entries, in archive order.</summary>
    public IEnumerable<ArchiveItem> Items()
    {
        var source = Format switch
        {
            ArchiveFormat.Zip => ZipItems(),
            ArchiveFormat.SevenZip => ReaderItems(),
            ArchiveFormat.Rar => _archive!.IsSolid ? ReaderItems() : EntryItems(),
            _ when ArchiveFormats.IsTar(Format) => TarItems(),
            _ => BareItems(),
        };

        using var e = source.GetEnumerator();
        var count = 0;

        while (true)
        {
            ArchiveItem item;

            try
            {
                Token.ThrowIfCancellationRequested();

                if (!e.MoveNext()) break;

                item = e.Current;
            }
            catch (Exception ex)
            {
                throw ArchiveReader.Classify(ex, this, count);
            }

            yield return item;

            count++;
        }
    }

    private IEnumerable<ArchiveItem> ZipItems()
    {
        var records = Directory!.Records;

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var entry = _entries![i];
            var mode = record.UnixMode;
            var kind = ZipKind(record);

            var info = new ArchiveEntryInfo(
                record.Name, kind, record.UncompressedSize, record.CompressedSize, record.Crc,
                CrcIsOurs: record.AesVersion != 2,
                When(entry.LastModifiedTime),
                mode & 0xFFF,
                record.Host == 3 ? null : (FileAttributes)(record.ExternalAttributes & 0xFFFF),
                record.Encrypted,
                null);

            yield return Item(info, () => entry.OpenEntryStream(), i);
        }
    }

    private static ArchiveEntryKind ZipKind(ZipRecord record) => (record.UnixMode & 0xF000) switch
    {
        0xA000 => ArchiveEntryKind.SymbolicLink,
        0x4000 => ArchiveEntryKind.Folder,
        0x1000 or 0x2000 or 0x6000 or 0xC000 => ArchiveEntryKind.Special,
        _ when record.Name.EndsWith('/') || record.Name.EndsWith('\\') => ArchiveEntryKind.Folder,
        _ when record.Host != 3 && (record.ExternalAttributes & 0x10) != 0 && record.UncompressedSize == 0 => ArchiveEntryKind.Folder,
        _ => ArchiveEntryKind.File,
    };

    private IEnumerable<ArchiveItem> EntryItems()
    {
        for (var i = 0; i < _entries!.Count; i++)
        {
            var entry = _entries[i];

            yield return Item(Describe(entry), () => entry.OpenEntryStream(), i);
        }
    }

    private IEnumerable<ArchiveItem> ReaderItems()
    {
        using var reader = _archive!.ExtractAllEntries();
        var i = 0;

        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;

            yield return Item(Describe(entry), reader.OpenEntryStream, i++);
        }
    }

    /// <summary>A 7z or RAR entry.</summary>
    private ArchiveEntryInfo Describe(IEntry entry)
    {
        var kind = entry.IsDirectory ? ArchiveEntryKind.Folder : ArchiveEntryKind.File;
        int? unix = null;
        FileAttributes? windows = null;

        if (Format == ArchiveFormat.SevenZip && entry.Attrib is { } attrib)
        {
            // 7-Zip's Unix extension: the mode in the high half, flagged by
            // 0x8000. And a Windows reparse point is a link however it was
            // stored.
            if ((attrib & 0x8000) != 0)
            {
                unix = (attrib >> 16) & 0xFFFF;

                kind = (unix & 0xF000) switch
                {
                    0xA000 => ArchiveEntryKind.SymbolicLink,
                    0x1000 or 0x2000 or 0x6000 or 0xC000 => ArchiveEntryKind.Special,
                    _ => kind,
                };

                unix &= 0xFFF;
            }

            if ((attrib & 0x400) != 0 && kind != ArchiveEntryKind.Folder) kind = ArchiveEntryKind.SymbolicLink;

            windows = (FileAttributes)(attrib & 0xFFFF);
        }

        if (!string.IsNullOrEmpty(entry.LinkTarget)) kind = ArchiveEntryKind.SymbolicLink;

        return new ArchiveEntryInfo(
            entry.Key ?? "", kind, entry.IsDirectory ? 0 : entry.Size, entry.CompressedSize, (uint)entry.Crc,

            // RAR checks its own CRC; 7z does not (E-25).
            CrcIsOurs: Format == ArchiveFormat.SevenZip && !entry.IsDirectory,
            When(entry.LastModifiedTime), unix, windows, entry.IsEncrypted, entry.LinkTarget);
    }

    private IEnumerable<ArchiveItem> TarItems()
    {
        var decoded = ArchiveReader.Decompress(_stream, Format);
        _owned.Add(decoded);

        using var tar = new TarReader(decoded, leaveOpen: true);
        var i = 0;

        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            // **Metadata about the archive, not an entry in it** (Core-17):
            // GNU tar writes these under names like /tmp/GlobalHead.1.
            if (entry is PaxGlobalExtendedAttributesTarEntry) continue;

            var kind = entry.EntryType switch
            {
                TarEntryType.Directory => ArchiveEntryKind.Folder,
                TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => ArchiveEntryKind.File,
                TarEntryType.SymbolicLink => ArchiveEntryKind.SymbolicLink,
                TarEntryType.HardLink => ArchiveEntryKind.HardLink,
                _ => ArchiveEntryKind.Special,
            };

            // **PAX sparse members arrive as ordinary files** (refutations 2:
            // a RegularFile called ./GNUSparseFile.NNN/big whose data is the
            // map), so writing one would write the map. Left out and counted.
            if (kind == ArchiveEntryKind.File && IsPaxSparse(entry)) kind = ArchiveEntryKind.Special;

            var current = entry;

            var info = new ArchiveEntryInfo(
                entry.Name, kind, entry.Length, null, null, CrcIsOurs: false,
                entry.ModificationTime, (int)entry.Mode, null, false,
                kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink ? entry.LinkName : null);

            // **Not disposed by the writer**: the data stream belongs to the
            // reader, which reads past whatever is left of it on the next
            // GetNextEntry.
            yield return Item(info, () => new ArchiveReader.Unowned(current.DataStream ?? Stream.Null), i++);
        }
    }

    private static bool IsPaxSparse(TarEntry entry)
        => (entry is PaxTarEntry pax && pax.ExtendedAttributes.Keys.Any(k => k.StartsWith("GNU.sparse.", StringComparison.Ordinal)))
           || entry.Name.Contains("GNUSparseFile.", StringComparison.Ordinal);

    private IEnumerable<ArchiveItem> BareItems()
    {
        var info = new ArchiveEntryInfo(
            ArchiveFormats.Stem(Path), ArchiveEntryKind.File, null, ArchiveLength, null, CrcIsOurs: false,
            When(File.GetLastWriteTimeUtc(Path)), null, null, false, null);

        yield return Item(info, () =>
        {
            var decoded = ArchiveReader.Decompress(_stream, Format);
            _owned.Add(decoded);
            return decoded;
        }, 0);
    }

    private ArchiveItem Item(ArchiveEntryInfo info, Func<Stream> open, int index)
        => new(info, () =>
        {
            try
            {
                return new Guarded(open(), this, index);
            }
            catch (Exception e)
            {
                throw ArchiveReader.Classify(e, this, index);
            }
        });

    private static DateTimeOffset? When(DateTime? time)
    {
        if (time is not { } t) return null;

        try
        {
            return new DateTimeOffset(t);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();

        _archive?.Dispose();
        _stream.Dispose();
    }

    /// <summary>An entry's bytes, with every failure while reading them
    /// classified before the writer sees it.</summary>
    private sealed class Guarded(Stream inner, ArchivePass pass, int index) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception e)
            {
                throw ArchiveReader.Classify(e, pass, index);
            }
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
            if (disposing)
            {
                try { inner.Dispose(); }
                catch (Exception e) when (e is not OperationCanceledException) { Quiet.Swallowed("archive", e); }
            }

            base.Dispose(disposing);
        }
    }
}
