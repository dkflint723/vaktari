using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Where the user actually keeps Documents and Downloads.
///
/// **There were two parsers for one file, and the sidebar used the worse
/// one.** XdgUserDirs — whose own comment calls this file "the only
/// authority" — reads XDG_CONFIG_HOME, trims each line, skips comments and
/// requires an XDG_ prefix. LinuxPlacesProvider had its own, which hardcoded
/// ~/.config and matched keys with a bare StartsWith on an untrimmed line. So
/// on a session that moves the config home, the sidebar's Documents and
/// Downloads rows vanished while the icons naming those same folders went on
/// working, because those came through the other parser.
/// </summary>
public sealed class UserDirsTests
{
    private const string Home = "/home/u";

    private static readonly string[] RealFile =
    [
        "# This file is written by xdg-user-dirs-update",
        "XDG_DESKTOP_DIR=\"$HOME/Desktop\"",
        "XDG_DOWNLOAD_DIR=\"$HOME/Downloads\"",
        "",
        "  XDG_DOCUMENTS_DIR=\"$HOME/Documents\"  ",
        "#XDG_MUSIC_DIR=\"$HOME/Music\"",
        "NOT_AN_XDG_KEY=\"$HOME/Elsewhere\"",
    ];

    [Fact]
    public void Home_is_expanded_and_the_quotes_come_off()
        => Assert.Equal("/home/u/Downloads",
                        XdgUserDirs.Parse(RealFile, Home)["XDG_DOWNLOAD_DIR"]);

    /// <summary>
    /// The line the other parser lost. Its rule was StartsWith on the raw line,
    /// so two spaces of indentation — which xdg-user-dirs-update does not write
    /// but a person editing the file easily does — hid the key entirely.
    ///
    /// This one trims the key and the value separately as well as the line, so
    /// the line-level trim is belt and braces here rather than the mechanism.
    /// </summary>
    [Fact]
    public void An_indented_line_is_still_a_line()
        => Assert.Equal("/home/u/Documents",
                        XdgUserDirs.Parse(RealFile, Home)["XDG_DOCUMENTS_DIR"]);

    /// <summary>
    /// Also belt and braces, and worth saying so: no comment can survive the
    /// XDG_ prefix check below either, because a commented key starts with '#'
    /// and so is not one. Both guards are kept because the file is somebody
    /// else's format and neither costs anything.
    /// </summary>
    [Fact]
    public void A_commented_out_directory_is_not_one()
        => Assert.DoesNotContain("XDG_MUSIC_DIR", XdgUserDirs.Parse(RealFile, Home).Keys);

    [Fact]
    public void And_neither_is_something_that_is_not_an_xdg_key()
        => Assert.DoesNotContain("NOT_AN_XDG_KEY", XdgUserDirs.Parse(RealFile, Home).Keys);

    [Fact]
    public void A_line_with_no_value_is_skipped_rather_than_stored_empty()
        => Assert.Empty(XdgUserDirs.Parse(["XDG_DESKTOP_DIR", "=nothing"], Home));

    // ---- and where the file is looked for ----------------------------------

    /// <summary>
    /// The whole of finding 197 in one line: a session that sets
    /// XDG_CONFIG_HOME keeps its user-dirs somewhere else, and the parser the
    /// sidebar used went on reading ~/.config and finding nothing.
    /// </summary>
    [Fact]
    public void A_session_that_moves_the_config_home_is_followed_there()
        => Assert.Equal(Path.Combine("/run/user/1000/config", "user-dirs.dirs"),
                        XdgUserDirs.ConfigFile("/run/user/1000/config", Home));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void And_one_that_does_not_gets_the_conventional_place(string? configHome)
        => Assert.Equal(Path.Combine(Home, ".config", "user-dirs.dirs"),
                        XdgUserDirs.ConfigFile(configHome, Home));

    /// <summary>
    /// The delegation itself. Both consumers have to reach the same rules, or
    /// the sidebar and the icons go on disagreeing about where Documents is —
    /// which is the shape the bug actually took.
    /// </summary>
    [Fact]
    public void The_places_provider_asks_that_one_rather_than_its_own()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "LinuxPlacesProvider.cs");

        Assert.Contains("private static string? ReadUserDir(string key) => XdgUserDirs.Read(key);",
                        source);

        // And has not kept a second copy of the file's path anywhere.
        Assert.DoesNotContain("\"user-dirs.dirs\"", source);
    }
}
