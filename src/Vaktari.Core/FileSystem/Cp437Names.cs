using System.Text;
using SharpCompress.Common;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// How a zip entry's name bytes become a name.
///
/// **A zip says which encoding its names are in with one bit, and most zips
/// ever written do not set it.** Without the bit the format means code page
/// 437, the original IBM PC set; with it, UTF-8. In practice plenty of tools
/// write UTF-8 without the bit, so an unflagged name that is VALID UTF-8 and
/// uses a byte above 0x7F is read as UTF-8 — no CP437 name made of accented
/// letters happens to be valid UTF-8 as well, except by accident.
///
/// **Our own table, because the framework has none.** This application runs
/// with InvariantGlobalization, and <see cref="Encoding.GetEncoding(int)"/>
/// has no code page 437 without the code-pages provider, which drags in every
/// code page there is.
///
/// **A flagged name whose bytes are not UTF-8 falls back to CP437**, rather
/// than decoding with U+FFFD in place of each bad byte: measured in the
/// refutations, two different flagged-invalid names (<c>x\xFF</c> and
/// <c>x\xFE</c>) both became <c>x�</c> — one name for two entries.
/// </summary>
internal static class Cp437Names
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>0x80 to 0xFF of code page 437.</summary>
    private const string High =
        "ÇüéâäàåçêëèïîìÄÅ" +
        "ÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
        "áíóúñÑªº¿⌐¬½¼¡«»" +
        "░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
        "└┴┬├─┼╞╟╚╔╩╦╠═╬╧" +
        "╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩" +
        "≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    internal static string Decode(byte[] bytes, int index, int count, EncodingType type)
    {
        var span = bytes.AsSpan(index, count);

        if (type == EncodingType.UTF8 || span.ContainsAnyInRange((byte)0x80, (byte)0xFF))
        {
            try
            {
                return Strict.GetString(span);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8, whatever the flag says: CP437 below.
            }
        }

        return Cp437(span);
    }

    internal static string Cp437(ReadOnlySpan<byte> span)
    {
        var chars = new char[span.Length];

        for (var i = 0; i < span.Length; i++)
            chars[i] = span[i] < 0x80 ? (char)span[i] : High[span[i] - 0x80];

        return new string(chars);
    }
}
