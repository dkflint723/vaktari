using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Clicking a volume that is present but not mounted.
///
/// **The provider half of this shipped alone.** Unmounted volumes were listed,
/// dimmed, with an empty Path because there is no folder to open yet, and
/// MountAsync was implemented and tested on both platforms — and nothing in the
/// application ever called it. The row's command navigated to the Path, so the
/// click met an empty-path guard and did nothing at all.
///
/// The provider is a fake, so these run on any machine: the real one needs a
/// second partition nobody has mounted, which is exactly the thing a test box
/// does not have.
/// </summary>
public sealed class MountOnClickTests : OwnedViewModels
{
    private static readonly XNamespace Axaml = "https://github.com/avaloniaui";

    /// <summary>Long enough that a loaded agent never trips it, short enough
    /// that a regression is a red test inside the minute.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

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
    /// One pinned folder and one volume, which is unmounted until it is
    /// mounted — the shape LinuxPlacesProvider builds out of /proc/mounts and
    /// the device list, without either.
    /// </summary>
    private sealed class OneVolume(string home, string mountPoint) : IPlacesProvider
    {
        /// <summary>The id the unmounted row carries.</summary>
        public const string Waiting = "unmounted:/dev/sdb1";

        private bool _mounted;

        /// <summary>A desktop with no mount helper: the call is made and the
        /// volume stays exactly where it was.</summary>
        public bool Refuses { get; set; }

        /// <summary>Brings up a second volume alongside the one asked for — a
        /// card reader with two slots, which is the case the shell must not
        /// guess between.</summary>
        public bool MountsTwo { get; set; }

        /// <summary>Brings up a place that has a path and cannot be reached —
        /// a Windows share that reconnects while a mount is in flight. It is
        /// not somewhere to send anybody.</summary>
        public bool AlsoUnreachable { get; set; }

        public int Calls { get; private set; }
        public string? AskedFor { get; private set; }

        /// <summary>Says the provider has been entered, so a test can act while
        /// a mount is genuinely in flight rather than after a sleep.</summary>
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the mount open. Already completed, so every test that
        /// does not care is unaffected.</summary>
        public TaskCompletionSource Gate { get; set; } = Opened();

        private static TaskCompletionSource Opened()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        public event EventHandler? PlacesChanged;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
        {
            var rows = new List<Place>
            {
                new()
                {
                    Id = "pin:" + home,
                    Label = "work",
                    Path = home,
                    Kind = PlaceKind.Bookmark,
                    Icon = "folder",
                    IsUserPinned = true,
                },
            };

            if (!_mounted)
            {
                rows.Add(new Place
                {
                    Id = Waiting,
                    Label = "STICK",
                    Path = "",
                    Kind = PlaceKind.RemovableDevice,
                    Icon = "device-drive",
                    IsAvailable = false,
                    CanMount = true,
                });
            }
            else
            {
                rows.Add(Landed("STICK", mountPoint));

                if (MountsTwo) rows.Add(Landed("CARD", mountPoint + "-two"));

                if (AlsoUnreachable)
                {
                    rows.Add(Landed("archive", mountPoint + "-share") with
                    {
                        IsAvailable = false,
                    });
                }
            }

            return ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
                [new PlaceGroup("DEVICES", rows)]);
        }

        private static Place Landed(string label, string at) => new()
        {
            Id = "dev:" + at,
            Label = label,
            Path = at,
            Kind = PlaceKind.RemovableDevice,
            Icon = "usb",
            CanEject = true,
        };

