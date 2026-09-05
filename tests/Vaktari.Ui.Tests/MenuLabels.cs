namespace Vaktari.Ui.Tests;

/// <summary>
/// Reading a menu header the way the theme does: an underscore marks the next
/// character as the row's access key and is not a letter of the word.
///
/// **ONE underscore is notation; every other one is text.** Measured on
/// Avalonia 12.1, by opening a hand-built ContextMenu in a headless window and
/// reading, for each MenuItem's realized AccessText, both its AccessKey and the
/// glyphs its TextLayout actually draws:
///
///     Header         drawn        access key
///     "_Open"        "Open"       O
///     "Cop_y"        "Copy"       y
///     "Open _with"   "Open with"  w
///     "A__B"         "A_B"        _
///     "C__"          "C_"         _
///     "D_"           "D_"         none
///     "a_b_c"        "ab_c"       b
///
/// The rule that explains all seven: the FIRST underscore that has a character
/// after it is the marker — it is dropped from the drawn text and the character
/// after it becomes the access key — and every other underscore is drawn as
/// itself and declares nothing.
///
/// **A doubled underscore is not an escape, though it looks like one.** It
/// draws the single underscore an escape would, so a reader checking the label
/// sees what they expected; but the character the marker consumed IS that
/// second underscore, so the row quietly answers to '_' rather than to no key
/// at all. <see cref="Key"/> reports it, so <see cref="ContextMenuKeysTests"/>
/// counts it against the row like any other key rather than losing it. No
/// header in the markup carries a second underscore today — measured, over
/// every label attribute in both markup files — and
/// ContextMenuKeysTests.No_row_draws_a_stray_underscore is what keeps it that
/// way.
///
/// Every assertion in this assembly that names a menu row by its words goes
/// through <see cref="Plain"/> rather than spelling the marker into the
/// expected string. That was a decision with a loser: writing "_Paste" into
/// PasteOfferedTests would have pinned the access key from there too, but it
/// would also have broken half a dozen unrelated tests the first time a key
/// moved from one letter of a word to another — and those tests are about
/// gating and commands, not about keys. The keys are pinned in one place,
/// <see cref="ContextMenuKeysTests"/>, which is the file that is supposed to
/// fail when one changes.
/// </summary>
internal static class MenuLabels
{
    /// <summary>
    /// Where the one underscore the theme consumes is, or -1 when the header
    /// declares no key. A trailing underscore has nothing after it to mark, and
    /// is drawn — "D_" shows "D_" and answers to nothing.
    /// </summary>
    private static int Marker(string header)
    {
        var at = header.IndexOf('_', StringComparison.Ordinal);

        return at >= 0 && at + 1 < header.Length ? at : -1;
    }

    /// <summary>The words the row shows: the header with the marker taken out.</summary>
    internal static string Plain(string? header)
    {
        if (header is null) return "";

        var marker = Marker(header);

        return marker < 0 ? header : header.Remove(marker, 1);
    }

    /// <summary>
    /// The access key the header declares, lower cased, or null for none. At
    /// most one, because the theme consumes at most one marker — the rule that
    /// a second underscore would be a letter nobody can press is not a rule
    /// about keys at all, it is <see cref="Plain"/> coming back with an
    /// underscore still in it.
    /// </summary>
    internal static char? Key(string? header)
    {
        if (header is null) return null;

        var marker = Marker(header);

        return marker < 0 ? null : char.ToLowerInvariant(header[marker + 1]);
    }
}
