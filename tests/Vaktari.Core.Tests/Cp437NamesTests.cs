using SharpCompress.Common;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Zip names without the UTF-8 flag are code page 437; with it, UTF-8 — and
/// a flag that lies falls back rather than turning two names into one.
/// </summary>
public sealed class Cp437NamesTests
{
    private static string Decode(byte[] bytes, bool flagged)
        => Cp437Names.Decode(bytes, 0, bytes.Length, flagged ? EncodingType.UTF8 : EncodingType.Default);

    /// <summary>
    /// Latin-1 bytes are not UTF-8, so the name is CP437 — and the two stay
    /// two. Decoding them as UTF-8 would give <c>caf�</c> for both.
    /// </summary>
    [Fact]
    public void Unflagged_latin1_names_decode_as_cp437_and_stay_distinct()
    {
        var one = Decode([(byte)'c', (byte)'a', (byte)'f', 0xE9], flagged: false);
        var two = Decode([(byte)'c', (byte)'a', (byte)'f', 0xE8], flagged: false);

        Assert.Equal("cafΘ", one);
        Assert.Equal("cafΦ", two);
        Assert.DoesNotContain('�', one + two);
    }

    [Fact]
    public void The_whole_cp437_high_half_maps()
    {
        Assert.Equal("Ç", Decode([0x80], flagged: false));
        Assert.Equal("é", Decode([0x82], flagged: false));
        Assert.Equal(" ", Decode([0xFF], flagged: false));
    }

    [Fact]
    public void Flagged_utf8_is_utf8() => Assert.Equal("café", Decode("café"u8.ToArray(), flagged: true));

    [Fact]
    public void A_flag_on_bytes_that_are_not_utf8_falls_back_to_cp437()
    {
        var one = Decode([(byte)'x', 0xFF], flagged: true);
        var two = Decode([(byte)'x', 0xFE], flagged: true);

        Assert.NotEqual(one, two);
        Assert.DoesNotContain('�', one + two);
    }

    /// <summary>Plenty of tools write UTF-8 names without the flag.</summary>
    [Fact]
    public void Unflagged_valid_utf8_with_high_bytes_is_utf8()
        => Assert.Equal("тест.txt", Decode("тест.txt"u8.ToArray(), flagged: false));

    [Fact]
    public void Plain_ascii_is_itself() => Assert.Equal("readme.txt", Decode("readme.txt"u8.ToArray(), flagged: false));
}
