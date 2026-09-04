using System.Collections.Concurrent;
using Microsoft.Win32;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// The thumbnail Windows itself would draw for a file — the video frame, the
/// first page of the PDF, the HEIC that no bundled decoder can read.
///
/// **Two separate questions, answered by two separate mechanisms.**
///
/// "Can this KIND of file have a thumbnail at all" is a fact about the machine,
/// not about the file, and the machine already records it: a thumbnail handler
/// is a COM object registered under
/// <c>ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}</c> for an extension, its
/// ProgID, or its perceived type. Reading that is what <see cref="HasHandler"/>
/// does, once per extension, and it is cheap enough for the per-row gate that
/// <see cref="IThumbnailProvider.CanThumbnail"/> is.
///
/// **A hardcoded list of "formats Windows thumbnails" would be a lie**, and the
/// registry on this machine says so plainly. Measured 3 September 2026: .mp4,
/// .mkv and .mp3 resolve to the shell's own media handler, .heic to the photo
/// handler, .tif through PerceivedType=image, .pdf to whichever reader is
/// installed (Foxit here, not Edge), .docx to the Office handler — and .svg to
/// NOTHING, because Windows ships no SVG thumbnail provider. A list naming SVG
/// would promise a picture this machine cannot produce, and a list omitting
/// .docx would decline one it can.
///
/// "Does this PARTICULAR file have one" only the handler can answer, and it
/// answers by being run — which is third-party code, on the other side of a COM
/// call that cannot be cancelled. Hence <see cref="Bound"/>.
///
/// **A registered handler is not a promise, and null here is a normal answer
/// rather than a fault.** Measured on real files: a .docx saved without a
/// preview picture and an .mp3 with no album art both came back empty from
/// handlers that certainly exist, and so did everything under this machine's
/// Proton Drive sync root — while byte-identical copies of the same .mp4 and
/// .pdf on the local disk produced a video frame and a first page. Each of
/// those rows simply keeps the icon it already had.
/// </summary>
internal static class WindowsShellThumbnails
{
    /// <summary>
    /// IThumbnailProvider's own interface id, which is also the subkey name the
    /// shell looks under. Handlers register themselves by it.
    /// </summary>
    private const string ThumbnailHandler = "{E357FCCD-A995-4576-B01F-234630154E96}";

    /// <summary>
    /// How long a handler gets before the row keeps its icon instead.
    ///
    /// **Deliberately shorter than <see cref="ShellContextMenu"/>'s four
    /// seconds, and for a different reason than "faster is better".** That bound
    /// gates a menu somebody has just clicked for and is waiting on, so waiting
    /// is the point. This one is per FILE and runs for every visible row, so a
    /// folder of them multiplies it; and the thing being waited for is
    /// decoration, with a perfectly good icon already on screen behind it.
    ///
    /// Two seconds is roughly five times the slowest real answer measured here:
    /// a 15 GB .mp4 the shell had never seen took 376 ms to produce a frame from
    /// cold and 11 ms once it had, and the in-box photo handler answered in 40 ms
    /// cold. That leaves room for a codec that has to be paged in, and still
    /// makes a bad handler cost a scroll rather than a session.
    ///
    /// A file that times out is not remembered as hopeless: nothing is cached,
    /// so the next time the row is realized it asks again, by which point the
    /// shell's own thumbcache may well have been filled by the very call that
    /// was abandoned.
    /// </summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Which extensions the machine has a handler for. Keyed by extension
    /// because that is the granularity of the answer — every .mp4 on the
    /// machine has the same handler or none.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> Handlers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Distinct extensions remembered before the map is dropped. Extensions ARE
    /// effectively a fixed set in any real folder, but nothing enforces that —
    /// a directory of "part.0001" through "part.90000" is ninety thousand of
    /// them — and an unbounded map on the listing path is the same fault the
    /// icon cache next door was fixed for.
    /// </summary>
    private const int MaxRemembered = 4000;

    /// <summary>How many are held, for the test that pins the bound.</summary>
    internal static int Remembered => Handlers.Count;

