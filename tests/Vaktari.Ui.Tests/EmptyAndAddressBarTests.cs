using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Two things said in the wrong words, or unsaid.
///
/// **"This folder is empty" was printed over the bin, over Recent and over This
/// PC**, none of which is a folder. Over an empty bin it is worse than clumsy:
/// it invites the reading that a folder somewhere has lost its contents, when
/// what it means is that nothing has been deleted lately. Dolphin says "Trash
/// is empty"; Explorer never calls This PC a folder.
///
/// **And pressing Ctrl+L twice wiped what had been typed.** Ctrl+L, Alt+D and a
/// double-click on the bar all begin editing, and beginning again reset the box
/// to the folder you were in — so half a typed path went, silently, for a
/// keystroke whose meaning is "put me in the address bar" pressed while already
/// there.
/// </summary>
public sealed class EmptyAndAddressBarTests : OwnedViewModels
{
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

    private PaneViewModel At(string path)
        => Own(new PaneViewModel(new Inert()) { CurrentPath = path });

    // ---- what an empty listing says -----------------------------------------

    [AvaloniaFact]
    public void An_empty_bin_is_not_called_a_folder()
    {
        var line = At(VirtualPaths.Trash).EmptyText;

        Assert.DoesNotContain("folder", line);
        Assert.Contains("empty", line);
    }

    /// <summary>
    /// "This folder" is the phrase that has to go: Recent's own line names
    /// folders, and correctly, because folders are what it lists.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("vaktari:computer")]
    [InlineData("vaktari:recent-files")]
    [InlineData("vaktari:recent-locations")]
    public void No_virtual_listing_calls_itself_a_folder(string path)
        => Assert.DoesNotContain("this folder", At(path).EmptyText);

    /// <summary>A real folder still says the true and useful thing.</summary>
    [AvaloniaFact]
    public void A_real_folder_is_still_called_one()
        => Assert.Equal("this folder is empty", At(Path.GetTempPath()).EmptyText);

    /// <summary>
    /// It is a computed property on a path that changes under it, so it has to
    /// announce itself or the first listing's wording sticks for the session.
    /// </summary>
    [AvaloniaFact]
    public void The_line_changes_when_the_listing_does()
    {
        var pane = At(Path.GetTempPath());

        var told = 0;
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.EmptyText)) told++;
        };

        pane.CurrentPath = VirtualPaths.Trash;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(told > 0, "nothing announced EmptyText, so the wording never updates");
    }

    /// <summary>
    /// And the listing reads it, rather than keeping the literal it used to
    /// print. Without this the rule above is correct and shown to nobody.
    /// </summary>
    [AvaloniaFact]
    public void The_listing_shows_the_line_rather_than_a_literal()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        Assert.DoesNotContain("Text=\"this folder is empty\"", markup);
        Assert.Contains("Text=\"{Binding EmptyText}\"", markup);
    }

    // ---- Escape means two different things ----------------------------------

    /// <summary>
    /// **Escape in the listing closed a filter bar that was meant to stay.**
    /// The startup setting opens it deliberately, for people who filter
    /// constantly, and a key pressed to mean "never mind" about anything at all
    /// took it away — with the way back a chip two levels into a menu.
    /// </summary>
    [AvaloniaFact]
    public void Escape_in_the_listing_leaves_the_filter_bar_open()
    {
        var pane = At(Path.GetTempPath());

        pane.IsFilterVisible = true;

        pane.DismissInListing();

        Assert.True(pane.IsFilterVisible);
    }

    /// <summary>It still does the two things Escape has always promised
    /// here.</summary>
    [AvaloniaFact]
    public void Escape_in_the_listing_still_clears_the_filter_and_the_cut()
    {
        var pane = At(Path.GetTempPath());

        pane.IsFilterVisible = true;
        pane.FilterText = "report";
        CutMarks.Mark([Path.Combine(Path.GetTempPath(), "a.txt")]);

        pane.DismissInListing();

        Assert.Equal("", pane.FilterText);
        Assert.Empty(CutMarks.Paths);
        Assert.True(pane.IsFilterVisible);
    }

    /// <summary>
    /// From inside the box it still closes, because closing the box is
    /// something you do TO the box. Losing that would be a worse bug than the
    /// one this fixes: the filter would have no keyboard way out at all.
    /// </summary>
    [AvaloniaFact]
    public void Escape_inside_the_box_still_closes_it_once_it_is_empty()
    {
        var pane = At(Path.GetTempPath());

        pane.IsFilterVisible = true;
        pane.FilterText = "report";

        pane.ClearFilter();
        Assert.True(pane.IsFilterVisible);

        pane.ClearFilter();
        Assert.False(pane.IsFilterVisible);
    }

    /// <summary>
    /// And the listing's Escape reaches the new rule rather than the old one.
    /// The two commands differ by one line, so calling the wrong one leaves
    /// every view-model test above passing and the bug exactly as it was.
    /// </summary>
    [AvaloniaFact]
    public void The_listing_asks_for_the_listing_rule()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        Assert.Contains("_shell.ActiveTab?.DismissInListing()", source);
        Assert.DoesNotContain("_shell.ActiveTab?.ClearFilter()", source);
    }

    // ---- the address bar keeps what you typed --------------------------------

    [AvaloniaFact]
    public void Beginning_to_edit_again_keeps_what_was_typed()
    {
        var pane = At(Path.GetTempPath());

        pane.BeginEditPath();
        pane.PathText = @"C:\half-typed";

        pane.BeginEditPath();

        Assert.Equal(@"C:\half-typed", pane.PathText);
    }

    /// <summary>Not a blanket refusal: the first press still fills the box with
    /// where you are, which is the whole point of it.</summary>
    [AvaloniaFact]
    public void The_first_press_still_shows_the_current_folder()
    {
        var pane = At(Path.GetTempPath());

        pane.PathText = "stale";
        pane.BeginEditPath();

        Assert.Equal(Path.GetTempPath(), pane.PathText);
        Assert.True(pane.IsPathEditing);
    }

    /// <summary>And leaving the bar resets it, so the next press is a first
    /// press again rather than resuming an abandoned edit.</summary>
    [AvaloniaFact]
    public void Leaving_and_returning_shows_the_current_folder_again()
    {
        var pane = At(Path.GetTempPath());

        pane.BeginEditPath();
        pane.PathText = "abandoned";
        pane.IsPathEditing = false;

        pane.BeginEditPath();

        Assert.Equal(Path.GetTempPath(), pane.PathText);
    }
}
