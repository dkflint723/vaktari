using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Reaching a search result without a mouse.
///
/// **Enter and Down did nothing at all.** The results were a column of Buttons,
/// which carry no selection — so there was nothing for an arrow key to move and
/// nothing for Enter to open. Type-then-Enter is the reflex in both Explorer
/// and Dolphin, and here it dead-ended: a result could only be reached by
/// clicking it, which makes the whole feature unusable without a pointer.
/// </summary>
public sealed class SearchKeyboardTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static FileEntry Hit(string name)
        => new(name, "/found/" + name, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    [AvaloniaFact]
    public void Enter_opens_the_first_result()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        var search = shell.Sidebar.Search;

        search.Results.ReplaceAll([Hit("prog.txt"), Hit("progress.md")]);

        FileEntry? chosen = null;
        search.ResultChosen += (_, entry) => chosen = entry;

        search.OpenFirstCommand.Execute(null);

        Assert.Equal("prog.txt", chosen?.Name);
    }

    /// <summary>With nothing found, Enter does nothing rather than throwing —
    /// the box is empty for most of the time it is open.</summary>
    [AvaloniaFact]
    public void Enter_with_no_results_does_nothing()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        var raised = 0;
        shell.Sidebar.Search.ResultChosen += (_, _) => raised++;

        shell.Sidebar.Search.OpenFirstCommand.Execute(null);

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Down hands the keyboard to the list. The signal pulses rather than
    /// latching, because FocusBehavior acts on the false-to-true edge and the
    /// gesture has to work a second time.
    /// </summary>
    [AvaloniaFact]
    public void Down_asks_for_focus_in_the_results()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        shell.Sidebar.Search.Results.ReplaceAll([Hit("prog.txt")]);

        var edges = 0;
        shell.Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarViewModel.FocusResults) && shell.Sidebar.FocusResults)
                edges++;
        };

        shell.Sidebar.FocusResultsListCommand.Execute(null);
        shell.Sidebar.FocusResultsListCommand.Execute(null);

        Assert.Equal(2, edges);
    }

    [AvaloniaFact]
    public void Down_with_no_results_does_not_move_focus()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());

        shell.Sidebar.FocusResultsListCommand.Execute(null);

        Assert.False(shell.Sidebar.FocusResults);
    }

    /// <summary>
    /// **The results have to be a real list.** Buttons cannot be selected, so
    /// arrow navigation and Enter inside the popup are properties of the
    /// control, not of the view model — a view-model-only test would pass with
    /// the popup still keyboard-dead.
    /// </summary>
    [AvaloniaFact]
    public void The_results_are_a_selectable_list_not_buttons()
    {
        var markup = XDocument.Load(
            Path.Combine(Repo(), "src", "Vaktari.Ui", "MainWindow.axaml"));

        var results = markup
            .Descendants(Avalonia + "ListBox")
            .SingleOrDefault(l => (string?)l.Attribute("Name") == "SearchResults"
                                  || (string?)l.Attribute(XNamespace.Get(
                                        "http://schemas.microsoft.com/winfx/2006/xaml") + "Name")
                                     == "SearchResults");

        Assert.True(results is not null, "the search results are not a ListBox any more");

        // And Enter inside it opens the selected row.
        var enter = results!.Descendants(Avalonia + "KeyBinding")
            .Any(k => (string?)k.Attribute("Gesture") == "Enter");

        Assert.True(enter, "Enter does nothing inside the results list");
    }

    private static string Repo()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        return here ?? throw new InvalidOperationException("could not find the repository root");
    }

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
}
