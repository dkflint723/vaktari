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
    /// What the size cell says, and whether a count still has to be fetched.
    ///
    /// Pure and synchronous so the decision can be read without a provider, a
    /// dispatcher or a control behind it.
    ///
    /// ContentSize is deliberately treated as ItemCount: the providers only
    /// count, the settings dialog cannot reach that mode, and the view model
    /// preserves it rather than writing it.
    /// </summary>
    public static (string Text, bool Counting) SizeCell(
        FileEntry entry, Core.Settings.FolderSizeMode folders)
    {
        // A default FileEntry reaches a recycled container. The converter this
        // replaced guarded the same case.
        if (entry.FullPath is null) return ("", false);

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
            return (entry.Length > 0 ? ByteSize.Format(entry.Length) : "\u2014", false);

        // The sixth and last copy of this. It was the only one already using
        // binary unit names, which is why the Size column and the status bar
        // beside it once disagreed about the same file.
        if (!entry.IsDirectory) return (ByteSize.Format(entry.Length), false);

        if (folders == Core.Settings.FolderSizeMode.None) return ("\u2014", false);

        // The em dash is the placeholder while the count is in flight, and what
        // stays if the folder cannot be read.
        return ("\u2014", true);
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

            var (text, counting) = SizeCell(value, Settings.AppSettings.Current.Views.Details.FolderSize);

            target.Text = text;

            if (!counting || Provider is null) return;
            if (!Provider.CanDescribe(value.FullPath, isDirectory: true)) return;

            // The same key the Entry path uses: it is literally the same call
            // on the same path.
            var key = "m:" + value.FullPath;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    if (cached is { Length: > 0 }) target.Text = cached;
                    return;
                }
            }

            var counted = await Provider
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
