using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Thumbnails: images are handed back as themselves for the UI to decode, and
/// everything else is asked of the shell.
///
/// **The freedesktop thumbnail cache has no Windows counterpart**, so
/// XdgThumbnailProvider's whole caching strategy is moot here rather than
/// portable. Windows caches thumbnails too, in thumbcache_*.db, and the way to
/// read it is IShellItemImageFactory — which is what
/// <see cref="WindowsShellThumbnails"/> now does.
///
/// **Video, PDF, HEIC and TIFF used to return null**, so they kept their drawn
/// icon; the first pass here decoded only the six formats Avalonia itself
/// understands and declined everything else, which on a folder of holiday
/// videos meant a wall of identical glyphs. The shell has had the pictures all
/// along.
///
/// **Images are still NOT asked of the shell, and that is the load-bearing
/// division here.** For a format we can decode, the original file is the better
/// source — full resolution rather than the shell's cached copy — and, more
/// importantly, a null answer from <see cref="GetThumbnailPathAsync"/> for one
/// of those means "deliberately no thumbnail": the picture was too small to
/// enlarge without turning it to mush. Asking the shell as a second try would
/// quietly undo that rule for exactly the files it exists for.
/// </summary>
public sealed class WindowsThumbnailProvider : IThumbnailProvider
{
    /// <summary>
    /// Only what Avalonia's own decoder handles. Deliberately not the full list
    /// ImageSize can parse — BMP is in there for header reading, and it is
    /// decodable, but the point of this set is "will the UI be able to show it".
    /// </summary>
    private static readonly HashSet<string> Decodable =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public bool CanThumbnail(string path)
    {
        var extension = Path.GetExtension(path);

        // The shell half is a registry lookup per EXTENSION, cached — not a
        // stat, and not a call into any handler. That matters because this runs
        // once per visible row and the listing is deliberately stat-free.
        return Decodable.Contains(extension) || WindowsShellThumbnails.HasHandler(extension);
    }

    public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
    {
        if (!Decodable.Contains(Path.GetExtension(path))) return ValueTask.FromResult<string?>(null);

        try
        {
            // A file too small to enlarge cleanly keeps its icon rather than
            // being blown up into a blur — the behaviour the README promises.
            // ImageSize reads the header only, so this costs a few bytes and
            // never decodes a 40 megapixel photo to find out it is large.
            if (ImageSize.TryRead(path) is { } dimensions
                && dimensions.Width < size && dimensions.Height < size)
                return ValueTask.FromResult<string?>(null);

            // Null from TryRead means "unknown", never "small" — an unparsed
            // header is not a reason to suppress a thumbnail, so it falls
            // through to returning the file.
            return ValueTask.FromResult<string?>(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }

    public ValueTask<IconPixels?> GetThumbnailPixelsAsync(string path, int size, CancellationToken ct)
    {
        // A decodable image has already had its answer, including the
        // deliberate null for one too small to enlarge. See the class note.
        if (Decodable.Contains(Path.GetExtension(path)))
            return ValueTask.FromResult<IconPixels?>(null);

        return WindowsShellThumbnails.PixelsAsync(path, size, ct);
    }
}
