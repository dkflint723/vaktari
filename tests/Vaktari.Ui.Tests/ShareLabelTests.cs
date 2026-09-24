using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The Share rows name what they would share, and keep up with the selection.
///
/// **"Share public…" shared the folder around it.** The label follows the
/// focused row — a selected folder, or the folder being listed — but it was
/// only announced when the listing loaded. The menu stays in the tree between
/// openings, so after right-clicking a folder once, right-clicking a file in
/// the same listing still offered to share that folder by name, while the
/// command shared the whole listing: every sibling, writable with "allow
/// uploads".
/// </summary>
public sealed class ShareLabelTests : OwnedViewModels
{
    [AvaloniaFact]
    public async Task The_label_is_announced_as_the_focused_row_moves()
    {
        var folder = Path.Combine(Path.GetTempPath(), "listing");
        var pane = Own(new PaneViewModel(new Quiet()));

        await pane.NavigateAsync(folder);

        var told = new List<string>();
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.ShareTargetLabel)) told.Add(pane.ShareTargetLabel);
        };

        pane.SelectedEntry = new FileEntry("public", Path.Combine(folder, "public"), 0,
                                           DateTimeOffset.UnixEpoch, EntryFlags.Directory);

        pane.SelectedEntry = new FileEntry("notes.txt", Path.Combine(folder, "notes.txt"), 1,
                                           DateTimeOffset.UnixEpoch, EntryFlags.None);

        Assert.Equal(["public", "listing"], told);
    }

    private sealed class Quiet : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [EnumeratorCancellation] CancellationToken ct)
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
}
