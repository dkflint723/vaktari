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

    /// <summary>
    /// **The code inside SharpCompress that is not SharpCompress's own**,
    /// from the audit at the commit the 0.50.4 package was built from. The
    /// unRAR licence is the one that insists: its paragraph 2 must be included
    /// in full, starting from the words "UnRAR source code". One anchor per
    /// component, so dropping any of them fails here by name.
    /// </summary>
    [Theory]
    [InlineData("c083c6efd843a844b0c8f7878787360e815be781")]
    [InlineData("UnRAR source code may be used in any software to handle RAR archives")]
    [InlineData("full text of this paragraph, starting from \"UnRAR source code\" words, is")]
    [InlineData("Copyright (c) 2007 innoSysTec (R) GmbH, Germany. All rights reserved.")]
    [InlineData("Copyright (c) 2006-2010 Dino Chiesa and Microsoft Corporation.")]
    [InlineData("Copyright (c) 2000,2001,2002,2003 ymnk, JCraft,Inc. All rights reserved.")]
    [InlineData("Jean-loup Gailly")]
    [InlineData("Copyright 2001,2004-2005 The Apache Software Foundation")]
    [InlineData("Copyright (c) Six Labors.")]
    [InlineData("Version 2.0, January 2004")]
    [InlineData("Copyright (c) 2016 Claunia.com")]
    [InlineData("Copyright (c) 2021 Oleg Stepanischev")]
    [InlineData("Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.")]
    [InlineData("Dmitry Shkarin")]
    [InlineData("the LZMA SDK is placed in the public domain")]
    public void The_components_inside_SharpCompress_are_covered(string expected)
        => Assert.Contains(expected, Notices, StringComparison.Ordinal);

    /// <summary>
    /// Every way Vaktari leaves this repository carries the file.
    ///
    /// **The Arch package left it out while the README said it shipped
    /// "inside every tarball, installer and package".** The PKGBUILD installed
    /// LICENSE and README.md and nothing else, and nothing read the PKGBUILD,
    /// so the one channel that builds from source was the one that shipped no
    /// notices. One row per channel, each the line that does the copying,
    /// because a notices file nobody installs satisfies nothing.
    /// </summary>
    [Theory]
    [InlineData("packaging/PKGBUILD", "install -Dm644 THIRD-PARTY-NOTICES.txt")]
    [InlineData("packaging/vaktari.spec", "%license THIRD-PARTY-NOTICES.txt")]
    [InlineData("packaging/vaktari.iss", "Source: \"..\\THIRD-PARTY-NOTICES.txt\"")]
    [InlineData(".github/workflows/build.yml", "cp LICENSE THIRD-PARTY-NOTICES.txt README.md dist/vaktari/")]
    public void Every_package_carries_the_notices(string channel, string line)
    {
        Assert.Contains(line, RepoSource.Read(channel.Split('/')), StringComparison.Ordinal);
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
