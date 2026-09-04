using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// This PC — every drive on the machine, in one listing.
///
/// **There was nowhere above a drive root.** Up was disabled at the top of C:\
/// by construction, the breadcrumbs stopped at the drive, and typing "This PC"
/// failed as a missing directory. The sidebar lists the drives, but a sidebar
/// is not somewhere you can sort, select in, or open two of in tabs.
/// </summary>
public sealed class ComputerListingTests : OwnedViewModels
{
    private static PlaceGroup Group(string label, params Place[] places)
        => new(label, places);

    private static Place At(string path, string label, PlaceKind kind, long capacity = 0)
        => new()
        {
            Id = "dev:" + path,
            Label = label,
            Path = path,
            Kind = kind,
            Icon = "device-desktop",
            CapacityBytes = capacity == 0 ? null : capacity,
        };

    private static string P(string name)
        => OperatingSystem.IsWindows() ? $@"{name}:\" : $"/mnt/{name}";

    /// <summary>A share whose server is not answering. No capacity either — the
    /// provider only reads TotalSize from a drive that is ready.</summary>
    private static Place Away(string path, string label)
        => At(path, label, PlaceKind.Network) with { IsAvailable = false };

    /// <summary>
    /// **A mapped drive whose server had gone away drew like a working one.**
    /// The sidebar has dimmed unreachable places since it was written; this
    /// listing threw IsAvailable away, so This PC showed a dead Z: at full
    /// strength beside C:.
    /// </summary>
    [AvaloniaFact]
    public void A_drive_that_is_not_there_is_marked_unreadable()
    {
        var rows = ComputerListing.Build([Group("NETWORK", Away(P("Z"), "work (Z:)"))]);

        Assert.True(rows[0].IsUnreadable, "This PC draws a dead drive like a live one");
    }

    /// <summary>
    /// And the other direction, so "ghost everything" is not a passing answer.
    /// </summary>
    [AvaloniaFact]
    public void A_drive_that_is_there_is_left_alone()
    {
        var rows = ComputerListing.Build(
            [Group("DEVICES", At(P("C"), "Windows (C:)", PlaceKind.Device, capacity: 512))]);

        Assert.True(rows[0].IsVolume, "a drive row is not marked a volume, so the size cell cannot tell");
        Assert.False(rows[0].IsUnreadable, "every drive is ghosted, including the ones that work");
    }

    /// <summary>
    /// **The capacity never reached the screen.** The row has carried it since
    /// This PC was built; the size cell had one rule for a directory — an em
    /// dash, and then go and count what is inside — so the column read "184
    /// items" for C: while the metadata provider enumerated the root of every
    /// drive on the machine to put it there.
    /// </summary>
    [AvaloniaFact]
    public void The_size_cell_shows_a_drive_its_capacity()
    {
        var rows = ComputerListing.Build(
        [
            Group("DEVICES",
                At(P("C"), "Windows (C:)", PlaceKind.Device, capacity: 3L * 1024 * 1024 * 1024)),
        ]);

        var (text, counting) = RowMetadata.SizeCell(rows[0], FolderSizeMode.ItemCount);

        Assert.Equal("3 GiB", text);
        Assert.False(counting, "This PC enumerates a drive root to fill a cell that already has its answer");

        // "Show no size for folders" is about folders, whose size costs
        // something to work out. A drive's capacity is already in the row.
        Assert.Equal("3 GiB", RowMetadata.SizeCell(rows[0], FolderSizeMode.None).Text);
    }

    /// <summary>An unreachable share and an empty optical drive both arrive
    /// with no capacity at all, and "0 B" is a claim about a drive nobody has
    /// managed to measure.</summary>
    [AvaloniaFact]
    public void A_drive_nobody_could_measure_shows_no_size_rather_than_zero()
    {
        var rows = ComputerListing.Build([Group("NETWORK", Away(P("Z"), "work (Z:)"))]);

        var (text, counting) = RowMetadata.SizeCell(rows[0], FolderSizeMode.ItemCount);

        Assert.Equal("\u2014", text);
        Assert.False(counting, "an unreachable share is dialled to fill its size cell");
    }

