using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Decodes thumbnails and keeps a bounded in-memory cache of them.
///
/// Bounded because a bitmap per row across a few large directories will happily
/// consume hundreds of megabytes, and a file manager that grows without limit
/// while you browse is worse than one with no thumbnails at all.
/// </summary>
public static class ThumbnailLoader
{
    /// <summary>
    /// Counted in path+size pairs, not files. The layouts request 64, 256 and
    /// 512, so a folder can occupy three entries per file — 600 was only ~200
    /// files, which is why ordinary folders hit the cap at all.
    /// </summary>
    private const int MaxCached = 2400;

    /// <summary>
    /// **The count alone does not bound anything that matters.** Entries are
    /// not comparable: a 512 pixel grid tile is a megabyte of pixels and a 64
    /// pixel row icon is sixteen kilobytes, sixty-four times less, so 2400 of
    /// them is anywhere between 40 MB and 2.4 GB depending only on which layout
    /// somebody happened to be using. The stated purpose of this cache is that
    /// a file manager must not grow without limit while you browse, and a limit
    /// that varies by a factor of sixty is not one.
    ///
    /// 192 MB, with the count kept as a second ceiling: bytes catch the tile
    /// case, the count still catches a pathological run of tiny images.
    /// </summary>
    private const long MaxCachedBytes = 192L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
    private static readonly ConcurrentQueue<string> Order = new();

    /// <summary>Decoded pixels currently held, by the estimate in
    /// <see cref="BytesOf"/>.</summary>
    private static long _cachedBytes;

    /// <summary>What the cache is holding, in bytes — for the test that pins
    /// the bound, since the number is otherwise unobservable.</summary>
    internal static long CachedBytes => Interlocked.Read(ref _cachedBytes);

    /// <summary>Empties the cache, for tests that need a known starting
    /// point — the state is static and would otherwise carry between them.</summary>
    internal static void Forget()
    {
        Cache.Clear();
        while (Order.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _cachedBytes, 0);
    }

    public static IThumbnailProvider? Provider { get; set; }

    /// <summary>
    /// Local paths of everything currently mounted from elsewhere, refreshed by
    /// the sidebar whenever it rediscovers them. Held here rather than asking
    /// IRemoteMounts per row: Discover() reads directories, and this is called
    /// once per visible row.
    /// </summary>
    public static IReadOnlyList<string> RemoteRoots { get; set; } = [];

    public static bool CanThumbnail(string path)
    {
        if (Provider is null) return false;

        var general = Settings.AppSettings.Current.General;
        if (!general.ShowPreviews) return false;

        if (!Provider.CanThumbnail(path)) return false;

        var limit = IsRemote(path)
            ? general.MaxRemotePreviewMegabytes
            : general.MaxLocalPreviewMegabytes;

        // 0 means no limit, which is the default — and it matters that the
        // stat below is skipped entirely in that case, because this runs once
        // per visible row and the listing is deliberately stat-free.
        if (limit <= 0) return true;

        try
        {
            return new FileInfo(path).Length <= (long)limit * 1024 * 1024;
        }
        catch
        {
            // Gone or unreadable between listing and here — no thumbnail.
            return false;
        }
    }

    /// <summary>
    /// A remote file costs network to read, which is the entire reason the two
    /// limits are separate: a 50 MB photo on an SMB share is a very different
    /// proposition from the same file on the local disk.
    ///
    /// Public because <see cref="RowIcon"/> needs the same judgement for the
    /// same reason, and the roots are already gathered here — asking the
    /// question twice from two lists is how they come to disagree.
    /// </summary>
    public static bool IsRemote(string path)
    {
        // **A UNC path is remote by its shape**, whatever is mounted. Nothing
        // needs to have been discovered for \\server\share to be over a wire.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        foreach (var root in RemoteRoots)
        {
            // PathRules.Comparison, not Ordinal: this compared case-sensitively
            // on the one platform where paths are case-insensitive, so a root
            // discovered as "Z:\" did not match a path spelled "z:\".
            if (path.StartsWith(root, Vaktari.Core.FileSystem.PathRules.Comparison)) return true;
        }

        return false;
    }

