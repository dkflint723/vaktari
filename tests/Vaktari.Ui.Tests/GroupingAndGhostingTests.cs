using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Three things the listing showed wrongly.
///
///  - **Descending reversed the rows and left the bands alone**, so a
///    descending listing read Today, Yesterday, This week downwards while the
///    files inside each ran the other way. Two directions in one list.
///  - **Headers did not follow live changes.** They are computed once per
///    rebuild, and a watcher event is not one — so deleting the first row of a
///    band took its heading with it, and a file arriving at the top of one got
///    none.
///  - **Hidden files looked exactly like real content** once "show hidden
///    files" was on, which is the whole reason turning it on is survivable in
///    both references and was not here.
/// </summary>
public sealed class GroupingAndGhostingTests : OwnedViewModels
{
    private static FileEntry At(string name, DateTimeOffset when, EntryFlags flags = EntryFlags.None)
        => new(name, "/g/" + name, 1, when, flags);

    private async Task<PaneViewModel> Pane(GroupMode mode, params FileEntry[] entries)
    {
        var pane = Own(new PaneViewModel(new Canned(entries), null, null)
        {
            ViewportWidth = 1400,
            GroupBy = mode,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    private static List<string> Headers(PaneViewModel pane)
        => pane.DetailsEntries.Select(e => pane.HeaderFor(e.FullPath)).OfType<string>().ToList();

    // ---- descending turns the bands over too --------------------------------

    [AvaloniaFact]
    public async Task Descending_reverses_the_bands_as_well_as_the_rows()
    {
        var now = DateTimeOffset.Now;

        var pane = await Pane(
            GroupMode.Modified,
            At("today.txt", now),
            At("older.txt", now.AddDays(-3)));

        var ascending = Headers(pane);

        // Newest first: the first click on a date column is descending, which
        // is what Explorer does and what SortDefaults now encodes.
        pane.SortByCommand.Execute("modified");
        var first = Headers(pane).First();

        pane.SortByCommand.Execute("modified");   // and now oldest first
        var flipped = Headers(pane).First();

        Assert.NotEqual(first, flipped);
        Assert.Equal(2, ascending.Count);
    }

    // ---- headers follow the watcher -----------------------------------------

    /// <summary>
    /// A file arriving at the top of a band is precisely when a heading has to
    /// appear. It did not, so any download into a grouped folder left the
    /// bands wrong until a manual refresh.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_arriving_gets_its_band_heading()
    {
        var now = DateTimeOffset.Now;
        var fs = new Canned([At("beta.txt", now)]);

        var pane = Own(new PaneViewModel(fs, null, null)
        {
            ViewportWidth = 1400,
            GroupBy = GroupMode.Name,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        Assert.Single(Headers(pane));

        // Through the watcher, which is the path that was not recomputing.
        fs.Arriving = At("alpha.txt", now);
        fs.Raise(new FileSystemChange(ChangeKind.Added,
                                      Path.Combine(pane.CurrentPath, "alpha.txt")));

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(60);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var headers = Headers(pane);

        Assert.Equal(2, headers.Count);
        Assert.Equal(["A", "B"], headers);
    }

    // ---- hidden files are ghosted -------------------------------------------

    [AvaloniaFact]
    public void A_hidden_file_is_ghosted()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("desktop.ini", DateTimeOffset.Now, EntryFlags.Hidden),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0.55, Assert.IsType<double>(faded), 3);
    }

    [AvaloniaFact]
    public void A_system_file_is_ghosted_too()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("thumbs.db", DateTimeOffset.Now, EntryFlags.System),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0.55, Assert.IsType<double>(faded), 3);
    }

    [AvaloniaFact]
    public void An_ordinary_file_is_not()
    {
        var faded = FileConverters.HiddenFade.Convert(
            At("report.txt", DateTimeOffset.Now),
            typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1.0, Assert.IsType<double>(faded), 3);
    }

    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        private Action<FileSystemChange>? _onChange;

        /// <summary>What the next stat should return, for a watcher event.</summary>
        public FileEntry? Arriving { get; set; }

        public void Raise(FileSystemChange change) => _onChange?.Invoke(change);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(Arriving);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            _onChange = onChange;
            return new Nothing();
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
