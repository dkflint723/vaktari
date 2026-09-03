using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The names under /dev/disk/by-label, as a person wrote them.
///
/// **A stick called "MY STICK" showed up in the sidebar as "MY\x20STICK".**
/// Those directories are made of symlink names, a name cannot hold every byte a
/// label can, and udev writes the rest as hex — so reading the link name and
/// printing it puts the escape on screen. A space is the commonest label
/// character there is after the letters.
/// </summary>
public sealed class UdevNameTests
{
    [Fact]
    public void A_space_comes_back_a_space()
        => Assert.Equal("MY STICK", Decode(@"MY\x20STICK"));

    [Fact]
    public void And_several_do()
        => Assert.Equal("my back up disk", Decode(@"my\x20back\x20up\x20disk"));

    /// <summary>
    /// **Udev escapes each BYTE, not each character.** An accented letter is
    /// two escapes that together mean one character, so decoding them one at a
    /// time yields two replacement characters — a label reading "Sauvegarde"
    /// would come back as "Sauvegarde" with the é replaced by mojibake.
    /// </summary>
    [Fact]
    public void A_letter_written_as_two_bytes_comes_back_as_one_letter()
        => Assert.Equal("café", Decode(@"caf\xc3\xa9"));

    /// <summary>Escaped and plain text in one name decode together.</summary>
    [Fact]
    public void Escaped_and_plain_parts_are_read_as_one_name()
        => Assert.Equal("Fotos 2026 · é", Decode(@"Fotos\x202026\x20·\x20\xc3\xa9"));

    /// <summary>A name with nothing escaped in it is handed straight back.</summary>
    [Fact]
    public void An_ordinary_name_is_untouched()
        => Assert.Equal("BACKUP", Decode("BACKUP"));

    /// <summary>
    /// Anything that is not a well-formed escape stands as it is. A backslash
    /// is legal in a label, and a name this does not understand is still the
    /// best answer available — an empty sidebar row would not be.
    /// </summary>
    [Theory]
    [InlineData(@"a\xzz b", @"a\xzz b")]
    [InlineData(@"trailing\x2", @"trailing\x2")]
    [InlineData(@"back\slash", @"back\slash")]
    [InlineData(@"\x", @"\x")]
    public void A_name_that_is_not_an_escape_stands_as_it_is(string given, string expected)
        => Assert.Equal(expected, Decode(given));

    /// <summary>The forward slash udev must escape, because a name cannot hold
    /// one at all.</summary>
    [Fact]
    public void A_slash_in_a_label_comes_back()
        => Assert.Equal("in/out", Decode(@"in\x2fout"));

    /// <summary>
    /// **And the sidebar really passes the link name through it.**
    ///
    /// Read from the source, which is weak and is the honest ceiling here: the
    /// only caller reads /dev/disk/by-label and resolves symlinks, which exists
    /// on no test machine and cannot be faked without the privilege to create
    /// symlinks. The seam beside it hands the provider labels that are already
    /// decoded, so a test through that route would pass with this line deleted.
    /// A source read at least fails when the call goes away.
    /// </summary>
    [Fact]
    public void The_places_provider_decodes_the_names_it_reads()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "LinuxPlacesProvider.cs");

        Assert.Contains("UdevNames.Decode(Path.GetFileName(link))", source);
        Assert.DoesNotContain("] = Path.GetFileName(link);", source);
    }

    private static string Decode(string name) => UdevNames.Decode(name);
}
