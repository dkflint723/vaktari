using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Ctrl+C on a "report " row, Ctrl+V in a plain pane that already holds
/// "report".** From the seventh review round (FINDING 7-B): with the source
/// folder opened through "\\?\" or "\??\", the row keeps its true name, and a
/// paste into a pane opened by the ordinary name composed "dst\report " —
/// which Win32 folds to "report". The prompt asked about "dst\report ", and
/// Overwrite replaced the destination's own "report"; Ctrl+X then Ctrl+V also
/// took the source away.
///
/// Driven through both panes' own commands, on the real Windows listing and
/// engine, in a temporary folder, with the prompt answering Overwrite to
/// whatever it is asked: the destination's "report" must be untouched, the
/// source still there, and the prompt never asked about a file it would
/// misname. Shares ConflictPromptTests' collection, the other class that sets
/// the prompt.
/// </summary>
[Collection(ConflictPromptCollection.Name)]
public sealed class TrailingNamePasteTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-trailingpaste").FullName;
    private readonly Shelf _shelf = new();

    private string Source => Path.Combine(_root, "src");

    private string Destination => Path.Combine(_root, "dst");

    public override void Dispose()
    {
        PaneViewModel.AskConflict = null;

        base.Dispose();

        try
        {
            // Through the extended prefix, the only way to reach the trailing
            // names; there is none on Linux, where the class is skipped.
            var root = OperatingSystem.IsWindows() ? Extended(_root) : _root;

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
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

    /// <summary>Holds what was copied, and hands it back to a paste.</summary>
    private sealed class Shelf : IClipboardService
    {
        private ClipboardPayload? _held;

        public Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
        {
            _held = new ClipboardPayload(action, paths);
            return Task.FromResult(true);
        }

        public Task<ClipboardPayload?> GetFilesAsync() => Task.FromResult(_held);

        public Task<bool> HasFilesAsync() => Task.FromResult(_held is not null);

        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
    }

    private async Task<PaneViewModel> Opened(string folder, int rows)
    {
        var pane = Own(new PaneViewModel(
            Windows<IFileSystemProvider>("WindowsFileSystemProvider"),
            Windows<IFileOperations>("WindowsFileOperations"),
            clipboard: _shelf) { ViewportWidth = 1400 });

        await pane.NavigateAsync(folder);
        await Until(() => pane.Entries.Count == rows, $"{folder} did not list its {rows} row(s)");

        return pane;
    }

    [AvaloniaTheory(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [InlineData(@"\\?\", "report ", false)]
    [InlineData(@"\\?\", "report ", true)]
    [InlineData(@"\??\", "report ", false)]
    [InlineData(@"\??\", "report.", true)]
    public async Task A_pasted_trailing_named_row_never_replaces_the_destination_s_own_file(string prefix, string name, bool cut)
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Extended(Path.Combine(Source, name)), "the source");
        File.WriteAllText(Path.Combine(Destination, "report"), "the destination's own report");

        var asked = new List<FileConflict>();

        PaneViewModel.AskConflict = conflict =>
        {
            asked.Add(conflict);
            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Overwrite, false));
        };

        var from = await Opened(prefix + Source, 1);
        var into = await Opened(Destination, 1);

        var row = from.Entries.Single();
        Assert.Equal(name, row.Name);

        from.SelectedEntry = row;

        if (cut) await from.CutSelectionToClipboardAsync();
        else await from.CopySelectionToClipboardAsync();

        var started = new List<IOperationHandle>();
        into.OperationStarted += (_, h) => started.Add(h);

        await into.PasteAsync();

        var paste = Assert.Single(started);
        await paste.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationState.Failed, paste.State);
        Assert.Contains($"\"{name}\"", paste.Error?.Message ?? "", StringComparison.Ordinal);
        Assert.Empty(asked);
        Assert.Equal("the destination's own report", File.ReadAllText(Path.Combine(Destination, "report")));
        Assert.Equal(["report"], Directory.GetFiles(Extended(Destination)).Select(Path.GetFileName));
        Assert.Equal("the source", File.ReadAllText(Extended(Path.Combine(Source, name))));
    }
}

/// <summary>
/// The classes that set <see cref="PaneViewModel.AskConflict"/>, a static the
/// window's prompt also answers through: run one at a time, so one class's
/// answer is never the one another's operation hears.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConflictPromptCollection
{
    public const string Name = "conflict prompt";
}
