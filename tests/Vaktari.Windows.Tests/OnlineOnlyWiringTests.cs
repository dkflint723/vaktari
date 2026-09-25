using System.Text.RegularExpressions;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The platform adopting the online-only test, which no unit can be asked
/// about: Core holds the seam and cannot reference this assembly.
///
/// **It sat in OnlineOnlyFilesTests and was a plain substring match.** So it
/// passed with the line commented out, and failed for no reason of its own on
/// a machine that refused that class's sync-root registration, since that
/// class's constructor registers a root before every test it runs. Here, with
/// no fixture, and counting only a line that is code.
/// </summary>
public sealed class OnlineOnlyWiringTests
{
    [WindowsFact]
    public void The_windows_platform_adopts_the_online_test()
    {
        var constructor = RepoSource.Body(
            RepoSource.Read("src", "Vaktari.Windows", "WindowsPlatform.cs"),
            "public WindowsPlatform(string stateDirectory)");

        // Block comments out first, then a line that IS the statement: a "//"
        // in front of it, or anything else, is not the platform adopting it.
        var code = Regex.Replace(constructor, @"/\*.*?\*/", "", RegexOptions.Singleline);

        Assert.Contains(
            "OnlineOnly.Test = Placeholders.IsHeldOnline;",
            code.Split('\n').Select(line => line.Trim()));
    }
}
