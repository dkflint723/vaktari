using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Reading an icon theme somebody downloaded.
///
/// **The format is not Linux, which is the whole point of this.** A freedesktop
/// icon theme is an index.theme file and a directory tree; nothing in reading
/// one is a platform call. Somebody on Windows who extracts Papirus has exactly
/// that on disk, and until now the code that could read it lived in an assembly
/// Windows never loads.
///
/// Built here rather than shipped as a fixture: a real theme is tens of
/// thousands of files, and what needs proving is the reading, not the theme.
/// </summary>
/// <summary>
/// Shares the icon-index collection because reading a theme now WRITES a cache,
/// and where it writes is a static. Run alongside the cache tests, this class
/// drops its own cache files into whichever folder those tests are asserting
/// about — which is how it was found: two of them failed intermittently, and
/// only when the whole suite ran.
/// </summary>
[Collection("icon index cache")]
public sealed class ImportedIconThemeTests : IDisposable
{
    private readonly string _root;
    private readonly string _theme;

    public ImportedIconThemeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vaktari-theme-" + Guid.NewGuid().ToString("N")[..12]);
        _theme = Path.Combine(_root, "Papirus");

        // The layout every theme archive extracts to.
        Directory.CreateDirectory(Path.Combine(_theme, "48x48", "mimetypes"));
        Directory.CreateDirectory(Path.Combine(_theme, "48x48", "places"));

        File.WriteAllText(Path.Combine(_theme, "index.theme"), """
            [Icon Theme]
            Name=Papirus
            Directories=48x48/mimetypes,48x48/places

            [48x48/mimetypes]
            Size=48
            Context=MimeTypes
            Type=Fixed

            [48x48/places]
            Size=48
            Context=Places
            Type=Fixed
            """);

        foreach (var name in new[] { "image-png", "text-plain", "text-x-generic", "application-pdf" })
            File.WriteAllBytes(Path.Combine(_theme, "48x48", "mimetypes", name + ".png"), [0]);

        File.WriteAllBytes(Path.Combine(_theme, "48x48", "places", "folder.png"), [0]);
        File.WriteAllBytes(Path.Combine(_theme, "48x48", "places", "user-home.png"), [0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void A_downloaded_theme_folder_is_read()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme);

        Assert.NotNull(theme);
        Assert.Equal("Papirus", theme!.ThemeName);
    }

