using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Fills a TextBlock with a file's inline metadata, asynchronously.
///
/// Same shape as the thumbnail loader and for the same reason: the list
/// virtualizes, so attaching to the realized control makes the work
/// viewport-driven without the collection having to hold anything extra.
/// </summary>
public static class RowMetadata
{
    private const int MaxCached = 2000;

    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();

    public static IFileMetadataProvider? Provider { get; set; }

    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, FileEntry?>("Entry", typeof(RowMetadata));

    /// <summary>Same mechanism, different fact: the POSIX mode string.</summary>
    // TWO token slots, not one. EntryProperty and AccessProperty are separate
    // attached properties and nothing stops a single TextBlock carrying both —
    // sharing one slot would mean each silently cancelling the other.
    private static readonly AttachedProperty<CancellationTokenSource?> TokenProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, CancellationTokenSource?>("Token", typeof(RowMetadata));

    private static readonly AttachedProperty<CancellationTokenSource?> AccessTokenProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, CancellationTokenSource?>("AccessToken", typeof(RowMetadata));

    private static readonly AttachedProperty<CancellationTokenSource?> SizeTokenProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, CancellationTokenSource?>("SizeToken", typeof(RowMetadata));

    /// <summary>
    /// The whole size cell: a file's bytes, or a folder's item count once it
    /// has been counted.
    ///
    /// **"Show item counts for folders" did nothing at all.** The setting
    /// round-tripped faithfully, both platform providers counted directories,
    /// and the gate below even honoured "None" — but nothing in the application
    /// ever set Entry or Access, so the entire provider path was dead code. The
    /// size cell was bound to a converter that returned an em dash for every
    /// directory, whatever the setting said. On by default, so it had never
    /// worked.
    /// </summary>
    public static readonly AttachedProperty<FileEntry?> SizeProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, FileEntry?>("Size", typeof(RowMetadata));

    public static readonly AttachedProperty<FileEntry?> AccessProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, FileEntry?>("Access", typeof(RowMetadata));

    static RowMetadata()
    {
        EntryProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
            OnEntryChanged(text, e.NewValue as FileEntry?, access: false));

        AccessProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
            OnEntryChanged(text, e.NewValue as FileEntry?, access: true));

        SizeProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
            OnSizeChanged(text, e.NewValue as FileEntry?));
    }

    public static void SetEntry(TextBlock target, FileEntry? value)
        => target.SetValue(EntryProperty, value);

    public static FileEntry? GetEntry(TextBlock target) => target.GetValue(EntryProperty);

    public static void SetAccess(TextBlock target, FileEntry? value)
        => target.SetValue(AccessProperty, value);

    public static FileEntry? GetAccess(TextBlock target) => target.GetValue(AccessProperty);

    public static void SetSize(TextBlock target, FileEntry? value)
        => target.SetValue(SizeProperty, value);

    public static FileEntry? GetSize(TextBlock target) => target.GetValue(SizeProperty);

    /// <summary>
    /// What a folder's size cell still owes, once the text has been decided.
    /// </summary>
    public enum SizeFill
    {
        /// <summary>The text is the whole answer. Files, drives, measured rows,
        /// and folders the setting says to leave alone.</summary>
        Nothing,

        /// <summary>Ask the platform provider how many things are in it.</summary>
        Count,

        /// <summary>Walk it and total what is underneath.</summary>
        Measure,
    }

    /// <summary>
    /// What the size cell says, and what still has to be fetched for it.
    ///
    /// Pure and synchronous so the decision can be read without a provider, a
    /// dispatcher or a control behind it.
    ///
    /// **ContentSize used to be treated as ItemCount**, because the providers
    /// only count and there was no recursive summing to ask — the settings
    /// dialog could not reach the mode, and the view model preserved it rather
    /// than ever writing it. <see cref="Core.FileSystem.SpaceUsage.Measure"/>
    /// is that summing, so the mode is now its own answer and the dialog
    /// offers it.
    /// </summary>
    public static (string Text, SizeFill Fill) SizeCell(
        FileEntry entry, Core.Settings.FolderSizeMode folders)
    {
        // A default FileEntry reaches a recycled container. The converter this
        // replaced guarded the same case.
        if (entry.FullPath is null) return ("", SizeFill.Nothing);

        // **This PC's Size column reported how many things were at the top of
        // each drive.** ComputerListing has carried the volume's capacity as
        // the row's Length since This PC was built, and the only rule below for
        // a directory is "em dash, then ask the provider" — and the provider
        // says yes to every directory and counts it. So the column that should
        // have read "931 GiB" read "184 items", and filling it enumerated the
        // root of every drive on the machine, including a disconnected share
        // that answers nothing until the network gives up.
        //
        // Zero is "not known" rather than an empty drive: a share whose server
        // is unreachable and an optical drive with no disc in it both arrive
        // with no capacity at all — WindowsPlacesProvider only reads TotalSize
        // when the drive is ready — and "0 B" is a claim about a drive nobody
        // has managed to measure.
        if (entry.IsVolume)
            return (entry.Length > 0 ? ByteSize.Format(entry.Length) : "\u2014", SizeFill.Nothing);

        // A folder someone asked to have measured, in the listing that went and
        // did it. **Above both rules below, or the one listing built to show a
        // folder's size is the one that will not show it**: "no size for
        // folders" would blank the column that is the whole point, and the
        // counting rule would throw the measured total away and ask the
        // provider for an item count instead.
        //
        // Nothing is owed, so a measured row never reaches the per-row fetch or
        // the cache it shares with ordinary listings.
        if (entry.IsMeasured) return (ByteSize.Format(entry.Length), SizeFill.Nothing);

        // The sixth and last copy of this. It was the only one already using
        // binary unit names, which is why the Size column and the status bar
        // beside it once disagreed about the same file.
        if (!entry.IsDirectory) return (ByteSize.Format(entry.Length), SizeFill.Nothing);

        if (folders == Core.Settings.FolderSizeMode.None) return ("\u2014", SizeFill.Nothing);

        // The em dash is the placeholder while the answer is in flight, and what
        // stays if the folder cannot be read.
        return ("\u2014", folders == Core.Settings.FolderSizeMode.ContentSize
            ? SizeFill.Measure
            : SizeFill.Count);
    }

    private static async void OnSizeChanged(TextBlock target, FileEntry? entry)
    {
        if (target.GetValue(SizeTokenProperty) is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        target.SetValue(SizeTokenProperty, cts);
        var token = cts.Token;

        // async void: nothing may escape, or scrolling crashes the app.
        try
        {
            if (entry is not { } value)
            {
                target.Text = "";
                return;
            }

            var (text, fill) = SizeCell(value, Settings.AppSettings.Current.Views.Details.FolderSize);

            target.Text = text;

            if (fill == SizeFill.Nothing) return;

            // **The provider is asked for the count and for nothing else.**
            // Measuring is Core's walk, which needs no provider — but the gate
            // still applies to both: it is what keeps a listing from walking a
            // path the platform has already said it cannot answer for.
            if (Provider is null) return;
            if (!Provider.CanDescribe(value.FullPath, isDirectory: true)) return;

            var key = CacheKey(value.FullPath, fill);

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    if (cached is { Length: > 0 }) target.Text = cached;
                    return;
                }
            }

            var counted = fill == SizeFill.Measure
                ? await MeasureAsync(value.FullPath, token).ConfigureAwait(true)
                : await Provider
                    .DescribeAsync(value.FullPath, isDirectory: true, token)
                    .ConfigureAwait(true);

            Remember(key, counted);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Recycled onto another row while we were counting.
                if (GetSize(target)?.FullPath != value.FullPath) return;

                // A folder that could not be read keeps the em dash rather than
                // blanking the column.
                if (counted is { Length: > 0 }) target.Text = counted;
            });
        }
        catch (OperationCanceledException)
        {
            // The row scrolled away.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] folder count failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Where a folder's answer is kept.
    ///
    /// **The two answers may not share a slot.** The count uses the same key
    /// the Entry path does, because it is literally the same call on the same
    /// path — but a measured total is a different question about that path,
    /// and one key for both would leave "184 items" sitting in the Size column
    /// after the setting changed to ask for bytes, until something evicted it.
    ///
    /// Pulled out of the fetch so it can be said in a test: nothing here can
    /// drive the async fill, so a key computed inline would be a claim with
    /// nothing holding it.
    /// </summary>
    internal static string CacheKey(string path, SizeFill fill)
        => (fill == SizeFill.Measure ? "b:" : "m:") + path;

    /// <summary>
    /// Everything under a folder, totalled, as the Size column wants it.
    ///
    /// **On the pool, because this walks a tree.** Every other fetch here is
    /// already asynchronous at the provider; this one is a synchronous walk in
    /// Core, so it is the one that would otherwise run where the rows are
    /// drawn — and it is the row fetch that can take seconds rather than
    /// milliseconds. The token is the same one the row cancels when it scrolls
    /// away, so a folder nobody is looking at any more stops being walked.
    ///
    /// Null for a folder it could not read at all, which keeps the em dash —
    /// the same answer a count that fails gives. A tree only partly readable
    /// still returns its total: what <see cref="Core.FileSystem.SpaceUsage"/>
    /// could not open is counted separately, and a Size column has no room to
    /// say so. The listing built to say it is Show space usage.
    /// </summary>
    private static async Task<string?> MeasureAsync(string path, CancellationToken ct)
    {
        // A row for /proc or /sys in a listing of "/" is not measured: it is
        // one of many rows, not a folder anybody asked about.
        if (Core.FileSystem.SafeWalk.DoNotEnter?.Invoke(path) == true) return null;

        try
        {
            var usage = await Task.Run(
                () => Core.FileSystem.SpaceUsage.Measure(path, progress: null, ct), ct)
                .ConfigureAwait(true);

            // **Nothing read is not zero bytes.** Measure answers a folder it
            // could not open at all with an empty total and one unreadable,
            // and that drew "0 B" — for System Volume Information, or /root —
            // where the doc above promises the em dash.
            if (usage is { Files: 0, Folders: 0, Unreadable: > 0 }) return null;

            return ByteSize.Format(usage.Bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async void OnEntryChanged(TextBlock target, FileEntry? entry, bool access)
    {
        var slot = access ? AccessTokenProperty : TokenProperty;

        if (target.GetValue(slot) is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        target.SetValue(slot, cts);
        var token = cts.Token;

        // async void: nothing may escape, or scrolling crashes the app.
        try
        {
            target.Text = "";

            if (Provider is null || entry is not { } value) return;

            // "Show no size" for folders. Files keep their inline fact — this
            // setting is about folders, which are the ones whose size costs
            // something to work out.
            if (!access && value.IsDirectory
                && Settings.AppSettings.Current.Views.Details.FolderSize
                    == Core.Settings.FolderSizeMode.None) return;

            if (!access && !Provider.CanDescribe(value.FullPath, value.IsDirectory)) return;

            // Prefixed so the two facts about one path do not share a slot.
            var key = (access ? "a:" : "m:") + value.FullPath;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    target.Text = cached ?? "";
                    return;
                }
            }

            var described = await (access
                    ? Provider.DescribeAccessAsync(value.FullPath, value.IsDirectory, token)
                    : Provider.DescribeAsync(value.FullPath, value.IsDirectory, token))
                .ConfigureAwait(true);

            Remember(key, described);

            // The container may have been recycled onto another file while we
            // were reading; only paint if it still wants this one.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var current = access ? GetAccess(target) : GetEntry(target);
                if (current?.FullPath == value.FullPath) target.Text = described ?? "";
            });
        }
        catch (OperationCanceledException)
        {
            // The row scrolled away.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] metadata failed: {ex.Message}");
        }
    }

    private static void Remember(string key, string? value)
    {
        lock (Gate)
        {
            if (!Cache.TryAdd(key, value)) return;

            Order.Enqueue(key);
            while (Order.Count > MaxCached) Cache.Remove(Order.Dequeue());
        }
    }
}
