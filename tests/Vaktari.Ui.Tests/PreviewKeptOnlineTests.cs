using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The preview pane and a file a sync client keeps online.
///
/// **The preview read such a file, or drew it blank and said nothing.** Its
/// text branch opens the file and reads the head, which downloads it; and a
/// picture came back from the thumbnail loader with no image, since the
/// readers under it decline an online-only file, leaving an empty pane with no
/// reason given. The pane now asks first and says why on its detail line.
///
/// Through the Core seam with a fake answer, and a real file on the disk that
/// DOES hold the text: an empty preview can then only mean it was not read,
/// and the same file answered as on the disk is read — the control that stops
/// "never previews anything" from passing. The thumbnail provider is a counter,
/// so the picture half can say the loader was not even asked.
/// </summary>
public sealed class PreviewKeptOnlineTests : OwnedViewModels
{
    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>Counts what the loader asks, and answers nothing.</summary>
    private sealed class Counting : IThumbnailProvider
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public bool CanThumbnail(string path) => true;

        public ValueTask<string?> GetThumbnailPathAsync(string path, int size, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            return ValueTask.FromResult<string?>(null);
        }
    }

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-preview-online").FullName;
    private readonly Func<string, bool>? _onlineBefore = OnlineOnly.Test;
    private readonly IThumbnailProvider? _providerBefore = ThumbnailLoader.Provider;
    private readonly Counting _thumbnails = new();

    /// <summary>The paths the fake answers "kept online" for.</summary>
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public PreviewKeptOnlineTests()
    {
        OnlineOnly.Test = path => { lock (_held) return _held.Contains(path); };

        ThumbnailLoader.Forget();
        ThumbnailLoader.Provider = _thumbnails;
    }

    public override void Dispose()
    {
        base.Dispose();

        OnlineOnly.Test = _onlineBefore;
        ThumbnailLoader.Provider = _providerBefore;
        ThumbnailLoader.Forget();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static FileEntry Entry(string path)
        => new(Path.GetFileName(path), path, new FileInfo(path).Length, DateTimeOffset.UnixEpoch, EntryFlags.None);

    private void Hold(string path, bool online)
    {
        lock (_held)
        {
            if (online) _held.Add(path);
            else _held.Remove(path);
        }
    }

    /// <summary>A pane with its preview open and <paramref name="path"/> selected.</summary>
    private PaneViewModel Previewing(string path)
    {
        var pane = Own(new PaneViewModel(new Inert()));

        pane.IsPreviewVisible = true;
        pane.SelectedEntry = Entry(path);

        return pane;
    }

    private static async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();

            if (done()) return;

            await Task.Delay(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), what);
    }

    /// <summary>
    /// **A text file kept online is not read, and the pane says why.** Then
    /// the same file, answered as on the disk, is read — so the empty preview
    /// was the check and not a preview that shows nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_text_file_kept_online_is_not_read_and_the_pane_says_why()
    {
        var notes = Write("notes.txt", "remember the milk");
        Hold(notes, online: true);

        var pane = Previewing(notes);

        await Until(() => pane.PreviewDetail == PaneViewModel.KeptOnline,
                    $"the detail line said '{pane.PreviewDetail}'");

        Assert.Equal("", pane.PreviewText);
        Assert.Equal(0, _thumbnails.Asked);

        Hold(notes, online: false);
        pane.SelectedEntry = null;
        pane.SelectedEntry = Entry(notes);

        await Until(() => pane.PreviewText == "remember the milk",
                    $"the file on the disk was not previewed: '{pane.PreviewText}' / '{pane.PreviewDetail}'");

        Assert.NotEqual(PaneViewModel.KeptOnline, pane.PreviewDetail);
    }

    /// <summary>
    /// **A picture kept online says why it is blank**, and the loader is not
    /// asked for it. Answered as on the disk, the loader is asked.
    /// </summary>
    [AvaloniaFact]
    public async Task A_picture_kept_online_is_not_loaded_and_the_pane_says_why()
    {
        var photo = Write("photo.png", "not really a png");
        Hold(photo, online: true);

        var pane = Previewing(photo);

        await Until(() => pane.PreviewDetail == PaneViewModel.KeptOnline,
                    $"the detail line said '{pane.PreviewDetail}'");

        Assert.Null(pane.PreviewImage);
        Assert.Equal(0, _thumbnails.Asked);

        Hold(photo, online: false);
        pane.SelectedEntry = null;
        pane.SelectedEntry = Entry(photo);

        await Until(() => _thumbnails.Asked > 0, "the loader was not asked for a picture on the disk");

        Assert.NotEqual(PaneViewModel.KeptOnline, pane.PreviewDetail);
    }
}
