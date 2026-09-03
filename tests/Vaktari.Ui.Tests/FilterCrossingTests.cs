using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Getting from the filter box to the rows.
///
/// **Both keys somebody presses to leave the box did nothing.** Explorer's box
/// runs the search and moves focus to the results, and Enter in a field above a
/// list means "I am done here" everywhere else on the desktop — so the filter
/// sat holding the keyboard, and the way out was Tab, F6 or the mouse.
///
/// Landing on the rows is only half of it. A filter that has just narrowed the
/// listing leaves no selection behind, so focusing the list without picking a
/// row means Enter changes nothing on screen and Down — which means "go down
/// one row" — moves zero. That reads, to the person pressing the key, exactly
/// like the bug being closed.
/// </summary>
public sealed class FilterCrossingTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static FileEntry Entry(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    private async Task<PaneViewModel> Listing(params string[] names)
    {
        var pane = Own(new PaneViewModel(new Canned([.. names.Select(Entry)]))
        {
            ViewportWidth = 1400,
        });

        await pane.NavigateAsync(Path.GetTempPath());

        return pane;
    }

    /// <summary>
    /// The whole point: the keyboard leaves the box, and lands somewhere the
    /// arrow keys can move FROM.
    /// </summary>
    [AvaloniaFact]
    public async Task Crossing_to_the_rows_lands_on_a_row_rather_than_nowhere()
    {
        var pane = await Listing("a.txt", "b.txt");

        Assert.Null(pane.SelectedEntry);

        pane.GoToListingCommand.Execute(null);

        Assert.Equal("a.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// **A selection that survived the filter is the person's own.** Narrowing
    /// to something you had already picked and then pressing Enter must not
    /// throw that away and jump to the top.
    /// </summary>
    [AvaloniaFact]
    public async Task A_selection_that_survived_the_filter_is_not_moved()
    {
        var pane = await Listing("a.txt", "b.txt");

        pane.SelectedEntry = pane.Entries[1];

        pane.GoToListingCommand.Execute(null);

        Assert.Equal("b.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>And an empty listing is crossed into without a row to pick.</summary>
    [AvaloniaFact]
    public async Task Crossing_into_an_empty_listing_picks_nothing()
    {
        var pane = await Listing();

        pane.GoToListingCommand.Execute(null);

        Assert.Null(pane.SelectedEntry);
    }

    /// <summary>
    /// The filter and its text stay put. This is crossing to the rows, not
    /// finishing with the filter — Escape is what clears it, and somebody who
    /// crosses over and then wants the box back should find their words in it.
    /// </summary>
    [AvaloniaFact]
    public async Task Crossing_keeps_the_filter_and_its_text()
    {
        var pane = await Listing("report.txt");

        pane.ToggleFilterCommand.Execute(null);
        pane.FilterText = "rep";

        pane.GoToListingCommand.Execute(null);

        Assert.True(pane.IsFilterVisible);
        Assert.Equal("rep", pane.FilterText);
    }

    /// <summary>
    /// The signal pulses rather than latching, because the focus behaviour acts
    /// on the false-to-true edge and the gesture has to work a second time.
    /// </summary>
    [AvaloniaFact]
    public async Task Crossing_twice_moves_the_keyboard_twice()
    {
        var pane = await Listing("a.txt");
        var edges = 0;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.FocusListing) && pane.FocusListing) edges++;
        };

        pane.GoToListingCommand.Execute(null);
        pane.GoToListingCommand.Execute(null);

        Assert.Equal(2, edges);
    }

    // ---- and the markup ------------------------------------------------------

    private static XElement FilterBox()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "TextBox")
            .Single(t => (string?)t.Attribute("Text") == "{Binding ActiveTab.FilterText}");

    [Fact]
    public void Both_keys_cross_from_the_box()
    {
        var bound = FilterBox().Descendants(Avalonia + "KeyBinding")
            .ToDictionary(k => (string?)k.Attribute("Gesture") ?? "",
                          k => (string?)k.Attribute("Command") ?? "");

        Assert.Equal("{Binding ActiveTab.GoToListingCommand}", bound["Enter"]);
        Assert.Equal("{Binding ActiveTab.GoToListingCommand}", bound["Down"]);

        // And Escape still clears, which is the gesture these two must not
        // become: leaving the rows is not the same as abandoning the filter.
        Assert.Equal("{Binding ActiveTab.ClearFilterCommand}", bound["Escape"]);
    }

    /// <summary>The box says so, the way the path box above it does.</summary>
    [Fact]
    public void The_box_says_which_keys_leave_it()
    {
        var tip = (string?)FilterBox().Attribute("ToolTip.Tip") ?? "";

        Assert.Contains("enter", tip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("↓", tip);
    }

    /// <summary>
    /// **Every listing, not just the one that happened to be open.** The pulse
    /// is per pane and the three layouts are three controls; a layout left
    /// unwired would swallow the gesture in exactly the view somebody was using
    /// at the time.
    /// </summary>
    [Theory]
    [InlineData("DetailsEntries")]
    [InlineData("CompactEntries")]
    [InlineData("GridEntries")]
    public void Every_layout_can_be_crossed_into(string source)
    {
        var listing = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "ListBox")
            .Single(l => (string?)l.Attribute("ItemsSource") == "{Binding " + source + "}");

        Assert.Equal(
            "{Binding FocusListing}",
            (string?)listing.Attribute(XNamespace.Get(
                "clr-namespace:Vaktari.Ui") + "FocusBehavior.FocusWhen")
            ?? (string?)listing.Attribute("FocusBehavior.FocusWhen"));
    }

    /// <summary>
    /// **The premise, measured rather than assumed:** a KeyBinding on the box
    /// itself is claimed ahead of the TextBox's own caret handling, so Down
    /// crosses to the rows rather than moving the caret. If a future Avalonia
    /// reverses that, the gesture goes dead while still looking shipped, and it
    /// should be a failing test that says so rather than a bug report.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Enter, PhysicalKey.Enter)]
    [InlineData(Key.Down, PhysicalKey.ArrowDown)]
    public void A_binding_on_the_box_is_claimed_before_the_caret(Key key, PhysicalKey physical)
    {
        var ran = 0;
        var box = new TextBox { Text = "report", CaretIndex = 3 };

        box.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(key),
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => ran++),
        });

        var window = new Window { Content = box };

        window.Show();
        box.Focus();

        window.KeyPress(key, RawInputModifiers.None, physical, null);

        Assert.Equal(1, ran);
        Assert.True(box.IsFocused, "the box lost the keyboard to the key itself");
        Assert.Equal(3, box.CaretIndex);

        window.Close();
    }

    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
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
}
