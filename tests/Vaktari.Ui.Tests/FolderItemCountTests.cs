using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    private static FileEntry Measured(string name, long length)
        => new(name, Path.Combine(Path.GetTempPath(), name), length,
               DateTimeOffset.UnixEpoch, EntryFlags.Directory | EntryFlags.Measured);

    [Fact]
    public void A_folder_is_counted_when_the_setting_is_on()
    {
        var (text, fill) = RowMetadata.SizeCell(Folder("things"), FolderSizeMode.ItemCount);

        Assert.Equal(RowMetadata.SizeFill.Count, fill);

        // The em dash is what shows while the count is in flight, and what
        // stays if the folder cannot be read.
        Assert.Equal("—", text);
    }

    [Fact]
    public void A_folder_is_left_alone_when_the_setting_is_off()
    {
        var (text, fill) = RowMetadata.SizeCell(Folder("things"), FolderSizeMode.None);

        Assert.Equal(RowMetadata.SizeFill.Nothing, fill);
        Assert.Equal("—", text);
    }

    /// <summary>
    /// **ContentSize used to be treated as ItemCount**, and this test said so:
    /// the providers only counted, there was no recursive summing to ask, and
    /// the settings dialog had no way to reach the mode — so a person who set
    /// it by editing settings.json got item counts and no sign of why.
    /// SpaceUsage.Measure is that summing, and the mode is its own answer now.
    /// </summary>
    [Fact]
    public void Asking_for_the_size_of_the_contents_measures_rather_than_counting()
    {
        var (text, fill) = RowMetadata.SizeCell(Folder("things"), FolderSizeMode.ContentSize);

        Assert.Equal(RowMetadata.SizeFill.Measure, fill);

        // The em dash is what shows while the walk runs, and what stays if the
        // folder cannot be read at all.
        Assert.Equal("—", text);
    }

    /// <summary>A file keeps its bytes, and asks nothing of the provider — this
    /// setting is about folders, whose size costs something to work out.</summary>
    [Fact]
    public void A_file_still_shows_its_own_size()
    {
        var (text, fill) = RowMetadata.SizeCell(File("notes.txt", 2048), FolderSizeMode.ItemCount);

        Assert.Equal(RowMetadata.SizeFill.Nothing, fill);
        Assert.Contains("2", text);
        Assert.DoesNotContain("—", text);
    }

    /// <summary>
    /// **The one listing built to show a folder's size is the one that must
    /// show it.** Both rules after this one would have thrown the total away:
    /// "no size for folders" blanks the column outright, and the counting rule
    /// replaces a measured number with an item count fetched from the provider.
    /// Whatever the setting says about folders in general, a row that was
    /// measured shows what was measured.
    /// </summary>
    [Theory]
    [InlineData(FolderSizeMode.ItemCount)]
    [InlineData(FolderSizeMode.None)]
    [InlineData(FolderSizeMode.ContentSize)]
    public void A_measured_folder_shows_its_size_whatever_the_setting_says(FolderSizeMode mode)
    {
        var (text, fill) = RowMetadata.SizeCell(Measured("photos", 2048), mode);

        Assert.Equal(RowMetadata.SizeFill.Nothing, fill);
        Assert.Contains("2", text);
        Assert.DoesNotContain("—", text);
    }

    /// <summary>A default entry reaches a recycled container, and a stale size
    /// under a name that has changed is worse than a blank one.</summary>
    [Fact]
    public void A_row_with_nothing_in_it_yet_shows_nothing()
        => Assert.Equal(("", RowMetadata.SizeFill.Nothing), RowMetadata.SizeCell(default, FolderSizeMode.ItemCount));

    /// <summary>
    /// **A count and a measurement of one folder are two answers, and a cache
    /// holds one thing per key.** Sharing a key would leave "184 items" in the
    /// Size column after the setting changed to ask for bytes, until something
    /// evicted it — and the two are fetched by the same code on the same path,
    /// so nothing else keeps them apart.
    /// </summary>
    [Fact]
    public void A_measured_folder_and_a_counted_one_are_kept_apart()
        => Assert.NotEqual(
            RowMetadata.CacheKey(Folder("things"), RowMetadata.SizeFill.Count),
            RowMetadata.CacheKey(Folder("things"), RowMetadata.SizeFill.Measure));

    /// <summary>Counts every question and answers with the count.</summary>
    private sealed class Asked : IFileMetadataProvider
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool CanDescribe(string path, bool isDirectory) => true;

        public ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct)
            => ValueTask.FromResult<string?>($"asked {Interlocked.Increment(ref _count)}");

        public ValueTask<string?> DescribeAccessAsync(string path, bool isDirectory, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }

    /// <summary>
    /// And the count keeps the key the details line uses, because it is the
    /// same call on the same path — sharing that key is the point.
    ///
    /// **This used to check only the shape of CacheKey's answer**, which the
    /// details line never has to use: it builds its key for itself, and had it
    /// gone back to a key of its own spelling the two would have stopped
    /// sharing a slot — one question per folder become two, and the details
    /// line's answer no longer tied to the folder's modified time — with this
    /// still passing. So both fills are driven: the details line first, and
    /// then the Size cell must find its answer waiting rather than ask.
    /// </summary>
    [AvaloniaFact]
    public async Task A_counted_folder_shares_the_key_the_details_line_uses()
    {
        var providerBefore = RowMetadata.Provider;
        var settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;

        var asked = new Asked();

        // A path of its own, so no answer another test left in the cache can
        // stand in for this one.
        var row = new FileEntry(
            "things",
            Path.Combine(Path.GetTempPath(), "vaktari-shared-" + Guid.NewGuid().ToString("N")),
            0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

        try
        {
            RowMetadata.Provider = asked;

            Vaktari.Ui.Settings.AppSettings.Apply(settingsBefore with
            {
                Views = settingsBefore.Views with
                {
                    Details = settingsBefore.Views.Details with { FolderSize = FolderSizeMode.ItemCount },
                },
            });

            var key = RowMetadata.CacheKey(row, RowMetadata.SizeFill.Count);

            RowMetadata.SetEntry(new TextBlock(), row);

            for (var i = 0; i < 400 && !RowMetadata.Holds(key); i++)
            {
                await Task.Delay(5);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(RowMetadata.Holds(key), "the details line did not keep its answer under the count's key");
            Assert.Equal(1, asked.Count);

            var size = new TextBlock();

            RowMetadata.SetSize(size, row);

            Assert.Equal("asked 1", size.Text);
            Assert.Equal(1, asked.Count);
        }
        finally
        {
            RowMetadata.Provider = providerBefore;
            Vaktari.Ui.Settings.AppSettings.Apply(settingsBefore);
        }
    }

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
