using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Fills an Image with the desktop's themed icon for a file. Same viewport-driven
/// attached-property shape as thumbnails and metadata, and the same reason:
/// only realized rows pay for it.
/// </summary>
public static class RowIcon
{
    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<Image, FileEntry?>("Entry", typeof(RowIcon));

    public static readonly AttachedProperty<int> SizeProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Size", typeof(RowIcon), 24);

    /// <summary>
    /// Per-row cancellation, mirroring ThumbnailImage. The stale-check before
    /// painting already stopped the WRONG icon appearing; this stops the work
    /// happening at all for a row that has scrolled away. Task.Run with a token
    /// will not interrupt a lookup already running, but it does drop one still
    /// queued — which is the case that matters, because a fast scroll queues far
    /// more than it starts.
    /// </summary>
    private static readonly AttachedProperty<CancellationTokenSource?> TokenProperty =
        AvaloniaProperty.RegisterAttached<Image, CancellationTokenSource?>("Token", typeof(RowIcon));

    static RowIcon()
    {
        EntryProperty.Changed.AddClassHandler<Image>((image, e) =>
            OnEntryChanged(image, e.NewValue as FileEntry?));
    }

    public static void SetEntry(Image image, FileEntry? value) => image.SetValue(EntryProperty, value);
    public static FileEntry? GetEntry(Image image) => image.GetValue(EntryProperty);

    public static void SetSize(Image image, int value) => image.SetValue(SizeProperty, value);
    public static int GetSize(Image image) => image.GetValue(SizeProperty);

