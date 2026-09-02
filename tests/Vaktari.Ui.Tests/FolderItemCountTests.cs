using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Thumbnails;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The Size column, for folders.
///
/// **"Show item counts for folders" was a complete no-op, end to end.** The
/// setting round-tripped faithfully, both platform providers counted
/// directories, and the metadata loader even honoured "None" — but nothing in
/// the application ever bound its attached property, so the whole provider path
/// was dead code. The size cell was wired to a converter that returned an em
/// dash for every directory whatever the setting said. It is on by default, so
/// it had never worked at all.
///
/// The decision is a pure static here, and the cell now has ONE owner: the
/// attached property writes Text directly, and a Binding on the same property
/// would write it back at the same priority — two answers to one question,
/// racing in an order nobody can see.
/// </summary>
public sealed class FolderItemCountTests
{
    private static FileEntry Folder(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 0,
               DateTimeOffset.UnixEpoch, EntryFlags.Directory);

    private static FileEntry File(string name, long length)
        => new(name, Path.Combine(Path.GetTempPath(), name), length,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    [Fact]
    public void A_folder_is_counted_when_the_setting_is_on()
    {
        var (text, counting) = RowMetadata.SizeCell(Folder("things"), FolderSizeMode.ItemCount);

        Assert.True(counting, "the count is never fetched, so the setting does nothing");

        // The em dash is what shows while the count is in flight, and what
        // stays if the folder cannot be read.
        Assert.Equal("—", text);
    }

    [Fact]
    public void A_folder_is_left_alone_when_the_setting_is_off()
    {
        var (text, counting) = RowMetadata.SizeCell(Folder("things"), FolderSizeMode.None);

        Assert.False(counting, "the setting says no size, and a count is still fetched");
        Assert.Equal("—", text);
    }

    /// <summary>
    /// ContentSize is treated as ItemCount deliberately: the providers only
    /// count, the settings dialog cannot reach that mode, and the view model
    /// preserves it rather than writing it.
    /// </summary>
    [Fact]
    public void The_unreachable_mode_counts_rather_than_doing_nothing()
        => Assert.True(RowMetadata.SizeCell(Folder("things"), FolderSizeMode.ContentSize).Counting);

    /// <summary>A file keeps its bytes, and asks nothing of the provider — this
    /// setting is about folders, whose size costs something to work out.</summary>
    [Fact]
    public void A_file_still_shows_its_own_size()
    {
        var (text, counting) = RowMetadata.SizeCell(File("notes.txt", 2048), FolderSizeMode.ItemCount);

        Assert.False(counting);
        Assert.Contains("2", text);
        Assert.DoesNotContain("—", text);
    }

    /// <summary>A default entry reaches a recycled container, and a stale size
    /// under a name that has changed is worse than a blank one.</summary>
    [Fact]
    public void A_row_with_nothing_in_it_yet_shows_nothing()
        => Assert.Equal(("", false), RowMetadata.SizeCell(default, FolderSizeMode.ItemCount));

    // ---- and the cell is actually wired to it -------------------------------

    /// <summary>
    /// The half that was missing for the whole life of the feature: nothing
    /// bound the property, so every rule above could be right and change
    /// nothing on screen.
    /// </summary>
    [AvaloniaFact]
    public void The_size_cell_asks_the_provider()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        Assert.Contains("th:RowMetadata.Size=\"{Binding}\"", markup);

        // One owner. The attached property writes Text directly, so a Binding
        // on the same TextBlock would write it back at the same priority.
        Assert.DoesNotContain("FileConverters.Size}", markup);
    }

    /// <summary>And the converter it replaced is gone rather than left behind
    /// as a second, disagreeing answer to the same question.</summary>
    [AvaloniaFact]
    public void The_converter_it_replaced_is_gone()
        => Assert.DoesNotContain(
            "IValueConverter Size =",
            RepoSource.Ui("ViewModels", "FileConverters.cs"));
}
