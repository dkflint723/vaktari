using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **The row a person clicks, and the path the pane builds from it.** With
/// "report", "report " and "report." in one folder the listing shows all
/// three by their true names, and the pane hands the engine the row's own
/// path — the folder as the pane opened it, joined to the name. Opened by its
/// ordinary name or through "\\.\", that path is one Win32 folds, so "report "
/// would reach "report": the sixth review round found the engine exempting
/// "\\.\" and deleting the wrong file through it. Opened through "\\?\", the
/// path is opened as written and reaches the row that was clicked.
///
/// Driven through the pane's own Shift+Delete command, on the real Windows
/// listing and engine, in a temporary folder. (The bin's route is the
/// engine's, asked in DeviceSpelledTrailingNameTests with a recording
/// recycler, which this assembly cannot reach.)
/// </summary>
public sealed class TrailingNameRowTests : OwnedViewModels
{
    private static readonly Dictionary<string, string> Contents = new()
    {
        ["report"] = "the innocent one",
        ["report "] = "the one with a space",
        ["report."] = "the one with a dot",
    };

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-trailingrow").FullName;

    public TrailingNameRowTests()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var (name, content) in Contents)
            File.WriteAllText(Extended(Path.Combine(_root, name)), content);
    }

    public override void Dispose()
    {
        base.Dispose();

        try
        {
            // Through the extended prefix, which is the only way to reach two of
            // the three names; there is none on Linux, where the class is skipped.
            var root = OperatingSystem.IsWindows() ? Extended(_root) : _root;

            foreach (var file in Directory.GetFiles(root))
                File.Delete(file);

            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    private static string Extended(string path) => @"\\?\" + path;

    private static Dictionary<string, string> Files(string folder)
        => Directory.GetFiles(Extended(folder)).ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

    /// <summary>The Windows listing or engine, by name: this assembly is built
    /// on Linux too, where Vaktari.Ui references no Windows assembly.</summary>
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

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData("", "report ", false)]
    [InlineData("", "report.", false)]
    [InlineData(@"\\.\", "report ", false)]
    [InlineData(@"\\.\", "report.", false)]
    [InlineData(@"\\?\", "report ", true)]
    [InlineData(@"\\?\", "report.", true)]
    public async Task A_trailing_named_row_is_deleted_as_itself_or_refused(string prefix, string name, bool reaches)
    {
        var folder = prefix + _root;
        var pane = Own(new PaneViewModel(Windows<IFileSystemProvider>("WindowsFileSystemProvider"), Windows<IFileOperations>("WindowsFileOperations")) { ViewportWidth = 1400 });
        var started = new List<IOperationHandle>();

        pane.OperationStarted += (_, h) => started.Add(h);

        await pane.NavigateAsync(folder);
        await Until(() => pane.Entries.Count == 3, $"{folder} did not list its three files");

        var row = pane.Entries.Single(e => e.Name == name);

        // What the pane builds: the folder as it opened it, and the true name.
        Assert.Equal(folder + @"\" + name, row.FullPath);

        pane.SelectedEntry = row;
        pane.DeleteSelected();

        var delete = Assert.Single(started);
        await delete.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        if (reaches)
        {
            Assert.Equal(OperationState.Completed, delete.State);
            Assert.Empty(delete.Problems);
            Assert.Equal(Contents.Where(c => c.Key != name).ToDictionary(), Files(_root));
        }
        else
        {
            Assert.Contains("Windows cannot open it by name", Assert.Single(delete.Problems).Error.Message, StringComparison.Ordinal);
            Assert.Equal(Contents, Files(_root));
        }
    }
}
