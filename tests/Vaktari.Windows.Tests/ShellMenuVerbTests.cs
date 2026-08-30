using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which hosted-menu entries are dropped as duplicates of our own.
///
/// By canonical verb, never by label — labels are localized and filtering
/// "Copy" would strip nothing in German. The set is exactly the verbs whose
/// function already sits at the top of Vaktari's own menu.
/// </summary>
[SupportedOSPlatform("windows")]
public class ShellMenuVerbTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("openas")]
    [InlineData("cut")]
    [InlineData("Copy")]
    [InlineData("delete")]
    [InlineData("properties")]
    [InlineData("copyaspath")]
    [InlineData("runas")]
    [InlineData("Windows.Share")]
    public void Native_twins_are_dropped(string verb)
        => Assert.True(ShellContextMenu.IsRedundantVerb(verb));

    /// <summary>What has no native twin stays — dropping capability is not
    /// tidying.</summary>
    [Theory]
    [InlineData("link")]          // Create shortcut: same-folder, nothing native does it
    [InlineData("sendto")]
    [InlineData("print")]
    [InlineData("edit")]
    [InlineData("restoreprevious")]
    public void Unique_verbs_stay(string verb)
        => Assert.False(ShellContextMenu.IsRedundantVerb(verb));

    [Fact]
    public void No_verb_at_all_stays()
        => Assert.False(ShellContextMenu.IsRedundantVerb(null));
}
