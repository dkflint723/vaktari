using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The shell's own per-file icons.
///
/// **This exists because a review pass claimed the feature could not work** —
/// that the GDI import naming GetObject would fail to bind, since gdi32
/// exports GetObjectA and GetObjectW rather than a plain GetObject, so every
/// icon would come back null and fall through to the drawn set. Icons had been
/// watched changing on screen, which is the opposite conclusion, and an
/// argument between a reading and a screenshot is worth one assertion.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileIconsTests : IDisposable
{
    private readonly string _folder;
    private readonly string _file;

    public WindowsFileIconsTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "vaktari-icons-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);

        _file = Path.Combine(_folder, "sample.txt");
        File.WriteAllText(_file, "sample");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void A_file_gets_pixels_from_the_shell()
    {
        var icon = new WindowsFileIcons().IconFor(_file, isDirectory: false, size: 48);

        Assert.NotNull(icon);
        Assert.True(icon!.Width > 0 && icon.Height > 0);
        Assert.Equal(icon.Width * icon.Height * 4, icon.Bgra.Length);
    }

    [Fact]
    public void A_folder_gets_pixels_too()
    {
        var icon = new WindowsFileIcons().IconFor(_folder, isDirectory: true, size: 48);

        Assert.NotNull(icon);
    }

    /// <summary>
    /// Not every pixel transparent. The shell returns some 32-bit bitmaps whose
    /// alpha channel is entirely zero, which drawn literally is an invisible
    /// icon — the provider treats that as opaque, and this is the assertion
    /// that would notice if it stopped.
    /// </summary>
    /// <summary>
    /// **The per-path half of the key has no natural ceiling.** Extensions are
    /// a small fixed set, but folders, shortcuts and executables are cached
    /// individually — so walking a drive would hold a bitmap per folder for the
    /// life of the process.
    /// </summary>
    [Fact]
    public void The_cache_does_not_grow_without_limit()
    {
        var icons = new WindowsFileIcons();

        // **Through the seam, because these folders do not exist.** This asked
        // the real shell for 3,200 paths that were never created, and the shell
        // answered null to every one. That was fine while a null was cached —
        // the nulls were what filled the cache and tripped the wipe — and it
        // became a test of nothing the moment failures stopped being stored:
        // it held ZERO entries and would have passed with the bound deleted.
        // Measured, not reasoned about: a probe asserting Cached > 0 failed.
        WindowsFileIcons.ComposeOverride = (_, _) => new IconPixels(1, 1, new byte[4]);

        try
        {
            // Folders are the per-path case, so each of these is a distinct entry.
            for (var i = 0; i < 3200; i++)
            {
                var dir = Path.Combine(_folder, "f" + i);
                icons.IconFor(dir, isDirectory: true, size: 48);
            }

            Assert.True(WindowsFileIcons.Cached > 0, "nothing was cached, so the bound is untested");

            Assert.True(WindowsFileIcons.Cached < 3200,
                $"held {WindowsFileIcons.Cached} entries");
        }
        finally
        {
            WindowsFileIcons.ComposeOverride = null;
        }
    }

    // ---- what may be remembered --------------------------------------------

    /// <summary>
    /// **A refusal used to be remembered for the life of the process.** The
    /// cache was a GetOrAdd, so a null went in under the file's EXTENSION and
    /// stayed: one handler declining once — a third-party extension, an offline
    /// file, a moment of handle pressure — and every file of that type drew
    /// this application's own glyph for the rest of the session while the types
    /// beside it drew Windows'. Nothing cleared it; IconLoader.Invalidate does
    /// not reach in here. A folder of mixed types came out half converted,
    /// which reads exactly like a setting that only half works — and it is what
    /// "Use my desktop's icons" would have looked like the moment the
    /// precedence fix let that box take effect.
    /// </summary>
    [Fact]
    public void A_refusal_is_not_remembered()
    {
        var icons = new WindowsFileIcons();
        var asked = 0;

        // Declines once, then answers — which is what a handler that was busy
        // does, and the case the old cache could never recover from.
        WindowsFileIcons.ComposeOverride =
            (_, _) => ++asked == 1 ? null : new IconPixels(1, 1, new byte[4]);

        try
        {
            // A distinct extension per test in this class: ordinary files are
            // keyed BY EXTENSION, so sharing one would have this test find the
            // entry the test below had already cached and never reach the seam.
            var file = Path.Combine(_folder, "one.refusal");

            Assert.Null(icons.IconFor(file, isDirectory: false, size: 48));

            // The same key again: it has to reach the shell a second time.
            Assert.NotNull(icons.IconFor(file, isDirectory: false, size: 48));
            Assert.Equal(2, asked);
        }
        finally
        {
            WindowsFileIcons.ComposeOverride = null;
        }
    }

    /// <summary>
    /// **A handler that never answers must not hold the listing.** The shell
    /// call runs code somebody else wrote and takes no argument that says give
    /// up, so the WAIT is what is bounded: on expiry the work is abandoned to
    /// finish in its own time and the row keeps the glyph it already has. The
    /// sibling thumbnail path made the same trade for the same reason.
    ///
    /// Uses the real bound rather than a shortened one, so the number under
    /// test is the number that ships.
    /// </summary>
    [Fact]
    public void A_handler_that_never_answers_is_abandoned()
    {
        var icons = new WindowsFileIcons();
        var released = new ManualResetEventSlim(false);

        WindowsFileIcons.ComposeOverride = (_, _) =>
        {
            released.Wait();
            return new IconPixels(1, 1, new byte[4]);
        };

        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            var icon = icons.IconFor(
                Path.Combine(_folder, "one.hangs"), isDirectory: false, size: 48);

            started.Stop();

            Assert.Null(icon);

            // A LITERAL rather than Bound plus a margin: reading the constant
            // under test would make the assertion follow it, so raising the
            // bound to a minute would still pass. Comfortably above the two
            // seconds that ship, and far below a wait that never ends.
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10),
                $"waited {started.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            // Before the override is dropped, or the abandoned thread runs on
            // into another test holding a delegate this one owns.
            released.Set();
            WindowsFileIcons.ComposeOverride = null;
        }
    }

    /// <summary>
    /// And a success still is remembered, or the fix above would have turned
    /// every row of a folder of four thousand text files into its own shell
    /// call — which is the cost the cache exists to avoid.
    /// </summary>
    [Fact]
    public void An_answer_is_still_remembered()
    {
        var icons = new WindowsFileIcons();
        var asked = 0;

        WindowsFileIcons.ComposeOverride = (_, _) =>
        {
            asked++;
            return new IconPixels(1, 1, new byte[4]);
        };

        try
        {
            // Two DIFFERENT files of one type: ordinary files are keyed by
            // extension, so the second must not reach the shell at all.
            icons.IconFor(Path.Combine(_folder, "a.answer"), isDirectory: false, size: 48);
            icons.IconFor(Path.Combine(_folder, "b.answer"), isDirectory: false, size: 48);

            Assert.Equal(1, asked);
        }
        finally
        {
            WindowsFileIcons.ComposeOverride = null;
        }
    }

    [Fact]
    public void The_icon_is_not_entirely_transparent()
    {
        var icon = new WindowsFileIcons().IconFor(_file, isDirectory: false, size: 48)!;

        var visible = false;

        for (var i = 3; i < icon.Bgra.Length; i += 4)
        {
            if (icon.Bgra[i] == 0) continue;

            visible = true;
            break;
        }

        Assert.True(visible, "every pixel was fully transparent");
    }
}
