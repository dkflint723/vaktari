using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The rule-based repoint on the real Windows provider (rename QA, round 3):
/// the window hands over where each pin now is and what a pin there is called
/// when nobody chose a name, and the provider applies both — a folder pin and
/// a search pin alike, keeping a name the person chose.
///
/// The search path and its generated name are spelled out here as the window
/// writes them; this assembly cannot reach the window's own VirtualPaths, and
/// the window's half is FolderHandoverTests'.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PinRuleRepointTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-pin-rule").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a temp dir is not worth failing over */ }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private static string Search(string query, string origin)
        => "vaktari:search:" + Uri.EscapeDataString(query) + ":" + Uri.EscapeDataString(origin) + ":here:any:names";

    private static string OriginOf(string search) => Uri.UnescapeDataString(search.Split(':')[3]);

    /// <summary>The rule the window hands over: a folder by PathRules, a
    /// search by the folder it was started in.</summary>
    private static string? Rebase(string path, string from, string to)
        => path.StartsWith("vaktari:search:", StringComparison.Ordinal)
            ? PathRules.Rebase(OriginOf(path), from, to) is { } moved
                ? Search(Uri.UnescapeDataString(path.Split(':')[2]), moved)
                : null
            : PathRules.Rebase(path, from, to);

    /// <summary>What a pin is called when nobody chose: "report  in one" for
    /// a search, the leaf for a folder.</summary>
    private static string Given(string path)
        => path.StartsWith("vaktari:search:", StringComparison.Ordinal)
            ? $"{Uri.UnescapeDataString(path.Split(':')[2])}  in {PathRules.LeafName(OriginOf(path))}"
            : PathRules.LeafName(path);

    private static async Task<List<Place>> Pins(WindowsPlacesProvider provider)
        => [.. (await provider.GetPlacesAsync(CancellationToken.None)).SelectMany(g => g.Places).Where(p => p.IsUserPinned)];

    [WindowsFact]
    public async Task A_search_pin_and_a_folder_pin_follow_by_the_rule_and_a_chosen_name_stays()
    {
        Directory.CreateDirectory(At("one", "sub"));

        var state = At("state");
        var provider = new WindowsPlacesProvider(state);

        var generated = Search("report", At("one"));
        var chosen = Search("invoice", At("one", "sub"));

        await provider.PinAsync(At("one"), null, CancellationToken.None);
        await provider.PinAsync(generated, Given(generated), CancellationToken.None);
        await provider.PinAsync(chosen, "My invoices", CancellationToken.None);

        Directory.Move(At("one"), At("uno"));

        Assert.True(await provider.RepointAsync(
            path => Rebase(path, At("one"), At("uno")), Given, CancellationToken.None));

        var again = await Pins(new WindowsPlacesProvider(state));
        string Label(string path) => again.Single(p => p.Path == path).Label;

        Assert.Equal("uno", Label(At("uno")));
        Assert.Equal("report  in uno", Label(Search("report", At("uno"))));
        Assert.Equal("My invoices", Label(Search("invoice", At("uno", "sub"))));
        Assert.Equal(3, again.Count);
    }
}
