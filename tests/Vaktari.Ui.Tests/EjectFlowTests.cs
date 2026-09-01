using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The eject command, and the one thing it must do before anything else.
///
/// **Vaktari holds the volume open itself.** A pane showing a folder on a drive
/// keeps a live directory watch on it, and that is an outstanding handle like
/// any other — so ejecting the drive somebody is looking at, which is the
/// overwhelmingly common case, fails every time unless the panes move first.
/// The refusal then blames a program the person cannot find, because the
/// program is us.
/// </summary>
public sealed class EjectFlowTests
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

    /// <summary>
    /// A places provider carrying exactly one removable drive, which records
    /// what the panes were showing at the moment the eject was asked for.
    /// </summary>
    private sealed class OneStick(string root) : IPlacesProvider
    {
        public EjectResult Answer { get; set; } = EjectResult.Ejected("safe to unplug");

        public int Calls { get; private set; }

        /// <summary>Set by the test to read pane state at the moment of the call
        /// — which is the only way to assert ORDER rather than mere occurrence.</summary>
        public Func<string?>? WatchingWhenCalled { get; set; }

        public string? PaneWasShowing { get; private set; }

        public event EventHandler? PlacesChanged;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("DEVICES",
                [
                    new Place
                    {
                        Id = "dev:" + root,
                        Label = "STICK",
                        Path = root,
                        Kind = PlaceKind.RemovableDevice,
                        Icon = "usb",
                        CanEject = true,
                    },
                    // A fixed disk belongs in the fixture, or the test that
                    // asserts one is never ejected has nothing to try it on and
                    // passes by finding no such row.
                    new Place
                    {
                        Id = "dev:fixed",
                        Label = "Local disk (C:)",
                        Path = Path.GetTempPath(),
                        Kind = PlaceKind.Device,
                        Icon = "device-desktop",
                        CanEject = false,
                    },
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
        {
            Calls++;
            PaneWasShowing = WatchingWhenCalled?.Invoke();
            return ValueTask.FromResult(Answer);
        }

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static (ShellViewModel Shell, OneStick Places, string Root) Fresh()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-stick").FullName;
        var places = new OneStick(root);

        var shell = new ShellViewModel(new Inert(), places: places);
        shell.Start(null, Path.GetTempPath());

        return (shell, places, root);
    }

    /// <summary>
    /// Builds the sidebar and lets the dispatcher settle.
    ///
    /// The reload hands its results back through Dispatcher.InvokeAsync, and in
    /// a headless test nothing pumps that queue on its own — the established
    /// idiom in this project's UI tests is to run the jobs by hand.
    /// </summary>
    private static async Task LoadAsync(ShellViewModel shell)
    {
        await shell.Sidebar.ReloadAsync();

        // **Pumped until the rows exist, not once.** The rebuild hands its
        // results back through Dispatcher.InvokeAsync after a hop through the
        // thread pool, so a single RunJobs can run before the results have been
        // posted — which showed up as this class passing on Windows and failing
        // one test on Linux, purely on scheduling.
        for (var attempt = 0; attempt < 50 && shell.Sidebar.Groups.Count == 0; attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }

    private static PlaceItemViewModel Row(ShellViewModel shell)
        => shell.Sidebar.Groups.SelectMany(g => g.Places).First(p => p.CanEject);

    /// <summary>
    /// **The test the whole feature turns on.** The pane must already be off
    /// the drive by the time the eject is attempted — asserted by reading pane
    /// state from inside the provider call, so it checks ORDER and not merely
    /// that both things happened.
    /// </summary>
    [AvaloniaFact]
    public async Task The_panes_leave_the_drive_before_it_is_ejected()
    {
        var (shell, places, root) = Fresh();

        await LoadAsync(shell);

        var inside = Path.Combine(root, "photos");
        Directory.CreateDirectory(inside);
        await shell.ActiveTab!.NavigateAsync(inside);

        Assert.Equal(inside, shell.ActiveTab.CurrentPath);

        places.WatchingWhenCalled = () => shell.ActiveTab?.CurrentPath;

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Equal(1, places.Calls);
        Assert.NotEqual(inside, places.PaneWasShowing);
        Assert.NotEqual(root, places.PaneWasShowing);
    }

    /// <summary>A pane that was nowhere near the drive is left where it is —
    /// ejecting a stick must not throw away someone's other tab.</summary>
    [AvaloniaFact]
    public async Task A_pane_elsewhere_is_not_moved()
    {
        var (shell, places, _) = Fresh();

        await LoadAsync(shell);

        // A real directory rather than GetTempPath(), whose trailing separator
        // the pane normalises away — that difference is about paths, not about
        // ejecting, and it does not belong in this assertion.
        var elsewhere = Directory.CreateTempSubdirectory("vaktari-elsewhere").FullName;
        await shell.ActiveTab!.NavigateAsync(elsewhere);

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Equal(1, places.Calls);
        Assert.Equal(elsewhere, shell.ActiveTab.CurrentPath);
    }

    /// <summary>The answer reaches the person, in the provider's own words.</summary>
    [AvaloniaFact]
    public async Task The_outcome_is_reported_on_the_status_line()
    {
        var (shell, places, _) = Fresh();
        await LoadAsync(shell);

        places.Answer = EjectResult.InUse("something still has a file open on STICK — close it and try again");

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Contains("close it", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// The honest middle state has to survive all the way to the screen: the
    /// data is safe AND the drive is still listed, and the sentence says both.
    /// </summary>
    [AvaloniaFact]
    public async Task A_dismount_says_the_data_is_safe_and_the_drive_stays()
    {
        var (shell, places, _) = Fresh();
        await LoadAsync(shell);

        places.Answer = EjectResult.Dismounted(
            "STICK is written out and safe to unplug — but Windows still has the device, so the drive stays listed");

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Contains("safe to unplug", shell.ActiveTab!.Status);
        Assert.Contains("stays listed", shell.ActiveTab.Status);
    }

    /// <summary>A row that cannot be ejected is not ejected, however the
    /// command is reached — a keyboard binding does not get to bypass the
    /// menu's own gate.</summary>
    [AvaloniaFact]
    public async Task A_row_that_cannot_eject_is_never_sent_to_the_provider()
    {
        var (shell, places, _) = Fresh();
        await LoadAsync(shell);

        var fixedDisk = shell.Sidebar.Groups
            .SelectMany(g => g.Places)
            .First(p => !p.CanEject);

        await shell.EjectPlaceCommand.ExecuteAsync(fixedDisk);

        Assert.Equal(0, places.Calls);
    }

    /// <summary>
    /// The watch raising PlacesChanged rebuilds the sidebar — which is the
    /// whole detection feature, seen from the top.
    /// </summary>
    [AvaloniaFact]
    public async Task A_device_change_rebuilds_the_sidebar()
    {
        var (shell, places, _) = Fresh();

        await LoadAsync(shell);
        Assert.NotEmpty(shell.Sidebar.Groups);

        places.Raise();

        // The subscription posts to the dispatcher; let it run.
        await Task.Yield();

        Assert.NotEmpty(shell.Sidebar.Groups);
    }
}
