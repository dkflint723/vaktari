using System.Text;
using Vaktari.Core.Search;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Whether a file contains some text, for searches with no index behind them.
///
/// Most of these read from a MemoryStream rather than a file, so a read comes
/// back exactly <see cref="ContentMatcher.BufferSize"/> long and the bytes that
/// straddle a buffer edge can be placed on purpose. Inputs are built as byte
/// arrays, never as source-file text, so the encoding of THIS file is not what
/// any of them is testing.
///
/// **The two buffer-edge tests guard different code, and that is checked.**
/// One places an ASCII match across the edge, which only the carried tail can
/// find; the other splits one character's bytes across the edge with the whole
/// match after it, which only a decoder that keeps its state can find.
/// Breaking the carry reddens the first and not the second; breaking the
/// decoder reddens the second and not the first.
/// </summary>
public sealed class ContentMatcherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-content").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ContentVerdict Search(byte[] bytes, string text, bool caseSensitive = false)
        => ContentMatcher.StreamContains(new MemoryStream(bytes), text, caseSensitive, CancellationToken.None);

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] Filler(int count) => Enumerable.Repeat((byte)'x', count).ToArray();

    [Fact]
    public void Text_that_is_there_is_found() =>
        Assert.Equal(ContentVerdict.Found, Search(Ascii("the quick brown fox"), "brown"));

    [Fact]
    public void Text_that_is_not_there_is_not() =>
        Assert.Equal(ContentVerdict.NotFound, Search(Ascii("the quick brown fox"), "purple"));

    [Fact]
    public void Case_is_ignored_unless_asked_for() =>
        Assert.Equal(ContentVerdict.Found, Search(Ascii("A Zebra Crossing"), "zebra"));

    /// <summary>The flag the search band's Match case sets reaches the file read.</summary>
    [Fact]
    public void Case_is_respected_when_asked_for() =>
        Assert.Equal(ContentVerdict.NotFound, Search(Ascii("A Zebra Crossing"), "zebra", caseSensitive: true));

    /// <summary>
    /// A match split across two reads, in ASCII so no decoder state is involved:
    /// "ABCD" ends the first read and "EFGH" begins the second. Only the tail
    /// carried from one buffer to the next can see it whole.
    /// </summary>
    [Fact]
    public void A_match_straddling_two_reads_is_found()
    {
        var bytes = Concat(Filler(ContentMatcher.BufferSize - 4), Ascii("ABCDEFGH"), Filler(100));

        Assert.Equal(ContentVerdict.Found, Search(bytes, "ABCDEFGH", caseSensitive: true));
    }

    /// <summary>
    /// One character's two bytes split across two reads, with the whole match
    /// after the split. The first read ends on 0xC3, the first byte of 'é'; the
    /// second begins 0xA9 't' 'é'. A decoder that keeps its state across reads
    /// produces "été" entirely inside the second buffer, so the carried tail is
    /// irrelevant here — and a decoder that starts fresh each read turns the
    /// split into two replacement characters and misses it.
    /// </summary>
    [Fact]
    public void A_character_split_across_two_reads_still_decodes_as_itself()
    {
        var bytes = Concat(Filler(ContentMatcher.BufferSize - 1),
                           [0xC3, 0xA9, (byte)'t', 0xC3, 0xA9],
                           Filler(100));

        Assert.Equal(ContentVerdict.Found, Search(bytes, "été", caseSensitive: true));
    }

    [Fact]
    public void Utf8_with_a_mark_is_read() =>
        Assert.Equal(ContentVerdict.Found,
                     Search(Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("naïve café")), "café"));

    /// <summary>Full of zero bytes, so it would be called binary without its mark.</summary>
    [Fact]
    public void Utf16_little_endian_with_a_mark_is_read() =>
        Assert.Equal(ContentVerdict.Found,
                     Search(Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes("hello world")), "world"));

    [Fact]
    public void Utf16_big_endian_with_a_mark_is_read() =>
        Assert.Equal(ContentVerdict.Found,
                     Search(Concat([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes("hello world")), "world"));

    /// <summary>
    /// Its mark begins with UTF-16LE's, so the order the marks are tested in
    /// decides whether this is read as UTF-32 or as garbage UTF-16.
    /// </summary>
    [Fact]
    public void Utf32_little_endian_is_not_mistaken_for_utf16() =>
        Assert.Equal(ContentVerdict.Found,
                     Search(Concat([0xFF, 0xFE, 0x00, 0x00], Encoding.UTF32.GetBytes("hello world")), "world"));

    [Fact]
    public void A_zero_byte_near_the_start_means_binary() =>
        Assert.Equal(ContentVerdict.Binary,
                     Search(Concat(Ascii("header"), [0x00], Ascii("the word is here")), "word"));

    /// <summary>
    /// The binary test only looks at the start. A text file with a stray zero
    /// byte deep inside it is still searched, which is git's behaviour too.
    /// </summary>
    [Fact]
    public void A_zero_byte_past_the_sniff_window_does_not_make_it_binary()
    {
        var bytes = Concat(Filler(ContentMatcher.SniffBytes + 10), [0x00], Ascii("needle"));

        Assert.Equal(ContentVerdict.Found, Search(bytes, "needle"));
    }

    /// <summary>
    /// A legacy code page file: 0xE9 is 'é' in cp1252 but not valid UTF-8. It
    /// becomes a replacement character rather than an error, and an ASCII
    /// question about the same file is still answered.
    /// </summary>
    [Fact]
    public void Bytes_that_are_not_utf8_do_not_stop_an_ascii_question()
    {
        var bytes = Concat(Ascii("caf"), [0xE9], Ascii(" and the menu"));

        Assert.Equal(ContentVerdict.Found, Search(bytes, "menu"));
    }

    /// <summary>
    /// An empty file cannot contain anything, so it is never opened. Asserted
    /// with a path that does not exist: had it been opened, the answer would be
    /// Unreadable. This is also what keeps a FIFO from being opened on Linux,
    /// where opening one for reading blocks where no cancellation can reach.
    /// </summary>
    [Fact]
    public void A_length_of_zero_is_answered_without_opening_anything()
    {
        var nowhere = Path.Combine(_root, "does-not-exist.txt");

        Assert.Equal(ContentVerdict.NotFound,
                     ContentMatcher.FileContains(nowhere, 0, "anything", false, CancellationToken.None));
    }

    /// <summary>Refused on the length alone, before any open — same technique.</summary>
    [Fact]
    public void A_file_over_the_limit_is_refused_without_being_opened()
    {
        var nowhere = Path.Combine(_root, "does-not-exist.bin");

        Assert.Equal(ContentVerdict.TooLarge,
                     ContentMatcher.FileContains(nowhere, ContentMatcher.MaxBytes + 1, "anything",
                                                 false, CancellationToken.None));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_reported_rather_than_thrown()
    {
        var nowhere = Path.Combine(_root, "does-not-exist.txt");

        Assert.Equal(ContentVerdict.Unreadable,
                     ContentMatcher.FileContains(nowhere, 100, "anything", false, CancellationToken.None));
    }

    /// <summary>The same answers through a real file, so the open path is exercised too.</summary>
    [Fact]
    public void A_real_file_on_disk_is_searched()
    {
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllBytes(path, Ascii("remember the milk"));

        Assert.Equal(ContentVerdict.Found,
                     ContentMatcher.FileContains(path, new FileInfo(path).Length, "milk", false,
                                                 CancellationToken.None));
    }

    /// <summary>Stop is honoured, and leaves by the same exception a walk cancelled between folders does.</summary>
    [Fact]
    public void A_cancelled_search_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ContentMatcher.StreamContains(new MemoryStream(Ascii("anything at all")), "all",
                                                false, cancelled.Token));
    }
}
