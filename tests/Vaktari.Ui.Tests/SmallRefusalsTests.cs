using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Three places that said no for no reason.
///
/// **A one-character query was refused with "keep typing…".** A single letter
/// is a real query — "b" for a folder of build outputs, "~" for the editor
/// backups — and every other file manager runs it.
///
/// **The preview limit boxes showed a literal "0"** beside help text promising
/// that blank or 0 means no limit, which is also what hid the "No limit"
/// placeholder written for exactly that case. On screen it read as "skip files
/// larger than nothing".
///
/// **And "Rename in bulk…" was offered for a single file**, directly under
/// plain Rename, which is the entry that handles that case.
/// </summary>
public sealed class SmallRefusalsTests : OwnedViewModels
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

    // ---- a single letter is a search ---------------------------------------
    //
    // Moved to SearchKeyboardTests, with the rest of what typing into the
    // search field does. There is no length rule left to test in isolation: a
    // draft costs nothing until Enter, so a one-character question takes
    // exactly the road a long one does.

    // ---- no limit looks like no limit --------------------------------------

    /// <summary>
    /// Zero is how "no limit" is stored, so the field has to be empty for the
    /// placeholder that says so to appear at all.
    /// </summary>
    [AvaloniaFact]
    public void No_preview_limit_leaves_the_box_empty()
    {
        var before = Vaktari.Ui.Settings.AppSettings.Current;

        try
        {
            Vaktari.Ui.Settings.AppSettings.Apply(before with
            {
                General = before.General with
                {
                    MaxLocalPreviewMegabytes = 0,
                    MaxRemotePreviewMegabytes = 24,
                },
            });

            var model = new SettingsViewModel(Vaktari.Ui.Settings.AppSettings.Current, null);

            Assert.Equal("", model.MaxLocalPreviewMegabytes);

            // And a real limit still reads as itself.
            Assert.Equal("24", model.MaxRemotePreviewMegabytes);
        }
        finally
        {
            Vaktari.Ui.Settings.AppSettings.Apply(before);
        }
    }

    /// <summary>The box the placeholder belongs to, so the fix above has
    /// something to reveal.</summary>
    [Fact]
    public void And_the_box_says_what_empty_means()
    {
        var boxes = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "TextBox")
            .Where(b => ((string?)b.Attribute("Text"))?.Contains(
                "PreviewMegabytes", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(2, boxes.Count);

        foreach (var box in boxes)
            Assert.Equal("No limit", (string?)box.Attribute("PlaceholderText"));
    }

    // ---- in bulk means more than one ---------------------------------------

    private PaneViewModel Pane()
    {
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });
        pane.CurrentPath = Path.GetTempPath();

        return pane;
    }

    private static FileEntry Row(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    [AvaloniaFact]
    public void One_file_is_not_a_bulk_rename()
    {
        var pane = Pane();

        pane.SelectedEntries.Add(Row("a.txt"));

        Assert.True(pane.CanActOnSelection);
        Assert.False(pane.CanRenameInBulk);

        pane.SelectedEntries.Add(Row("b.txt"));

        Assert.True(pane.CanRenameInBulk);
    }

    /// <summary>Nothing selected is not one either — the row hid there already
    /// and must go on doing so.</summary>
    [AvaloniaFact]
    public void And_nothing_selected_is_not_either()
        => Assert.False(Pane().CanRenameInBulk);

    [Fact]
    public void The_row_asks_the_new_question()
    {
        var row = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "MenuItem")
            .Single(m => ((string?)m.Attribute("Header"))?.StartsWith(
                "Rename in bulk", StringComparison.Ordinal) == true);

        Assert.Equal("{Binding ActiveTab.CanRenameInBulk}", (string?)row.Attribute("IsVisible"));
    }
}