        public async ValueTask MountAsync(string id, CancellationToken ct)
        {
            Calls++;
            AskedFor = id;
            Entered.TrySetResult();

            await Gate.Task.ConfigureAwait(false);

            if (!Refuses && id == Waiting) _mounted = true;

            // The real provider raises this whether or not the mount worked, so
            // the reload the sidebar does on top of its own is part of the
            // shape under test rather than something the fake spares it.
            PlacesChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.Failed("not what this test is about"));

        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private (ShellViewModel Shell, OneVolume Places, string Home, string MountPoint) Fresh()
    {
        var home = Directory.CreateTempSubdirectory("vaktari-home").FullName;
        var mountPoint = Directory.CreateTempSubdirectory("vaktari-stick").FullName;
        var places = new OneVolume(home, mountPoint);

        var shell = Own(new ShellViewModel(new Inert(), places: places));

        // Somewhere that is neither of the two the tests navigate to, so an
        // assertion about where the pane ended up cannot be satisfied by a
        // pane that never moved.
        shell.Start(null, Directory.CreateTempSubdirectory("vaktari-start").FullName);

        return (shell, places, home, mountPoint);
    }

    private static async Task LoadAsync(ShellViewModel shell)
    {
        await shell.Sidebar.ReloadAsync();

        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static PlaceItemViewModel Waiting(ShellViewModel shell)
        => shell.Sidebar.Groups.SelectMany(g => g.Places).First(p => p.CanMount);

    private static PlaceItemViewModel Pinned(ShellViewModel shell)
        => shell.Sidebar.Groups.SelectMany(g => g.Places).First(p => p.IsUserPinned);

    /// <summary>
    /// **The row's command decided what to do from the Path alone**, and an
    /// unmounted volume's Path is empty by design. A markup assertion because
    /// instantiating one of these rows needs the shell's whole object graph —
    /// the trade MarkupRulesTests already makes and explains.
    /// </summary>
    [Fact]
    public void The_place_row_hands_the_whole_place_to_the_command()
    {
        var row = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Axaml + "Button")
            .Single(b => (string?)b.Attribute("ContextRequested") == "OnPlaceContextRequested");

        Assert.EndsWith(".OpenPlaceCommand}", (string?)row.Attribute("Command"));
        Assert.Equal("{Binding}", (string?)row.Attribute("CommandParameter"));
    }

    /// <summary>The whole finding: the click reaches the provider, naming the
    /// device rather than the path it has not got.</summary>
    [AvaloniaFact]
    public async Task Clicking_an_unmounted_volume_mounts_it()
    {
        var (shell, places, _, _) = Fresh();
        await LoadAsync(shell);

        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        Assert.Equal(1, places.Calls);
        Assert.Equal(OneVolume.Waiting, places.AskedFor);
    }

    /// <summary>And lands where the volume did — mounting without opening is
    /// half a gesture.</summary>
    [AvaloniaFact]
    public async Task The_volume_is_opened_once_it_is_mounted()
    {
        var (shell, _, _, mountPoint) = Fresh();
        await LoadAsync(shell);

        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        Assert.Equal(mountPoint, shell.ActiveTab!.CurrentPath);
    }

    /// <summary>
    /// A desktop with no mount helper says so. The provider swallows what the
    /// helper said, so this is read off the rebuilt sidebar: the row is still
    /// sitting there unmounted.
    /// </summary>
    [AvaloniaFact]
    public async Task A_mount_that_did_not_happen_says_so()
    {
        var (shell, places, _, _) = Fresh();
        await LoadAsync(shell);

        places.Refuses = true;

        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        Assert.Equal(1, places.Calls);
        Assert.Contains("could not mount STICK", shell.ActiveTab!.Status);
    }

    /// <summary>
    /// **A mount that failed could not be tried again.** The guard has to be
    /// released whichever way the attempt went: a desktop that gained udisks2
    /// between two clicks would otherwise answer the second one with nothing at
    /// all — not even the message the first one managed.
    /// </summary>
    [AvaloniaFact]
    public async Task A_volume_that_refused_once_can_be_clicked_again()
    {
        var (shell, places, _, mountPoint) = Fresh();
        await LoadAsync(shell);

        places.Refuses = true;
        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        places.Refuses = false;
        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        Assert.Equal(2, places.Calls);
        Assert.Equal(mountPoint, shell.ActiveTab!.CurrentPath);
    }

