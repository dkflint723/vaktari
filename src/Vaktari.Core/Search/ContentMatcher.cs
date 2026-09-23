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
/// only match names. Both walks already have, for each entry, the length the
/// directory read gave them, which is what lets the cheap refusals below
/// happen without a system call.
///
/// **It reads; it does not index, and that is a deliberate trade.** The search
/// code once recorded that reading every file "would be indistinguishable from
/// a hang on any real folder". The answer here is that it can be stopped at
/// any point: cancellation is checked before every read, so pressing Stop
/// costs at most one <see cref="BufferSize"/> read, and a file over
/// <see cref="MaxBytes"/> is never opened at all.
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

        FileStream stream;

        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete,
                                    BufferSize, FileOptions.SequentialScan);
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
    /// The reading half of <see cref="FileContains"/>, on any stream, so the
    /// encoding and buffer-edge rules can be tested without a file.
    ///
    /// Stops early with TooLarge if the stream turns out longer than
    /// <see cref="MaxBytes"/> — a file can grow between the walk reading its
    /// length and this reading its contents, and an unbounded read is the one
    /// thing a Stop button cannot make short.
    /// </summary>
    internal static ContentVerdict StreamContains(
        Stream stream, string text, bool caseSensitive, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return ContentVerdict.NotFound;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var bytes = new byte[BufferSize];

        ct.ThrowIfCancellationRequested();

        var read = stream.ReadAtLeast(bytes, BufferSize, throwOnEndOfStream: false);

        if (read == 0) return ContentVerdict.NotFound;

        var (encoding, offset) = Sniff(bytes.AsSpan(0, read));

        if (encoding is null) return ContentVerdict.Binary;

        var decoder = encoding.GetDecoder();
        var carryMax = text.Length - 1;

        // Room for what one read can decode to, plus what is carried in front
        // of it. GetMaxCharCount already allows for a sequence the decoder is
        // holding over from the previous read; the margin is for the flush.
        var chars = new char[encoding.GetMaxCharCount(BufferSize) + carryMax + 4];
        var carry = 0;
        long total = read;

        while (true)
        {
            var decoded = decoder.GetChars(bytes, offset, read - offset, chars, carry, flush: false);
            ReadOnlySpan<char> window = chars.AsSpan(0, carry + decoded);

            if (window.IndexOf(text, comparison) >= 0) return ContentVerdict.Found;

            // Keep the tail that could be the start of a match finished by the
            // next read. Overlapping copy within one array: Span.CopyTo is
            // defined to behave as if through a temporary.
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
        var tail = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, carry, flush: true);

        return ((ReadOnlySpan<char>)chars.AsSpan(0, carry + tail)).IndexOf(text, comparison) >= 0
            ? ContentVerdict.Found
            : ContentVerdict.NotFound;
    }

    /// <summary>
    /// The encoding the first read implies and how many mark bytes to skip, or
    /// a null encoding for a file that looks binary.
    /// </summary>
    private static (Encoding? Encoding, int Skip) Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Utf8Mark)) return (new UTF8Encoding(false, false), 3);

        // Before UTF-16LE: this mark begins with UTF-16LE's.
        if (head.StartsWith(Utf32LeMark)) return (new UTF32Encoding(false, true, false), 4);

        if (head.StartsWith(Utf32BeMark)) return (new UTF32Encoding(true, true, false), 4);

        if (head.StartsWith(Utf16LeMark)) return (new UnicodeEncoding(false, true, false), 2);

        if (head.StartsWith(Utf16BeMark)) return (new UnicodeEncoding(true, true, false), 2);

        if (head[..Math.Min(SniffBytes, head.Length)].Contains((byte)0)) return (null, 0);

        return (new UTF8Encoding(false, false), 0);
    }
}
