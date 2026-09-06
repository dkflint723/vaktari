using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Which of the three icon sources a row draws with.
///
/// **Whether the desktop's icons are used has to be answerable from the two
/// settings alone**, and it was not: it was answered partly from
/// <c>IconLoader.Provider</c>, which is installed asynchronously. A theme whose
/// index was already cached went in before the first row painted, so the box
/// did nothing; a theme nobody had read yet took seconds to build, and for
/// those seconds Provider was null and the box worked. Same tick, same folder,
/// opposite outcomes, decided by an index cache nobody can see — which is
/// exactly how it was reported: "very flakey if it will actually apply them or
/// not".
///
/// Nothing anywhere asserted on <c>IconLoader.UseSystemIcons</c> before this,
/// which is how a three-term condition came to have a term nobody could
/// predict. These pin the rule from both sides, at both stages of an install.
/// </summary>
public sealed class DesktopIconsPrecedenceTests : IDisposable
{
    /// <summary>Stands in for WindowsFileIcons: present, and never asked.</summary>
    private sealed class Shell : IFileIconProvider
    {
        public IconPixels? IconFor(string path, bool isDirectory, int size) => null;
    }

    /// <summary>Stands in for a built freedesktop theme.</summary>
    private sealed class Theme : IIconThemeProvider
    {
        public string ThemeName => "Tela";
        public string? Resolve(IReadOnlyList<string> names, int size) => null;
        public IReadOnlyList<string> NamesFor(string path, bool isDirectory) => [];
        public void Reload(string? themeName) { }
    }

    private readonly IFileIconProvider? _filesBefore = IconLoader.Files;
    private readonly IIconThemeProvider? _themeBefore = IconLoader.Provider;
    private readonly SettingsState _settingsBefore = AppSettings.Current;

    // Windows always has one of these; what is under test is the other two
    // terms, so it is present throughout.
    public DesktopIconsPrecedenceTests() => IconLoader.Files = new Shell();

    public void Dispose()
    {
        IconLoader.Files = _filesBefore;
        IconLoader.Provider = _themeBefore;
        AppSettings.Apply(_settingsBefore);
    }

    private static void Choose(bool box, string theme) =>
        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with
            {
                UseSystemIcons = box,
                IconThemeFolder = theme,
            },
        });

    /// <summary>
    /// The one that would have caught it.
    ///
    /// A theme chosen but not yet read is exactly the state IconThemeInstall
    /// leaves behind while it builds: it applies the platform's own icons
    /// first, which on Windows is null, and swaps the theme in seconds later.
    /// Asserting at BOTH stages is the point — one answer, whichever stage the
    /// install happens to be in.
    /// </summary>
    [Fact]
    public void A_chosen_theme_wins_from_when_it_is_chosen_not_from_when_it_is_read()
    {
        Choose(box: true, theme: @"C:\icons\Tela\Tela");

        IconLoader.Provider = null;
        Assert.False(IconLoader.UseSystemIcons, "the theme is still being read, and the box won for a moment");

        IconLoader.Provider = new Theme();
        Assert.False(IconLoader.UseSystemIcons, "the theme has landed");
    }

    /// <summary>
    /// The other side: clearing the theme takes effect when it is cleared, not
    /// whenever some build still in flight for the old folder gets round to
    /// landing.
    /// </summary>
    [Fact]
    public void Clearing_the_theme_honours_the_box_while_the_old_one_is_still_installed()
    {
        Choose(box: true, theme: "");
        IconLoader.Provider = new Theme();

        Assert.True(IconLoader.UseSystemIcons);
    }

    /// <summary>
    /// And the box is still a box: nothing above pins it true by accident.
    /// Without this, deleting the setting from the condition entirely would
    /// leave the two facts above green.
    /// </summary>
    [Fact]
    public void An_unticked_box_is_still_unticked_with_no_theme_chosen()
    {
        Choose(box: false, theme: "");
        IconLoader.Provider = null;

        Assert.False(IconLoader.UseSystemIcons);
    }

    /// <summary>
    /// And a platform with no per-file provider — freedesktop, where an icon
    /// theme already is the answer — is not offered the route at all, however
    /// the other two are set.
    /// </summary>
    [Fact]
    public void A_platform_without_per_file_icons_never_takes_the_route()
    {
        Choose(box: true, theme: "");
        IconLoader.Files = null;
        IconLoader.Provider = null;

        Assert.False(IconLoader.UseSystemIcons);
    }
}
