using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The name an archive segment lands as: offending characters replaced with
/// <c>_</c> the way 7-Zip does, device names prefixed, lengths kept inside
/// what a filesystem will create.
/// </summary>
public sealed class ArchiveNamesTests
{
    private static string? Win(string s) => ArchiveNames.Land(s, windowsRules: true)?.Name;
    private static string? Posix(string s) => ArchiveNames.Land(s, windowsRules: false)?.Name;

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("conout$.log", "_conout$.log")]
    [InlineData("COM¹", "_COM¹")]
    [InlineData("LPT³.txt", "_LPT³.txt")]
    [InlineData("com7.txt", "_com7.txt")]
    [InlineData("LPT0", "_LPT0")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("COM10", "COM10")]
    public void Device_names_get_a_prefix_under_Windows_rules(string raw, string landed)
    {
        Assert.Equal(landed, Win(raw));
        Assert.Equal(raw, Posix(raw));
    }

    [Theory]
    [InlineData("trail. ", "trail__")]
    [InlineData("trail.", "trail_")]
    [InlineData("a:b", "a_b")]
    [InlineData("a<b>c", "a_b_c")]
    [InlineData("a\\b", "a_b")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("star*.txt", "star_.txt")]
    [InlineData("pipe|\"q\"", "pipe__q_")]
    public void Windows_rules_replace_what_Windows_refuses(string raw, string landed)
    {
        Assert.Equal(landed, Win(raw));
        Assert.Equal(raw, Posix(raw));
    }

    [Theory]
    [InlineData("a\nb", "a_b")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("del\u007F", "del_")]
    [InlineData("c1\u0085", "c1_")]
    [InlineData("inv‮gpj.exe", "inv_gpj.exe")]
    [InlineData("iso⁦late⁩", "iso_late_")]
    [InlineData("zero​width", "zero_width")]
    [InlineData("bom﻿", "bom_")]
    public void Controls_bidi_and_invisibles_are_replaced_under_both(string raw, string landed)
    {
        Assert.Equal(landed, Win(raw));
        Assert.Equal(landed, Posix(raw));
    }

    /// <summary>A fact, not a case above: xUnit's test-case serialisation
    /// turns a lone surrogate in InlineData into U+FFFD before the test sees
    /// it.</summary>
    [Fact]
    public void A_lone_surrogate_is_replaced()
    {
        Assert.Equal("lone_", Win("lone\uD800"));
        Assert.Equal("_x", Posix("\uDC00x"));
    }

    [Fact]
    public void A_surrogate_pair_is_kept() => Assert.Equal("smile\U0001F600", Posix("smile\U0001F600"));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void Not_a_name_is_refused(string raw)
    {
        Assert.Null(ArchiveNames.Land(raw, windowsRules: true));
        Assert.Null(ArchiveNames.Land(raw, windowsRules: false));
    }

    [Fact]
    public void An_unchanged_name_says_so()
    {
        Assert.False(ArchiveNames.Land("fine.txt", true)!.Value.Changed);
        Assert.True(ArchiveNames.Land("a:b", true)!.Value.Changed);
    }

    /// <summary>
    /// Too long to create, on either system: the stem is cut, the extension
    /// kept, and a surrogate pair never split.
    /// </summary>
    [Fact]
    public void A_name_too_long_to_create_is_cut_to_fit_keeping_its_extension()
    {
        var raw = new string('a', 249) + "\U0001F600\U0001F600" + ".txt";

        var landed = Posix(raw)!;

        Assert.EndsWith(".txt", landed);
        Assert.True(Units(landed) <= 255, $"{Units(landed)} units");
        Assert.False(char.IsHighSurrogate(landed[^5]), "a surrogate pair was split");
    }

    [Fact]
    public void A_numbered_name_still_fits()
    {
        var name = new string('b', 251) + ".txt";

        var numbered = ArchiveNames.Numbered(name, 12, isFolder: false);

        Assert.EndsWith(" (12).txt", numbered);
        Assert.True(Units(numbered) <= 255);
    }

    [Theory]
    [InlineData("notes.txt", false, "notes (2).txt")]
    [InlineData("docs", true, "docs (2)")]
    [InlineData("v1.2", true, "v1.2 (2)")]
    [InlineData(".bashrc", false, ".bashrc (2)")]
    public void Numbering_is_the_house_style(string name, bool folder, string expected)
        => Assert.Equal(expected, ArchiveNames.Numbered(name, 2, folder));

    [Fact]
    public void Collisions_fold_case_only_under_Windows_rules()
    {
        Assert.Equal(ArchiveNames.CollisionKey("Docs", true), ArchiveNames.CollisionKey("docs", true));
        Assert.NotEqual(ArchiveNames.CollisionKey("Docs", false), ArchiveNames.CollisionKey("docs", false));
    }

    [PosixFact]
    public void A_FAT_or_NTFS_destination_on_Linux_takes_Windows_rules()
    {
        Assert.True(ArchiveNames.WindowsRulesFor("vfat"));
        Assert.True(ArchiveNames.WindowsRulesFor("exfat"));
        Assert.True(ArchiveNames.WindowsRulesFor("ntfs3"));
        Assert.True(ArchiveNames.WindowsRulesFor("msdos"));
        Assert.False(ArchiveNames.WindowsRulesFor("ext4"));
        Assert.False(ArchiveNames.WindowsRulesFor(null));
    }

    [WindowsFact]
    public void Windows_always_takes_Windows_rules() => Assert.True(ArchiveNames.WindowsRulesFor("ext4"));

    private static int Units(string s) => OperatingSystem.IsWindows() ? s.Length : Encoding.UTF8.GetByteCount(s);
}
