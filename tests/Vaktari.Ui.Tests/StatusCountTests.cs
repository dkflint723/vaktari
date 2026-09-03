using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the status bar says is here.
///
/// **It said "items" and nothing else.** Both references split folders from
/// files, and the split is the more useful half of the count: a folder of 200
/// items is a different place depending on whether it holds two subfolders or
/// two hundred, and "items" cannot tell you which.
/// </summary>
public sealed class StatusCountTests : OwnedViewModels
{
    private static FileEntry Row(string name, bool folder = false)
        => new(name, "/here/" + name, folder ? 0 : 10, DateTimeOffset.UnixEpoch,
               folder ? EntryFlags.Directory : EntryFlags.None);

    private async Task<PaneViewModel> Listing(params FileEntry[] rows)
    {
        var pane = Own(new PaneViewModel(new Canned(rows)) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    [AvaloniaFact]
    public async Task The_count_says_how_many_of_each()
    {
        var pane = await Listing(
            Row("one", folder: true), Row("two", folder: true), Row("a.txt"));

        Assert.Equal("2 folders, 1 file", pane.Summary);
    }

    /// <summary>
    /// **A part that reads "0 folders" is noise** in the one place on screen
    /// with no room for any.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_of_only_files_does_not_mention_folders()
        => Assert.Equal("2 files", (await Listing(Row("a.txt"), Row("b.txt"))).Summary);

    /// <summary>And the same the other way about.</summary>
    [AvaloniaFact]
    public async Task A_folder_of_only_folders_does_not_mention_files()
        => Assert.Equal("1 folder", (await Listing(Row("one", folder: true))).Summary);

    /// <summary>
    /// The singular matters for the same reason the bin's own line does: "1
    /// files" is the sort of thing that makes a person trust the rest of the
    /// number less.
    /// </summary>
    [AvaloniaFact]
    public async Task One_of_each_is_said_in_the_singular()
        => Assert.Equal(
            "1 folder, 1 file",
            (await Listing(Row("one", folder: true), Row("a.txt"))).Summary);

    // ---- and what is picked --------------------------------------------------

    [AvaloniaFact]
    public async Task A_selection_is_counted_beside_it()
    {
        var pane = await Listing(Row("a.txt"), Row("b.txt"));

        pane.DetailsSelection.Add(pane.Entries[0]);

        Assert.Equal("2 files · 1 selected (10 B)", pane.Summary);
    }

    /// <summary>
    /// **Only when both kinds are in it.** "3 selected (3 files)" restates the
    /// number it has just given, and a status bar that repeats itself teaches
    /// people to stop reading it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_selection_of_one_kind_is_not_broken_down()
    {
        var pane = await Listing(Row("a.txt"), Row("b.txt"));

        pane.DetailsSelection.Add(pane.Entries[0]);
        pane.DetailsSelection.Add(pane.Entries[1]);

        // The size is bracketed; the breakdown would be a SECOND thing in
        // there, which is what this says has not happened.
        Assert.Equal("2 files · 2 selected (20 B)", pane.Summary);
    }

    /// <summary>And a mixed one is, because then it says something new.</summary>
    [AvaloniaFact]
    public async Task A_mixed_selection_says_how_many_of_each()
    {
        var pane = await Listing(Row("one", folder: true), Row("a.txt"));

        pane.DetailsSelection.Add(pane.Entries[0]);
        pane.DetailsSelection.Add(pane.Entries[1]);

        Assert.Equal("1 folder, 1 file · 2 selected (1 folder, 1 file, 10 B)", pane.Summary);
    }

    /// <summary>
    /// The size still follows, and still counts only the files — measuring the
    /// folders would mean walking the tree on every selection change.
    /// </summary>
    [AvaloniaFact]
    public async Task The_size_of_what_is_picked_still_follows()
    {
        var pane = await Listing(Row("a.txt"), Row("b.txt"));

        pane.DetailsSelection.Add(pane.Entries[0]);

        Assert.Contains("(10 B)", pane.Summary);
    }

    private sealed class Canned(IReadOnlyList<FileEntry> rows) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return rows;
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
