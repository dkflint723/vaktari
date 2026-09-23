using System.Buffers;
using System.Text;

namespace Vaktari.Core.Search;

/// <summary>What reading a file for some text found.</summary>
public enum ContentVerdict
{
    /// <summary>The text is in the file.</summary>
    Found,

    /// <summary>The file was read to the end, or never needed opening, and the text is not in it.</summary>
    NotFound,

    /// <summary>Not searched: it looks like a binary file, so text in it is not text anyone typed.</summary>
    Binary,

    /// <summary>Not searched: larger than <see cref="ContentMatcher.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>Not searched: it could not be opened or read. Never an exception.</summary>
    Unreadable,
}

/// <summary>
/// Whether a file contains some text, for the searches that have no index to
/// ask.
///
/// **One matcher for both platforms' walks.** Windows has no index behind its
/// search at all, and Linux has one only when Baloo is running; everywhere
/// else a search is a walk of the tree, and until this existed a walk could
/// only match names. Both walks already have each entry's length — on
/// Windows out of the directory read itself, on Linux from the one stat the
/// enumeration makes for it — so the cheap refusals below cost no open.
///
/// **It reads; it does not index, and that is a deliberate trade.** The search
/// code once recorded that reading every file "would be indistinguishable from
/// a hang on any real folder". The answer here is that it can be stopped at
/// any point: cancellation is checked before every open and every read, so
/// pressing Stop leaves at most one open or one <see cref="BufferSize"/> read
/// in flight on the walk's thread, and a file over <see cref="MaxBytes"/> is
/// never opened at all.
///
/// **Refused without opening, in this order.** An empty text or a length of
/// zero or less is NotFound — an empty file cannot contain anything, and on
/// Linux this is also what keeps a FIFO, socket, device node or procfs file
/// from being opened: they report a size of 0, and opening a FIFO for reading
/// blocks inside open() where no cancellation token can reach it. A length
/// over the cap is TooLarge. Everything else is opened shared, so a file
/// another program has open for writing or deleting can still be read.
///
/// **What counts as text.** A byte-order mark decides the encoding when there
/// is one: UTF-8, UTF-16 in either order, UTF-32 in either order — UTF-32LE is
/// tested before UTF-16LE because its mark begins with UTF-16LE's. With no
/// mark, a zero byte in the first <see cref="SniffBytes"/> bytes means binary,
/// which is git's rule; anything else is read as UTF-8, with bytes that are
/// not valid UTF-8 becoming U+FFFD rather than an error. Two consequences,
/// stated because they are real: a file in a legacy code page still matches
/// any question made of ASCII, and UTF-16 written without a mark is called
/// binary, because it is full of zero bytes.
///
/// **A match across a buffer edge is found.** The text is searched for in
/// decoded characters, and the last <c>text.Length - 1</c> characters of each
/// buffer are carried to the front of the next, so a match that straddles two
/// reads is seen whole. One stateful decoder is kept for the whole file, so a
/// character whose bytes are split between two reads decodes as that
/// character rather than as two replacement characters.
/// </summary>
public static class ContentMatcher
{
    /// <summary>
    /// The largest file that is read. Anything bigger is reported TooLarge and
    /// never opened, and the search says how many it skipped — which is the
    /// honest alternative to reading only the start and silently missing the
    /// rest.
    /// </summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>One read's worth, and the most a cancellation waits for.</summary>
    internal const int BufferSize = 64 * 1024;

    /// <summary>How far in a zero byte is looked for before calling a file binary.</summary>
    internal const int SniffBytes = 8000;

    private static ReadOnlySpan<byte> Utf8Mark => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf32LeMark => [0xFF, 0xFE, 0x00, 0x00];
    private static ReadOnlySpan<byte> Utf32BeMark => [0x00, 0x00, 0xFE, 0xFF];
    private static ReadOnlySpan<byte> Utf16LeMark => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf16BeMark => [0xFE, 0xFF];

