using Vaktari.Core.Places;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **Two consecutive sidebar sections both read NETWORK** once a mapped drive
/// or a mounted share was present. They hold different things: the group this
/// provider supplies is what has already been connected and can be opened,
/// while the literal section below it is servers announcing themselves that
/// have not been connected to — so two identical headings in a row read as one
/// section drawn twice.
///
/// The names live in <see cref="PlaceGroups"/> so both platforms cannot drift.
/// This is the half that says this provider really reads them: a Core test can
/// only prove the constant is not "network", not that anybody uses it.
/// </summary>
public sealed class PlaceGroupNamesTests
{
    private static string Source => RepoSource.Read("src", "Vaktari.Linux", "LinuxPlacesProvider.cs");

    [Fact]
    public void The_group_names_come_from_the_shared_list()
    {
        Assert.Contains("PlaceGroups.Shares", Source);
        Assert.Contains("PlaceGroups.Devices", Source);
        Assert.Contains("PlaceGroups.Places", Source);
    }

    /// <summary>And none of them is spelled out here, which is how they drifted
    /// apart in the first place.</summary>
    [Theory]
    [InlineData("\"network\"")]
    [InlineData("\"shares\"")]
    [InlineData("\"devices\"")]
    [InlineData("\"places\")")]
    public void And_none_is_typed_out_beside_them(string literal)
        => Assert.DoesNotContain("PlaceGroup(" + literal, Source);
}
