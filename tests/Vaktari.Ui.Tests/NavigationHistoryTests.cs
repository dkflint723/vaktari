using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where Back and Forward can take you.
///
/// **They exposed one step each, out of a history the pane had kept all
/// along** — and had been writing to the session file since tabs became
/// restorable. Nothing anywhere read the stacks' contents, so a pane ten
/// folders deep could only be walked out one press at a time, with no way to
/// see where the next press went. Both references put that list on the button.
/// </summary>
public sealed class NavigationHistoryTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

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

    private static string In(params string[] parts)
        => Path.Combine([Path.GetTempPath(), .. parts]);

    /// <summary>A pane that has walked four folders deep.</summary>
    private async Task<PaneViewModel> Walked()
    {
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        foreach (var step in new[] { "one", "two", "three", "four" })
            await pane.NavigateAsync(In(step));

        return pane;
    }

    [AvaloniaFact]
    public async Task The_back_button_knows_every_step_behind_it()
    {
        var pane = await Walked();

        Assert.Equal(
            ["three", "two", "one"],
            pane.BackSteps.Select(s => s.Name));
    }

    /// <summary>Nearest first: the place one press away comes first, which is
    /// the order the presses would have taken.</summary>
    [AvaloniaFact]
    public async Task The_nearest_step_is_first()
    {
        var pane = await Walked();

        Assert.Equal(1, pane.BackSteps[0].Depth);
        Assert.Equal(In("three"), pane.BackSteps[0].FullPath);
    }

    /// <summary>
    /// Going back three at once has to leave both stacks as three presses
    /// would have: everything stepped over lands on the forward stack, so
    /// Forward walks back through it.
    /// </summary>
    [AvaloniaFact]
    public async Task Going_back_several_steps_leaves_the_forward_history_behind_it()
    {
        var pane = await Walked();

        await pane.GoBackAsync(3);

        Assert.Equal(In("one"), pane.CurrentPath);

        Assert.Equal(
            ["two", "three", "four"],
            pane.ForwardSteps.Select(s => s.Name));

        Assert.False(pane.CanGoBack);
    }

    [AvaloniaFact]
    public async Task And_forward_walks_back_through_it()
    {
        var pane = await Walked();

        await pane.GoBackAsync(3);
        await pane.GoForwardAsync(2);

        Assert.Equal(In("three"), pane.CurrentPath);
        Assert.Equal(["two", "one"], pane.BackSteps.Select(s => s.Name));
    }

    /// <summary>A row's own command goes where the row says, which is the whole
    /// point of putting the list on the button.</summary>
    [AvaloniaFact]
    public async Task A_row_goes_where_it_says()
    {
        var pane = await Walked();

        pane.BackSteps.Single(s => s.Name == "one").Open.Execute(null);

        // The command starts the navigation; it settles on the next pass.
        await Task.Delay(60);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(In("one"), pane.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Nothing_behind_you_is_an_empty_menu()
    {
        var pane = Own(new PaneViewModel(new Inert()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        Assert.Empty(pane.BackSteps);
        Assert.False(pane.HasBackSteps);
    }

    /// <summary>
    /// Bounded: a long session should not produce a flyout that needs its own
    /// scrollbar.
    /// </summary>
    [AvaloniaFact]
    public void The_menu_is_bounded()
    {
        var deep = Enumerable.Range(0, 40).Select(i => In("f" + i));

        Assert.Equal(12, PaneViewModel.StepsFor(deep, _ => new NoCommand()).Count);
    }

    /// <summary>A virtual listing is named, and a drive root reads as itself
    /// rather than blank.</summary>
    [AvaloniaTheory]
    [InlineData("vaktari:trash")]
    [InlineData("vaktari:computer")]
    public void A_virtual_listing_is_named_rather_than_shown_as_its_scheme(string path)
    {
        var name = PaneViewModel.PlaceName(path);

        Assert.DoesNotContain("vaktari:", name);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The row carries its own command rather than reaching out through
    /// $parent[Window]: a flyout is its own popup root, and a reach-out would
    /// resolve to the SHELL's active tab — the wrong pane on the quiet half of
    /// a split.
    /// </summary>
    [AvaloniaFact]
    public void The_menus_are_on_both_buttons_and_bind_their_own_rows()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var flyouts = markup.Descendants(Avalonia + "MenuFlyout")
            .Where(f => ((string?)f.Attribute("ItemsSource"))?.Contains("Steps", StringComparison.Ordinal) == true)
            .ToList();

        // One each, and the right one on each — two Back menus would look
        // exactly as correct from a count.
        Assert.Equal(
            ["{Binding ActiveTab.BackSteps}", "{Binding ActiveTab.ForwardSteps}"],
            flyouts.Select(f => (string?)f.Attribute("ItemsSource")));

        foreach (var setter in flyouts.Descendants(Avalonia + "Setter")
                     .Where(x => (string?)x.Attribute("Property") == "Command"))
            Assert.Equal("{Binding Open}", (string?)setter.Attribute("Value"));
    }

    private sealed class NoCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