    private static async void OnEntryChanged(Image image, FileEntry? entry)
    {
        if (image.GetValue(TokenProperty) is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        image.SetValue(TokenProperty, cts);

        // Captured before awaiting: a later call on this same Image disposes the
        // source while we are suspended, and reading .Token on a disposed source
        // throws — the token struct stays safe to query.
        var token = cts.Token;

        try
        {
            image.Source = null;
            image.IsVisible = false;

            if (entry is not { } value) return;

            // Always show something: the themed icon when the desktop has one,
            // otherwise the drawn glyph. One element, one source — nothing to
            // overlap.
            void Paint(IImage icon)
            {
                if (GetEntry(image)?.FullPath != value.FullPath) return;

                image.Source = icon;
                image.IsVisible = true;
            }

            var size = image.GetValue(SizeProperty);

            // **The desktop's own icons, when asked for.** Checked before the
            // theme provider because it answers a different question — per
            // file rather than per icon name — so an executable shows its own
            // icon and a folder shows the custom one it was given, which is
            // most of the reason somebody turns this on.
            //
            // The drawn glyph goes up first either way, so a row is never blank
            // while the shell composes.
            if (IconLoader.UseSystemIcons)
            {
                Paint(FileTypeIcon.For(value.Name, value.IsDirectory));

                // Off-thread: composing an icon reads a resource out of some
                // DLL, and this runs once per visible row.
                var pixels = await Task.Run(
                    () => IconLoader.SystemPixels(value.FullPath, value.IsDirectory, size), token)
                    .ConfigureAwait(true);

                if (pixels is not null && IconLoader.Draw(pixels) is { } drawn)
                {
                    // **No contents probe after this.** The papers-in-the-folder
                    // affordance repaints the DRAWN folder, so running it here
                    // put Vaktari's icon back over the shell's for every folder
                    // that had anything in it — leaving empty folders showing
                    // Windows' icon and full ones showing ours, which is the
                    // opposite of a setting called "use my desktop's icons".
                    //
                    // Nothing is lost: the shell draws its own distinction
                    // between an ordinary folder and one it has an opinion
                    // about, and borrowing that is the whole point of the
                    // setting.
                    Paint(drawn);
                    return;
                }

                // Only where the shell gave us nothing: then the drawn set is
                // what is on screen, and its own folder affordance applies.
                await ShowContentsIfAnyAsync(value, Paint, token).ConfigureAwait(true);
                return;
            }

            if (IconLoader.Provider is null)
            {
                Paint(FileTypeIcon.For(value.Name, value.IsDirectory));
                await ShowContentsIfAnyAsync(value, Paint, token).ConfigureAwait(true);
                return;
            }


            // Only the filesystem lookup goes off-thread. Building the drawable
            // creates Avalonia objects and reads application resources, so it
            // must happen on the UI thread — doing it in the Task.Run crashed
            // the process outright.
            // The drawn glyph goes up immediately so a row is never blank while
            // the theme lookup runs.
            Paint(FileTypeIcon.For(value.Name, value.IsDirectory));

            var file = await Task.Run(
                    () => IconLoader.ResolveFile(value.FullPath, value.IsDirectory, size), token)
                                 .ConfigureAwait(true);

            if (file is null) return;

            // Default priority, deliberately.
            //
            // This was Background for a while, added while chasing a 44-second
            // navigation stall on the theory that row decoration was starving
            // the dispatcher. It was not — the cause was an xdg-mime subprocess
            // per row exhausting the thread pool — and the timings got WORSE
            // with it, not better. A change made for a reason that turned out to
            // be false does not get to stay on the grounds that it is already
            // there. Cancellation above now bounds the backlog properly.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Containers recycle while we work; only replace if this row
                // still wants the file we resolved.
                if (IconLoader.Load(file) is { } icon) Paint(icon);
            });
        }
        catch (OperationCanceledException)
        {
            // The row scrolled away. Expected, and not worth a line.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] icon failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Repaints a folder with papers in it once we know there are some.
    ///
    /// **The listing is deliberately stat-free, and this is a stat.** FileEntry
    /// carries what the directory enumeration already handed over and nothing
    /// more, precisely so a listing costs one syscall rather than one per row —
    /// so knowing whether a folder has anything in it has to be bought
    /// separately, per realized row, cancellable, and off the UI thread. That is
    /// the same bargain thumbnails and metadata already make.
    ///
    /// **Any() rather than a count.** The enumeration stops at the first entry
    /// instead of walking the folder, which is the difference between a probe and
    /// a scan on a directory with fifty thousand files in it.
    ///
    /// **Adding the sheets rather than taking them away** is why the plain
    /// folder is the empty one. The bare icon is already on screen when this
    /// starts, so the only thing that ever changes is a folder gaining papers —
    /// where the other way round every folder in the listing would flash a full
    /// stack and then be stripped of it. The cost is that a folder we cannot
    /// open, or one whose probe is cancelled by a fast scroll, is drawn as
    /// empty when it is really unknown; the papers appearing late is a smaller
    /// lie than the whole listing flickering.
    /// </summary>
    private static async Task ShowContentsIfAnyAsync(
        FileEntry entry, Action<IImage> paint, CancellationToken token)
    {
        if (!entry.IsDirectory) return;

        // Answers already bought. Scrolling down a folder of folders and back
        // up re-realizes every row and asked the disk again each time, so the
        // cost was not once per folder but once per time it crossed the
        // viewport — and a directory open is the one thing this probe was
        // written to keep cheap.
        //
        // **The write time is what makes keeping the answer safe.** Creating or
        // removing an entry updates the containing folder's mtime, on NTFS and
        // ext4 alike, and that transition is precisely the one being cached: a
        // folder that gains its first file gets a new key and is asked again.
        // A stale answer here would be a folder drawn as empty while holding
        // something, which is the lie this cache must not tell.
        var probeKey = (entry.FullPath, entry.LastWriteTime);

        if (Probed.TryGetValue(probeKey, out var known))
        {
            if (known) paint(FileTypeIcon.For(entry.Name, isDirectory: true, hasContents: true));
            return;
        }

        // **Not on a share.** Opening a directory is a network round trip on
        // SMB or WebDAV, and this runs once per realized row — a screenful of
        // folders is a screenful of round trips, paid again on every scroll
        // that recycles the containers. A remote folder keeps the plain icon,
        // which is exactly what it had before any of this existed. The same
        // judgement, from the same list, that decides the preview size limit.
        if (ThumbnailLoader.IsRemote(entry.FullPath)) return;

        try
        {
            var full = await Task.Run(() =>
            {
                try { return Directory.EnumerateFileSystemEntries(entry.FullPath).Any(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }, token).ConfigureAwait(true);

            // Cleared wholesale rather than evicted one at a time: this is a
            // bool per folder, so the bound exists to stop a long session
            // browsing a filesystem from retaining every folder it ever passed,
            // not to protect a working set worth the bookkeeping of an LRU.
            if (Probed.Count >= MaxProbed) Probed.Clear();
            Probed[probeKey] = full;

            if (full && !token.IsCancellationRequested)
                paint(FileTypeIcon.For(entry.Name, isDirectory: true, hasContents: true));
        }
        catch (OperationCanceledException)
        {
            // Scrolled away before the probe finished. The ordinary folder icon
            // is already on screen, which is the right thing to leave there.
            // Nothing is recorded: an unfinished probe has no answer to keep.
        }
    }

    /// <summary>Folders already probed, keyed by path and write time — see
    /// <see cref="ShowContentsIfAnyAsync"/> for why the timestamp is in the
    /// key.</summary>
    private static readonly ConcurrentDictionary<(string Path, DateTimeOffset Written), bool>
        Probed = new();

    private const int MaxProbed = 4096;
}
