using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Reading a theme from cache alone, which is what lets a window wait for one.
///
/// **The trap this is mostly about.** A theme is not one directory: Papirus-Dark
/// answers for what it recoloured and leaves everything else to Papirus behind
/// it. Accepting a cache for the variant while the base has none would produce a
/// theme that resolves folders and nothing else — icons missing rather than
/// late, and missing for as long as the process runs. Whole chain or nothing.
/// </summary>
/// <summary>
/// Shares a collection with <see cref="IconIndexCacheTests"/> — see the note
/// there. The static both classes set is the whole reason.
/// </summary>
[Collection("icon index cache")]
public sealed class IconThemeCachedReadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-cached-read-" + Guid.NewGuid().ToString("N"));

    private readonly string _themes;
    private readonly string _cache;

    public IconThemeCachedReadTests()
    {
        _themes = Path.Combine(_root, "themes");
        _cache = Path.Combine(_root, "cache");

        Directory.CreateDirectory(_themes);
        Directory.CreateDirectory(_cache);

        FreedesktopIconTheme.IndexCacheFolder = _cache;

        // Papirus carries the icon FromFolder probes with; Papirus-Dark does
        // not, which is what makes the base load-bearing rather than decorative.
        Write("Papirus", "text-x-generic", "folder");
        Write("Papirus-Dark", "folder");
    }

    public void Dispose()
    {
        FreedesktopIconTheme.IndexCacheFolder = null;

        // Only what this test built, under its own temporary root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string theme, params string[] icons)
    {
        var dir = Path.Combine(_themes, theme);
        var places = Path.Combine(dir, "48x48", "places");

        Directory.CreateDirectory(places);
        File.WriteAllText(Path.Combine(dir, "index.theme"), $"[Icon Theme]\nName={theme}\n");

        foreach (var icon in icons)
            File.WriteAllText(Path.Combine(places, icon + ".svg"), "<svg/>");
    }

    private string Dir(string theme) => Path.Combine(_themes, theme);

    [Fact]
    public void A_theme_nobody_has_read_is_not_available_from_cache()
        => Assert.Null(FreedesktopIconTheme.FromCache(Dir("Papirus-Dark")));

    /// <summary>Reading it once is what makes the next launch cheap.</summary>
    [Fact]
    public void Reading_it_once_makes_it_available_from_cache()
    {
        Assert.NotNull(FreedesktopIconTheme.FromFolder(Dir("Papirus-Dark")));

        var cached = FreedesktopIconTheme.FromCache(Dir("Papirus-Dark"));

        Assert.NotNull(cached);
        Assert.Equal("Papirus-Dark", cached.ThemeName);

        // And it still answers for what only the base has, which is the point
        // of insisting on the whole chain.
        Assert.NotNull(cached.Resolve(["text-x-generic"], 48));
    }

    /// <summary>
    /// The one that would catch a per-directory cache being trusted piecemeal:
    /// the variant is cached, the base is not, and the answer must be no.
    /// </summary>
    [Fact]
    public void A_variant_cached_without_its_base_is_refused()
    {
        Assert.NotNull(FreedesktopIconTheme.FromFolder(Dir("Papirus-Dark")));
        Assert.NotNull(FreedesktopIconTheme.FromCache(Dir("Papirus-Dark")));

        // Drop everything, then put back only the variant's half.
        foreach (var file in Directory.GetFiles(_cache)) File.Delete(file);

        IconIndexCache.Save(
            Dir("Papirus-Dark"),
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["folder"] = [Path.Combine(Dir("Papirus-Dark"), "48x48", "places", "folder.svg")],
            });

        Assert.NotNull(IconIndexCache.Load(Dir("Papirus-Dark")));
        Assert.Null(IconIndexCache.Load(Dir("Papirus")));

        Assert.Null(FreedesktopIconTheme.FromCache(Dir("Papirus-Dark")));
    }

    /// <summary>
    /// With nowhere to keep indexes, the cached read simply never succeeds —
    /// rather than falling back to building one, which would put the seconds
    /// back in front of the window.
    /// </summary>
    [Fact]
    public void With_caching_off_the_cached_read_never_succeeds()
    {
        Assert.NotNull(FreedesktopIconTheme.FromFolder(Dir("Papirus-Dark")));

        FreedesktopIconTheme.IndexCacheFolder = null;

        Assert.Null(FreedesktopIconTheme.FromCache(Dir("Papirus-Dark")));
    }
}
