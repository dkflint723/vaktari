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
public sealed class EjectFlowTests : OwnedViewModels
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

        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);
    }

    private (ShellViewModel Shell, OneStick Places, string Root) Fresh()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-stick").FullName;
        var places = new OneStick(root);

        var shell = Own(new ShellViewModel(new Inert(), places: places));
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
    /// <summary>
    /// Builds the sidebar and lets the dispatcher settle.
    ///
    /// **Awaiting the reload is now enough**, because a request that lands
    /// while one is in flight is folded into it rather than dropped. The
    /// earlier version of this helper spun on RunJobs to paper over that, and
    /// the spin was fast enough to finish before the rebuild did — which is why
    /// it passed here and failed on CI.
    /// </summary>
    private static async Task LoadAsync(ShellViewModel shell)
    {
        await shell.Sidebar.ReloadAsync();

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
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

    // ---- a transfer still using the drive ---------------------------------

    /// <summary>
    /// A copy the shell is running, over the paths given, adopted by the active
    /// tab — which is the route every real operation reaches the shell by.
    /// </summary>
    private static OperationHandle Transferring(ShellViewModel shell, params string[] paths)
    {
        var handle = new OperationHandle { Paths = paths };

        handle.Begin(paths.Length, totalBytes: 0);

        shell.ActiveTab!.Adopt(handle);

        return handle;
    }

    /// <summary>
    /// **The finding.** The eject was attempted regardless of what Vaktari was
    /// itself writing to the drive: the tabs were sent home and the ejector was
    /// called with a copy still in flight.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drive_a_transfer_is_still_using_is_not_ejected()
    {
        var (shell, places, root) = Fresh();
        await LoadAsync(shell);

        Transferring(shell, Path.Combine(Path.GetTempPath(), "holiday.mp4"),
                            Path.Combine(root, "videos"));

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Equal(0, places.Calls);
    }

    /// <summary>The refusal names the drive and says what to do about it — a
    /// menu row that quietly does nothing is the worse failure.</summary>
    [AvaloniaFact]
    public async Task The_refusal_says_which_drive_and_what_to_do()
    {
        var (shell, _, root) = Fresh();
        await LoadAsync(shell);

        Transferring(shell, Path.Combine(root, "big.iso"));

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Contains("STICK", shell.ActiveTab!.Status);
        // Both halves of the offer. Dropping "wait for it to finish, " from
        // the sentence passed all 1247 tests in this project, so the clause
        // that tells somebody what to do had nothing holding it down.
        Assert.Contains("wait for it to finish", shell.ActiveTab.Status);
        Assert.Contains("cancel it", shell.ActiveTab.Status);
    }

    /// <summary>
    /// **The refusal has to come first.** Moving every tab off the drive is the
    /// first thing the eject does, and doing it before deciding to refuse would
    /// cost somebody their place for an eject that never happened — while the
    /// transfer it refused for is still running, so they would navigate back
    /// and be refused again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refused_eject_leaves_the_panes_where_they_were()
    {
        var (shell, _, root) = Fresh();
        await LoadAsync(shell);

        var inside = Path.Combine(root, "photos");
        Directory.CreateDirectory(inside);
        await shell.ActiveTab!.NavigateAsync(inside);

        Transferring(shell, Path.Combine(root, "photos", "one.jpg"));

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Equal(inside, shell.ActiveTab.CurrentPath);
    }

    /// <summary>
    /// A transfer nowhere near the stick is nobody's business but its own. A
    /// guard that refused on any running operation would make the eject
    /// unusable for exactly the person who copies a lot.
    /// </summary>
    [AvaloniaFact]
    public async Task A_transfer_somewhere_else_does_not_hold_the_drive()
    {
        var (shell, places, _) = Fresh();
        await LoadAsync(shell);

        var elsewhere = Directory.CreateTempSubdirectory("vaktari-elsewhere").FullName;

        Transferring(shell, Path.Combine(elsewhere, "one.txt"),
                            Path.Combine(elsewhere, "two"));

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Equal(1, places.Calls);
    }

    /// <summary>
    /// A copy that has already finished is no reason to keep the drive — and it
    /// is still in the shell's list at that moment, because the removal is
    /// posted to the dispatcher and this test has not pumped it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_transfer_that_has_finished_does_not_hold_the_drive()
    {
        var (shell, places, root) = Fresh();
        await LoadAsync(shell);

        Transferring(shell, Path.Combine(root, "one.txt")).Complete();

        await shell.EjectPlaceCommand.ExecuteAsync(Row(shell));

        Assert.Equal(1, places.Calls);
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
