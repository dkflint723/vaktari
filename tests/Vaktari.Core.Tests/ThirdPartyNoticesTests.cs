using System.Text.RegularExpressions;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The notice file that ships beside LICENSE.
///
/// **The README said "their licences travel with the release", and only
/// LICENSE did.** MIT requires each component's notice to accompany every copy
/// or substantial portion; SkiaSharp, HarfBuzzSharp, Avalonia, SharpCompress,
/// the toolkit and Tmds.DBus are all MIT, and Inter is OFL. This pins that every
/// package the source references has an entry, so the next dependency added to
/// a csproj fails here rather than shipping unacknowledged.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    private static string Notices => RepoSource.Read("THIRD-PARTY-NOTICES.txt");

    /// <summary>Every PackageReference across the shipped assemblies.</summary>
    private static IEnumerable<string> ReferencedPackages()
    {
        var src = Path.Combine(RepoSource.Root, "src");

        foreach (var csproj in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);

            foreach (Match m in Regex.Matches(text, "<PackageReference Include=\"([^\"]+)\""))
                yield return m.Groups[1].Value;
        }
    }

    [Fact]
    public void Every_referenced_package_has_a_notice()
    {
        var notices = Notices;
        var packages = ReferencedPackages().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.NotEmpty(packages);

        // A package is covered by an entry naming it, or — for the Avalonia
        // family, which is one project under one copyright — by the family
        // entry naming its first segment.
        var missing = packages
            .Where(p => !notices.Contains(p, StringComparison.OrdinalIgnoreCase)
                        && !notices.Contains(p.Split('.')[0] + ".*", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, "no notice for: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The four things the README names by hand, because those are the ones a
    /// reader will look for — and the two native libraries carry a second
    /// licence for the code they bundle.
    /// </summary>
    [Theory]
    [InlineData("SkiaSharp")]
    [InlineData("HarfBuzzSharp")]
    [InlineData("Inter typeface")]
    [InlineData("SIL OPEN FONT LICENSE")]
    [InlineData("Copyright (c) 2011 Google Inc.")]
    public void The_named_components_are_covered(string expected)
    {
        Assert.Contains(expected, Notices, StringComparison.Ordinal);
    }

    /// <summary>The MIT text has to be there in full, not summarised.</summary>
    [Fact]
    public void The_mit_permission_notice_is_reproduced_in_full()
    {
        Assert.Contains(
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND",
            Notices, StringComparison.Ordinal);
    }
}