    /// <summary>
    /// **The folder IS the theme.** That is the shape of every archive: extract
    /// Papirus and you get a folder called Papirus with index.theme inside it,
    /// which is what a person will point at — so the name comes from the folder
    /// and the root is its parent.
    /// </summary>
    [Fact]
    public void Its_own_folder_name_is_the_theme_name()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        Assert.NotNull(theme.Resolve(["folder"], 48));
    }

    /// <summary>
    /// Windows has no mime database, so names come from the extension. This is
    /// the half that decides whether an imported theme shows anything at all.
    /// </summary>
    [Theory]
    [InlineData("holiday.png", "image-png")]
    [InlineData("notes.txt", "text-plain")]
    [InlineData("manual.pdf", "application-pdf")]
    public void A_file_resolves_through_its_extension(string name, string expected)
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var names = theme.NamesFor(Path.Combine(_root, name), isDirectory: false);

        Assert.Contains(expected, names);
        Assert.NotNull(theme.Resolve(names, 48));
    }

    /// <summary>An unknown type still lands on a name themes actually ship,
    /// rather than on nothing.</summary>
    [Fact]
    public void An_unknown_type_falls_back_to_something_the_theme_has()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var names = theme.NamesFor(Path.Combine(_root, "thing.qqq"), isDirectory: false);

        Assert.NotNull(theme.Resolve(names, 48));
    }

    [Fact]
    public void A_folder_resolves_to_the_folder_icon()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var names = theme.NamesFor(_root, isDirectory: true);

        Assert.Contains("folder", names);
        Assert.NotNull(theme.Resolve(names, 48));
    }

    /// <summary>
    /// **The mistake this has to catch.** People pick the folder they extracted
    /// the archive INTO rather than the theme inside it, and the difference is
    /// invisible until no icons change — so it is refused at the moment of
    /// choosing, while the dialog is still in mind.
    /// </summary>
    /// <summary>
    /// **The size chooser was dead on Windows and nothing noticed.** It split
    /// the candidate path on '/' alone, which was fine while this code was
    /// Linux-only: on Windows the whole path came back as one segment, no size
    /// ever parsed, every candidate scored identically, and the first file the
    /// enumeration returned won — 16x16 for Papirus, painted into a 64-pixel
    /// tile. The first version of this test file could not catch it, because
    /// the theme it built had a single size directory.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(48)]
    [InlineData(128)]
    public void The_closest_size_is_chosen(int wanted)
    {
        foreach (var size in new[] { 16, 48, 128 })
        {
            var dir = Path.Combine(_theme, $"{size}x{size}", "mimetypes");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "text-plain.png"), [0]);
        }

        File.WriteAllText(Path.Combine(_theme, "index.theme"), $"""
            [Icon Theme]
            Name=Papirus
            Directories=16x16/mimetypes,48x48/mimetypes,128x128/mimetypes

            [16x16/mimetypes]
            Size=16
            Context=MimeTypes
            Type=Fixed

            [48x48/mimetypes]
            Size=48
            Context=MimeTypes
            Type=Fixed

            [128x128/mimetypes]
            Size=128
            Context=MimeTypes
            Type=Fixed
            """);

        var theme = FreedesktopIconTheme.FromFolder(_theme)!;

        var resolved = theme.Resolve(["text-plain"], wanted);

        Assert.NotNull(resolved);
        Assert.Contains($"{wanted}x{wanted}", resolved!);
    }

    [Fact]
    public void A_folder_that_is_not_a_theme_is_refused()
    {
        Assert.Null(FreedesktopIconTheme.FromFolder(_root));
    }

    /// <summary>
    /// **The one that shipped as a crash.** A settings file written before the
    /// iconThemeFolder key existed does not carry it, and deserialization does
    /// not run property initializers — so the string arrives NULL rather than
    /// empty, TrimEnd threw a NullReferenceException out of the MainWindow
    /// constructor, and 0.8.0 could not start at all for anybody upgrading.
    ///
    /// The old test asserted the empty string and stopped one case short.
    /// </summary>
    [Fact]
    public void A_folder_that_is_not_there_is_refused()
    {
        Assert.Null(FreedesktopIconTheme.FromFolder(Path.Combine(_root, "gone")));
        Assert.Null(FreedesktopIconTheme.FromFolder(""));
        Assert.Null(FreedesktopIconTheme.FromFolder(null));
    }

    /// <summary>A trailing separator is what a folder picker hands back on some
    /// platforms, and it must not turn the theme's name into an empty string.</summary>
    [Fact]
    public void A_trailing_separator_does_not_lose_the_name()
    {
        var theme = FreedesktopIconTheme.FromFolder(_theme + Path.DirectorySeparatorChar);

        Assert.NotNull(theme);
        Assert.Equal("Papirus", theme!.ThemeName);
    }

    /// <summary>
    /// Builds the variant beside Papirus exactly as one arrives on Windows: its
    /// own recoloured artwork intact, and nothing where the aliases into the
    /// base theme used to be.
    ///
    /// This is not a contrived shape. Papirus-Dark expresses its relationship to
    /// Papirus in about forty thousand symbolic links, and Windows creates no
    /// symbolic links without Developer Mode or an elevated extraction — so
    /// 7-Zip skips every one and says so, which is the wall of "Dangerous link
    /// path was ignored" people see. What lands on disk is a real theme with
    /// holes precisely where files and folders are: measured on the actual
    /// download, 7,579 icons of its own and not one mimetype among them.
    /// </summary>
    private string BuildDarkVariant()
    {
        var dark = Path.Combine(_root, "Papirus-Dark");

        // Its own artwork survives, because those are real files.
        Directory.CreateDirectory(Path.Combine(dark, "48x48", "panel"));
        File.WriteAllBytes(Path.Combine(dark, "48x48", "panel", "battery.png"), [0]);

        // The mimetype and places directories were links. They are simply absent.
        File.WriteAllText(Path.Combine(dark, "index.theme"), """
            [Icon Theme]
            Name=Papirus-Dark
            Inherits=breeze-dark,hicolor
            Directories=48x48/panel

            [48x48/panel]
            Size=48
            Context=Panel
            Type=Fixed
            """);

        return dark;
    }

    /// <summary>
    /// **Inherits= does not name the theme it is actually built from.**
    /// Papirus-Dark inherits breeze-dark and hicolor, neither of which a Windows
    /// user has; its real dependency on Papirus is carried entirely by the links
    /// that did not survive. So the chain is followed faithfully, finds nothing,
    /// and the theme resolves no icon at all.
    ///
    /// The base is sitting in the same folder, out of the same archive. Putting
    /// it behind the variant is what those links meant.
    /// </summary>
    [Fact]
    public void A_variant_whose_links_did_not_survive_falls_back_to_its_base()
    {
        var dark = BuildDarkVariant();

        var theme = FreedesktopIconTheme.FromFolder(dark);

        Assert.NotNull(theme);
        Assert.Equal("Papirus-Dark", theme!.ThemeName);

        // The icons it lost now come from Papirus...
        var names = theme.NamesFor(Path.Combine(_root, "notes.txt"), isDirectory: false);
        var resolved = theme.Resolve(names, 48);

        Assert.NotNull(resolved);
        Assert.Contains("Papirus" + Path.DirectorySeparatorChar, resolved!);

        Assert.NotNull(theme.Resolve(theme.NamesFor(_root, isDirectory: true), 48));

        // ...while its own still win where it has them.
        var own = theme.Resolve(["battery"], 48);

        Assert.NotNull(own);
        Assert.Contains("Papirus-Dark", own!);
    }

    /// <summary>
    /// **A theme earlier in the chain wins, but not at any size.**
    ///
    /// Papirus-Dark keeps a real 16-pixel folder icon and gets its larger ones
    /// by linking to Papirus. Taking the first theme that had the name at all
    /// therefore drew a 48-pixel row with 16-pixel artwork while a perfectly
    /// good 48-pixel icon sat one theme further down — visible immediately on
    /// the real download, and invisible to every test here, because a theme
    /// built by hand has whatever sizes the test gave it in every theme.
    /// </summary>
    [Fact]
    public void A_theme_too_small_to_use_yields_to_the_one_behind_it()
    {
        var dark = BuildDarkVariant();

        // Its own, and far too small for the row being asked about.
        Directory.CreateDirectory(Path.Combine(dark, "16x16", "places"));
        File.WriteAllBytes(Path.Combine(dark, "16x16", "places", "folder.png"), [0]);

        var theme = FreedesktopIconTheme.FromFolder(dark)!;

        var resolved = theme.Resolve(["folder"], 48);

        Assert.NotNull(resolved);
        Assert.Contains($"48x48{Path.DirectorySeparatorChar}", resolved!, StringComparison.Ordinal);

        // ...and at a size it can actually serve, its own is still preferred.
        Assert.Contains("Papirus-Dark", theme.Resolve(["folder"], 16)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The size is the theme's, not the path's.** Read from the front, a
    /// theme unpacked under a folder that happens to be called 2024 gave every
    /// icon in it a size of 2024 — and since that beat nothing, the icon chosen
    /// was whichever the enumeration returned first.
    ///
    /// Not hypothetical: the fetched themes land under a folder named for the
    /// pack, and a temporary directory named with random hex is all digits
    /// often enough to have made this suite flaky rather than red.
    /// </summary>
    [Fact]
    public void A_number_in_the_folders_above_the_theme_is_not_a_size()
    {
        var awkward = Path.Combine(_root, "2024", "Papirus");

        foreach (var size in new[] { 16, 48 })
        {
            Directory.CreateDirectory(Path.Combine(awkward, $"{size}x{size}", "mimetypes"));
            File.WriteAllBytes(
                Path.Combine(awkward, $"{size}x{size}", "mimetypes", "text-x-generic.png"), [0]);
        }

        File.WriteAllText(Path.Combine(awkward, "index.theme"), "[Icon Theme]\nName=Papirus\n");

        var theme = FreedesktopIconTheme.FromFolder(awkward)!;

        Assert.Contains("16x16", theme.Resolve(["text-x-generic"], 16)!, StringComparison.Ordinal);
        Assert.Contains("48x48", theme.Resolve(["text-x-generic"], 48)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback is for a variant of a theme that is present, not a licence
    /// to borrow from any folder that happens to be nearby. A name must extend
    /// the base at a separator, so an unrelated theme is never quietly used.
    /// </summary>
    [Fact]
    public void An_empty_theme_with_no_base_beside_it_is_still_refused()
    {
        var orphan = Path.Combine(_root, "Something-Dark");

        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "index.theme"), """
            [Icon Theme]
            Name=Something-Dark
            Inherits=hicolor
            """);

        Assert.Null(FreedesktopIconTheme.FromFolder(orphan));
    }
}
