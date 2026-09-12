using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A pane showing what is using the space in a folder.
///
/// **This is the one virtual listing that measures a real tree**, so unlike the
/// bin or This PC it cannot be driven by a fake provider: the path carries the
/// folder, and the listing walks it. The folders below are this class's own,
/// made in the temp directory and removed with it.
/// </summary>
public sealed class UsageListingTests : OwnedViewModels
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-usage-listing").FullName;

    private readonly IFolderViewStore? _storeBefore = PaneViewModel.FolderViews;
    private readonly Vaktari.Core.Settings.SettingsState _settingsBefore =
        Vaktari.Ui.Settings.AppSettings.Current;

    /// <summary>
    /// Puts back the two per-process statics this class writes, and removes the
    /// folders it made. This assembly disables parallelisation, so the window
    /// between them cannot be observed by another class — but a static left set
    /// would reach every class that runs after.
    /// </summary>
    public override void Dispose()
    {
        PaneViewModel.FolderViews = _storeBefore;
        Vaktari.Ui.Settings.AppSettings.Apply(_settingsBefore);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RememberViews(bool on)
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        Vaktari.Ui.Settings.AppSettings.Apply(
            before with { General = before.General with { RememberViewPerFolder = on } });
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);

        return path;
    }

    private void File_(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }

    private async Task<PaneViewModel> Measuring(string folder)
    {
        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(VirtualPaths.Usage(folder));

        // The listing lands on the dispatcher. Wait on the pane saying it has
        // loaded rather than on a count of turns, under a wall-clock ceiling so
        // a hang fails instead of spinning.
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!pane.IsLoaded && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(pane.IsLoaded, "the measurement never finished");

        return pane;
    }

    [AvaloniaFact]
    public async Task A_row_arrives_for_each_child_carrying_what_is_under_it()
    {
        File_("loose.bin", 10);
        File_(Path.Combine("big", "a.bin"), 1_000);
        File_(Path.Combine("big", "deeper", "b.bin"), 500);

        var pane = await Measuring(_root);

        var big = Assert.Single(pane.Entries, e => e.Name == "big");

        Assert.Equal(1_500, big.Length);
        Assert.True(big.IsDirectory);

        // Without this the Size column shows an em dash for the one row the
        // listing exists to put a number on.
        Assert.True(big.IsMeasured);

        Assert.Equal(10, Assert.Single(pane.Entries, e => e.Name == "loose.bin").Length);
    }

    /// <summary>
    /// **The biggest thing is at the top when sorted by size**, folder or file.
    /// Folders first is a browsing convention and it answers the question this
    /// listing exists to ask: with it, the 1 KiB folder would sort above the
    /// 9 KiB file whatever the column said.
    /// </summary>
    [AvaloniaFact]
    public async Task Sorting_by_size_puts_the_biggest_first_whatever_it_is()
    {
        File_("huge.bin", 9_000);
        File_(Path.Combine("small", "a.bin"), 1_000);

        var pane = await Measuring(_root);

        pane.Sort = SortField.Size;
        pane.SortDescending = true;

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (pane.Entries.FirstOrDefault().Name != "huge.bin" && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal(
            ["huge.bin", "small"],
            pane.Entries.Select(e => e.Name));
    }

    /// <summary>
    /// It is a view, not a folder, so everything gated on
    /// <c>IsRealFolder</c> — compare, copy across, properties, pinning, the
    /// watcher — refuses it as it refuses the bin.
    /// </summary>
    [AvaloniaFact]
    public async Task It_is_a_view_rather_than_a_folder()
    {
        File_("loose.bin", 10);

        var pane = await Measuring(_root);

        Assert.True(pane.IsUsageListing);
        Assert.False(pane.IsRealFolder);
    }

    /// <summary>
    /// **There has to be a way back to the folder.** Up is refused for every
    /// virtual path and the single breadcrumb's command does nothing, so
    /// without this row Back is the only way out — and Back is gone the moment
    /// the person goes anywhere else first.
    /// </summary>
    [AvaloniaFact]
    public async Task The_folder_it_measured_can_be_gone_to()
    {
        File_("loose.bin", 10);

        var pane = await Measuring(_root);

        Assert.True(pane.CanGoToLocation);
        Assert.Equal(_root, pane.UsageFolder);
    }

    /// <summary>
    /// A measured folder with nothing in it is not "this folder is empty" about
    /// a folder the person is not looking at — the label's catch-all would have
    /// said exactly that.
    /// </summary>
    [AvaloniaFact]
    public async Task An_empty_folder_says_what_is_actually_empty()
    {
        var pane = await Measuring(Dir("nothing"));

        Assert.Empty(pane.Entries);
        Assert.Equal("nothing is using space here", pane.EmptyText);
    }

    /// <summary>
    /// **Every usage listing is remembered under one key.** The path carries
    /// the folder that was measured, so keyed as it is spelled, a person who
    /// looked at fifty folders would leave fifty records in a store kept for
    /// the life of the profile and reachable again by none of them. An ordinary
    /// folder still gets its own, which is the half saying this did not
    /// collapse the store itself.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_usage_listing_is_remembered_under_one_key()
    {
        var store = new Recording();

        PaneViewModel.FolderViews = store;
        RememberViews(true);

        (await Measuring(Dir("one"))).RememberFolderView();
        (await Measuring(Dir("two"))).RememberFolderView();

        Assert.Equal([VirtualPaths.UsageViewKey], store.Written.Distinct());

        var plain = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await plain.NavigateAsync(_root);
        plain.RememberFolderView();

        Assert.Contains(_root, store.Written);
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

    /// <summary>One folder, yielding nothing: the pane needs a provider, and a
    /// usage listing never asks it anything.</summary>
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
