using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// An icon name that is a path to the file itself.
///
/// **The theme index has no key that begins with a separator**, so a rooted name
/// was searched for, found nowhere, and cached as nothing. The icon theme
/// specification says an Icon= value that is an absolute path names the file
/// directly — and the launchers that write one are exactly the applications no
/// theme ships an icon for: an AppImage, a JetBrains Toolbox entry, anything
/// installed under /opt.
///
/// Two of these keep no theme on disk at all: what they prove is the branch
/// taken BEFORE the search, and a theme directory would only give the search
/// something to find. The last one builds a small one, to show the branch is a
/// branch rather than a replacement — which is also why this shares the
/// icon-index collection, since reading a theme WRITES a cache into a folder
/// that is a static, and the classes asserting about that folder run beside
/// this one.
/// </summary>
[Collection("icon index cache")]
public sealed class IconNamedByPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-iconpath-" + Guid.NewGuid().ToString("N")[..12]);

    public IconNamedByPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private FreedesktopIconTheme Theme() => new("Papirus", roots: [_root]);

    [Fact]
    public void An_icon_named_by_its_path_resolves_to_that_file()
    {
        var icon = Path.Combine(_root, "toolbox.png");
        File.WriteAllBytes(icon, [0]);

        Assert.Equal(icon, Theme().Resolve([icon], 48));
    }

    /// <summary>
    /// **A path is believed only as far as the filesystem agrees.** An entry
    /// left behind by an uninstalled application names a file that is gone, and
    /// answering with it would hand the loader a path it can only fail on —
    /// while the generic name after it is a picture that exists.
    /// </summary>
    [Fact]
    public void A_path_that_is_not_there_falls_through_to_the_next_name()
    {
        var icon = Path.Combine(_root, "uninstalled.png");
        var next = Path.Combine(_root, "application-x-executable.png");

        File.WriteAllBytes(next, [0]);

        Assert.Equal(next, Theme().Resolve([icon, next], 48));
    }

    /// <summary>
    /// An ordinary theme name is still searched for, which is every other
    /// caller of this method — so the new branch is a branch and not a
    /// replacement.
    /// </summary>
    [Fact]
    public void An_unrooted_name_is_still_searched_for()
    {
        var icons = Path.Combine(_root, "Papirus", "48x48", "mimetypes");
        Directory.CreateDirectory(icons);

        File.WriteAllText(Path.Combine(_root, "Papirus", "index.theme"), """
            [Icon Theme]
            Name=Papirus
            Directories=48x48/mimetypes

            [48x48/mimetypes]
            Size=48
            Context=MimeTypes
            Type=Fixed
            """);

        var icon = Path.Combine(icons, "text-x-generic.png");
        File.WriteAllBytes(icon, [0]);

        Assert.Equal(icon, Theme().Resolve(["text-x-generic"], 48));
    }
}