    // One of each, because an Encoding is immutable and safe to share; the
    // decoder each file needs is made from it per file.
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private static readonly Encoding Utf32Le = new UTF32Encoding(false, true, false);
    private static readonly Encoding Utf32Be = new UTF32Encoding(true, true, false);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, true, false);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, true, false);

    /// <summary>
    /// Whether the file at <paramref name="path"/> contains
    /// <paramref name="text"/>. <paramref name="length"/> is the size the walk
    /// already has for the entry; it is trusted for the two refusals that
    /// happen before any system call.
    /// </summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled, by the same
    /// route a cancel between directories leaves the walk.</exception>
    public static ContentVerdict FileContains(
        string path, long length, string text, bool caseSensitive, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text) || length <= 0) return ContentVerdict.NotFound;

        if (length > MaxBytes) return ContentVerdict.TooLarge;

        // Before the open as well as before every read. An open on a share
        // whose server has gone can wait out the network timeout, and a Stop
        // pressed during one should not be followed by another for every
        // file left in the folder.
        ct.ThrowIfCancellationRequested();

        FileStream stream;

        try
        {
            // No buffer of the stream's own: every read here is already a
            // whole BufferSize, and a FileStream that buffers allocates a
            // second 64 KB array the moment a short file's second read comes
            // back smaller.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete,
                                    bufferSize: 0, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ContentVerdict.Unreadable;
        }

        using (stream)
        {
            try
            {
                return StreamContains(stream, text, caseSensitive, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ContentVerdict.Unreadable;
            }
        }
    }

    /// <summary>
    /// The walks' one call: whether this file's contents answer the query.
    ///
    /// Shared so that the refusal a search has to own up to is counted in one
    /// place. Both walks would otherwise each switch on the verdict, and the
    /// first to forget the TooLarge arm would drop files from its answers
    /// without the band ever hearing about them.
    /// </summary>
    public static bool Answers(SearchQuery query, string path, long length, CancellationToken ct)
    {
        var verdict = FileContains(path, length, query.Text, query.CaseSensitive, ct);

        if (verdict == ContentVerdict.TooLarge) query.Skipped?.CountTooLarge();

        return verdict == ContentVerdict.Found;
    }

    /// <summary>
    /// The reading half of <see cref="FileContains"/>, on any stream, so the
    /// encoding and buffer-edge rules can be tested without a file.
    ///
    /// Stops early with TooLarge if the stream turns out longer than
    /// <see cref="MaxBytes"/> — a file can grow between the walk reading its
    /// length and this reading its contents, and an unbounded read is the one
    /// thing a Stop button cannot make short.
    ///
    /// **Both buffers are rented, not made.** The characters one read decodes
    /// to need about 128 KB — past the 85,000 bytes where an array goes on the
    /// large object heap, which is only collected with the whole heap — and a
    /// search of contents does this once for every file it opens. Made fresh
    /// each time, that is a large array per file for the length of a walk;
    /// rented, the same few arrays serve the whole search.
    /// </summary>
    internal static ContentVerdict StreamContains(
        Stream stream, string text, bool caseSensitive, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return ContentVerdict.NotFound;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var rented = ArrayPool<byte>.Shared.Rent(BufferSize);
        char[]? chars = null;

        try
        {
            // Rent may hand back more than was asked for; a read is always
            // exactly one BufferSize, which the buffer-edge tests rely on.
            var bytes = rented.AsSpan(0, BufferSize);

            ct.ThrowIfCancellationRequested();

            var read = stream.ReadAtLeast(bytes, BufferSize, throwOnEndOfStream: false);

            if (read == 0) return ContentVerdict.NotFound;

            var (encoding, offset) = Sniff(bytes[..read]);

            if (encoding is null) return ContentVerdict.Binary;

            var decoder = encoding.GetDecoder();
            var carryMax = text.Length - 1;

            // Room for what one read can decode to, plus what is carried in
            // front of it. GetMaxCharCount already allows for a sequence the
            // decoder is holding over from the previous read; the margin is for
            // the flush.
            chars = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(BufferSize) + carryMax + 4);
            var carry = 0;
            long total = read;

            while (true)
            {
                var decoded = decoder.GetChars(bytes[offset..read], chars.AsSpan(carry), flush: false);
                ReadOnlySpan<char> window = chars.AsSpan(0, carry + decoded);

                if (window.IndexOf(text, comparison) >= 0) return ContentVerdict.Found;

                // Keep the tail that could be the start of a match finished by
                // the next read. Overlapping copy within one array: Span.CopyTo
                // is defined to behave as if through a temporary.
                carry = Math.Min(carryMax, window.Length);
                window[^carry..].CopyTo(chars);

                ct.ThrowIfCancellationRequested();

                read = stream.ReadAtLeast(bytes, BufferSize, throwOnEndOfStream: false);
                offset = 0;

                if (read == 0) break;

                total += read;

                if (total > MaxBytes) return ContentVerdict.TooLarge;
            }

            // Whatever the decoder was still holding when the stream ended.
            var tail = decoder.GetChars([], chars.AsSpan(carry), flush: true);

            return ((ReadOnlySpan<char>)chars.AsSpan(0, carry + tail)).IndexOf(text, comparison) >= 0
                ? ContentVerdict.Found
                : ContentVerdict.NotFound;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);

            if (chars is not null) ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>
    /// The encoding the first read implies and how many mark bytes to skip, or
    /// a null encoding for a file that looks binary.
    /// </summary>
    private static (Encoding? Encoding, int Skip) Sniff(ReadOnlySpan<byte> head)
    {
        // GUARD, not a tested rule. Without this line a UTF-8 file with a mark
        // falls through to the no-mark arm, decodes the mark as U+FEFF and is
        // searched just as well, since no question anybody types begins with
        // an invisible character. Kept so the mark is never part of the text.
        if (head.StartsWith(Utf8Mark)) return (Utf8, 3);

        // Before UTF-16LE: this mark begins with UTF-16LE's.
        if (head.StartsWith(Utf32LeMark)) return (Utf32Le, 4);

        if (head.StartsWith(Utf32BeMark)) return (Utf32Be, 4);

        if (head.StartsWith(Utf16LeMark)) return (Utf16Le, 2);

        if (head.StartsWith(Utf16BeMark)) return (Utf16Be, 2);

        if (head[..Math.Min(SniffBytes, head.Length)].Contains((byte)0)) return (null, 0);

        return (Utf8, 0);
    }
}