    [AvaloniaFact]
    public void Every_drive_becomes_a_row()
    {
        var rows = ComputerListing.Build(
        [
            Group("DEVICES",
                At(P("C"), "Windows (C:)", PlaceKind.Device, 500),
                At(P("E"), "STICK (E:)", PlaceKind.RemovableDevice, 32)),
            Group("NETWORK", At(P("Z"), "work (Z:)", PlaceKind.Network)),
        ]);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Name == "Windows (C:)");
        Assert.Contains(rows, r => r.Name == "STICK (E:)");
        Assert.Contains(rows, r => r.Name == "work (Z:)");
    }

    /// <summary>
    /// **Hardware, not shortcuts.** Documents is not a drive, and listing the
    /// user folders here would make This PC a second copy of the sidebar
    /// rather than an answer to "what is attached to this machine".
    /// </summary>
    [AvaloniaFact]
    public void User_folders_and_pins_are_not_drives()
    {
        var rows = ComputerListing.Build(
        [
            Group("PLACES",
                At(P("home"), "Home", PlaceKind.UserFolder),
                At(P("work"), "Work", PlaceKind.Bookmark)),
            Group("DEVICES", At(P("C"), "Windows (C:)", PlaceKind.Device)),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("Windows (C:)", row.Name);
    }

    /// <summary>
    /// A place can legitimately appear in two groups — a removable drive that
    /// is also pinned — and two rows for one drive is the sort of thing nobody
    /// notices until they select both and act on them.
    /// </summary>
    [AvaloniaFact]
    public void One_drive_appearing_twice_is_still_one_row()
    {
        var rows = ComputerListing.Build(
        [
            Group("DEVICES", At(P("E"), "STICK (E:)", PlaceKind.RemovableDevice)),
            Group("PINNED", At(P("E"), "STICK (E:)", PlaceKind.RemovableDevice)),
        ]);

        Assert.Single(rows);
    }

    /// <summary>Opening a drive navigates into it, so the rows have to be
    /// directories — everything downstream keys on that flag.</summary>
    [AvaloniaFact]
    public void A_drive_row_opens_like_a_folder()
    {
        var rows = ComputerListing.Build(
            [Group("DEVICES", At(P("C"), "Windows (C:)", PlaceKind.Device))]);

        Assert.True(rows[0].IsDirectory);
    }

    [AvaloniaFact]
    public void Nothing_attached_is_an_empty_listing_rather_than_a_failure()
        => Assert.Empty(ComputerListing.Build([]));

    /// <summary>The size column says how big the drive is, rather than zero.</summary>
    [AvaloniaFact]
    public void A_drive_reports_its_capacity()
    {
        var rows = ComputerListing.Build(
            [Group("DEVICES", At(P("C"), "Windows (C:)", PlaceKind.Device, capacity: 12345))]);

        Assert.Equal(12345, rows[0].Length);
    }

    /// <summary>The name goes there. It was a missing directory before, because
    /// a label is not a path.</summary>
    [AvaloniaFact]
    public async Task Typing_the_name_opens_it()
    {
        var pane = Own(new PaneViewModel(new Inert(), null, null)
        {
            CurrentPath = Path.GetTempPath(),
        });

        pane.PathText = Vaktari.Core.Naming.ComputerTitle;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(VirtualPaths.Computer, pane.CurrentPath);
    }

    /// <summary>
    /// **Up from a drive root goes to the machine.** It went nowhere before:
    /// CanGoUp was false at the top of every drive by construction, so the
    /// button was disabled and there was no way out but the sidebar.
    /// </summary>
    [AvaloniaFact]
    public async Task Up_from_a_drive_root_reaches_the_machine()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var pane = Own(new PaneViewModel(new Inert(), null, null) { CurrentPath = root });

        Assert.True(pane.CanGoUp, "the top of a drive is not the top of the machine");

        await pane.GoUpCommand.ExecuteAsync(null);

        Assert.Equal(VirtualPaths.Computer, pane.CurrentPath);
    }

    /// <summary>And there is nothing above the machine itself.</summary>
    [AvaloniaFact]
    public void The_machine_has_nothing_above_it()
    {
        var pane = Own(new PaneViewModel(new Inert(), null, null)
        {
            CurrentPath = VirtualPaths.Computer,
        });

        Assert.False(pane.CanGoUp);
    }


    /// <summary>
    /// **Caught by looking at it, not by a test.** The path a virtual listing
    /// carries is an internal scheme, and the location bar's tooltip was
    /// showing "vaktari:computer" to anyone who hovered it.
    /// </summary>
    [AvaloniaFact]
    public void The_location_bar_never_shows_the_internal_scheme()
    {
        var pane = Own(new PaneViewModel(new Inert(), null, null)
        {
            CurrentPath = VirtualPaths.Computer,
        });

        Assert.Equal(Vaktari.Core.Naming.ComputerTitle, pane.DisplayPath);
        Assert.DoesNotContain("vaktari:", pane.DisplayPath);
    }

    /// <summary>An ordinary folder still shows its real path.</summary>
    [AvaloniaFact]
    public void A_real_folder_still_shows_its_path()
    {
        var pane = Own(new PaneViewModel(new Inert(), null, null)
        {
            CurrentPath = Path.GetTempPath(),
        });

        Assert.Equal(Path.GetTempPath(), pane.DisplayPath);
    }

    /// <summary>
    /// **Also caught by looking.** A drive has no meaningful modified time, so
    /// the listing carries the epoch — which the column rendered as
    /// "31 Dec 1969", a date that reads as real and is not.
    /// </summary>
    [AvaloniaFact]
    public void A_drive_shows_no_date_rather_than_the_epoch()
    {
        var shown = FileConverters.Modified.Convert(
            DateTimeOffset.UnixEpoch, typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("", shown);
    }

    [AvaloniaFact]
    public void A_real_date_is_still_shown()
    {
        var shown = FileConverters.Modified.Convert(
            DateTimeOffset.Now.AddDays(-3), typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture) as string;

        Assert.False(string.IsNullOrEmpty(shown));
    }

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
}
