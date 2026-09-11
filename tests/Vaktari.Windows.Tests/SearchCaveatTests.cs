using System.Runtime.Versioning;
using Vaktari.Core.Search;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the Windows search admits to leaving out.
///
/// The walk skips every file carrying the System attribute — the framework's
/// default narrowed, not a decision — and for a long time nothing on screen
/// said so. The provider now states it, and this pins that the statement
/// exists and names the thing it is about, so a future edit to the walk's
/// <c>AttributesToSkip</c> has to come back here and change the words too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SearchCaveatTests
{
    [Fact]
    public void The_windows_walk_confesses_what_it_skips()
    {
        ISearchProvider provider = new WindowsSearchProvider();

        Assert.False(string.IsNullOrWhiteSpace(provider.Caveat), "the walk skips files and says nothing");
        Assert.Contains("system", provider.Caveat, StringComparison.OrdinalIgnoreCase);
    }
}
