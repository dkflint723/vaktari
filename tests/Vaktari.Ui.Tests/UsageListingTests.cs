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
    /// The command takes the pane to what is using the space in the folder it
    /// is standing in.
    ///
    /// **The address bar is left saying something else on purpose.** With it
    /// agreeing, this could not tell the folder the pane is IN from the text
    /// somebody has typed over it: measured, taking the typed text instead
    /// passed this test unchanged. A half-typed path must not decide what gets
    /// measured.
    /// </summary>
    [AvaloniaFact]
    public async Task The_command_measures_the_folder_the_pane_is_in()
    {
        File_("loose.bin", 10);

        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);

        pane.PathText = Path.Combine(_root, "half-typed");

        await pane.ShowSpaceUsageAsync();

        Assert.Equal(VirtualPaths.Usage(_root), pane.CurrentPath);
        Assert.True(pane.IsUsageListing);
    }

    /// <summary>
    /// **The total is published when the walk ends.** It is worked out on the
    /// walking thread and no row carries it, so it is the one figure that can
    /// be computed correctly and then dropped on the floor with nothing on
    /// screen any the wiser.
    /// </summary>
    [AvaloniaFact]
    public async Task The_total_is_published_when_the_measurement_finishes()
    {
        File_("loose.bin", 10);
        File_(Path.Combine("big", "a.bin"), 20);

        var pane = await Measuring(_root);

        Assert.Equal(30, pane.UsageTotal.Bytes);
        Assert.Equal(2, pane.UsageTotal.Files);
        Assert.Equal(1, pane.UsageTotal.Folders);

        Assert.Contains("30 B", pane.UsageTotalLine);
        Assert.Contains("1 folder", pane.UsageTotalLine);
        Assert.Contains("2 files", pane.UsageTotalLine);
    }

    /// <summary>
    /// **And what it could not read, which is the half no row can carry.** A
    /// total short by whatever sat behind a denied folder looks exactly like an
    /// exact one; saying so is the whole reason the measurement keeps a count.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1, "1 folder could not be read")]
    [InlineData(2, "2 folders could not be read")]
    public void The_line_says_what_could_not_be_read(int unreadable, string expected)
    {
        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        pane.UsageTotal = new Usage(1024, 3, 1, unreadable);

        Assert.Contains(expected, pane.UsageTotalLine);
    }

    /// <summary>And it says nothing at all when everything was readable.</summary>
    [AvaloniaFact]
    public void The_line_stays_quiet_when_everything_was_read()
    {
        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        pane.UsageTotal = new Usage(1024, 3, 1, 0);

        Assert.DoesNotContain("could not be read", pane.UsageTotalLine);
    }

    /// <summary>
    /// The band that carries it, gated on this listing and on nothing else.
    /// </summary>
    [AvaloniaFact]
    public void The_band_is_shown_only_in_a_usage_listing()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        Assert.Contains("x:Name=\"UsageBand\"", markup);
        Assert.Contains("IsVisible=\"{Binding IsUsageListing}\"", markup);
        Assert.Contains("Text=\"{Binding UsageTotalLine}\"", markup);
    }

    /// <summary>
    /// **The row is not offered outside a real folder**, so the refusal above
    /// is a backstop rather than the only thing standing between a person and a
    /// measurement of nothing.
    ///
    /// Read out of the markup, because the gate IS the markup: measured, the
    /// binding could be swapped for any other property and every test stayed
    /// green. Scoped to this one element — <c>IsRealFolder</c> gates other rows
    /// too, so a search of the whole file would pass with this row's gate gone.
    /// </summary>
    [AvaloniaFact]
    public void The_row_is_offered_only_in_a_real_folder()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");
        var at = markup.IndexOf("Header=\"Show space usage\"", StringComparison.Ordinal);

        Assert.True(at >= 0, "the listing menu no longer offers the row at all");

        var row = markup[at..];

        row = row[..row.IndexOf("/>", StringComparison.Ordinal)];

        Assert.Contains("IsVisible=\"{Binding ActiveTab.IsRealFolder}\"", row);
        Assert.Contains("Command=\"{Binding ActiveTab.ShowSpaceUsageCommand}\"", row);
    }

    /// <summary>
    /// **From a view it refuses rather than measuring the wrong thing.** A
    /// search, the bin, This PC and Recent hold rows from anywhere, so there is
    /// no one folder to measure — and a path built from the scheme of another
    /// view would name a folder that does not exist.
    /// </summary>
    [AvaloniaFact]
    public async Task From_a_view_it_refuses_rather_than_measuring_nothing()
    {
        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(VirtualPaths.Computer);
        await pane.ShowSpaceUsageAsync();

        Assert.Equal(VirtualPaths.Computer, pane.CurrentPath);
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
