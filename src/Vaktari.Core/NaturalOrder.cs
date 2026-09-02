namespace Vaktari.Core;

/// <summary>
/// Compares names the way a person reads them, so <c>file2</c> sorts before
/// <c>file10</c> rather than after it.
///
/// Ordinal comparison puts "10" before "2" because '1' &lt; '2', which is
/// correct for bytes and wrong for anything a person named. Digit runs are
/// compared as numbers, everything else ordinally.
///
/// Works on spans and never allocates: this runs once per comparison while
/// sorting a directory, and a 200,000-entry sort is millions of calls.
/// </summary>
public static class NaturalOrder
{
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0;

        // Held rather than returned: see the comment on the letter comparison.
        var diacritics = 0;

        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                // Leading zeros do not change the value, but they do decide
                // ties: "01" and "1" are equal numerically, so the shorter run
                // wins to keep the order stable.
                var startA = i;
                var startB = j;

                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var runA = a[startA..i].TrimStart('0');
                var runB = b[startB..j].TrimStart('0');

                if (runA.Length != runB.Length)
                    return runA.Length - runB.Length;

                var digits = runA.SequenceCompareTo(runB);
                if (digits != 0) return digits;

                var padding = (i - startA) - (j - startB);
                if (padding != 0) return padding;

                continue;
            }

            // **Primary key: the base letter. Secondary key: the accent, and
            // only if the base letters never disagree.**
            //
            // Comparing raw code units put "Écoles" below "Zebra" — 'É' is 201
            // and 'Z' is 90 — so every accented name in a European folder fell
            // off the bottom of the alphabet.
            //
            // Returning the accent difference where it is found would be just
            // as wrong the other way: "Édam" would sort after "Elephant",
            // because the É/E difference would decide the name before the d/l
            // difference was ever read. An accent is a tie-break between names
            // that are otherwise the same word, which is what "Ecoles" before
            // "Écoles" means.
            var left = LatinFolding.FoldUpper(a[i]);
            var right = LatinFolding.FoldUpper(b[j]);

            if (left != right) return left - right;

            if (diacritics == 0)
                diacritics = char.ToUpperInvariant(a[i]) - char.ToUpperInvariant(b[j]);

            i++;
            j++;
        }

        var length = (a.Length - i) - (b.Length - j);

        // The shorter name first, and only then the accent — so "Ecoles" and
        // "Écoles" are adjacent and in that order, rather than a whole alphabet
        // apart.
        return length != 0 ? length : diacritics;
    }

    public static int Compare(string? a, string? b)
        => Compare(a.AsSpan(), b.AsSpan());
}
