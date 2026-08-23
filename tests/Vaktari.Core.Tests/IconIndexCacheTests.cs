using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Reading an icon theme, remembered between launches.
///
/// **Why it exists, in one measurement.** Papirus-Dark chains to Papirus, and
/// building the index for the pair took 2.8–3.1 seconds on the machine that
/// reported a slow start, against 0 ms for a hundred lookups afterwards. The
/// theme does not change between launches; the three seconds were being spent
/// again every time.
///
/// The risk a cache carries is answering with something that is no longer true,
/// so most of what follows is about the ways it must refuse to.
/// </summary>
/// <summary>
/// Shares a collection with <see cref="IconThemeCachedReadTests"/> because both
/// drive the same static cache folder, and xunit runs classes in parallel.
/// **Not a precaution:** written without this, each passed alone and they
/// failed together.
/// </summary>
[Collection("icon index cache")]
public sealed class IconIndexCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-index-cache-" + Guid.NewGuid().ToString("N"));

    private readonly string _themes;
    private readonly string _cache;

    public IconIndexCacheTests()
    {
        _themes = Path.Combine(_root, "themes");
        _cache = Path.Combine(_root, "cache");

        Directory.CreateDirectory(_themes);
        Directory.CreateDirectory(_cache);

        IconIndexCache.Folder = _cache;
    }

    public void Dispose()
    {
        IconIndexCache.Folder = null;

        // Only what this test built, and only under its own temporary root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A theme directory with one real icon file in it.</summary>
    private string Theme(string name)
    {
        var dir = Path.Combine(_themes, name);
        Directory.CreateDirectory(Path.Combine(dir, "48x48", "places"));

        File.WriteAllText(Path.Combine(dir, "index.theme"), "[Icon Theme]\nName=" + name + "\n");
        File.WriteAllText(Path.Combine(dir, "48x48", "places", "folder.svg"), "<svg/>");

        return dir;
    }

    private static Dictionary<string, List<string>> MapOf(params (string Name, string Path)[] entries)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, path) in entries)
        {
            if (!map.TryGetValue(name, out var paths)) map[name] = paths = [];
            paths.Add(path);
        }

        return map;
    }

    [Fact]
    public void What_was_saved_is_what_comes_back()
    {
        var dir = Theme("Papirus");
        var icon = Path.Combine(dir, "48x48", "places", "folder.svg");

        IconIndexCache.Save(dir, MapOf(("folder", icon), ("folder", icon), ("text-x-generic", icon)));

        var read = IconIndexCache.Load(dir);

        Assert.NotNull(read);
        Assert.Equal(2, read.Count);
        Assert.Equal([icon, icon], read["folder"]);
        Assert.Equal([icon], read["text-x-generic"]);
    }

    /// <summary>Icon names are matched without regard to case, and the cache
    /// has to come back the same way or lookups quietly start missing.</summary>
    [Fact]
    public void Names_still_match_without_regard_to_case()
    {
        var dir = Theme("Papirus");
        IconIndexCache.Save(dir, MapOf(("Folder", Path.Combine(dir, "48x48", "places", "folder.svg"))));

        Assert.True(IconIndexCache.Load(dir)!.ContainsKey("FOLDER"));
    }

    [Fact]
    public void Nothing_cached_is_simply_a_miss()
        => Assert.Null(IconIndexCache.Load(Theme("Never-Read")));

    /// <summary>
    /// With no folder set, Core keeps nothing and says so — the default, so
    /// that a test or a tool using Core does not silently start writing files.
    /// </summary>
    [Fact]
    public void Without_somewhere_to_keep_it_nothing_is_kept()
    {
        var dir = Theme("Papirus");
        IconIndexCache.Folder = null;

        IconIndexCache.Save(dir, MapOf(("folder", Path.Combine(dir, "48x48", "places", "folder.svg"))));

        Assert.Empty(Directory.GetFiles(_cache));
        Assert.Null(IconIndexCache.Load(dir));
    }

    /// <summary>
    /// **A replaced theme must not answer with the old one's icons.** Replacing
    /// a theme is how one is updated, and the folder's own timestamp is what
    /// says so without walking it.
    /// </summary>
    [Fact]
    public void A_theme_touched_since_is_refused()
    {
        var dir = Theme("Papirus");
        IconIndexCache.Save(dir, MapOf(("folder", Path.Combine(dir, "48x48", "places", "folder.svg"))));

        Assert.NotNull(IconIndexCache.Load(dir));

        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(5));

        Assert.Null(IconIndexCache.Load(dir));
    }

    /// <summary>index.theme rewritten in place changes what the theme
    /// inherits, which changes the chain the index was built for.</summary>
    [Fact]
    public void An_edited_index_theme_is_refused()
    {
        var dir = Theme("Papirus");
        IconIndexCache.Save(dir, MapOf(("folder", Path.Combine(dir, "48x48", "places", "folder.svg"))));

        var index = Path.Combine(dir, "index.theme");
        var when = Directory.GetLastWriteTimeUtc(dir);

        File.WriteAllText(index, "[Icon Theme]\nName=Papirus\nInherits=hicolor\n");

        // The folder's own stamp is deliberately put back, so this proves the
        // file is checked rather than the directory that contains it.
        Directory.SetLastWriteTimeUtc(dir, when);

        Assert.Null(IconIndexCache.Load(dir));
    }

    /// <summary>
    /// **The check the stamp cannot make.** A theme whose files were deleted
    /// while its folder timestamp stayed put would otherwise hand back a map of
    /// paths that are all gone — a listing where every icon is missing, for as
    /// long as the process runs. One probe turns that back into a rebuild.
    /// </summary>
    [Fact]
    public void A_cache_pointing_at_files_that_are_gone_is_refused()
    {
        var dir = Theme("Papirus");
        var icon = Path.Combine(dir, "48x48", "places", "folder.svg");

        IconIndexCache.Save(dir, MapOf(("folder", icon)));

        var when = Directory.GetLastWriteTimeUtc(dir);
        File.Delete(icon);
        Directory.SetLastWriteTimeUtc(dir, when);

        Assert.Null(IconIndexCache.Load(dir));
    }

    /// <summary>
    /// Two themes are two caches. The file is named from a hash of the path, so
    /// this is the test that would catch a hash of something too coarse — the
    /// theme's leaf name, say, which repeats across roots.
    /// </summary>
    [Fact]
    public void Two_themes_do_not_share_a_cache()
    {
        var papirus = Theme("Papirus");
        var tela = Theme("Tela");

        IconIndexCache.Save(papirus, MapOf(("folder", Path.Combine(papirus, "48x48", "places", "folder.svg"))));
        IconIndexCache.Save(tela, MapOf(("home", Path.Combine(tela, "48x48", "places", "folder.svg"))));

        Assert.True(IconIndexCache.Load(papirus)!.ContainsKey("folder"));
        Assert.True(IconIndexCache.Load(tela)!.ContainsKey("home"));
        Assert.False(IconIndexCache.Load(tela)!.ContainsKey("folder"));
    }

    /// <summary>
    /// A truncated or scribbled-on cache is a miss, not a crash. This is the
    /// half-written file that a machine losing power mid-save would leave, if
    /// the write were not staged — and it is staged, but the reader must not
    /// depend on that.
    /// </summary>
    [Fact]
    public void A_damaged_cache_is_a_miss()
    {
        var dir = Theme("Papirus");
        IconIndexCache.Save(dir, MapOf(("folder", Path.Combine(dir, "48x48", "places", "folder.svg"))));

        var file = Directory.GetFiles(_cache, "*.idx").Single();
        File.WriteAllText(file, "vaktari-icon-index 1\nnot a theme\n");

        Assert.Null(IconIndexCache.Load(dir));
    }

    /// <summary>
    /// **Nothing half-written is left behind when the rename fails.**
    ///
    /// Found as a flaky test rather than reasoned about: the cache folder
    /// occasionally held two files where it should hold one, and the second was
    /// a staging file whose rename into place had failed. That happens on a real
    /// machine when something — a virus scanner, most often — opens a file the
    /// instant it is written. It matters beyond the litter: a failed rename
    /// means no cache at all, silently, and every launch afterwards pays the
    /// seconds the cache exists to avoid.
    ///
    /// The failure is forced here rather than waited for. Holding the
    /// destination open with no sharing is exactly what the scanner does, and
    /// it makes a test that passed by luck into one that passes by construction.
    /// </summary>
    [Fact]
    public void A_rename_that_cannot_happen_leaves_no_half_written_file_behind()
    {
        var dir = Theme("Papirus");
        var icon = Path.Combine(dir, "48x48", "places", "folder.svg");

        IconIndexCache.Save(dir, MapOf(("folder", icon)));

        var written = Directory.GetFiles(_cache, "*.idx").Single();

        using (new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Cannot replace the file it is holding, and must not leave the
            // half-written one behind either.
            IconIndexCache.Save(dir, MapOf(("folder", icon)));
        }

        Assert.Empty(Directory.GetFiles(_cache, "*.writing"));
        Assert.Single(Directory.GetFiles(_cache, "*.idx"));
    }
}
