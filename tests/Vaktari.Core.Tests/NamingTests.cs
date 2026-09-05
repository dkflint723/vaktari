using Vaktari.Core;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What the interface calls the bin, and the grammar around it.
///
/// **The grammar is the part worth testing.** Swapping "trash" for "Recycle
/// Bin" is not a find-and-replace: one is a common noun that stays lowercase in
/// running text, the other a proper noun that keeps its capitals everywhere. A
/// naive substitution yields "Empty trash…" becoming "Empty Recycle Bin…"
/// correctly and "the trash is already empty" becoming "the Recycle Bin is
/// already empty" — right — but also turns a sentence-initial "Trash" into
/// "Recycle Bin" while quietly leaving a mid-sentence "Trash" capitalised.
///
/// These run in Core, without a window, because which word is used is a fact
/// about the platform rather than a drawing decision — the same split that puts
/// FileCategories here.
/// </summary>
public class NamingTests
{
    [Theory]
    [InlineData("trash", "the trash")]
    [InlineData("Recycle Bin", "the Recycle Bin")]
    public void A_sentence_gets_the_article(string bin, string expected)
    {
        Naming.Adopt(bin, "test");

        Assert.Equal(expected, Naming.TheBin);
    }

    /// <summary>
    /// A label that starts with the word capitalises it; a name that already
    /// carries capitals is left exactly as the platform wrote it.
    /// </summary>
    [Theory]
    [InlineData("trash", "Trash")]
    [InlineData("Recycle Bin", "Recycle Bin")]
    public void A_label_capitalises_only_the_first_letter(string bin, string expected)
    {
        Naming.Adopt(bin, "test");

        Assert.Equal(expected, Naming.BinTitle);
    }

    /// <summary>
    /// **The interior capitals must survive.** ToUpper on the whole string, or
    /// a ToTitleCase, would give "RECYCLE BIN" or leave "Recycle bin" — both
    /// wrong, and both the kind of thing that looks fine until it is on screen.
    /// </summary>
    [Fact]
    public void A_two_word_name_keeps_its_second_capital()
    {
        Naming.Adopt("Recycle Bin", "windows");

        Assert.Equal("Recycle Bin", Naming.BinTitle);
        Assert.Equal("the Recycle Bin", Naming.TheBin);
        Assert.DoesNotContain("bin", Naming.BinTitle, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Blank input leaves the previous words standing rather than emptying the
    /// interface. A platform that answered with nothing would otherwise produce
    /// labels reading "Empty …" and "move 2 item(s) to the ?".
    /// </summary>
    [Fact]
    public void An_empty_answer_is_ignored()
    {
        Naming.Adopt("Recycle Bin", "windows");
        Naming.Adopt("", "");

        Assert.Equal("Recycle Bin", Naming.BinName);
        Assert.Equal("windows", Naming.Platform);
    }

    /// <summary>
    /// The drive listing has the same two forms as the bin, and it needed the
    /// second one for the same reason.
    ///
    /// **On Linux the two forms differ and on Windows they do not, which is
    /// exactly why this is a branch and not a ToLower.** "This computer" is a
    /// description and drops its capital inside a sentence — the Startup page's
    /// radio otherwise read "Open This computer". "This PC" is Explorer's
    /// proper noun and keeps its capitals wherever it stands, so lower-casing
    /// the sentence form would have produced "Open this PC" on the platform the
    /// name was borrowed from.
    /// </summary>
    [Theory]
    [InlineData("windows", "This PC", "This PC")]
    [InlineData("linux", "This computer", "this computer")]
    public void The_drive_listing_has_a_label_form_and_a_sentence_form(
        string platform, string title, string sentence)
    {
        Naming.Adopt("trash", platform);

        Assert.Equal(title, Naming.ComputerTitle);
        Assert.Equal(sentence, Naming.ComputerName);
    }

    /// <summary>
    /// Copy that branches must branch on the platform identity, never on the
    /// label. Both are strings and it is an easy mistake — the first version of
    /// the sweep explanation tested the label, which couples an English
    /// paragraph to the exact spelling of a name somebody may recapitalise.
    /// </summary>
    [Fact]
    public void The_platform_identity_is_kept_separately_from_the_label()
    {
        Naming.Adopt("Recycle Bin", "windows");

        Assert.Equal("windows", Naming.Platform);
        Assert.NotEqual(Naming.Platform, Naming.BinName);
    }
}
