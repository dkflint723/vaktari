using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The number on a drive row, and how long it stayed true.
///
/// **It was measured once and then left alone.** A Place carries the capacity
/// and the free space the provider read while it was building the list, and the
/// sidebar rebuilds only when the PLACES change — a stick plugged in, a pin, a
/// mount, an eject. Copying forty gibibytes onto a drive is none of those, so
/// the row and the bar under it went on reporting the space free before the
/// copy started, and the only way to correct them was to plug something in.
///
/// **And what it did report could not be read as a proportion.** The tooltip
/// said "154 GiB free" with no total anywhere: roomy on a laptop disk, nearly
/// full on a sixteen-tebibyte array, and the row draws a used-fraction bar with
/// no number saying what the fraction is against.
/// </summary>
public sealed class DriveCapacityTests : OwnedViewModels
{
    private const long Tebibyte = 1024L * 1024 * 1024 * 1024;

    /// <summary>
    /// One drive, big and nearly empty, at a path that really exists — the
    /// refresh reads through DriveInfo and a made-up root would only ever be
    /// the "would not say" branch.
    /// </summary>
    private sealed class OneDrive(bool available = true) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged;

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup(PlaceGroups.Devices,
                [
                    new Place
                    {
                        Id = "dev:test",
                        Label = "Test (T:)",
                        Path = Path.GetTempPath(),
                        Kind = PlaceKind.Device,
                        Icon = "device-desktop",
                        CapacityBytes = 4 * Tebibyte,

                        // The stale figure every test below starts from: one
                        // byte is a number no real volume reports, so a row
                        // still showing it has not been re-measured.
                        FreeBytes = 1,
                        IsAvailable = available,
                    },
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.NotRemovable("nothing to eject"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class Inert : IFileSystemProvider
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
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    /// <summary>A sidebar over the one drive, with the measurement described
    /// rather than taken — which is what makes the assertions exact.</summary>
    private static async Task<(SidebarViewModel Sidebar, PlaceItemViewModel Row)> Fresh(
        Func<string, long?> measure, bool available = true)
    {
        var sidebar = new SidebarViewModel(new OneDrive(available), freeSpace: measure);

        await sidebar.ReloadAsync();
        Dispatcher.UIThread.RunJobs();

        return (sidebar, sidebar.Groups.SelectMany(g => g.Places).Single(p => p.HasCapacity));
    }

    /// <summary>
    /// **Half one of the finding.** The tooltip is the only place with room for
    /// the total, and it did not carry it.
    /// </summary>
    [AvaloniaFact]
    public void The_tooltip_says_what_the_free_space_is_free_of()
    {
        var row = new PlaceItemViewModel(new Place
        {
            Id = "dev:test",
            Label = "Test (T:)",
            Path = Path.GetTempPath(),
            Kind = PlaceKind.Device,
            Icon = "device-desktop",
            CapacityBytes = 4 * Tebibyte,
            FreeBytes = Tebibyte,
        });

        // Both halves formatted by the one ByteSize.Format that This PC's Size
        // column also uses, so the total here and the total there are the same
        // string rather than nearly.
        Assert.Equal("1 TiB free of 4 TiB", row.CapacityText);

        // And the short form beside the label is still only what is left: the
        // row is one line and the total does not fit on it.
        Assert.Equal("1 TiB", row.CapacityShort);
    }

    /// <summary>
    /// **Half two.** The figure the row was built with is replaced by a fresh
    /// measurement, without the places being rebuilt around it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_replaces_the_free_figure_the_row_was_built_with()
    {
        var (sidebar, row) = await Fresh(_ => 2 * Tebibyte);

        Assert.Equal("1 B", row.CapacityShort);

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("2 TiB", row.CapacityShort);
        Assert.Equal("2 TiB free of 4 TiB", row.CapacityText);
        Assert.Equal(0.5, row.UsedFraction, 3);
    }

    /// <summary>
    /// The tooltip and the used-space bar are computed properties, so a fresh
    /// measurement that raises nothing is a measurement nobody sees.
    /// </summary>
    [AvaloniaFact]
    public async Task A_moved_figure_is_announced_to_the_view()
    {
        var (sidebar, row) = await Fresh(_ => 2 * Tebibyte);

        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(PlaceItemViewModel.CapacityText), raised);
        Assert.Contains(nameof(PlaceItemViewModel.CapacityShort), raised);
        Assert.Contains(nameof(PlaceItemViewModel.UsedFraction), raised);
    }

    /// <summary>
    /// A volume that will not answer keeps the figure it had. Blanking the row
    /// on a failed read would turn a momentary refusal into a drive that looks
    /// like it has no size at all.
    /// </summary>
    [AvaloniaFact]
    public async Task A_volume_that_will_not_say_keeps_the_figure_it_had()
    {
        var (sidebar, row) = await Fresh(_ => null);

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 B", row.CapacityShort);
    }

    /// <summary>
    /// **An unreachable share is not measured.** The whole reason the sidebar's
    /// rebuild was moved off the UI thread is that asking a mapped drive whose
    /// server has gone for its size blocks for the SMB timeout, and this runs
    /// after every copy — so a dead mount must not be asked at all.
    /// </summary>
    [AvaloniaFact]
    public async Task An_unavailable_drive_is_not_asked()
    {
        var asked = new List<string>();

        var (sidebar, _) = await Fresh(
            path => { asked.Add(path); return 2 * Tebibyte; },
            available: false);

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(asked);
    }

    /// <summary>
    /// **The wiring, end to end and through the real measurement.** An
    /// operation finishing is the single point every copy, move, delete and
    /// restore passes through, and it is where the bin's glyph is already
    /// refreshed for the same reason.
    ///
    /// No injected reader here on purpose: this is the one test that runs
    /// DriveInfo over a real path, so the default the application actually uses
    /// is covered rather than only the seam.
    /// </summary>
    [AvaloniaFact]
    public async Task Finishing_an_operation_re_measures_the_drives()
    {
        var shell = Own(new ShellViewModel(new Inert(), places: new OneDrive()));
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();
        Dispatcher.UIThread.RunJobs();

        var row = shell.Sidebar.Groups.SelectMany(g => g.Places).Single(p => p.HasCapacity);
        Assert.Equal("1 B", row.CapacityShort);

        shell.ActiveOperation = new OperationHandle();
        shell.ActiveOperation = null;

        // The refresh is not awaited by the property hook — it stats the
        // filesystem on a pool thread and posts the answer back — so this waits
        // for it the way RenameStepTests waits out a reload it did not start.
        for (var i = 0; i < 400 && row.CapacityShort == "1 B"; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.NotEqual("1 B", row.CapacityShort);
    }

    /// <summary>
    /// A row that is not a drive is never stat'ed.
    ///
    /// The predicate is what keeps this off everything else in the sidebar: the
    /// pinned folders have real paths and no capacity, and the bin and the two
    /// recent listings carry an internal scheme instead of a path at all.
    /// Without it, every operation would stat every row.
    /// </summary>
    [AvaloniaFact]
    public async Task Only_the_drive_rows_are_measured()
    {
        var asked = new List<string>();

        var sidebar = new SidebarViewModel(
            new MixedSidebar(),
            freeSpace: path => { asked.Add(path); return 2 * Tebibyte; });

        await sidebar.ReloadAsync();
        Dispatcher.UIThread.RunJobs();

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("T:\\", Assert.Single(asked));
    }

    /// <summary>
    /// The figure is written back on the UI thread.
    ///
    /// The tooltip, the short figure and the bar are all bound, so
    /// <see cref="PlaceItemViewModel.SetFree"/> raises PropertyChanged straight
    /// into the binding system — which is the one thing a pool thread may not
    /// touch. Measured by asking, from inside the handler, whose thread it is.
    /// </summary>
    [AvaloniaFact]
    public async Task The_new_figure_arrives_on_the_ui_thread()
    {
        var (sidebar, row) = await Fresh(_ => 2 * Tebibyte);

        var threads = new List<bool>();
        row.PropertyChanged += (_, _) => threads.Add(Dispatcher.UIThread.CheckAccess());

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(threads);
        Assert.DoesNotContain(false, threads);
    }

    /// <summary>
    /// A figure that has not moved says nothing.
    ///
    /// This runs after every operation, and most operations leave a given
    /// drive exactly where it was — a copy onto C: moves nothing on D:. Each
    /// announcement re-evaluates three bound getters on the row, so a refresh
    /// that found nothing new must be silent rather than nudge every drive in
    /// the sidebar.
    /// </summary>
    [AvaloniaFact]
    public async Task A_figure_that_has_not_moved_is_not_announced_again()
    {
        var (sidebar, row) = await Fresh(_ => 2 * Tebibyte);

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(raised);
    }

    /// <summary>
    /// A sidebar with nothing to measure does not go near the pool.
    ///
    /// This runs on every finished operation, so the machine with no drive row
    /// in its sidebar must not pay a thread-pool item and a dispatcher post per
    /// copy for an empty list.
    /// </summary>
    [AvaloniaFact]
    public void Nothing_to_measure_costs_nothing()
        => Assert.True(new SidebarViewModel(null).RefreshCapacityAsync().IsCompleted);

    /// <summary>
    /// A drive on its way out is not stat'ed either.
    ///
    /// The filter asks ShowCapacity, which is what the markup asks before it
    /// draws the number and the bar. A drive being dismounted has stopped
    /// drawing them, and asking a volume that is going away for its size is
    /// the one question guaranteed to be slow and pointless at once.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drive_being_ejected_is_not_measured()
    {
        var asked = new List<string>();

        var (sidebar, row) = await Fresh(path => { asked.Add(path); return 2 * Tebibyte; });

        row.IsEjecting = true;

        await sidebar.RefreshCapacityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(asked);
    }

    /// <summary>A drive, a pinned folder and the bin — one of each thing the
    /// sidebar holds, so the predicate has something to get wrong.</summary>
    private sealed class MixedSidebar : IPlacesProvider
    {
        public event EventHandler? PlacesChanged;

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup(PlaceGroups.Places,
                [
                    // A real path and no capacity: a pin is a folder, and
                    // asking DriveInfo about it would be asking about the
                    // volume it happens to sit on.
                    new Place
                    {
                        Id = "pin:docs",
                        Label = "Documents",
                        Path = "T:\\Documents",
                        Kind = PlaceKind.Bookmark,
                        Icon = "folder",
                    },

                    // A capacity on an internal scheme. No provider builds this
                    // today — both put a real root on the only places that
                    // carry a size — and the point of the row is that the
                    // filter does not depend on that staying true.
                    new Place
                    {
                        Id = "bin",
                        Label = "Recycle Bin",
                        Path = VirtualPaths.Trash,
                        Kind = PlaceKind.Bookmark,
                        Icon = "trash",
                        CapacityBytes = 4 * Tebibyte,
                        FreeBytes = 1,
                    },
                ]),

                new PlaceGroup(PlaceGroups.Devices,
                [
                    new Place
                    {
                        Id = "dev:T",
                        Label = "Test (T:)",
                        Path = "T:\\",
                        Kind = PlaceKind.Device,
                        Icon = "device-desktop",
                        CapacityBytes = 4 * Tebibyte,
                        FreeBytes = 1,
                    },
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.NotRemovable("nothing to eject"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }
}
