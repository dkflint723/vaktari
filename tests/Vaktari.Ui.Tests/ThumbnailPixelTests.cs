using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A thumbnail that is not a file.
///
/// **The loader's whole contract used to be a path**, which is right for the
/// freedesktop cache — a PNG on disk, decoded once, when something asks to see
/// it. The Windows shell has no such file: a thumbnail is composed on demand by
/// a registered handler and handed back as a bitmap, and video, PDF, HEIC and
/// TIFF previews there exist in no other form.
///
/// The providers here are fakes rather than the real Windows one on purpose:
/// the subject is which question the loader asks and when, and a real shell
/// call would make these assertions about the machine instead.
/// </summary>
public sealed class ThumbnailPixelTests
{
    /// <summary>Answers with pixels and no path, the way the Windows provider
    /// does for a video.</summary>
    private sealed class PixelsOnly : IThumbnailProvider
    {
        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);

        public ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<IconPixels?>(new IconPixels(240, 160, new byte[240 * 160 * 4]));
    }

    /// <summary>
    /// Offers a path that cannot be decoded, and pixels underneath it. Nothing
    /// should reach the pixels: a path was offered.
    /// </summary>
    private sealed class PathThatFails : IThumbnailProvider
    {
        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<string?>(path + ".missing");

        public ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<IconPixels?>(new IconPixels(6, 4, new byte[6 * 4 * 4]));
    }

    /// <summary>Answers with a 32 pixel picture whatever is asked for, the
    /// way the shell does for a small .ico.</summary>
    private sealed class TinyPixels : IThumbnailProvider
    {
        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);

        public ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<IconPixels?>(new IconPixels(32, 32, new byte[32 * 32 * 4]));
    }

    /// <summary>
    /// **The favicon rule survives the second route.** A picture too small to
    /// enlarge gets no thumbnail, and here it would also HIDE the icon it
    /// covers — ThumbnailImage.SetShowing hides the RowIcon sibling whenever a
    /// bitmap arrives. Measured on this machine: a 32x32 favicon.ico asked of
    /// the Windows shell at 256 comes back 32x32, while the icon layer it
    /// would cover is a real 256x256. ImageSize cannot measure a .ico, which
    /// is exactly why the judgement has to be made on the answer.
    /// </summary>
    [AvaloniaFact]
    public async Task A_picture_the_platform_returns_too_small_is_not_drawn()
    {
        ThumbnailLoader.Forget();
        var previous = ThumbnailLoader.Provider;
        ThumbnailLoader.Provider = new TinyPixels();

        try
        {
            Assert.Null(await ThumbnailLoader.LoadAsync(FaviconPath, 256, default));

            // The control, so "always null" cannot pass: the same picture at a
            // size it can fill IS drawn.
            Assert.NotNull(await ThumbnailLoader.LoadAsync(FaviconPath, 64, default));
        }
        finally
        {
            ThumbnailLoader.Provider = previous;
            ThumbnailLoader.Forget();
        }
    }

    private const string FaviconPath = @"C:\x\favicon.ico";

    /// <summary>A provider that answers neither way — the interface's own
    /// default for the pixels half, which is what Linux uses.</summary>
    private sealed class Silent : IThumbnailProvider
    {
        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }

    [AvaloniaFact]
    public async Task Pixels_are_drawn_when_there_is_no_file_to_point_at()
    {
        ThumbnailLoader.Forget();
        var previous = ThumbnailLoader.Provider;
        ThumbnailLoader.Provider = new PixelsOnly();

        try
        {
            var bitmap = await ThumbnailLoader.LoadAsync(@"C:\x\holiday.mp4", 256, default);

            Assert.NotNull(bitmap);
            Assert.Equal(240, bitmap!.PixelSize.Width);
            Assert.Equal(160, bitmap.PixelSize.Height);
        }
        finally
        {
            ThumbnailLoader.Provider = previous;
            ThumbnailLoader.Forget();
        }
    }

    /// <summary>
    /// **Second, never first.** A platform that can point at a file keeps the
    /// cheaper answer, and a path that then fails to decode is a failed
    /// thumbnail rather than an invitation to ask again a different way — the
    /// alternative is every unreadable JPEG quietly going through a second,
    /// slower route.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_is_asked_of_the_pixels_when_a_path_was_offered()
    {
        ThumbnailLoader.Forget();
        var previous = ThumbnailLoader.Provider;
        ThumbnailLoader.Provider = new PathThatFails();

        try
        {
            Assert.Null(await ThumbnailLoader.LoadAsync(@"C:\x\holiday.mp4", 256, default));
        }
        finally
        {
            ThumbnailLoader.Provider = previous;
            ThumbnailLoader.Forget();
        }
    }

    /// <summary>
    /// The default implementation on the interface, which is what keeps the
    /// freedesktop provider from having to know this exists.
    /// </summary>
    [AvaloniaFact]
    public async Task A_provider_that_only_knows_paths_still_answers_null()
    {
        ThumbnailLoader.Forget();
        var previous = ThumbnailLoader.Provider;
        ThumbnailLoader.Provider = new Silent();

        try
        {
            Assert.Null(await ThumbnailLoader.LoadAsync(@"C:\x\holiday.mp4", 256, default));
        }
        finally
        {
            ThumbnailLoader.Provider = previous;
            ThumbnailLoader.Forget();
        }
    }
}
