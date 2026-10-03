using System.Runtime.Versioning;
using Vaktari.Core.Places;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Rename QA, round 2: the CHANGELOG promises that pinned places follow a
/// folder Vaktari renames: "a pin with the folder's own name takes the new
/// name; a name you gave it is kept". `RepointAsync` had no test of its own.
///
/// Pins are made through the provider's own `PinAsync` and `RenameAsync`.
/// After the repoint they are read back through a NEW provider on the same
/// state folder, so what is checked is what reached places.json.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PinRepointTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-pin-repoint").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a temp dir is not worth failing over */ }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private static async Task<List<Place>> Pins(WindowsPlacesProvider provider)
        => [.. (await provider.GetPlacesAsync(CancellationToken.None)).SelectMany(g => g.Places).Where(p => p.IsUserPinned)];

    [WindowsFact]
    public async Task Qa_pins_follow_a_renamed_folder_keeping_a_chosen_name_and_leaving_a_sibling()
    {
        Directory.CreateDirectory(At("A", "sub1"));
        Directory.CreateDirectory(At("A", "sub2"));
        Directory.CreateDirectory(At("AB"));

        var state = At("state");
        var provider = new WindowsPlacesProvider(state);

        await provider.PinAsync(At("A"), null, CancellationToken.None);
        await provider.PinAsync(At("A", "sub1"), null, CancellationToken.None);
        await provider.PinAsync(At("A", "sub2"), null, CancellationToken.None);
        await provider.PinAsync(At("AB"), null, CancellationToken.None);

        var sub2 = (await Pins(provider)).Single(p => p.Path == At("A", "sub2"));
        await provider.RenameAsync(sub2.Id, "My Second", CancellationToken.None);

        Directory.Move(At("A"), At("A9"));
        Assert.True(await provider.RepointAsync(At("A"), At("A9"), CancellationToken.None));

        var again = await Pins(new WindowsPlacesProvider(state));
        string Label(string path) => again.Single(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)).Label;

        Assert.Equal("A9", Label(At("A9")));
        Assert.Equal("sub1", Label(At("A9", "sub1")));
        Assert.Equal("My Second", Label(At("A9", "sub2")));
        Assert.Equal("AB", Label(At("AB")));

        Assert.DoesNotContain(again, p => p.Path.StartsWith(At("A") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(p.Path, At("A"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, again.Count);
    }

    /// <summary>
    /// A folder renamed to a name only its case differs from is the same
    /// folder by both names. The repoint must still change the spelling the
    /// sidebar shows.
    /// </summary>
    [WindowsFact]
    public async Task Qa_a_case_only_rename_respells_the_pin()
    {
        Directory.CreateDirectory(At("photos"));

        var state = At("state");
        var provider = new WindowsPlacesProvider(state);

        await provider.PinAsync(At("photos"), null, CancellationToken.None);

        Assert.True(await provider.RepointAsync(At("photos"), At("Photos"), CancellationToken.None));

        var pin = Assert.Single(await Pins(new WindowsPlacesProvider(state)));

        Assert.Equal(At("Photos"), pin.Path);
        Assert.Equal("Photos", pin.Label);
    }
}
