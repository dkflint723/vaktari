using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a batch left behind, all of it.
///
/// **The bar named one file and counted the rest.** "report.docx and 2 more
/// were left behind" is the right length for a line sharing a bar with a
/// progress fraction, and it was the whole of what anybody was ever told: the
/// other two could be found only by comparing the source folder against the
/// destination by hand, which is exactly the work that carrying on past a
/// locked file is supposed to save.
/// </summary>
public sealed class ProblemDetailsTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static ItemProblem Locked(string name)
        => new(Path.Combine("C:", "work", name), new IOException("in use"));

    private static XElement Bar()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "Border")
            .Single(b => (string?)b.Attribute("IsVisible") == "{Binding ShowOperationBar}");

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

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Every_item_left_behind_gets_a_row()
    {
        var rows = ShellViewModel.ListProblems(
            [Locked("report.docx"), Locked("notes.txt"), Locked("data.csv")]);

        Assert.Equal(["report.docx", "notes.txt", "data.csv"], rows.Select(r => r.Name));
    }

    /// <summary>The leaf name stops being an answer the moment a copy has
    /// subfolders in it: three "index.html" left behind name none of the
    /// three.</summary>
    [Fact]
    public void A_row_carries_the_whole_path()
    {
        var problem = Locked("report.docx");

        var row = Assert.Single(ShellViewModel.ListProblems([problem]));

        Assert.Equal(problem.Path, row.Path);
        Assert.NotEqual(row.Name, row.Path);
    }

    [Fact]
    public void A_row_says_why_in_plain_words()
    {
        var row = Assert.Single(ShellViewModel.ListProblems(
            [new ItemProblem("/home/flint/notes.txt", new UnauthorizedAccessException())]));

        Assert.Contains("permission", row.Reason);
        Assert.DoesNotContain("Exception", row.Reason);
    }

    [Fact]
    public void A_folder_left_behind_gets_a_named_row()
    {
        var row = Assert.Single(ShellViewModel.ListProblems(
            [new ItemProblem("/home/flint/photos" + Path.DirectorySeparatorChar,
                             new IOException("busy"))]));

        Assert.Equal("photos", row.Name);
    }

    /// <summary>
    /// **A folder drags its contents down with it.** The engines record every
    /// planned descendant of a folder they could not create, so a raw list is
    /// hundreds of rows about one unreadable folder — the number
    /// RetryRoots.Outermost already exists to refuse for the retry button.
    /// </summary>
    [Fact]
    public void A_folder_that_failed_stands_for_its_contents()
    {
        var rows = ShellViewModel.ListProblems(
        [
            new ItemProblem(Path.Combine("C:", "work", "site"), new IOException("busy")),
            new ItemProblem(Path.Combine("C:", "work", "site", "index.html"), new IOException("busy")),
            new ItemProblem(Path.Combine("C:", "work", "notes.txt"), new IOException("in use")),
        ]);

        Assert.Equal(["site", "notes.txt"], rows.Select(r => r.Name));
    }

    /// <summary>
    /// The wiring: that the shell asks for the list at all. Everything above
    /// calls the builder by hand and is blind to it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_finished_batch_hands_the_bar_everything_it_left_behind()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        var handle = new OperationHandle();

        shell.ActiveTab!.Adopt(handle);
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "report.docx", "notes.txt", "data.csv" })
            handle.ItemFailed(Path.Combine(Path.GetTempPath(), name), new IOException("in use"));

        handle.Complete();

        // Wall-clock rather than a number of turns, for the reason
        // OperationProgressTests.A_running_copy_drives_the_bar records: the
        // completion goes through a pool continuation before it posts, and
        // fifty immediate yields did not outlast one on a loaded CI runner.
        for (var i = 0; i < 400 && shell.ActiveOperation is not null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(shell.CanShowProblems);
        Assert.Equal(["report.docx", "notes.txt", "data.csv"],
                     shell.OperationProblems.Select(r => r.Name));

        // The finding restated: the sentence still abridges, the list does not.
        Assert.Contains("2 more", shell.OperationStatus);
    }

    [AvaloniaFact]
    public void Dismissing_the_message_takes_the_list_with_it()
    {
        var shell = Own(new ShellViewModel(new Inert())
        {
            OperationStatus = "report.docx and 2 more were left behind",
            OperationProblems = ShellViewModel.ListProblems([Locked("report.docx")]),
        });

        Assert.True(shell.CanShowProblems);

        shell.DismissOperationStatusCommand.Execute(null);

        Assert.Empty(shell.OperationProblems);
        Assert.False(shell.CanShowProblems);
    }

    /// <summary>And the same in the other exit.</summary>
    [AvaloniaFact]
    public void Retrying_takes_the_list_with_the_offer()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        shell.Start(null, Path.GetTempPath());

        shell.OperationStatus = "report.docx and 2 more were left behind";
        shell.OperationProblems = ShellViewModel.ListProblems([Locked("report.docx")]);
        shell.Retryable = new RetryOffer(1, () => new OperationHandle());

        Assert.True(shell.CanShowProblems);

        shell.RetryOperationCommand.Execute(null);

        Assert.Empty(shell.OperationProblems);
        Assert.False(shell.CanShowProblems);
    }

    /// <summary>CanShowProblems is computed, so without this hook the button
    /// never appears however full the list is.</summary>
    [AvaloniaFact]
    public void Setting_the_list_tells_the_button()
    {
        var shell = Own(new ShellViewModel(new Inert()));

        var raised = new List<string?>();

        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.OperationProblems = ShellViewModel.ListProblems([Locked("report.docx")]);

        Assert.Contains(nameof(ShellViewModel.CanShowProblems), raised);
    }

    /// <summary>
    /// The markup: on the bar, docked so a 255-character filename cannot push
    /// it off, absent rather than present-and-empty, and wired to the real
    /// collection.
    /// </summary>
    [Fact]
    public void The_bar_offers_the_list_only_when_there_is_one()
    {
        var button = Bar().Descendants(Avalonia + "Button")
            .Single(b => (string?)b.Attribute("IsVisible") == "{Binding CanShowProblems}");

        Assert.Equal("Right", (string?)button.Attribute("DockPanel.Dock"));

        var list = Assert.Single(button.Descendants(Avalonia + "ListBox"));

        Assert.Equal("{Binding OperationProblems}", (string?)list.Attribute("ItemsSource"));

        var flyout = Assert.Single(button.Descendants(Avalonia + "Flyout"));

        Assert.Equal("TopEdgeAlignedRight", (string?)flyout.Attribute("Placement"));

        Assert.Equal("What was left behind",
                     (string?)button.Attribute("AutomationProperties.Name"));
    }

    /// <summary>
    /// The row template: name, reason, and the full path a hover away. The name
    /// alone would only repeat the sentence, and the reason is what says
    /// whether to go and close a program or go and free some disk.
    /// </summary>
    [Fact]
    public void Each_row_names_the_file_and_says_why()
    {
        var template = Bar().Descendants(Avalonia + "DataTemplate")
            .Single(t => (string?)t.Attribute(X + "DataType") == "vm:ProblemRow");

        var bound = template.Descendants(Avalonia + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .ToList();

        Assert.Contains("{Binding Name}", bound);
        Assert.Contains("{Binding Reason}", bound);

        var grid = template.Descendants(Avalonia + "Grid").Single();

        Assert.Equal("{Binding Path}", (string?)grid.Attribute("ToolTip.Tip"));

        // MarkupRulesTests' hit-test rule reaches only <Panel> elements inside
        // fs:FileEntry templates, so this Grid is outside it twice over: with no
        // brush the path tip is dead everywhere but over the letters.
        Assert.Equal("Transparent", (string?)grid.Attribute("Background"));
    }
}
