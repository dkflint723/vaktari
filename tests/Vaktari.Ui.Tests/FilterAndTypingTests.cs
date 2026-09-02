using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Six small things a Windows or Dolphin user hits in the first hour.
///
///  - Typing "new folder" toggled the preview on the fourth keystroke, so every
///    two-word name in the folder was unreachable by type-ahead.
///  - A filter that matched nothing said "this folder is empty", which over a
///    folder full of files reads as data loss — and the way out was the one
///    thing the message gave no reason to try.
///  - "*.png" in the filter hid everything, because the filter only asked
///    whether a name CONTAINED the text and no name contains an asterisk.
///  - "Copy as path" copied one path out of five selected.
///  - A quoted path — what Explorer's own Copy as path produces — failed in the
///    address bar with a raw Win32 error.
///  - F2 with several files selected renamed the focused one and ignored the
///    rest without a word.
/// </summary>
public sealed class FilterAndTypingTests : OwnedViewModels
{
    private static FileEntry Entry(string name)
        => new(name, "/f/" + name, 3, DateTimeOffset.UnixEpoch, EntryFlags.None);

    private async Task<PaneViewModel> Pane(params string[] names)
    {
        var pane = Own(new PaneViewModel(new Canned([.. names.Select(Entry)]), null, null)
        {
            ViewportWidth = 1400,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    /// <summary>
    /// Sets the filter and waits for it to land. **The filter is debounced by
    /// 120 ms**, so reading the listing straight after setting the text tests
    /// nothing — the first version of these passed against the bug for exactly
    /// that reason.
    /// </summary>
    private static async Task Filter(PaneViewModel pane, string text)
    {
        pane.FilterText = text;

        await Task.Delay(250);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- the filter says what is true ---------------------------------------

    [AvaloniaFact]
    public async Task A_filter_that_matches_nothing_does_not_claim_the_folder_is_empty()
    {
        var pane = await Pane("alpha.txt", "beta.txt");

        await Filter(pane, "zzz");

        Assert.False(pane.IsEmpty, "it said the folder was empty over two real files");
        Assert.True(pane.HasNoMatches);
        Assert.Contains("zzz", pane.NoMatchesLine);
    }

    /// <summary>A genuinely empty folder still says so.</summary>
    [AvaloniaFact]
    public async Task A_folder_with_nothing_in_it_still_says_so()
    {
        var pane = await Pane();

        Assert.True(pane.IsEmpty);
        Assert.False(pane.HasNoMatches);
    }

    [AvaloniaFact]
    public async Task A_filter_that_matches_shows_neither_message()
    {
        var pane = await Pane("alpha.txt", "beta.txt");

        await Filter(pane, "alpha");

        Assert.False(pane.IsEmpty);
        Assert.False(pane.HasNoMatches);
    }

    // ---- wildcards ----------------------------------------------------------

    [AvaloniaFact]
    public async Task A_wildcard_filters_by_pattern()
    {
        var pane = await Pane("photo.png", "notes.txt", "chart.png");

        await Filter(pane, "*.png");

        Assert.Equal(2, pane.DetailsEntries.Count());
        Assert.All(pane.DetailsEntries, e => Assert.EndsWith(".png", e.Name));
    }

    [AvaloniaFact]
    public async Task A_question_mark_matches_one_character()
    {
        var pane = await Pane("a1.txt", "a12.txt");

        await Filter(pane, "a?.txt");

        Assert.Equal("a1.txt", Assert.Single(pane.DetailsEntries).Name);
    }

    /// <summary>Plain text still matches anywhere in the name, which is what
    /// somebody typing three letters means.</summary>
    [AvaloniaFact]
    public async Task Plain_text_still_matches_anywhere_in_the_name()
    {
        var pane = await Pane("report-final.txt", "notes.txt");

        await Filter(pane, "final");

        Assert.Equal("report-final.txt", Assert.Single(pane.DetailsEntries).Name);
    }

    // ---- type-ahead ---------------------------------------------------------

    /// <summary>
    /// While a word is being typed the space belongs to it. Outside that, Space
    /// is still the preview toggle.
    /// </summary>
    [AvaloniaFact]
    public async Task A_space_belongs_to_the_word_being_typed()
    {
        var pane = await Pane("New folder", "notes.txt");

        Assert.False(pane.IsTypeAheadActive, "nothing typed yet, so Space is the preview");

        pane.TypeAhead("n");
        pane.TypeAhead("e");
        pane.TypeAhead("w");

        Assert.True(pane.IsTypeAheadActive, "a space here would toggle the preview instead");
    }

    // ---- F2 on several ------------------------------------------------------

    [AvaloniaFact]
    public async Task F2_on_several_files_asks_for_the_batch_dialog()
    {
        var pane = await Pane("a.txt", "b.txt", "c.txt");

        var batch = 0;
        var single = 0;

        pane.BatchRenameRequested += (_, _) => batch++;
        pane.RenameRequested += (_, _) => single++;

        foreach (var entry in pane.DetailsEntries.Take(2)) pane.DetailsSelection.Add(entry);

        pane.BeginRenameCommand.Execute(null);

        Assert.Equal(1, batch);
        Assert.Equal(0, single);
    }

    /// <summary>One file still goes to the inline prompt.</summary>
    [AvaloniaFact]
    public async Task F2_on_one_file_still_renames_it_inline()
    {
        var pane = await Pane("a.txt", "b.txt");

        var batch = 0;
        var single = 0;

        pane.BatchRenameRequested += (_, _) => batch++;
        pane.RenameRequested += (_, _) => single++;

        pane.SelectedEntry = pane.DetailsEntries.First();

        pane.BeginRenameCommand.Execute(null);

        Assert.Equal(0, batch);
        Assert.Equal(1, single);
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
