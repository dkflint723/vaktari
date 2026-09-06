using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Vaktari.Core;
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
    private static readonly ConcurrentDictionary<string, IconPixels> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct icons held before the cache is dropped. Comfortably
    /// more than any one folder needs, and far below what a drive walk
    /// accumulates.</summary>
    private const int MaxCached = 3000;

    /// <summary>How many are held, for the test that pins the bound.</summary>
    internal static int Cached => Cache.Count;

    /// <summary>
    /// How long one composition is waited for before the row keeps the glyph it
    /// already has.
    ///
    /// **The call runs a handler somebody else wrote and takes no argument that
    /// says "give up".** So the WAIT is what is bounded, exactly as
    /// <see cref="WindowsShellThumbnails.Bound"/> bounds the same factory for
    /// the same reason: on expiry the work is abandoned to finish in its own
    /// time and the caller is told there is no icon. That strands a pool
    /// thread, which is the smaller problem of the two — and here it is bounded
    /// by how many distinct icons are visible at once.
    ///
    /// Two seconds, matching the thumbnail path, because it is the same shell
    /// and the same class of badly-behaved extension.
    /// </summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Stands in for the shell, so the caching rules and the bound can be
    /// tested without a deliberately broken icon handler installed on the
    /// machine. Null in the application.
    /// </summary>
    internal static Func<string, int, IconPixels?>? ComposeOverride { get; set; }

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

        if (Cache.TryGetValue(key, out var known)) return known;

        var composed = Compose(path, bucket);

        // **A failure is not an answer, and must not be remembered as one.**
        // This used GetOrAdd, so a null went into the cache under the file's
        // EXTENSION and stayed there for the life of the process: one shell
        // handler declining once — a third-party extension, an offline
        // OneDrive file, a moment of handle pressure — and every file of that
        // type drew this application's own glyph for the rest of the session,
        // while the types around it drew Windows'. Nothing cleared it either;
        // IconLoader.Invalidate does not reach in here. A folder of mixed types
        // came out half converted, which reads exactly like a setting that only
        // half works. The sibling thumbnail path had already decided this
        // question the other way, deliberately.
        //
        // The cost is that a type which always fails is asked again for each
        // row that shows it. That is bounded by what is on screen rather than
        // by what is in the folder, because rows are recycled — and the bound
        // above stops one slow handler from making that expensive.
        if (composed is null) return null;

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

        Cache[key] = composed;

        return composed;
    }

    /// <summary>
    /// One composition, waited for no longer than <see cref="Bound"/>.
    ///
    /// Never throws: the contract on <see cref="IFileIconProvider.IconFor"/>
    /// says so, because an exception here is a listing that does not appear at
    /// all. A refusal and a timeout are the same answer to the caller — there
    /// is no icon — and neither is cached.
    /// </summary>
    private static IconPixels? Compose(string path, int bucket)
    {
        // **The seam stands in for the SHELL, inside the bound rather than
        // around it.** Putting it in front would have left the one thing the
        // bound exists for — a handler that never answers — reachable only by
        // installing a broken extension on the machine running the tests.
        var ask = ComposeOverride is { } fake
            ? () => fake(path, bucket)

            // BIGGERSIZEOK: the shell would otherwise scale a 48 up to 256 and
            // hand back the blur. Given the choice it returns the next size it
            // actually has, and letting the UI scale down beats scaling up.
            : (Func<IconPixels?>)(() => ShellImage.Pixels(
                path, bucket, ShellImage.IconOnly | ShellImage.BiggerSizeOk, "file-icons"));

        var work = Task.Run(ask);

        try
        {
            if (work.Wait(Bound)) return work.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("file-icons", ex);
            return null;
        }

        // Abandoned rather than cancelled — see Bound. The row keeps the glyph
        // it was drawn with, which is the right outcome, so this is a debugging
        // aid rather than something to tell anyone about.
        Quiet.Swallowed(
            "file-icons",
            new TimeoutException(
                $"no icon for {Path.GetFileName(path)} in {Bound.TotalSeconds:0}s"));

        return null;
    }
}
