using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// For the fixer, red at 7593a3e (batch-0.11.2e QA, round 7): **the safe-stem
/// prefilter rejects real aliases.** Every pair below is a folder made on NTFS
/// (D:, short names on) and the 8.3 name the volume gave it, read back with
/// GetShortPathName. MayBeAliasOf must accept each: a rejected one is a return
/// not heard until the slow look, up to 30 s late. Three kinds:
/// - characters outside the 8.3 set (accented, Greek, CJK, fullwidth, ligatures,
///   emoji, a combining mark) are dropped from the alias, not replaced;
/// - a hashed alias of a base with one valid character is that character and
///   four hex digits (X31AB~1), and of a base with none, four hex digits alone
///   (D26C~1);
/// - so a name of only dropped characters before its extension hashes to hex alone.
/// </summary>
public sealed class FolderReturnWatchAliasTests
{
    [Theory]
    [InlineData("éabcdefgh", "ABCDEF~1")]
    [InlineData("Ünïcödé folder", "NCDFOL~1")]
    [InlineData("日本語フォルダ名前です", "BD82~1")]
    [InlineData("ßtraße langer name", "TRAELA~1")]
    [InlineData("ąćęłńóśźż długa nazwa", "DUGANA~1")]
    [InlineData("Ωmega long folder", "MEGALO~1")]
    [InlineData("ﬁle ligature folder", "LELIGA~1")]
    [InlineData("ＡＢＣ fullwidth folder", "FULLWI~1")]
    [InlineData("İstanbul klasörü", "STANBU~1")]
    [InlineData("Ärger mit Ölen", "RGERMI~1")]
    [InlineData("naïve café folder", "NAVECA~1")]
    [InlineData("🙂emoji folder", "EMOJIF~1")]
    [InlineData("éé folder5", "FO1A27~1")]
    [InlineData("ÆØ folder1", "FOLDER~1")]
    [InlineData("x.longext", "X31AB~1.LON")]
    [InlineData("a.longext1", "A5B2A~1.LON")]
    [InlineData("z.a10longer", "Z311C~1.A10")]
    [InlineData("é", "D26C~1")]
    [InlineData("éé", "B4BC~1")]
    [InlineData("ü.longext", "7A8A~1.LON")]
    [InlineData("é.longext1", "CF87~1.LON")]
    public void A_real_alias_is_never_rejected(string name, string alias)
        => Assert.True(FolderReturnWatch.MayBeAliasOf(name, alias), $"{alias} is the volume's own 8.3 name for {name}");

    /// <summary>The kinds already accepted, kept so a fix does not lose them.</summary>
    [Theory]
    [InlineData("TargetFolderName", "TARGET~1")]
    [InlineData("TargetFolderName6", "TAEAE4~1")]
    [InlineData("+abcdefghij", "_ABCDE~1")]
    [InlineData(" abcdefghij", "ABCDEF~1")]
    [InlineData("a.b.c.d.e.f.g", "ABCDEF~1.G")]
    [InlineData("my-folder-name", "MY-FOL~1")]
    [InlineData("a+", "A_9011~1")]
    [InlineData("ab.longext1", "AB405E~1.LON")]
    [InlineData("1 folder5", "1F856E~1")]
    public void The_aliases_accepted_today_stay_accepted(string name, string alias)
        => Assert.True(FolderReturnWatch.MayBeAliasOf(name, alias));

    /// <summary>And unrelated names are still turned away, or the filter is gone.</summary>
    [Theory]
    [InlineData("f12345", "SOMELO~1")]
    [InlineData("Photo_0001.jpg", "TARGET~1")]
    public void An_unrelated_name_is_still_turned_away(string name, string alias)
        => Assert.False(FolderReturnWatch.MayBeAliasOf(name, alias));
}