    public static async Task<Bitmap?> LoadAsync(string path, int size, CancellationToken ct)
    {
        if (Provider is not { } provider) return null;

        var key = $"{path}|{size}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        // **Everything past this point reads the disk, and all of it used to
        // run on the UI thread.** Only the decode was ever moved off it; the
        // header read below and the provider call after it are synchronous file
        // access wearing an async signature — two File.Exists calls and an open
        // stream apiece — and they ran once per visible row, per layout, on the
        // thread drawing the scroll. On a local disk that is invisible; over
        // SMB it is the difference between a list that scrolls and one that
        // stops dead, and the whole point of the remote size limit above is
        // that this project already knows remote reads are not free.
        //
        // One Task.Run around the lot rather than three: each hop back to the
        // UI thread costs a dispatcher round trip, and nothing in between needs
        // to be there.
        var bitmap = await Task.Run<Bitmap?>(async () =>
        {
            // A picture too small to be worth showing gets NO thumbnail, so the
            // generic mime icon stands instead.
            //
            // [stated] the ask: "for images like this favicon.png file, can we make
            // them just fall back to using a generic image icon instead of trying to
            // render something such low resolution." A 32 pixel favicon blown up to
            // fill a 256 pixel tile is mush, and mush reads as a rendering fault
            // rather than as a small file.
            //
            // Measured on the ORIGINAL, never on `source`: the freedesktop cache may
            // hold an already-upscaled thumbnail, so asking it how big the picture
            // is would get the answer we are trying to avoid trusting.
            //
            // Half the requested size is the floor, which keeps this proportional —
            // 128 for a tile, 32 for a details row — rather than pinning one number
            // that is wrong at some other scale. **An unreadable or unknown format
            // returns null from TryRead and is treated as "big enough", because
            // declining to thumbnail a format we merely cannot measure would be a
            // far larger regression than the blur.**
            if (ImageSize.TryRead(path) is { } natural
                && Math.Max(natural.Width, natural.Height) < size / 2)
                return null;

            var source = await provider.GetThumbnailPathAsync(path, size, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return null;

            // **No file to point at is not the same as no picture.** A
            // freedesktop thumbnail is a PNG in a shared cache, so the path
            // above is the whole answer on Linux; the Windows shell composes
            // one on demand from a registered handler and hands back pixels,
            // which is the only form video, PDF, HEIC and TIFF thumbnails come
            // in there. Second, never first: a path costs no decode at all
            // until something asks to see it.
            if (source is null)
            {
                var pixels = await provider
                    .GetThumbnailPixelsAsync(path, size, ct).ConfigureAwait(false);

                if (pixels is null || ct.IsCancellationRequested) return null;

                // The same rule the header read at the top of this method
                // applies, measured on the RESULT because nothing else can
                // measure these formats — an unmeasurable header is the reason
                // they went to the platform at all. Measured: a 32 pixel
                // favicon.ico asked of the Windows shell at 256 comes back
                // 32x32, and drawing it would stretch it eightfold over the
                // crisp 256x256 icon it hides.
                if (Math.Max(pixels.Width, pixels.Height) < size / 2) return null;

                // Off the UI thread, like the decode below it: a WriteableBitmap
                // is a plain object in the same way Bitmap is. It is
                // IconLoader's DRAWABLES — the DrawingImage and its brushes —
                // that may only be built on the UI thread.
                return IconLoader.ToBitmap(pixels);
            }

            try
            {
                // DecodeToWidth so a huge original is never fully materialised —
                // decoding a 40 megapixel photo to draw a 16 pixel icon is exactly
                // the kind of work that makes scrolling stutter.
                using var stream = File.OpenRead(source);
                return Bitmap.DecodeToWidth(stream, size);
            }
            catch
            {
                // Corrupt, unreadable, or an unsupported format — no thumbnail.
                return null;
            }
        }, ct).ConfigureAwait(false);

        if (bitmap is null) return null;

        Remember(key, bitmap);
        return bitmap;
    }

    /// <summary>
    /// Internal rather than private only so the bound can be tested — the
    /// alternative was decoding real images inside a headless test, which tests
    /// Skia rather than the policy this is.
    /// </summary>
    internal static void Remember(string key, Bitmap bitmap)
    {
        if (!Cache.TryAdd(key, bitmap)) return;

        Order.Enqueue(key);
        Interlocked.Add(ref _cachedBytes, BytesOf(bitmap));

        // Crude FIFO rather than true LRU: tracking access order would need a
        // lock on the read path, which is the path that has to stay fast.
        //
        // EVICTED BITMAPS ARE **NOT** DISPOSED, and that is the whole point.
        // This cache does not own them exclusively — every realized row holds
        // one as its Image.Source, and all three layouts stay alive when
        // hidden. Disposing on eviction destroyed bitmaps that were still on
        // screen, so cycling list → grid → compact made icons vanish: the key
        // is path|size and the layouts ask for 64, 256 and 512, so ~300 files
        // is already at the cap and one more switch evicts something visible.
        //
        // Dropping the reference is enough to bound what the cache retains; the
        // GC frees each bitmap once no row still points at it. That trades
        // prompt native-memory release for not corrupting the display, which is
        // the right way round.
        while ((Order.Count > MaxCached || Interlocked.Read(ref _cachedBytes) > MaxCachedBytes)
               && Order.TryDequeue(out var oldest))
        {
            if (Cache.TryRemove(oldest, out var evicted))
                Interlocked.Add(ref _cachedBytes, -BytesOf(evicted));
        }
    }

    /// <summary>
    /// What a decoded bitmap costs, near enough: four bytes a pixel, which is
    /// what Skia holds these as. Deliberately an estimate — the exact figure
    /// would mean asking the backend about stride and alpha per bitmap, and the
    /// point is a bound, not an audit.
    /// </summary>
    private static long BytesOf(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        return (long)size.Width * size.Height * 4;
    }
}
