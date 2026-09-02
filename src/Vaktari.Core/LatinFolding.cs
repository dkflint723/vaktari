namespace Vaktari.Core;

/// <summary>
/// The A–Z letter a Latin character sorts under.
///
/// **"Écoles", "Über" and "Ångström" sorted below "Zebra", and banded under
/// '#'.** Upper-casing a name and subtracting code units is ordinal order
/// wearing a hat: 'É' is U+00C9, which is 201, and 'Z' is 90 — so every
/// accented name in a European folder fell off the bottom of the alphabet.
///
/// **Not ICU, and deliberately.** InvariantGlobalization is on and stays on:
/// culture-aware comparison degrades to ordinal in that mode, so reaching for
/// CompareInfo would compile, ship, and change nothing — which is the trap this
/// bug sets. Turning the flag off would buy a libicu dependency that BUILDING
/// advertises as absent, make the order depend on the machine's locale, and put
/// a collation call on a path whose own comment promises it never allocates,
/// because a 200,000-entry sort is millions of calls.
///
/// **One-to-one, so no expansions**: Æ folds to A rather than AE, ß to S rather
/// than SS. "Æon" beside "Aon" instead of beside "Aeon" is an error of one
/// position; "Æon" after "Zebra" is an error of the whole alphabet. Danish and
/// Swedish really do sort Æ Ø Å after Z, but there is no locale to ask in
/// invariant mode, and the Unicode root collation folds them too.
///
/// Anything outside the table — Greek, Cyrillic, CJK, punctuation, and the two
/// maths signs sitting inside the Latin-1 block — comes back merely
/// upper-cased, which keeps today's behaviour for scripts that have no Latin
/// base letter to fold to.
/// </summary>
public static class LatinFolding
{
    /// <summary>
    /// Indexed by <c>c - 'À'</c>: Latin-1 Supplement then Latin Extended-A, one
    /// contiguous run of 192. U+00D7 (×) and U+00F7 (÷) are not letters and map
    /// to themselves, so they keep landing in the '#' band.
    /// </summary>
    private static ReadOnlySpan<char> Bases =>
        "AAAAAAACEEEEIIII" +   // U+00C0
        "DNOOOOO×OUUUUYTS" +   // U+00D0
        "AAAAAAACEEEEIIII" +   // U+00E0
        "DNOOOOO÷OUUUUYTY" +   // U+00F0
        "AAAAAACCCCCCCCDD" +   // U+0100
        "DDEEEEEEEEEEGGGG" +   // U+0110
        "GGGGHHHHIIIIIIII" +   // U+0120
        "IIIIJJKKKLLLLLLL" +   // U+0130
        "LLLNNNNNNNNNOOOO" +   // U+0140
        "OOOORRRRRRSSSSSS" +   // U+0150
        "SSTTTTTTUUUUUUUU" +   // U+0160
        "UUUUWWYYYZZZZZZS";    // U+0170

    /// <summary>
    /// The upper-case letter <paramref name="c"/> sorts under: 'é' and 'É' both
    /// give 'E', 'ø' gives 'O', 'ß' gives 'S'. A character with no Latin base
    /// comes back merely upper-cased.
    /// </summary>
    public static char FoldUpper(char c)
    {
        if (char.IsAsciiLetter(c)) return char.ToUpperInvariant(c);

        return c is >= 'À' and <= 'ſ' ? Bases[c - 'À'] : char.ToUpperInvariant(c);
    }
}
