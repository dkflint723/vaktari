using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The icons Windows itself draws for files, for people who would rather see
/// their own desktop's set than the one this application ships.
///
/// The shell call, the HBITMAP and the flags all live in <see cref="ShellImage"/>
/// now: <see cref="WindowsShellThumbnails"/> asks the same factory the same way
/// and differs only in which flag it passes, and the lessons underneath — row
/// order, empty alpha, the GetObjectW entry point — are worth having in one
/// place. What is left here is the part that is about ICONS: which flag, and
/// what may be cached under which key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileIcons : IFileIconProvider
{
    /// <summary>
    /// Keyed by EXTENSION for ordinary files, and by path for the things whose
    /// icon is their own: executables, shortcuts, folders.
    ///
    /// **Because this is called once per visible row.** Asking the shell for
    /// every .txt in a folder of four thousand is four thousand compositions of
    /// an identical picture; asking once is the difference between a listing
    /// that draws and one that crawls. Getting the key wrong the other way —
    /// caching an .exe by extension — would draw every program with the icon of
    /// whichever one was seen first, which is why those are excluded.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IconPixels?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct icons held before the cache is dropped. Comfortably
    /// more than any one folder needs, and far below what a drive walk
    /// accumulates.</summary>
    private const int MaxCached = 3000;

    /// <summary>How many are held, for the test that pins the bound.</summary>
    internal static int Cached => Cache.Count;

    /// <summary>Types whose icon belongs to the individual file rather than to
    /// the file type.</summary>
    private static readonly HashSet<string> PerFile =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".msi", ".cpl", ".scr", ".url" };

    public IconPixels? IconFor(string path, bool isDirectory, int size)
    {
        // Rounded to the sizes the shell actually composes at, so a pane at 41
        // pixels and one at 43 share a cache entry instead of each building
        // their own copy of every icon in the folder.
        var bucket = size switch
        {
            <= 16 => 16,
            <= 32 => 32,
            <= 48 => 48,
            <= 96 => 96,
            <= 128 => 128,
            _ => 256,
        };

        var extension = Path.GetExtension(path);

        var key = isDirectory || extension.Length == 0 || PerFile.Contains(extension)
            ? $"{path}|{bucket}"
            : $"{extension}|{bucket}";

        // **Bounded, because the per-PATH half of the key has no ceiling.**
        // Extensions are a small fixed set, but folders, shortcuts and
        // executables are keyed individually — so browsing a drive with fifty
        // thousand folders in it would hold fifty thousand bitmaps for the life
        // of the process, at a megabyte each for the large sizes.
        //
        // Cleared wholesale rather than evicted one at a time: the working set
        // is whatever folder is on screen, recomposing an icon is cheap next to
        // the bookkeeping an LRU would need on the path that has to stay fast,
        // and this only ever happens after several thousand distinct icons.
        if (Cache.Count >= MaxCached) Cache.Clear();

        // BIGGERSIZEOK: the shell would otherwise scale a 48 up to 256 and
        // hand back the blur. Given the choice it returns the next size it
        // actually has, and letting the UI scale down beats scaling up.
        return Cache.GetOrAdd(key, _ => ShellImage.Pixels(
            path, bucket, ShellImage.IconOnly | ShellImage.BiggerSizeOk, "file-icons"));
    }
}