    /// <summary>
    /// Two volumes arriving at once is a card reader, and opening whichever one
    /// sorted first would be a guess presented as an answer.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_volumes_arriving_at_once_are_not_guessed_between()
    {
        var (shell, places, _, mountPoint) = Fresh();
        await LoadAsync(shell);

        places.MountsTwo = true;

        var before = shell.ActiveTab!.CurrentPath;

        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        Assert.Equal("mounted STICK", shell.ActiveTab.Status);
        Assert.Equal(before, shell.ActiveTab.CurrentPath);
        Assert.NotEqual(mountPoint, shell.ActiveTab.CurrentPath);
    }

    /// <summary>
    /// A place that turns up with a path it cannot answer for is not somewhere
    /// that arrived — a share reconnecting during a mount would otherwise count
    /// as a second volume and turn a good answer into "mounted STICK".
    /// </summary>
    [AvaloniaFact]
    public async Task A_place_that_cannot_be_reached_does_not_count_as_arriving()
    {
        var (shell, places, _, mountPoint) = Fresh();
        await LoadAsync(shell);

        places.AlsoUnreachable = true;

        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        Assert.Equal(mountPoint, shell.ActiveTab!.CurrentPath);
    }

    /// <summary>An ordinary row still just opens, and is never sent to the
    /// mounter — the command took over every place in the sidebar.</summary>
    [AvaloniaFact]
    public async Task A_place_with_a_path_is_still_opened_by_a_click()
    {
        var (shell, places, home, _) = Fresh();
        await LoadAsync(shell);

        await shell.OpenPlaceCommand.ExecuteAsync(Pinned(shell));

        Assert.Equal(home, shell.ActiveTab!.CurrentPath);
        Assert.Equal(0, places.Calls);
    }

    /// <summary>
    /// A mount takes seconds, says so while it does, and leaves the rest of the
    /// sidebar working.
    ///
    /// **Every row binds to the one command object**, and an async RelayCommand
    /// refuses a second execution while the first is running — so without
    /// AllowConcurrentExecutions every other place in the list would grey out
    /// for the length of a mount. The same trap is written up on
    /// PropertiesViewModel.MeasureAsync.
    /// </summary>
    [AvaloniaFact]
    public async Task A_mount_in_flight_says_so_and_leaves_the_other_places_alone()
    {
        var (shell, places, _, _) = Fresh();
        await LoadAsync(shell);

        places.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        await places.Entered.Task.WaitAsync(Patience);

        Assert.Equal("mounting STICK…", shell.ActiveTab!.Status);
        Assert.True(shell.OpenPlaceCommand.CanExecute(Pinned(shell)));

        places.Gate.SetResult();
        await first;
    }

    /// <summary>
    /// The row is a Button and a double-click sends the command twice. Two
    /// mount requests on one device end with the loser's refusal written over
    /// the winner's success.
    /// </summary>
    [AvaloniaFact]
    public async Task A_second_click_while_the_first_is_still_mounting_is_ignored()
    {
        var (shell, places, _, _) = Fresh();
        await LoadAsync(shell);

        places.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell));

        // Not a sleep: the provider itself says when it has been entered, so
        // the guard is provably set before the second click rather than
        // probably set.
        //
        // Bounded all the same. **Both of these waits are on things a
        // regression removes rather than delays**: a click that stops reaching
        // the provider never sets Entered, and a second click that gets past
        // the guard sits on the gate this test is holding shut — either way an
        // unbounded await hangs the run instead of failing it, and a hung suite
        // reports nothing at all.
        await places.Entered.Task.WaitAsync(Patience);

        await shell.OpenPlaceCommand.ExecuteAsync(Waiting(shell)).WaitAsync(Patience);

        Assert.Equal(1, places.Calls);

        places.Gate.SetResult();
        await first;
    }
}
