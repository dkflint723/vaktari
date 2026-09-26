using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

public sealed class ArchiveKeysTests
{
    private static string[]? Split(string raw, ArchiveFormat format = ArchiveFormat.Zip)
        => ArchiveKeys.Split(raw, format, out _);

    [Theory]
    [InlineData("./a", "a")]
    [InlineData("a//b", "a|b")]
    [InlineData("/abs/x", "abs|x")]
    [InlineData("a/./b", "a|b")]
    [InlineData("docs/a.txt", "docs|a.txt")]
    public void Keys_are_cut_into_segments(string raw, string segments)
        => Assert.Equal(segments.Split('|'), Split(raw));

    [Theory]
    [InlineData("a/../b")]
    [InlineData("../x")]
    [InlineData("x/..")]
    [InlineData("a\\..\\b")]
    [InlineData("/")]
    [InlineData("./")]
    [InlineData("")]
    public void A_climb_or_nothing_is_refused(string raw) => Assert.Null(Split(raw));

    [Fact]
    public void A_trailing_slash_is_a_folder()
    {
        Assert.Equal(["a"], ArchiveKeys.Split("a/", ArchiveFormat.Zip, out var folder)!);
        Assert.True(folder);

        ArchiveKeys.Split("a", ArchiveFormat.Zip, out folder);
        Assert.False(folder);
    }

    /// <summary>
    /// **A backslash separates only in zip and RAR** (E-23: SharpCompress
    /// hands RAR keys over as <c>exe\test.exe</c>). In tar and 7z it is part
    /// of the name, and a <c>..</c> between two of them is not a climb.
    /// </summary>
    [Theory]
    [InlineData(ArchiveFormat.Zip, "a|b")]
    [InlineData(ArchiveFormat.Rar, "a|b")]
    [InlineData(ArchiveFormat.Tar, "a\\b")]
    [InlineData(ArchiveFormat.TarGz, "a\\b")]
    [InlineData(ArchiveFormat.SevenZip, "a\\b")]
    public void A_backslash_separates_only_where_the_format_says_so(ArchiveFormat format, string segments)
        => Assert.Equal(segments.Split('|'), Split("a\\b", format));

    [Fact]
    public void A_backslash_climb_in_a_tar_is_one_name()
        => Assert.Equal(["a\\..\\b"], Split("a\\..\\b", ArchiveFormat.Tar)!);
}
