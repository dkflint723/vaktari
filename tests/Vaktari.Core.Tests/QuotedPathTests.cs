using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// A quoted path is still a path.
///
/// **It is what Explorer's own "Copy as path" produces**, so it is the likeliest
/// thing anyone ever pastes into the address bar — and it failed with a raw
/// Win32 error naming Vaktari's own working directory, because the quotes made
/// it relative.
/// </summary>
public sealed class QuotedPathTests
{
    [Theory]
    [InlineData("\"C:\\\\Users\\\\me\\\\Documents\"", "C:\\\\Users\\\\me\\\\Documents")]
    [InlineData("\"/home/me/Documents\"", "/home/me/Documents")]
    public void A_surrounding_quote_pair_is_stripped(string typed, string expected)
        => Assert.Equal(expected, PathVariables.Expand(typed));

    /// <summary>Whitespace around the quotes too — a paste often brings it.
    /// </summary>
    [Fact]
    public void Space_around_the_quotes_does_not_defeat_it()
        => Assert.Equal("/home/me", PathVariables.Expand("  \"/home/me\"  "));

    /// <summary>
    /// **Only as a matching pair.** A quote in the middle of a name is part of
    /// the name — legal on every filesystem Vaktari runs on except NTFS, and
    /// stripping one would be inventing a different path.
    /// </summary>
    [Theory]
    [InlineData("/home/me/say \"hello\".txt")]
    [InlineData("\"/home/me/unbalanced")]
    [InlineData("/home/me/unbalanced\"")]
    public void An_unmatched_quote_is_left_alone(string typed)
        => Assert.Equal(typed.Trim(), PathVariables.Expand(typed));

    [Fact]
    public void An_ordinary_path_is_untouched()
        => Assert.Equal("/home/me/Documents", PathVariables.Expand("/home/me/Documents"));
}
