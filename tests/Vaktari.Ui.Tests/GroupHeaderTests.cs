using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// One band, one header.
///
/// **Every band used to appear twice.** Folders-first was applied before the
/// group key, so the order was [every folder, by band][every file, by band] —
/// and a header is emitted wherever the label changes. Grouping a folder and a
/// file both modified today produced "Today", the folders, then "Today" again,
/// the files. Explorer and Dolphin both put them under one heading, folders at
/// the top of it.
///
/// Size and Kind never showed the fault, because both give folders a band of
/// their own; it was Name and Modified, which band folders and files the same
/// way, that repeated.
/// </summary>
public sealed class GroupHeaderTests : OwnedViewModels
{
    private static FileEntry Entry(string name, bool directory, DateTimeOffset when)
        => new(name, "/tmp/" + name, 10, when,
               directory ? EntryFlags.Directory : EntryFlags.None);

    private async Task<PaneViewModel> Listing(GroupMode mode, params FileEntry[] entries)
    {
        var pane = Own(new PaneViewModel(new Canned(entries), null, null)
        {
            ViewportWidth = 1400,
            GroupBy = mode,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    /// <summary>The headers as they would be drawn, in row order.</summary>
    private static List<string> Headers(PaneViewModel pane)
        => pane.DetailsEntries
            .Select(e => pane.HeaderFor(e.FullPath))
            .OfType<string>()
            .ToList();

    /// <summary>The fault, in the smallest shape that shows it.</summary>
    [AvaloniaFact]
    public async Task A_folder_and_a_file_from_the_same_day_share_one_header()
    {
        var today = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Modified,
            Entry("a-folder", directory: true, today),
            Entry("b-file.txt", directory: false, today));

        var headers = Headers(pane);

        Assert.Single(headers);
    }

    /// <summary>And by name, the other mode that bands folders and files
    /// alike: "A" must not appear over the folders and again over the
    /// files.</summary>
    [AvaloniaFact]
    public async Task Grouping_by_name_does_not_repeat_a_letter()
    {
        var when = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Name,
            Entry("Apples", directory: true, when),
            Entry("apricot.txt", directory: false, when),
            Entry("Bananas", directory: true, when),
            Entry("berry.txt", directory: false, when));

        var headers = Headers(pane);

        Assert.Equal(headers.Count, headers.Distinct().Count());
        Assert.Equal(2, headers.Count);
    }

    /// <summary>
    /// Folders still come first — inside the band, which is where the
    /// convention actually lives. Fixing the duplicate must not cost the
    /// ordering everyone expects.
    /// </summary>
    [AvaloniaFact]
    public async Task Folders_still_come_first_inside_the_band()
    {
        var today = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Modified,
            Entry("zzz-folder", directory: true, today),
            Entry("aaa-file.txt", directory: false, today));

        var order = pane.DetailsEntries.Select(e => e.Name).ToList();

        Assert.Equal(["zzz-folder", "aaa-file.txt"], order);
    }

    /// <summary>
    /// With no grouping at all, folders-first is the only rule and still
    /// applies — the tie-break moved, it did not go.
    /// </summary>
    [AvaloniaFact]
    public async Task Without_grouping_folders_still_come_first()
    {
        var when = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.None,
            Entry("aaa-file.txt", directory: false, when),
            Entry("zzz-folder", directory: true, when));

        Assert.Equal("zzz-folder", pane.DetailsEntries.First().Name);
    }

    /// <summary>
    /// Grouping by size was never wrong, because folders get a band of their
    /// own there. Pinned so the reordering above cannot quietly break it.
    /// </summary>
    [AvaloniaFact]
    public async Task Grouping_by_size_still_puts_folders_in_their_own_band()
    {
        var when = DateTimeOffset.Now;

        var pane = await Listing(
            GroupMode.Size,
            Entry("a-folder", directory: true, when),
            Entry("b-file.txt", directory: false, when));

        var headers = Headers(pane);

        Assert.Equal(2, headers.Count);
        Assert.Contains("Folders", headers);
    }

    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
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
