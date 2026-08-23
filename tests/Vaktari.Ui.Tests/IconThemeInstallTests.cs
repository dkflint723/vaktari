using Vaktari.Core.FileSystem;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The window must not wait for the icon theme, and must not make a show of not
/// waiting either.
///
/// **Taken from a launch that took 1,750 ms and should have taken 300.** The
/// theme was read in the MainWindow constructor, before Show, so the whole cost
/// landed with nothing yet on screen. Reading Papirus-Dark — which chains to
/// Papirus, a quarter of a gigabyte across some fifty thousand files — measured
/// 2.8–3.1 seconds on the machine that reported it, against zero for every
/// lookup afterwards.
///
/// Two behaviours, then: a theme already indexed is applied outright, so the
/// icons are right from the first frame; one never seen before is built off the
/// thread and swapped in. These assert the shape rather than the duration — a
/// timing test would pass on a fast machine with the bug still present.
/// </summary>
public class IconThemeInstallTests
{
    private sealed class Fake(string name) : IIconThemeProvider
    {
        public string ThemeName { get; } = name;
        public string? Resolve(IReadOnlyList<string> names, int size) => null;
        public IReadOnlyList<string> NamesFor(string path, bool isDirectory) => [];
        public void Reload(string? themeName) { }
    }

    private static IIconThemeProvider? Nothing(string? folder) => null;

    /// <summary>
    /// The one that would have caught the original. Begin has to come back
    /// while the build is still running — held here by a gate that is not
    /// opened until after the assertion, so a synchronous implementation
    /// deadlocks the test rather than passing it slowly.
    /// </summary>
    [Fact]
    public async Task Begin_returns_before_an_unseen_theme_has_been_read()
    {
        using var building = new ManualResetEventSlim(false);
        var applied = new List<string?>();

        var task = IconThemeInstall.Begin(
            "/themes/Papirus-Dark",
            new Fake("shell"),
            Nothing,
            _ => { building.Wait(TimeSpan.FromSeconds(10)); return new Fake("Papirus-Dark"); },
            p => applied.Add(p?.ThemeName));

        // Still reading, and we are already back.
        Assert.False(task.IsCompleted);

        // And the window already has something to draw with.
        Assert.Equal(["shell"], applied);

        building.Set();
        await task;

        Assert.Equal(["shell", "Papirus-Dark"], applied);
    }

    /// <summary>
    /// **The ordinary launch.** An indexed theme is applied there and then, and
    /// the platform's icons never appear — applying them first would put the
    /// wrong icons in front of somebody for one frame, for no gain, and each
    /// apply throws away every icon cached so far.
    /// </summary>
    [Fact]
    public async Task A_cached_theme_is_applied_outright_and_nothing_is_built()
    {
        var builds = 0;
        var applied = new List<string?>();

        var task = IconThemeInstall.Begin(
            "/themes/Papirus-Dark",
            new Fake("shell"),
            _ => new Fake("Papirus-Dark"),
            _ => { builds++; return new Fake("rebuilt"); },
            p => applied.Add(p?.ThemeName));

        Assert.True(task.IsCompleted);
        await task;

        Assert.Equal(["Papirus-Dark"], applied);
        Assert.Equal(0, builds);
    }

    /// <summary>
    /// No theme chosen is the ordinary case for a fresh install, and it must
    /// not cost a thread — nor look at the cache, nor call the reader, which is
    /// what would touch the disk.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Nothing_chosen_reads_nothing(string? folder)
    {
        var looks = 0;
        var reads = 0;
        var applied = new List<string?>();

        await IconThemeInstall.Begin(
            folder,
            new Fake("shell"),
            _ => { looks++; return null; },
            _ => { reads++; return null; },
            p => applied.Add(p?.ThemeName));

        Assert.Equal(0, looks);
        Assert.Equal(0, reads);
        Assert.Equal(["shell"], applied);
    }

    /// <summary>
    /// **A folder that reads as nothing leaves the icons alone.** Applying the
    /// null would take away the ones the window is already drawing with, which
    /// is worse than the wrong theme: it is no icons at all.
    /// </summary>
    [Fact]
    public async Task An_unreadable_theme_leaves_the_platform_icons_in_place()
    {
        var applied = new List<string?>();

        await IconThemeInstall.Begin(
            "/themes/gone",
            new Fake("shell"),
            Nothing,
            _ => null,
            p => applied.Add(p?.ThemeName));

        Assert.Equal(["shell"], applied);
    }

    /// <summary>
    /// A platform with no icons of its own — Windows — still gets the null
    /// applied, because that null is what makes IconLoader fall through to the
    /// shell's per-file icons. Skipping it would leave whatever a previous
    /// window left behind.
    /// </summary>
    [Fact]
    public async Task A_platform_without_icons_still_applies_its_nothing()
    {
        var applied = new List<string?>();
        var calls = 0;

        await IconThemeInstall.Begin(
            null,
            platformIcons: null,
            Nothing,
            _ => null,
            p => { calls++; applied.Add(p?.ThemeName); });

        Assert.Equal(1, calls);
        Assert.Equal([null], applied);
    }
}
