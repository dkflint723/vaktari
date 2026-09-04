using System.Globalization;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Measuring what a selection actually holds.
///
/// Two defects underneath a design question. Whether properties should measure
/// a folder automatically is a deliberate choice this code documents and argues
/// for, and is not what these are about — both of these are wrong whichever way
/// that goes.
///
/// **The loose files were dropped from the total.** A mixed selection reads
/// "12 MB in 3 file(s), plus 1 folder(s) unmeasured" until you press measure,
/// and then reported only what the FOLDER held — a smaller number than the line
/// it replaced, for an operation whose whole purpose is to make the number
/// bigger and right.
///
/// **And the stop button was disabled while measuring.** One command both
/// starts and stops, and an async RelayCommand refuses a second execution while
/// the first runs — so CanExecute went false the moment the walk began, the
/// button that then reads "Stop" greyed out, and a measurement of a home
/// directory ran to the end whatever anybody pressed.
/// </summary>
public sealed class FolderSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-size-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderSizeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The two buttons on this window that build their own words.
    ///
    /// **Both pairs were lower case, on a window whose Apply button was not.**
    /// MeasureLabel is a converter and ChecksumButtonText a view-model
    /// property, so the markup carries only the binding and no markup scan
    /// reaches either word.
    /// </summary>
    [AvaloniaFact]
    public void The_windows_own_labels_are_sentence_case()
    {
        var model = new PropertiesViewModel(new Measures(4096), [Folder("here")], access: null);

        Assert.Equal("Compute", model.ChecksumButtonText);

        model.IsHashing = true;

        Assert.Equal("Stop", model.ChecksumButtonText);

        Assert.Equal("Measure", PropertiesConverters.MeasureLabel.Convert(
            false, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("Stop", PropertiesConverters.MeasureLabel.Convert(
            true, typeof(string), null, CultureInfo.InvariantCulture));
    }

    /// <summary>Reports a fixed size for any folder and never touches a disk,
    /// so the arithmetic under test is the view model's.</summary>
    private sealed class Measures(long bytes) : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                Kind = "Folder",
                IsDirectory = Directory.Exists(path),
                Size = 0,
                Created = DateTimeOffset.UnixEpoch,
                Modified = DateTimeOffset.UnixEpoch,
                Accessed = DateTimeOffset.UnixEpoch,
            });

        public ValueTask<SizeProgress> MeasureAsync(
            string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(bytes, 1, 1));

        public bool ShowSystemDialog(string path) => false;
    }

    private string File(string name, int size)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllBytes(path, new byte[size]);

        return path;
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);

        return path;
    }

    // ---- measuring without being asked ---------------------------------------

    /// <summary>
    /// The walk is started and not awaited, so the window opens at once and the
    /// figures arrive into it. A test therefore has to wait for the answer the
    /// way the window does.
    /// </summary>
    private static async Task Settles(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(5);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), "the measurement never arrived");
    }

    /// <summary>
    /// **Long enough that "it has not happened" is not just "not yet".** A walk
    /// is started and not awaited, so asserting a negative the instant the load
    /// returns passes whether or not one was started — which is exactly the
    /// mistake these tests exist to catch in the code.
    /// </summary>
    private static async Task Quiet()
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(5);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// **Both references start counting the moment the window opens**, and this
    /// waited to be asked — so the one figure somebody opens a folder's
    /// properties FOR was the one thing not on the page.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_local_folder_measures_it_without_being_asked()
    {
        var model = new PropertiesViewModel(new Measures(4096), [Folder("here")], access: null);

        await model.LoadAsync();
        await Settles(() => model.SizeText.Contains("4 KiB", StringComparison.Ordinal));

        Assert.Contains("4 KiB", model.SizeText);
    }

    /// <summary>
    /// **Not over a wire.** Measuring walks the whole tree, which on SMB or
    /// SFTP is a round trip per directory — so opening properties on a folder
    /// of a mounted share would spend the connection before anybody had decided
    /// they wanted the number. The button is still there for when they have.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_on_a_share_waits_to_be_asked()
    {
        var before = Vaktari.Ui.Thumbnails.ThumbnailLoader.RemoteRoots;

        try
        {
            Vaktari.Ui.Thumbnails.ThumbnailLoader.RemoteRoots = [_root];

            var model = new PropertiesViewModel(new Measures(4096), [Folder("far")], access: null);

            await model.LoadAsync();
            await Quiet();

            Assert.Equal("not measured", model.SizeText);
            Assert.True(model.CanMeasure, "and the button is still offered");
        }
        finally
        {
            Vaktari.Ui.Thumbnails.ThumbnailLoader.RemoteRoots = before;
        }
    }

    /// <summary>
    /// A file has nothing to walk, so nothing starts — its size was on the page
    /// already.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_starts_no_walk()
    {
        var model = new PropertiesViewModel(
            new Measures(4096), [File("a.bin", 300)], access: null);

        await model.LoadAsync();
        await Quiet();

        Assert.False(model.CanMeasure);

        // The size line is still the file's own, not a walk's. A walk reports
        // "N · files · folders" whatever it was pointed at, so a stray one over
        // a file would be counting a file it already had the size of.
        Assert.DoesNotContain("files", model.SizeText);
    }

    /// <summary>
    /// **One remote path in a selection is enough to wait**, because the walk
    /// would cross it either way — and a selection that measured three local
    /// folders quickly and then stalled on a share is worse than one that waits
    /// to be asked.
    /// </summary>
    [AvaloniaFact]
    public async Task One_remote_folder_holds_back_the_whole_selection()
    {
        var before = Vaktari.Ui.Thumbnails.ThumbnailLoader.RemoteRoots;
        var far = Path.Combine(Path.GetTempPath(), "vaktari-far-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(far);
            Vaktari.Ui.Thumbnails.ThumbnailLoader.RemoteRoots = [far];

            var model = new PropertiesViewModel(
                new Measures(1000), [Folder("near"), far], access: null);

            await model.LoadAsync();
            await Quiet();

            Assert.Contains("unmeasured", model.SizeText);
        }
        finally
        {
            Vaktari.Ui.Thumbnails.ThumbnailLoader.RemoteRoots = before;

            try { Directory.Delete(far, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// The one that matters: measuring a mixed selection must not lose the
    /// files it already knew about.
    /// </summary>
    [AvaloniaFact]
    public async Task Measuring_a_mixed_selection_keeps_the_loose_files()
    {
        var model = new PropertiesViewModel(
            new Measures(1000),
            [File("a.bin", 300), File("b.bin", 200), Folder("inside")],
            access: null);

        await model.MeasureCommand.ExecuteAsync(null);

        // 1000 from the folder, plus the 500 bytes of loose files that the
        // summary had already counted and the measurement used to discard.
        Assert.Contains("1.5 KiB", model.SizeText);
        Assert.Contains("3", model.SizeText);
    }

    /// <summary>A selection of folders alone is unaffected, which is the case
    /// that always worked.</summary>
    [AvaloniaFact]
    public async Task Measuring_folders_alone_is_unchanged()
    {
        var model = new PropertiesViewModel(
            new Measures(2048), [Folder("one"), Folder("two")], access: null);

        await model.MeasureCommand.ExecuteAsync(null);

        Assert.Contains("4 KiB", model.SizeText);
    }

    /// <summary>
    /// The stop button has to be pressable while the walk runs, or the branch
    /// that cancels it cannot be reached from the interface at all.
    /// </summary>
    [AvaloniaFact]
    public void The_measure_command_allows_the_second_press_that_stops_it()
    {
        var source = RepoSource.Ui("ViewModels", "PropertiesViewModel.cs");

        Assert.Contains("[RelayCommand(AllowConcurrentExecutions = true)]", source);

        // And the command really does both, which is why it must.
        Assert.Contains("if (IsMeasuring)", source);
    }
}
