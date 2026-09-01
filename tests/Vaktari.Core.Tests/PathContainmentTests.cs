using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Whether one path is inside another.
///
/// **Three separate places claimed to stop a folder being copied into itself,
/// and all three tested equality.** Equality catches dropping A onto A and
/// misses dropping A into A/sub — which is the case that actually goes wrong,
/// because the destination is then inside the thing being read and the copy
/// walks into its own output.
/// </summary>
public sealed class PathContainmentTests
{
    private static string P(params string[] parts)
        => Path.Combine([OperatingSystem.IsWindows() ? @"C:\" : "/", .. parts]);

    [Fact]
    public void A_folder_contains_itself()
        => Assert.True(PathRules.Contains(P("work"), P("work")));

    /// <summary>The one equality missed.</summary>
    [Fact]
    public void A_folder_contains_what_is_inside_it()
    {
        Assert.True(PathRules.Contains(P("work"), P("work", "sub")));
        Assert.True(PathRules.Contains(P("work"), P("work", "a", "b", "c")));
    }

    [Fact]
    public void A_sibling_is_not_inside()
        => Assert.False(PathRules.Contains(P("work"), P("play")));

    /// <summary>
    /// **The prefix has to end at a separator.** Without that rule
    /// "/media/one" claims "/media/onetwo", and a transfer into an unrelated
    /// folder would be refused as self-containment.
    /// </summary>
    [Fact]
    public void A_name_that_merely_starts_the_same_is_not_inside()
    {
        Assert.False(PathRules.Contains(P("media", "one"), P("media", "onetwo")));
        Assert.False(PathRules.Contains(P("work"), P("workshop")));
    }

    [Fact]
    public void The_child_does_not_contain_the_parent()
        => Assert.False(PathRules.Contains(P("work", "sub"), P("work")));

    /// <summary>
    /// A trailing separator and the two separator spellings are the same
    /// folder, which is what Normalise is for and why this uses it.
    /// </summary>
    [Fact]
    public void Spelling_does_not_change_the_answer()
    {
        var root = P("work");

        Assert.True(PathRules.Contains(root + Path.DirectorySeparatorChar, P("work", "sub")));

        if (OperatingSystem.IsWindows())
        {
            // Case is not a difference on Windows, and is on Linux.
            Assert.True(PathRules.Contains(@"C:\Work", @"C:\work\sub"));
        }
    }

    [Fact]
    public void A_drive_root_contains_everything_on_it()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";

        Assert.True(PathRules.Contains(root, P("anything")));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "/work")]
    [InlineData("/work", null)]
    public void Nothing_contains_nothing(string? root, string? candidate)
        => Assert.False(PathRules.Contains(root, candidate));
}
