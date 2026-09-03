using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A search that is running, and getting out of one.
///
/// **A running search neither showed that it was running nor could be
/// stopped.** IsSearching was set true past the debounce and false at all three
/// exits, and nothing in the window read it — no binding, no test, nothing — so
/// the flag was state the panel kept about itself and never told anybody.
///
/// The only way out was Escape, which clears the query and so takes the results
/// and the text that produced them with it. Stop is the other exit: the one
/// that keeps a partial answer, which for a broad query is usually the answer
/// you wanted.
/// </summary>
public sealed class SearchProgressTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Yields a few hits and then hangs until cancelled, which is what a walk
    /// of a large tree looks like from here. Records that it was really ended
    /// rather than merely abandoned.
    /// </summary>
    private sealed class Endless : ISearchProvider
    {
        public bool Ended { get; private set; }

        public bool IsAvailable => true;
        public string BackendName => "endless";
        public bool SupportsContentSearch => false;

        public async IAsyncEnumerable<FileEntry> SearchAsync(
            SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return new FileEntry(
                        "hit" + i, Path.Combine(Path.GetTempPath(), "hit" + i), 1,
                        DateTimeOffset.UnixEpoch, EntryFlags.None);

                    await Task.Delay(5, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                Ended = true;
            }
        }
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            if (done()) return;

            await Task.Delay(10);
        }

        Assert.Fail(what);
    }

    /// <summary>
    /// The flag the whole finding is about: it has to be true while the walk
    /// runs, because that is what the bar and the button are bound to.
    /// </summary>
    [AvaloniaFact]
    public async Task A_running_search_says_that_it_is_running()
    {
        var model = new SearchViewModel(new Endless(), () => null) { Query = "hit" };

        await Until(() => model.IsSearching, "the search never reported itself as running");
    }

    /// <summary>
    /// Stop keeps the hits — that is the entire difference between it and
    /// Escape, which clears the query and takes them with it.
    /// </summary>
    [AvaloniaFact]
    public async Task Stop_ends_the_search_and_keeps_what_it_found()
    {
        var backend = new Endless();
        var model = new SearchViewModel(backend, () => null) { Query = "hit" };

        await Until(() => model.Results.Count > 0, "the search never produced a result to keep");

        var found = model.Results.Count;

        model.StopCommand.Execute(null);

        Assert.False(model.IsSearching);
        Assert.Equal(found, model.Results.Count);
        Assert.Equal("hit", model.Query);
        Assert.Contains("stopped", model.Status);

        // And the walk itself is really called off, not just the flag flipped.
        await Until(() => backend.Ended, "the backend was left walking after Stop");
    }

    /// <summary>
    /// Stopping before the debounce has started anything must not leave the
    /// panel claiming a search it never began.
    /// </summary>
    [AvaloniaFact]
    public void Stopping_when_nothing_is_running_says_nothing()
    {
        var model = new SearchViewModel(new Endless(), () => null);

        model.StopCommand.Execute(null);

        Assert.False(model.IsSearching);
        Assert.Equal("", model.Status);
    }

    // ---- and the window actually shows it ----------------------------------

    private static XElement ById(string name)
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
                    .Descendants()
                    .Single(e => (string?)e.Attribute(X + "Name") == name);

    /// <summary>
    /// The bar. The status line changes its words only when a batch lands, so
    /// between batches a walk that was still going looked exactly like one that
    /// had finished.
    /// </summary>
    [Fact]
    public void The_panel_shows_a_bar_while_it_runs()
    {
        var bar = ById("SearchProgress");

        Assert.Equal("ProgressBar", bar.Name.LocalName);
        Assert.Contains("Search.IsSearching", (string?)bar.Attribute("IsVisible") ?? "");
        Assert.Equal("True", (string?)bar.Attribute("IsIndeterminate"));
    }

    /// <summary>And the way out, bound to the command rather than to
    /// nothing.</summary>
    [Fact]
    public void And_a_button_that_stops_it()
    {
        var button = ById("StopSearch");

        Assert.Equal("Button", button.Name.LocalName);
        Assert.Contains("Search.StopCommand", (string?)button.Attribute("Command") ?? "");
        Assert.Contains("Search.IsSearching", (string?)button.Attribute("IsVisible") ?? "");
    }
}
