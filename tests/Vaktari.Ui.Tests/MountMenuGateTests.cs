using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// When the Mount entry appears, and when it turns into Unmount.
///
/// This is the layer that excludes a FOLDER named holiday.iso — using the flag
/// the listing already carries, rather than a disk lookup inside a predicate
/// the menu evaluates for every item it opens over.
/// </summary>
public sealed class MountMenuGateTests
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

    /// <summary>An image layer that mounts nothing, and can be told what it
    /// considers already mounted.</summary>
    private sealed class FakeImages : IDiskImages
    {
        public bool IsAvailable { get; init; } = true;
        public string? UnavailableReason => IsAvailable ? null : "no tool";

        public string? Mounted { get; set; }

        public bool CanMount(string path)
            => Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase);

        public MountedImage? MountOf(string imagePath)
            => Mounted is not null
                && imagePath.Equals(Mounted, StringComparison.OrdinalIgnoreCase)
                ? new MountedImage(imagePath, "E:\\")
                : null;

        public Task<MountedImage> MountAsync(string imagePath, CancellationToken ct)
            => Task.FromResult(new MountedImage(imagePath, "E:\\"));

        public Task UnmountAsync(string imagePath, CancellationToken ct) => Task.CompletedTask;
    }

    private static PaneViewModel Pane(FakeImages images, string name, bool directory = false)
    {
        PaneViewModel.DiskImages = images;

        var pane = new PaneViewModel(new Inert(), null, null)
        {
            CurrentPath = Path.GetTempPath(),
        };

        pane.SelectedEntry = new FileEntry(
            name, Path.Combine(Path.GetTempPath(), name), 0, DateTimeOffset.UnixEpoch,
            directory ? EntryFlags.Directory : EntryFlags.None);

        return pane;
    }

    [AvaloniaFact]
    public void An_iso_offers_mount()
    {
        var pane = Pane(new FakeImages(), "holiday.iso");

        Assert.True(pane.CanMountSelection);
        Assert.False(pane.CanUnmountSelection);
    }

    /// <summary>
    /// **A folder named like an image is excluded here**, from the flag the
    /// listing already carries — which is why the platform gate is free to be a
    /// pure name check.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_named_like_an_image_offers_nothing()
    {
        var pane = Pane(new FakeImages(), "holiday.iso", directory: true);

        Assert.False(pane.CanMountSelection);
        Assert.False(pane.CanUnmountSelection);
    }

    /// <summary>One row that changes its word, never two rows at once.</summary>
    [AvaloniaFact]
    public void An_image_already_mounted_offers_unmount_instead()
    {
        var images = new FakeImages();
        var pane = Pane(images, "holiday.iso");

        images.Mounted = Path.Combine(Path.GetTempPath(), "holiday.iso");
        pane.SelectedEntry = pane.SelectedEntry;

        Assert.False(pane.CanMountSelection);
        Assert.True(pane.CanUnmountSelection);
    }

    [AvaloniaFact]
    public void An_ordinary_file_offers_neither()
    {
        var pane = Pane(new FakeImages(), "notes.txt");

        Assert.False(pane.CanMountSelection);
        Assert.False(pane.CanUnmountSelection);
    }

    /// <summary>A machine with no way to mount offers no entry at all, rather
    /// than one that fails when clicked.</summary>
    [AvaloniaFact]
    public void A_machine_that_cannot_mount_offers_nothing()
    {
        var pane = Pane(new FakeImages { IsAvailable = false }, "holiday.iso");

        Assert.False(pane.CanMountSelection);
        Assert.False(pane.CanUnmountSelection);
    }

    /// <summary>
    /// The bin and Recent hold rows naming where a file USED to be, so mounting
    /// there would attach whatever occupies that path now.
    /// </summary>
    [AvaloniaFact]
    public void The_bin_and_recent_never_offer_it()
    {
        PaneViewModel.DiskImages = new FakeImages();

        foreach (var listing in new[] { VirtualPaths.Trash, VirtualPaths.Files })
        {
            var pane = new PaneViewModel(new Inert(), null, null) { CurrentPath = listing };

            pane.SelectedEntry = new FileEntry(
                "holiday.iso", Path.Combine(Path.GetTempPath(), "holiday.iso"),
                0, DateTimeOffset.UnixEpoch, EntryFlags.None);

            Assert.False(pane.CanMountSelection, listing);
        }
    }
}
