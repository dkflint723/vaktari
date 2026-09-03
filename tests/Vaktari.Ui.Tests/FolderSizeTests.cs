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
/// button that then reads "stop" greyed out, and a measurement of a home
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