    /// <summary>
    /// Whether this machine has a thumbnail handler registered for the
    /// extension. One registry walk per extension, then a dictionary lookup.
    /// </summary>
    internal static bool HasHandler(string extension)
    {
        if (extension.Length == 0) return false;

        if (Handlers.Count >= MaxRemembered) Handlers.Clear();

        return Handlers.GetOrAdd(extension, Probe);
    }

    /// <summary>
    /// The four places a handler can be registered. Which one WINS is the
    /// shell's business; the question here is only whether any of them exists,
    /// so this stops at the first hit rather than ranking them.
    ///
    /// HKEY_CLASSES_ROOT is the merged view of HKLM and HKCU, so a handler the
    /// user installed for themselves counts exactly as much as a machine-wide
    /// one. Measured: 4,200 misses in 212 ms, and every extension is asked at
    /// most once.
    /// </summary>
    private static bool Probe(string extension)
    {
        try
        {
            if (Registered(extension)) return true;

            using var key = Registry.ClassesRoot.OpenSubKey(extension);

            if (key?.GetValue(null) is string progId && Registered(progId)) return true;

            if (Registered(@"SystemFileAssociations\" + extension)) return true;

            return key?.GetValue("PerceivedType") is string perceived
                   && Registered(@"SystemFileAssociations\" + perceived);
        }
        catch (Exception ex)
        {
            // A registry the process cannot read is a machine with no
            // thumbnails, not a listing that fails to draw.
            Quiet.Swallowed("shell-thumbnails", ex);
            return false;
        }
    }

    private static bool Registered(string classKey)
    {
        if (classKey.Length == 0) return false;

        using var handler = Registry.ClassesRoot.OpenSubKey(
            $@"{classKey}\ShellEx\{ThumbnailHandler}");

        return handler is not null;
    }

    /// <summary>
    /// The shell's thumbnail for one file, or null when it has none.
    /// </summary>
    internal static ValueTask<IconPixels?> PixelsAsync(string path, int size, CancellationToken ct)
    {
        if (!HasHandler(Path.GetExtension(path))) return ValueTask.FromResult<IconPixels?>(null);

        // THUMBNAILONLY alone. BIGGERSIZEOK is the icon path's flag and is
        // measured on ShellImage to make the shell answer LARGER than asked,
        // which here would only fill a byte-bounded cache faster.
        return Bounded(
            () => ShellImage.Pixels(path, size, ShellImage.ThumbnailOnly, "shell-thumbnails"),
            path,
            ct);
    }

    /// <summary>
    /// Runs one shell call under the time bound.
    ///
    /// **The call itself cannot be cancelled or interrupted.** GetImage runs a
    /// handler somebody else wrote and takes no argument that says "give up".
    /// So the wait is what is bounded:
    /// on expiry the task is abandoned to finish in its own time and the caller
    /// is told there is no thumbnail. That strands a pool thread, which is the
    /// same trade ShellContextMenu documents — a leak is a smaller problem than
    /// a hang, and this one is bounded by how many distinct files a person can
    /// scroll past.
    ///
    /// Takes the work as a delegate so the bound can be tested without a
    /// deliberately broken shell extension installed on the machine.
    /// </summary>
    internal static async ValueTask<IconPixels?> Bounded(
        Func<IconPixels?> ask, string path, CancellationToken ct)
    {
        // ct on the Task.Run as well as the wait: it will not interrupt a call
        // already running, but it does drop one still queued, which is the case
        // that matters when a fast scroll queues far more than it starts.
        var work = Task.Run(ask, ct);

        try
        {
            return await work.WaitAsync(Bound, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Through Quiet rather than straight to stderr. Nothing dedupes
            // this: a row is realized again on every scroll back, and the three
            // layouts ask at 64, 256 and 512, so it is a line per REALIZATION
            // and not one per file. The row keeps the icon it already had,
            // which is the right outcome, so this is a debugging aid rather
            // than something to tell the user about — which is exactly what
            // Quiet is for.
            Quiet.Swallowed(
                "shell-thumbnails",
                new TimeoutException(
                    $"no answer for {Path.GetFileName(path)} in {Bound.TotalSeconds:0}s", ex));

            return null;
        }
    }
}
