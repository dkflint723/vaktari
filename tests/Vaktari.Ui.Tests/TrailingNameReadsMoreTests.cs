using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Two reads of a folded row that nothing pinned.** fix8's verification
/// mutated each and nothing reddened:
///
/// - the preview asks whether the file is kept online BEFORE it reads a byte,
///   and that question is asked by path too — of "report" for "report ", so a
///   neighbour kept online hid the row's own text behind "kept online";
/// - the properties window's measure of a selection holding a folder sorts
///   the rest into folders and loose files, and a FILE "report " beside a
///   FOLDER "report" was sorted by the folder's answer: counted as a folder,
///   measured as one, and left out of the files.
///
/// The neighbour is always the other kind of thing, so an answer from it
/// cannot pass for the row's own. All in a temporary folder, the trailing
/// names made through "\\?\".
/// </summary>
public sealed class TrailingNameReadsMoreTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-readsmore").FullName;

    private readonly Func<string, bool>? _onlineBefore = OnlineOnly.Test;

    public override void Dispose()
    {
        base.Dispose();

        OnlineOnly.Test = _onlineBefore;

        try
        {
            var root = Raw(_root);

            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(f);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    private static string Raw(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;

    private string File_(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Raw(Path.GetDirectoryName(path)!));
        File.WriteAllText(Raw(path), content);
        return path;
    }

    private string Folder(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Raw(path));
        return path;
    }

    private static T Windows<T>(string type)
        => (T)Activator.CreateInstance(Type.GetType($"Vaktari.Windows.{type}, Vaktari.Windows", throwOnError: true)!)!;

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), what);
    }

    /// <summary>
    /// The seam stands in for the attribute read the application makes, and
    /// asks the same way it does — by the path it is given, through the file
    /// system, so a plain "…\report " is answered by "report". Here "kept
    /// online" is what a file says about itself.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_neighbour_kept_online_does_not_hide_the_rows_own_text()
    {
        File_("report", "KEPT-ONLINE");
        File_("report ", "its own text");

        var pane = Own(new PaneViewModel(
            Windows<IFileSystemProvider>("WindowsFileSystemProvider"),
            Windows<IFileOperations>("WindowsFileOperations")) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);
        await Until(() => pane.Entries.Count == 2, "the folder did not list its two rows");

        OnlineOnly.Test = path =>
        {
            try { return File.Exists(path) && File.ReadAllText(path) == "KEPT-ONLINE"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        };

        pane.SelectedEntry = pane.Entries.Single(e => e.Name == "report ");
        pane.TogglePreview();

        await Until(() => pane.PreviewText.Length > 0 || pane.PreviewDetail == PaneViewModel.KeptOnline,
                    "nothing was previewed");

        Assert.NotEqual(PaneViewModel.KeptOnline, pane.PreviewDetail);
        Assert.Equal("its own text", pane.PreviewText);

        // The control: the neighbour itself is kept online, and says so.
        pane.SelectedEntry = pane.Entries.Single(e => e.Name == "report");
        await Until(() => pane.PreviewDetail == PaneViewModel.KeptOnline, "the neighbour did not say it was kept online");
    }

    private sealed class Details : IPropertiesProvider
    {
        public List<string> Measured { get; } = [];

        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                Kind = "File",
                IsDirectory = false,
            });

        public ValueTask<SizeProgress> MeasureAsync(string path, IProgress<SizeProgress> progress, CancellationToken ct)
        {
            lock (Measured) Measured.Add(path);
            return ValueTask.FromResult(new SizeProgress(0, 0, 1));
        }

        public bool ShowSystemDialog(string path) => false;
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_folded_file_beside_a_folder_of_its_name_is_measured_as_a_file()
    {
        File_(Path.Combine("report", "inside.bin"), new string('n', 1000));
        var row = File_("report ", "12345");
        var album = Folder("album");

        var provider = new Details();
        var model = new PropertiesViewModel(provider, [row, album]);

        await model.LoadAsync();
        await Until(() => !model.IsMeasuring && model.SizeText.Contains(" folders", StringComparison.Ordinal),
                    "the measure did not finish");

        Assert.Equal($"{ByteSize.Format(5)} · 1 files · 1 folders", model.SizeText);
        lock (provider.Measured) Assert.Equal([album], provider.Measured);
    }
}
