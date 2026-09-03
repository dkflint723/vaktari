using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The Paste row, and whether there is anything to paste.
///
/// **It was live with an empty clipboard.** The row was offered in every
/// listing but the bin, and picking it posted "clipboard has no files" — an
/// answer the row could have given by looking grey, which is what Explorer does
/// with the same menu.
/// </summary>
public sealed class PasteOfferedTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

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

    /// <summary>Answers whatever it was told to, and counts being asked.</summary>
    private sealed class Holding(bool files) : IClipboardService
    {
        public int Asked { get; private set; }
        public bool Throws { get; init; }

        public Task<bool> HasFilesAsync()
        {
            Asked++;

            return Throws
                ? throw new InvalidOperationException("the clipboard is busy")
                : Task.FromResult(files);
        }

        public Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
            => Task.FromResult(true);

        public Task<ClipboardPayload?> GetFilesAsync()
            => Task.FromResult<ClipboardPayload?>(null);
    }

    private PaneViewModel Pane(IClipboardService clipboard)
        => Own(new PaneViewModel(new Inert(), clipboard: clipboard) { ViewportWidth = 1400 });

    /// <summary>
    /// Over-offered rather than under-offered while the answer is in flight:
    /// an over-offered Paste is exactly today's behaviour and explains itself,
    /// an under-offered one refuses a paste that would have worked.
    /// </summary>
    [AvaloniaFact]
    public void Before_anything_is_asked_the_row_is_live()
        => Assert.True(Pane(new Holding(files: false)).CanPaste);

    [AvaloniaFact]
    public async Task An_empty_clipboard_greys_the_row()
    {
        var pane = Pane(new Holding(files: false));

        await pane.RefreshClipboardAsync();

        Assert.False(pane.CanPaste);
    }

    [AvaloniaFact]
    public async Task And_one_holding_files_keeps_it_live()
    {
        var pane = Pane(new Holding(files: true));

        pane.CanPaste = false;
        await pane.RefreshClipboardAsync();

        Assert.True(pane.CanPaste);
    }

    /// <summary>
    /// Fails open. A probe that could not answer must not be the reason a paste
    /// is refused — the command itself still says so when there is nothing
    /// there.
    /// </summary>
    [AvaloniaFact]
    public async Task A_clipboard_that_will_not_answer_does_not_refuse_the_paste()
    {
        var pane = Pane(new Holding(files: false) { Throws = true });

        await pane.RefreshClipboardAsync();

        Assert.True(pane.CanPaste);
    }

    /// <summary>
    /// Copying knows without asking, because this pane just put them there —
    /// the probe runs when a menu opens, which is later.
    /// </summary>
    [AvaloniaFact]
    public async Task Copying_makes_the_row_live_without_a_round_trip()
    {
        var clipboard = new Holding(files: false);
        var pane = Pane(clipboard);

        pane.CanPaste = false;
        pane.SelectedEntries.Add(new FileEntry(
            "a.txt", Path.Combine(Path.GetTempPath(), "a.txt"), 1,
            DateTimeOffset.UnixEpoch, EntryFlags.None));

        await pane.CopySelectionToClipboardAsync();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(pane.CanPaste);
        Assert.Equal(0, clipboard.Asked);
    }

    // ---- and the row on screen asks ---------------------------------------

    [Fact]
    public void The_row_is_greyed_rather_than_hidden()
    {
        var row = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "MenuItem")
            .Single(m => (string?)m.Attribute("Header") == "Paste");

        Assert.Equal("{Binding ActiveTab.CanPaste}", (string?)row.Attribute("IsEnabled"));

        // Hidden would move the entries under it between two right-clicks in
        // the same folder; its neighbours hide on a rule about the SELECTION,
        // which does not change under you the way a clipboard does.
        Assert.Equal("{Binding !ActiveTab.IsTrashListing}", (string?)row.Attribute("IsVisible"));
    }

    /// <summary>
    /// And something asks, as the menu opens. In the ActiveTab block
    /// specifically — above the early return further down that has swallowed
    /// work in this handler before.
    /// </summary>
    [Fact]
    public void The_menu_asks_the_clipboard_as_it_opens()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnListingMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)");

        var probe = body.IndexOf("RefreshClipboardAsync()", StringComparison.Ordinal);

        Assert.True(probe > 0, "nothing asks the clipboard when the listing menu opens");

        // Inside the ActiveTab block, and above the share-menu walk — that walk
        // opens with a return, and work placed after it has been silently
        // swallowed in this handler before. The guard at the very top is a
        // different thing: without a menu there is nothing to ask for.
        var block = body.IndexOf("ActiveTab: { } tab }", StringComparison.Ordinal);
        var shareWalk = body.IndexOf("shareMenu", StringComparison.Ordinal);

        Assert.True(block > 0 && probe > block,
                    "the clipboard is asked outside the block that has a pane to ask for");

        Assert.True(shareWalk < 0 || probe < shareWalk,
                    "the clipboard is asked after the walk that can return early");
    }
}
