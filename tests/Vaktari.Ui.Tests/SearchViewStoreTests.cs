using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where a search's view is remembered.
///
/// **One record for every search, which is what the constant was written to
/// give.** VirtualPaths.SearchViewKey exists so the per-folder view store does
/// not gain a record for every search anybody runs — and nothing used it. The
/// pane read and wrote under the search's own path, which carries the query,
/// the folder, the scope and the case, so each distinct search became a key of
/// its own. SearchPathTests pinned the constant and never its use, which is how
/// the leak outlived it.
///
/// The settings dialog counts these records as folders — "N folders are
/// remembered" — so every search a person ran made that figure one larger.
/// </summary>
public sealed class SearchViewStoreTests : OwnedViewModels
{
    private readonly IFolderViewStore? _storeBefore = PaneViewModel.FolderViews;

    private readonly Vaktari.Core.Settings.SettingsState _settingsBefore =
        Vaktari.Ui.Settings.AppSettings.Current;

    /// <summary>Puts back the two per-process statics this class writes. This
    /// assembly disables parallelisation, but a static left set would reach
    /// every class that runs after.</summary>
    public override void Dispose()
    {
        PaneViewModel.FolderViews = _storeBefore;
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RememberViews(bool on)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(
            before with { General = before.General with { RememberViewPerFolder = on } });
    }

    private async Task<PaneViewModel> Standing(string path)
    {
        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(path);

        return pane;
    }

    /// <summary>
    /// **Two different questions, one record.** Different words, a different
    /// scope and a different case — everything a search path carries — and the
    /// store still sees a single key.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_different_searches_share_one_view_record()
    {
        var store = new Recording();

        PaneViewModel.FolderViews = store;
        RememberViews(true);

        var folder = Path.GetTempPath();

        (await Standing(VirtualPaths.Search("alpha", folder, scoped: true))).RememberFolderView();
        (await Standing(VirtualPaths.Search("beta", folder, scoped: false, matchCase: true))).RememberFolderView();

        Assert.NotEmpty(store.Written);
        Assert.Equal([VirtualPaths.SearchViewKey], store.Written.Distinct());
    }

    /// <summary>
    /// **And a folder still gets a record of its own** — the half that says
    /// this collapsed searches, not the store. Compared by exclusion rather than
    /// by the path itself, because loading a folder normalises its spelling
    /// before anything is written.
    /// </summary>
    [AvaloniaFact]
    public async Task An_ordinary_folder_still_keeps_its_own_record()
    {
        var store = new Recording();

        PaneViewModel.FolderViews = store;
        RememberViews(true);

        (await Standing(Path.GetTempPath())).RememberFolderView();

        var key = Assert.Single(store.Written);

        Assert.NotEqual(VirtualPaths.SearchViewKey, key);
        Assert.False(VirtualPaths.IsVirtual(key));
    }

    /// <summary>Remembers which keys were written, and nothing else.</summary>
    private sealed class Recording : IFolderViewStore
    {
        public List<string> Written { get; } = [];

        public FolderViewState? Read(string path) => null;

        public void Write(string path, FolderViewState state) => Written.Add(path);

        public void Forget(string path) { }

        public int ForgetAll()
        {
            var had = Written.Count;

            Written.Clear();

            return had;
        }

        public int Remembered => Written.Count;
    }

    /// <summary>Yields nothing: the pane needs a provider, and remembering a view
    /// asks it nothing.</summary>
    private sealed class Silent : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
