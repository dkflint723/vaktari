namespace Vaktari.Core.FileSystem;

/// <summary>
/// Finds an image to represent a file. Deliberately returns a *path* rather
/// than pixels: decoding belongs in the UI layer where the toolkit's own
/// decoder lives, and this way a cached thumbnail costs no decode at all until
/// something actually asks to see it.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>Cheap extension test, so the list can skip files that will never have one.</summary>
    bool CanThumbnail(string path);

    /// <summary>
    /// A cached thumbnail, or the original file when it can be decoded directly.
    /// Null when there is nothing to show.
    /// </summary>
    ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct);

    /// <summary>
    /// The same picture where the platform has one but it is not a FILE.
    ///
    /// **Asked only after <see cref="GetThumbnailPathAsync"/> has answered
    /// null**, so a platform that can point at a file keeps the cheaper path
    /// and nothing decodes twice.
    ///
    /// **Pixels rather than a path, for the reason
    /// <see cref="IFileIconProvider"/> already gives.** A freedesktop thumbnail
    /// is a PNG in a shared cache, so a path is the natural answer there and a
    /// miss costs nothing. The Windows shell has no such file: a thumbnail is
    /// composed on demand by a registered handler and handed back as an
    /// HBITMAP, and the only way to obtain a path for it would be to encode a
    /// PNG and cache it to disk — reimplementing the freedesktop cache purely
    /// to satisfy the shape of this method. WINDOWS.md §4 reached that
    /// conclusion for the neighbouring seam before either provider was
    /// written: deciding IIconThemeProvider stays null, it rejected
    /// "extracting the handle, encoding a PNG and caching it to disk purely to
    /// have a path to hand back", and named the right shape as "something
    /// per-file and bitmap-returning, closer to IThumbnailProvider than to
    /// this". IFileIconProvider is that shape for icons; this is the same
    /// shape, for the same reason, for thumbnails.
    ///
    /// Defaulted to null, so the platforms whose thumbnails genuinely are files
    /// say nothing and keep the path above.
    /// </summary>
    ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
        => ValueTask.FromResult<IconPixels?>(null);
}
