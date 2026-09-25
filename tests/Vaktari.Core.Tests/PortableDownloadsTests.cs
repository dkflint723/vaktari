using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a portable copy downloads stays in its portable folder.
///
/// **Both downloads went to the machine's own folder whatever the copy was.**
/// A fetched icon theme and the Proton Drive CLI landed under
/// LocalApplicationData on every machine a stick visited, and were missing on
/// the next — while the README promised that nothing is written outside the
/// portable folder. The application says where that folder is; these check
/// that both downloads go into it when it is said.
///
/// In the icon cache's collection because the install root is a static that
/// the installer tests also redirect.
/// </summary>
[Collection("icon index cache")]
public sealed class PortableDownloadsTests : IDisposable
{
    private readonly string _portable = Directory.CreateTempSubdirectory("vaktari-portable-dl").FullName;

    public void Dispose()
    {
        IconThemeCatalogue.PortableRoot = null;

        try { Directory.Delete(_portable, recursive: true); }
        catch (Exception) { /* a temp folder left behind is not worth failing over */ }
    }

    [Fact]
    public void Icon_themes_are_fetched_into_the_portable_folder()
    {
        Assert.False(IconThemeCatalogue.InstallRoot.StartsWith(_portable, StringComparison.Ordinal));

        IconThemeCatalogue.PortableRoot = _portable;

        Assert.Equal(Path.Combine(_portable, "Icons"), IconThemeCatalogue.InstallRoot);
        Assert.StartsWith(_portable, IconThemeCatalogue.FolderFor(IconThemeCatalogue.All[0]),
                          StringComparison.Ordinal);
    }

    /// <summary>
    /// **The theme's files travelled and the choice did not.** It is saved as
    /// a full path, and the stick that was E: when it was chosen is F: here —
    /// so the saved folder is gone while the theme sits in this copy's own
    /// Icons folder. The choice finds it there; a folder that is really gone,
    /// or never was one of this copy's themes, stays as it was saved.
    /// </summary>
    [Fact]
    public void A_theme_chosen_on_a_stick_mounted_elsewhere_is_found_in_the_portable_folder()
    {
        IconThemeCatalogue.PortableRoot = _portable;

        var here = Directory.CreateDirectory(Path.Combine(_portable, "Icons", "Papirus", "Papirus-Dark")).FullName;
        File.WriteAllText(Path.Combine(here, "index.theme"), "[Icon Theme]\nName=Papirus-Dark\n");

        Assert.Equal(here, IconThemeCatalogue.Relocated(@"Q:\vaktari\portable\Icons\Papirus\Papirus-Dark"));
        Assert.Equal(here, IconThemeCatalogue.Relocated("/run/media/someone/STICK/vaktari/portable/Icons/Papirus/Papirus-Dark"));

        const string gone = @"Q:\themes\Elsewhere\Nothing-Here";
        Assert.Equal(gone, IconThemeCatalogue.Relocated(gone));
    }

    /// <summary>
    /// Asked of the folder rather than by installing: an install that went
    /// to the wrong place would go to the developer's own tools folder, over
    /// a real CLI if one is there.
    /// </summary>
    [Fact]
    public void The_Proton_Drive_CLI_is_installed_into_the_portable_folder()
    {
        Assert.False(new ProtonDriveLinks().ToolsDir.StartsWith(_portable, StringComparison.Ordinal));

        Assert.Equal(
            Path.Combine(_portable, "tools"),
            new ProtonDriveLinks { PortableRoot = _portable }.ToolsDir);
    }
}
