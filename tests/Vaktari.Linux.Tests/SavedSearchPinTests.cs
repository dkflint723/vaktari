using Vaktari.Core.Places;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A pinned search, as the provider turns it into a row.
///
/// **The provider asked Directory.Exists of every pin and drew every pin as
/// a bookmark**, so a saved search would have been a dimmed bookmark named
/// after the tail of an internal scheme. The rule moved to Core's
/// PinnedPlaces, where both providers read it; this drives it through the
/// Linux one, seams and all, on any machine.
/// </summary>
public sealed class SavedSearchPinTests
{
    private static LinuxPlacesProvider Provider()
        => new(Directory.CreateTempSubdirectory("vaktari-saved").FullName)
        {
            MountLines = () => [],
            FilesystemDevices = () => [],
            SwapLines = () => [],
            VolumeLabels = () => new Dictionary<string, string>(),
        };

    private static async Task<IReadOnlyList<Place>> Places(LinuxPlacesProvider provider)
    {
        var groups = await provider.GetPlacesAsync(CancellationToken.None);

        return groups.Single(g => g.Label == PlaceGroups.Places).Places;
    }

    [Fact]
    public async Task A_pinned_search_is_an_openable_row_with_the_magnifier_and_its_own_name()
    {
        var provider = Provider();
        var search = PinnedPlaces.SearchPrefix + "report::/home/me/Documents::scoped";

        await provider.PinAsync(search, "report  in Documents", CancellationToken.None);

        var row = Assert.Single(await Places(provider), p => p.Path == search);

        Assert.True(row.IsAvailable, "a saved search is always openable — its folder is asked when it runs");
        Assert.Equal("search", row.Icon);
        Assert.Equal("report  in Documents", row.Label);
        Assert.True(row.IsUserPinned);
    }

    /// <summary>The folder rule is exactly what it was: a pinned folder that
    /// has gone is listed dimmed, as a bookmark.</summary>
    [Fact]
    public async Task A_pinned_folder_that_has_gone_is_still_a_dimmed_bookmark()
    {
        var provider = Provider();
        var gone = "/nowhere/vaktari-" + Guid.NewGuid().ToString("N");

        await provider.PinAsync(gone, null, CancellationToken.None);

        var row = Assert.Single(await Places(provider), p => p.Path == gone);

        Assert.False(row.IsAvailable);
        Assert.Equal("bookmark", row.Icon);
    }
}
