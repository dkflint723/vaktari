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

    /// <summary>The key prefix of a measured total, which is the one answer a
    /// change far below its folder can make wrong.</summary>
    private const string TotalPrefix = "b:";

    /// <summary>
    /// What a key holds, and which path it is an answer about.
    ///
    /// The path is carried beside the text rather than read back out of the
    /// key, because <see cref="Forget(IEnumerable{string})"/> has to match it at a separator and a
    /// key now carries a timestamp as well — a key parsed apart would be a
    /// second spelling of <see cref="CacheKey"/> to keep in step with it.
    /// </summary>
    private readonly record struct Remembered(string Path, string? Text);

    private static readonly Dictionary<string, Remembered> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();

    /// <summary>
    /// How many times anything has been forgotten, under <see cref="Gate"/>. A
    /// fetch reads it before it goes to the disk and keeps its answer only if
    /// it has not moved — see <see cref="Remember"/>.
    /// </summary>
    private static long _forgets;

    /// <summary>
    /// How many passes <see cref="Forget(IEnumerable{string})"/> has made over
    /// the cache. For tests: the answers come out the same whether an
    /// operation's paths are forgotten in one pass or one pass each, and the
    /// difference is the whole of the cost.
    /// </summary>
    internal static int Passes { get; private set; }

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

            var key = CacheKey(value, fill);

            long epoch;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    if (cached.Text is { Length: > 0 }) target.Text = cached.Text;
                    return;
                }

                // Read with the miss, before the walk — see Remember.
                epoch = _forgets;
            }

            var counted = fill == SizeFill.Measure
                ? await MeasureAsync(value.FullPath, token).ConfigureAwait(true)
                : await Provider
                    .DescribeAsync(value.FullPath, isDirectory: true, token)
                    .ConfigureAwait(true);

            Remember(key, value.FullPath, counted, epoch);

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
    ///
    /// **The key was the path alone, so an answer was kept for the whole
    /// session.** Nothing but the count eviction ever removed one, and the
    /// default setting is the item count — so a folder that gained or lost
    /// files went on reading its first "12 items" through F5, through the
    /// watcher, through everything short of a restart. Adding or removing
    /// something moves the folder's own modified time, and the row carries
    /// that time already, so it goes into the key: a folder that has changed
    /// since it was counted is a key nobody has asked about yet. What that
    /// cannot see — a total whose change is several folders down — is
    /// <see cref="Forget(IEnumerable{string})"/>'s.
    /// </summary>
    internal static string CacheKey(FileEntry entry, SizeFill fill)
        => (fill == SizeFill.Measure ? TotalPrefix : "m:")
           + entry.LastWriteTime.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + ":" + entry.FullPath;

    /// <summary>
    /// Drops every answer the cache holds about <paramref name="path"/> or
    /// anything under it, and every measured total that includes it.
    ///
    /// **A total several folders up is the one answer a changed time cannot
    /// reach.** Copying a file into a/b/c moves c's modified time and nothing
    /// above it, so the content size shown for "a" kept its old figure however
    /// often the listing was refreshed. Called when a pane reads its folder
    /// again and when an operation finishes, which are the two moments
    /// something is known to have changed; the totals ABOVE the path go too,
    /// because every one of them counted what was there. Counts above it do
    /// not: a count is of the folder's own entries, and the one folder whose
    /// entries changed has a new time and so a new key.
    ///
    /// Matched at a separator, the platform's way, so forgetting "/a" leaves
    /// "/ab" alone.
    /// </summary>
    public static void Forget(string path) => Forget([path]);

    /// <summary>
    /// <see cref="Forget(string)"/> for every path an operation touched, in
    /// one pass over the cache.
    ///
    /// **A finished operation froze the window once per file.** Its paths are
    /// every source and the destination, and each one was forgotten on its
    /// own: a pass over up to two thousand answers per path, each answer tested
    /// against it with two normalising comparisons, and the order rebuilt
    /// whenever one went — so select-all and delete in a folder of twenty
    /// thousand files cost tens of millions of comparisons on the UI thread
    /// before the listing could come back.
    ///
    /// Now the paths are normalised once, into a set, with every folder that
    /// holds one of them in a second set; and each answer asks only about its
    /// own path and the folders above it, which is a handful of lookups however
    /// many paths there are. The order is rebuilt once, if anything went.
    ///
    /// **And an answer still being fetched is not kept once it lands** — see
    /// <see cref="Remember"/>, which this moves the epoch on for.
    /// </summary>
    public static void Forget(IEnumerable<string> paths)
    {
        // The paths, and every folder that holds one of them: a total there
        // counted what was there. Walked upwards only as far as a folder some
        // earlier path already put in, which for siblings is one step.
        var named = new HashSet<string>(PathRules.Comparer);
        var holding = new HashSet<string>(PathRules.Comparer);

        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;

            var normal = PathRules.Normalise(path);

            named.Add(normal);

            var level = normal;

            while (level is not null && holding.Add(level)) level = PathRules.Parent(level);
        }

        lock (Gate)
        {
            // Moved on even when nothing is held: the answer this is about may
            // be the one still being fetched.
            _forgets++;

            Passes++;

            if (named.Count == 0) return;

            var stale = new List<string>();

            foreach (var (key, remembered) in Cache)
            {
                var at = PathRules.Normalise(remembered.Path);

                if (key.StartsWith(TotalPrefix, StringComparison.Ordinal) && holding.Contains(at))
                {
                    stale.Add(key);
                    continue;
                }

                for (var level = at; level is not null; level = PathRules.Parent(level))
                {
                    if (!named.Contains(level)) continue;

                    stale.Add(key);
                    break;
                }
            }

            if (stale.Count == 0) return;

            foreach (var key in stale) Cache.Remove(key);

            // The order goes with them. A key left queued behind its entry
            // would be queued twice once it was remembered again, and the
            // older copy reaching the front would evict the fresh answer.
            //
            // GUARD, not a tested rule: what it prevents shows only past
            // MaxCached answers, and no test fills two thousand cells.
            var kept = Order.Where(Cache.ContainsKey).ToList();

            Order.Clear();

            foreach (var key in kept) Order.Enqueue(key);
        }
    }

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

            // Prefixed so the two facts about one path do not share a slot. The
            // description is the count's own call on the same path, so it
            // shares the count's key — modified time and all.
            var key = access ? "a:" + value.FullPath : CacheKey(value, SizeFill.Count);

            long epoch;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    target.Text = cached.Text ?? "";
                    return;
                }

                epoch = _forgets;
            }

            var described = await (access
                    ? Provider.DescribeAccessAsync(value.FullPath, value.IsDirectory, token)
                    : Provider.DescribeAsync(value.FullPath, value.IsDirectory, token))
                .ConfigureAwait(true);

            Remember(key, value.FullPath, described, epoch);

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

    /// <summary>
    /// Whether an answer is kept under this key.
    ///
    /// For tests: a fill whose answer is to leave the em dash standing changes
    /// nothing on screen, so without this a test could not tell "finished and
    /// left it" from "not finished yet".
    /// </summary>
    internal static bool Holds(string key)
    {
        lock (Gate) return Cache.ContainsKey(key);
    }

    /// <summary>
    /// Keeps an answer, unless something was forgotten while it was being
    /// fetched.
    ///
    /// **A measure still walking when its tree changed put the old total
    /// back.** Forget drops what the cache holds, and an answer still being
    /// fetched is not held yet — so a copy into a/b/c that finished while the
    /// Size column was walking "a" forgot nothing, and the walk then kept its
    /// total, taken before the copy, under a key nothing would move: a's own
    /// modified time does not change for a file three levels down. Every later
    /// fill of that row, in either pane, served it until the next F5.
    ///
    /// <paramref name="epoch"/> is <see cref="_forgets"/> as it was when the
    /// fetch missed the cache. Any Forget since, not only one that matches this
    /// path: telling the two apart would mean keeping every forgotten path,
    /// and a fetch lost to an unrelated one costs only the fetch — the answer
    /// is still painted, and the next fill asks again.
    /// </summary>
    private static void Remember(string key, string path, string? value, long epoch)
    {
        lock (Gate)
        {
            if (_forgets != epoch) return;

            if (!Cache.TryAdd(key, new Remembered(path, value))) return;

            Order.Enqueue(key);
            while (Order.Count > MaxCached) Cache.Remove(Order.Dequeue());
        }
    }
}
