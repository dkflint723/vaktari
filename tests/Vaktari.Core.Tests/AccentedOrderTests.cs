using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Where an accented name sorts, and which band it lands in.
///
/// **"Écoles", "Über" and "Ångström" sorted below "Zebra".** The comparison
/// upper-cased each character and subtracted code units, which is ordinal order
/// wearing a hat: 'É' is U+00C9, or 201, and 'Z' is 90. So every accented name
/// in a European folder fell off the bottom of the alphabet — and the grouping
/// bands, which tested IsAsciiLetter on the raw character, dropped the same
/// names into '#' beside ".gitignore" and "2024-report".
///
/// **The obvious fix would have changed nothing.** InvariantGlobalization is on,
/// and under it every culture-aware comparison degrades to ordinal — so
/// swapping in CompareInfo or InvariantCultureIgnoreCase compiles, ships, and
/// sorts exactly as before. Turning that flag off would buy a libicu dependency
/// the build notes advertise as absent, make the order depend on the machine's
/// locale, and put a collation call on a path that is walked millions of times
/// per listing. The table is deterministic and costs nothing.
/// </summary>
public sealed class AccentedOrderTests
{
    private static List<string> Sorted(params string[] names)
    {
        var sorted = names.ToList();

        sorted.Sort(NaturalOrder.Compare);

        return sorted;
    }

    [Fact]
    public void An_accented_name_sorts_with_its_letter_rather_than_after_Z()
        => Assert.Equal(
            ["Apple", "Écoles", "Zebra"],
            Sorted("Zebra", "Écoles", "Apple"));

    [Theory]
    [InlineData("Über", "Umbrella", "Violet")]
    [InlineData("Ångström", "Apple", "Banana")]
    [InlineData("Øre", "Orange", "Peach")]
    public void Each_folds_to_the_letter_it_reads_as(string accented, string plain, string after)
    {
        var sorted = Sorted(after, accented, plain);

        Assert.Equal(after, sorted[2]);
        Assert.Contains(accented, sorted.Take(2));
    }

    /// <summary>
    /// The accent is a TIE-BREAK, not a primary key. Returning the difference
    /// where it is found would be wrong the other way round: "Édam" would sort
    /// after "Elephant", because É/E would decide the name before d/l was ever
    /// read.
    /// </summary>
    [Fact]
    public void The_accent_only_decides_names_that_are_otherwise_the_same()
    {
        Assert.Equal(["Édam", "Elephant"], Sorted("Elephant", "Édam"));

        // And where the words really are the same, the plain one comes first
        // and the two are adjacent.
        Assert.Equal(["Ecoles", "Écoles"], Sorted("Écoles", "Ecoles"));
    }

    /// <summary>Case still does not decide anything on its own, which is what
    /// the comparison promised before this and has to go on promising.</summary>
    [Fact]
    public void Case_still_does_not_decide()
        => Assert.Equal(0, NaturalOrder.Compare("écoles", "ÉCOLES"));

    /// <summary>Digits are still read as numbers, not characters.</summary>
    [Fact]
    public void Numbering_still_reads_as_numbers()
        => Assert.Equal(
            ["Ärger 2.txt", "Ärger 10.txt"],
            Sorted("Ärger 10.txt", "Ärger 2.txt"));

    /// <summary>
    /// One-to-one, so no expansions: Æ folds to A rather than AE. "Æon" beside
    /// "Aon" instead of beside "Aeon" is an error of one position; "Æon" after
    /// "Zebra" is an error of the whole alphabet.
    /// </summary>
    [Fact]
    public void A_ligature_folds_to_one_letter()
        => Assert.Equal(['A', 'S', 'O'], new[] { 'Æ', 'ß', 'ø' }.Select(LatinFolding.FoldUpper));

    /// <summary>
    /// Anything with no Latin base comes back merely upper-cased, which keeps
    /// today's behaviour for scripts this table has nothing to say about.
    /// </summary>
    [Theory]
    [InlineData('Ж', 'Ж')]
    [InlineData('α', 'Α')]
    [InlineData('漢', '漢')]
    [InlineData('×', '×')]
    [InlineData('-', '-')]
    public void Anything_else_is_left_as_it_was(char given, char folded)
        => Assert.Equal(folded, LatinFolding.FoldUpper(given));

    // ---- and the bands agree with the order ---------------------------------

    /// <summary>
    /// **The same names were banded under '#'.** A heading that disagrees with
    /// the order beneath it is worse than either being wrong alone: the row is
    /// sorted under E and filed under "#".
    /// </summary>
    [Theory]
    [InlineData("Écoles", "E")]
    [InlineData("Über", "U")]
    [InlineData("Ångström", "A")]
    [InlineData("Øre", "O")]
    [InlineData("ßeta", "S")]
    public void An_accented_name_bands_under_its_own_letter(string name, string band)
        => Assert.Equal(band, Grouping.Label(Entry(name), GroupMode.Name, DateTimeOffset.UnixEpoch));

    /// <summary>
    /// And the bands are ORDERED by the same folded letter, not just labelled
    /// with it. The heading and the rank are two functions, and a fix to one
    /// leaves a listing whose E band sits after Z with "E" written on it.
    /// </summary>
    [Fact]
    public void An_accented_band_is_ordered_by_its_letter_too()
    {
        var accented = Entry("Écoles");
        var zed = Entry("Zebra");
        var eee = Entry("Elephant");

        Assert.True(
            Grouping.CompareGroups(accented, zed, GroupMode.Name, DateTimeOffset.UnixEpoch) < 0,
            "the E band sorts after Z");

        Assert.Equal(
            0,
            Grouping.CompareGroups(accented, eee, GroupMode.Name, DateTimeOffset.UnixEpoch));
    }

    /// <summary>Still a catch-all for what genuinely has no letter.</summary>
    [Theory]
    [InlineData(".gitignore")]
    [InlineData("2024-report.txt")]
    [InlineData("漢字.txt")]
    public void Something_with_no_latin_letter_still_bands_under_hash(string name)
        => Assert.Equal("#", Grouping.Label(Entry(name), GroupMode.Name, DateTimeOffset.UnixEpoch));

    private static FileEntry Entry(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);
}
