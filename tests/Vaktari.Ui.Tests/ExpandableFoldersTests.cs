using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Opening a folder where it stands, in the list view — Dolphin's expandable
/// folders.
///
/// **The list could not look inside anything.** There was no TreeView, no set
/// of open paths and no per-row depth anywhere in the application; the details
/// rows were a flat list over one collection, so comparing two files a
/// directory apart meant navigating in, navigating out, and losing your place
/// both times.
///
/// The tree is a PROJECTION over the flat listing rather than a change to it.
/// That is the whole design, and most of what these tests are about: what is on
/// screen gains the children, and everything that describes the FOLDER — the
/// count in the status bar, the group bands, the "N of M" filter line, the
/// watcher's sorted insert, and the two grid layouts that bind the same
/// collection — goes on describing the folder.
/// </summary>
public sealed class ExpandableFoldersTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root => Path.Combine(Path.GetTempPath(), "vaktari-expand");

    private static string In(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Runs the dispatcher until an expansion's directory read and the
    /// re-projection behind it have both landed.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }

    /// <summary>Settles, and gives a continuation that answered on the thread
    /// pool real time to arrive: the fake holds its reads behind a
    /// TaskCompletionSource awaited with ConfigureAwait(false), and sixty
    /// dispatcher pumps with no wall-clock in them were measured not to be
    /// enough for one of those to land.</summary>
    private static async Task Drain()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }

    private static List<string> Names(PaneViewModel pane)
        => pane.DetailsEntries.Select(e => e.Name).ToList();

    /// <summary>
    /// One folder with a folder in it, so nesting has somewhere to go.
    ///
    ///   docs/            inner/          deep.txt
    ///                    kid-a.txt
    ///                    kid-b.txt
    ///   a.txt
    ///   z.txt
    /// </summary>
    private static Tree Sample()
    {
        var tree = new Tree();

        tree.Put(Root, Tree.Dir("docs"), Tree.File("a.txt"), Tree.File("z.txt"));
        tree.Put(In("docs"), Tree.Dir("inner"), Tree.File("kid-a.txt"), Tree.File("kid-b.txt"));
        tree.Put(In("docs", "inner"), Tree.File("deep.txt"));

        return tree;
    }

    private async Task<(PaneViewModel Pane, Tree Fs)> Pane(Tree? tree = null)
    {
        var fs = tree ?? Sample();
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Root);
        await Settle();

        return (pane, fs);
    }

    private static async Task Open(PaneViewModel pane, string path)
    {
        var row = Assert.Single(pane.DetailsEntries, e => e.FullPath == path);

        await pane.ToggleExpandAsync(row);
        await Settle();
    }

    // ---- what the gesture does ---------------------------------------------

    /// <summary>
    /// The whole feature in one assertion: the folder's rows appear under the
    /// folder, in its own order, and the rows around it do not move.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_folder_puts_its_rows_underneath_it()
    {
        var (pane, _) = await Pane();

        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));

        await Open(pane, In("docs"));

        Assert.Equal(["docs", "inner", "kid-a.txt", "kid-b.txt", "a.txt", "z.txt"],
                     Names(pane));

        // And the row says so, which is what turns its triangle.
        Assert.Contains(In("docs"), pane.Expanded);
    }

    /// <summary>And closing it takes exactly those rows away again.</summary>
    [AvaloniaFact]
    public async Task Closing_a_folder_takes_its_rows_away_again()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));
        await Open(pane, In("docs"));

        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));
        Assert.False(pane.IsExpanded(In("docs")));

        // Nothing left indented either: a stale map would keep a row shifted
        // right with nothing above it to explain the shift.
        Assert.Empty(pane.Indents);
    }

    /// <summary>
    /// The indent is what says a row came from inside something. The folder's
    /// own rows are absent from the map rather than present with a zero, so an
    /// unopened listing carries nothing at all.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_from_inside_is_indented_and_the_rows_beside_it_are_not()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        Assert.Equal(pane.IndentStep, pane.Indents[In("docs", "kid-a.txt")]);

        Assert.False(pane.Indents.ContainsKey(In("docs")));
        Assert.False(pane.Indents.ContainsKey(In("a.txt")));
    }

    /// <summary>
    /// A folder inside an open folder opens too, one step further in. Anything
    /// less would leave the second triangle on screen with nothing behind it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_inside_an_open_one_nests_a_step_further()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));
        await Open(pane, In("docs", "inner"));

        Assert.Equal(
            ["docs", "inner", "deep.txt", "kid-a.txt", "kid-b.txt", "a.txt", "z.txt"],
            Names(pane));

        Assert.Equal(pane.IndentStep * 2, pane.Indents[In("docs", "inner", "deep.txt")]);
    }

    // ---- the interactions ---------------------------------------------------

    /// <summary>
    /// **A band belongs to the LISTING, not to a folder inside it.** The header
    /// is drawn on the first row of each run down the folder's own rows, so a
    /// subfolder's contents have no heading anywhere to explain an order sorted
    /// by one — they take the field order alone.
    ///
    /// Measured with two children whose size bands and names disagree: banded,
    /// the 200 MiB file sorts after the 1-byte one; by name it sorts before.
    /// </summary>
    [AvaloniaFact]
    public async Task Rows_from_inside_are_ordered_without_the_bands()
    {
        var tree = new Tree();

        tree.Put(Root, Tree.Dir("docs"));
        tree.Put(In("docs"),
                 Tree.File("a-big.txt", 200L * 1024 * 1024),
                 Tree.File("b-small.txt", 1));

        var (pane, _) = await Pane(tree);

        pane.GroupBy = GroupMode.Size;

        await Open(pane, In("docs"));

        Assert.Equal(["docs", "a-big.txt", "b-small.txt"], Names(pane));

        // And again through the re-order a sort asks for, which is a second
        // route to the same comparer and the one a stored list reaches.
        pane.SortBy("name");

        Assert.Equal(["docs", "b-small.txt", "a-big.txt"], Names(pane));
    }

    /// <summary>
    /// And a row from inside carries no heading of its own, nor takes one off
    /// the row it was spliced in front of.
    ///
    /// The grouping is chosen AFTER the folder is opened, because that is the
    /// route that recomputes the headings with a tree already on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_from_inside_carries_no_band_heading()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.GroupBy = GroupMode.Kind;

        Assert.Null(pane.HeaderFor(In("docs", "kid-a.txt")));

        // The other half: the first file of the folder's own run still starts
        // it, rather than having lost the heading to a child above it.
        Assert.Equal("TXT", pane.HeaderFor(In("a.txt"))?.Label);
    }

    /// <summary>
    /// **The filter asks about this folder and answers flat.** Applying it to
    /// the children as well would mean a child that matches under a parent that
    /// does not — an orphan row, or a rule that keeps unmatched ancestors on
    /// screen and leaves the count line unable to say what it filtered.
    ///
    /// Ignored, not cleared: clear the box and the tree is where it was.
    /// </summary>
    [AvaloniaFact]
    public async Task A_filter_folds_the_tree_away_and_clearing_it_brings_it_back()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        await Filter(pane, "docs");

        Assert.Equal(["docs"], Names(pane));

        await Filter(pane, "");

        Assert.Equal(["docs", "inner", "kid-a.txt", "kid-b.txt", "a.txt", "z.txt"],
                     Names(pane));
    }

    /// <summary>
    /// And a sort made while the filter is up reaches the folded-away tree, so
    /// clearing the box does not bring back a subtree running the other way to
    /// the listing around it.
    ///
    /// The resort behind a filter goes through the filter rather than the plain
    /// rebuild — rank F1's rule — so this is the second of the two places that
    /// re-orders what is open, and the only one that route reaches.
    /// </summary>
    [AvaloniaFact]
    public async Task A_sort_made_behind_a_filter_reaches_the_tree_it_folded_away()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        await Filter(pane, "docs");

        pane.SortBy("name");

        await Filter(pane, "");

        Assert.Equal(["docs", "inner", "kid-b.txt", "kid-a.txt", "z.txt", "a.txt"],
                     Names(pane));
    }

    /// <summary>**The filter box is debounced by 120 ms**, so reading the
    /// listing straight after setting the text would test nothing.</summary>
    private static async Task Filter(PaneViewModel pane, string text)
    {
        pane.FilterText = text;

        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// **The tree is a details-only shape**, the way a grouping is: the other
    /// two layouts lay out fixed-size cells with room for neither an indent nor
    /// a triangle, and they bind the folder's own rows.
    ///
    /// Asked through the type-ahead, which is where it costs something: a key
    /// that jumps the selection to a row the layout on screen does not draw
    /// scrolls to nothing and leaves the keyboard somewhere invisible.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_in_the_grid_cannot_reach_a_row_only_the_list_shows()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.View = ViewMode.Grid;

        pane.TypeAhead("kid-a");

        Assert.NotEqual("kid-a.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// And the tree is still there on the way back — including when the listing
    /// was rebuilt while the grid had it, which is the case nothing else would
    /// have put right.
    /// </summary>
    [AvaloniaFact]
    public async Task The_tree_survives_a_trip_through_the_grid_and_a_sort()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.View = ViewMode.Grid;
        pane.SortBy("name");
        pane.View = ViewMode.Details;

        // Folders still come first — that rule is ahead of the direction — so
        // what reverses is the files beside docs and the rows inside it.
        Assert.Equal(["docs", "inner", "kid-b.txt", "kid-a.txt", "z.txt", "a.txt"],
                     Names(pane));
    }

    /// <summary>Sorting the folder keeps what was open, and re-splices it into
    /// the new order.</summary>
    [AvaloniaFact]
    public async Task Sorting_the_folder_keeps_the_tree()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.SortBy("name");

        // Folders still come first — that rule is ahead of the direction — so
        // what reverses is the files beside docs and the rows inside it.
        Assert.Equal(["docs", "inner", "kid-b.txt", "kid-a.txt", "z.txt", "a.txt"],
                     Names(pane));
    }

    /// <summary>
    /// Going somewhere else forgets it: a path opened here means nothing in the
    /// next folder, and carrying the set over would keep rows alive against a
    /// listing that has none of them.
    /// </summary>
    [AvaloniaFact]
    public async Task Leaving_the_folder_forgets_what_was_open()
    {
        var (pane, fs) = await Pane();

        fs.Put(In("elsewhere"), Tree.File("other.txt"));

        await Open(pane, In("docs"));

        await pane.NavigateAsync(In("elsewhere"));
        await Settle();

        await pane.NavigateAsync(Root);
        await Settle();

        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));
    }

    /// <summary>
    /// **A refresh keeps it and re-reads it.** Refreshes are constant — a
    /// rename, a paste, a delete and an undo all end in one — so collapsing on
    /// them would make the feature unusable at exactly the moment it is wanted.
    /// And the watcher watches the folder you are in and nothing below it, so a
    /// refresh is the only moment an open subfolder can learn that something
    /// inside it changed.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_keeps_the_tree_and_re_reads_it()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        fs.Put(In("docs"),
               Tree.Dir("inner"), Tree.File("kid-a.txt"), Tree.File("kid-b.txt"),
               Tree.File("kid-c.txt"));

        await pane.RefreshAsync();
        await Settle();

        Assert.Equal(
            ["docs", "inner", "kid-a.txt", "kid-b.txt", "kid-c.txt", "a.txt", "z.txt"],
            Names(pane));
    }

    /// <summary>
    /// And a folder that cannot be read any more drops out of the set rather
    /// than staying open over the rows it held before it went.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_the_refresh_cannot_read_stops_being_open()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        fs.Refuse(In("docs"));

        await pane.RefreshAsync();
        await Settle();

        Assert.False(pane.IsExpanded(In("docs")));
        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));
    }

    /// <summary>
    /// **What the status bar counts is the folder, not the screen.** A folder
    /// does not become bigger because you looked inside one of the folders in
    /// it, and a count that grew would be the one number on screen that nothing
    /// else agrees with — the "N of M" filter line beside it counts the folder
    /// too.
    /// </summary>
    [AvaloniaFact]
    public async Task The_status_bar_still_counts_the_folder_you_are_in()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        Assert.Equal("1 folder, 2 files", pane.Summary);
    }

    /// <summary>
    /// **Select-everything takes the FOLDER, not the screen.**
    ///
    /// A folder opened in place puts rows from two folders into one listing,
    /// and Ctrl+A is the gesture that takes them both without being asked. Copy
    /// a folder and something inside it and the child lands at the destination
    /// twice; delete both and the folder goes, then the child cannot be found;
    /// and the batch-rename preview computes its collisions against the wrong
    /// folder. Ctrl+clicking across the boundary is still allowed — that one is
    /// deliberate — but the keystroke is not.
    ///
    /// So the heading box goes on meaning the folder too: ticking every row of
    /// it is "all", with the children under them untouched.
    /// </summary>
    [AvaloniaFact]
    public async Task Select_everything_takes_the_folder_and_not_the_screen()
    {
        var (pane, _) = await Pane();

        var list = new ListBox
        {
            Width = 400,
            SelectionMode = SelectionMode.Multiple,
            DataContext = pane,
        };

        list.Bind(ItemsControl.ItemsSourceProperty,
                  new Avalonia.Data.Binding(nameof(PaneViewModel.DetailsEntries)));

        list.SelectedItems = pane.DetailsSelection;

        var window = new Window { Content = list, Width = 400, Height = 300 };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        try
        {
            await Open(pane, In("docs"));

            Assert.Equal(6, pane.DetailsEntries.Count());

            MainWindow.SelectWholeFolder(list, pane);

            Assert.Equal(
                ["a.txt", "docs", "z.txt"],
                pane.DetailsSelection.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
                    .ToList());

            // And the heading's box agrees, because it counts the same rows.
            Assert.True(pane.AllChosen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A GUARD, and it is labelled one because it cannot fail for the capture
    /// it looks like it is testing.
    ///
    /// Measured against this ListBox, bound to the pane the two ways the markup
    /// binds the real one: neither the ItemsSource swap on the first open — the
    /// listing changing from the folder's rows to the spliced ones — nor the
    /// Reset the watcher's rebuild raises loses the selection, because FileEntry
    /// is a record struct with value equality and the selected row is present
    /// in both collections. Taking the capture out of Republish leaves this
    /// green.
    ///
    /// It is here because the property is worth holding whatever the mechanism
    /// underneath it turns out to be: a folder opened in place must not cost
    /// you the files you had picked.
    /// </summary>
    [AvaloniaFact]
    public async Task The_listing_keeps_its_selection_through_the_splice()
    {
        var (pane, fs) = await Pane();

        var list = new ListBox
        {
            Width = 400,
            SelectionMode = SelectionMode.Multiple,
            DataContext = pane,
        };

        // Both bindings, the way the real listing has them: the focused row and
        // the selection sit behind one selection model, and a harness with only
        // half of it is not the control this has to work in.
        list.Bind(ItemsControl.ItemsSourceProperty,
                  new Avalonia.Data.Binding(nameof(PaneViewModel.DetailsEntries)));

        list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                  new Avalonia.Data.Binding(nameof(PaneViewModel.SelectedEntry)));

        list.SelectedItems = pane.DetailsSelection;

        var window = new Window { Content = list, Width = 400, Height = 300 };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        try
        {
            pane.DetailsSelection.Add(pane.DetailsEntries.Single(e => e.Name == "z.txt"));

            await Open(pane, In("docs"));

            Assert.Equal("z.txt", Assert.Single(pane.DetailsSelection).Name);

            fs.Describe(In("m.txt"));
            fs.Raise(new FileSystemChange(ChangeKind.Added, In("m.txt")));

            await Settle();

            Assert.Equal("z.txt", Assert.Single(pane.DetailsSelection).Name);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A row from inside is an ordinary row, so a rebuild has to put it back
    /// the way it puts every other selected row back.
    /// </summary>
    [AvaloniaFact]
    public async Task A_selected_row_from_inside_survives_a_sort()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.DetailsSelection.Add(
            pane.DetailsEntries.Single(e => e.Name == "kid-b.txt"));

        pane.SortBy("name");

        Assert.Equal("kid-b.txt", Assert.Single(pane.DetailsSelection).Name);
    }

    /// <summary>And typing reaches it, for the same reason: a row you can see
    /// and cannot jump to is worse than one that is not there.</summary>
    [AvaloniaFact]
    public async Task Typing_reaches_a_row_from_inside_an_open_folder()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.TypeAhead("kid-b");

        Assert.Equal("kid-b.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// And the same letter again cycles from where it landed, which is the
    /// convention every list control shares — so the "which row is this" step
    /// has to be able to find a row from inside an open folder. It asks by
    /// path, because IReadOnlyList has no IndexOf and FileEntry is a record
    /// struct carrying a length and a timestamp.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_the_same_letter_cycles_through_the_rows_from_inside()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        pane.TypeAhead("k");

        Assert.Equal("kid-a.txt", pane.SelectedEntry?.Name);

        pane.TypeAhead("k");

        Assert.Equal("kid-b.txt", pane.SelectedEntry?.Name);
    }

    // ---- the watcher ---------------------------------------------------------

    /// <summary>
    /// A file arriving in the folder lands in the folder's own order, with the
    /// tree still spliced around it. The batch keeps its incremental insert
    /// into the flat listing and re-projects once for the whole burst.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_arriving_lands_beside_the_tree()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        fs.Describe(In("m.txt"));
        fs.Raise(new FileSystemChange(ChangeKind.Added, In("m.txt")));

        await Settle();

        Assert.Equal(
            ["docs", "inner", "kid-a.txt", "kid-b.txt", "a.txt", "m.txt", "z.txt"],
            Names(pane));
    }

    /// <summary>
    /// **A folder that goes comes back shut.** The open set outlives what is on
    /// screen — the splice is derived from the listing, so a row the listing
    /// no longer has cannot be drawn — so a folder that was DELETED has to be
    /// taken out of it by hand, and so does anything opened inside it.
    /// Otherwise a folder re-created under the same name comes back holding the
    /// rows its namesake had before it went.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_goes_and_comes_back_comes_back_shut()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));
        await Open(pane, In("docs", "inner"));

        fs.Describe(In("docs"), EntryFlags.Directory);

        fs.Raise(new FileSystemChange(ChangeKind.Removed, In("docs")));
        await Settle();

        fs.Raise(new FileSystemChange(ChangeKind.Added, In("docs")));
        await Settle();

        Assert.False(pane.IsExpanded(In("docs")));
        Assert.False(pane.IsExpanded(In("docs", "inner")));
    }

    /// <summary>
    /// **A folder that cannot be read says so and stays shut.** The row is one
    /// click away from a permission error on either platform, and a triangle
    /// that turned down over nothing would read as an empty folder.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_cannot_be_read_says_so_and_stays_shut()
    {
        var tree = Sample();

        tree.Refuse(In("docs"));

        var (pane, _) = await Pane(tree);

        await Open(pane, In("docs"));

        Assert.False(pane.IsExpanded(In("docs")));
        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));
        Assert.Equal("could not open docs", pane.Status);
    }

    /// <summary>
    /// **A read still in flight when you navigate away belongs to the folder
    /// you left.** The rule every awaited step in this pane follows: the
    /// generation is captured before the read and re-checked after it.
    ///
    /// The splice itself cannot show the rows — it is derived from the listing,
    /// which is somewhere else now — but the OPEN SET would carry a folder from
    /// the previous listing into this one, where every later refresh would go
    /// on re-reading it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_read_that_lands_after_you_have_moved_on_is_dropped()
    {
        var (pane, fs) = await Pane();

        fs.Put(In("elsewhere"), Tree.File("other.txt"));
        fs.Hold(In("docs"));

        var row = pane.DetailsEntries.Single(e => e.Name == "docs");
        var opening = pane.ToggleExpandAsync(row);

        await pane.NavigateAsync(In("elsewhere"));
        await Settle();

        fs.Release(In("docs"));

        await opening;
        await Settle();

        Assert.False(pane.IsExpanded(In("docs")));
        Assert.Equal(["other.txt"], Names(pane));
    }

    // ---- which listings offer it at all -------------------------------------

    /// <summary>
    /// **Only a real folder.** The bin and Recent hold rows naming where a file
    /// USED to be, This PC holds volumes rather than directories, and a search
    /// result is a path from anywhere on the machine — opening one in place
    /// would list something the row does not stand for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_listing_that_is_not_a_folder_offers_no_triangle()
    {
        var (pane, _) = await Pane();

        Assert.True(pane.CanExpandRows);

        var folder = pane.DetailsEntries.Single(e => e.Name == "docs");

        await pane.NavigateAsync(VirtualPaths.Files);
        await Settle();

        Assert.False(pane.CanExpandRows);

        // And the gesture is refused as well as hidden: a command is reachable
        // without the control that draws it.
        await pane.ToggleExpandAsync(folder);
        await Settle();

        Assert.False(pane.IsExpanded(In("docs")));
    }

    // ---- the triangle, and the press that turns it --------------------------

    /// <summary>
    /// The heading and the rows reserve one slot between them, so the columns
    /// stay over the cells they label. The same arrangement the version-control
    /// mark uses, and the same reason the numbers are read from both sites
    /// rather than trusted: two grids kept in step by hand.
    /// </summary>
    [Fact]
    public void The_heading_reserves_the_same_slot_the_rows_do()
    {
        var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        var heading = doc.Descendants(Xaml + "Border")
            .Single(b => (string?)b.Attribute(X + "Name") == "ExpandHeadingSlot");

        var cell = doc.Descendants(Xaml + "ListBox")
            .Single(l => (string?)l.Attribute("ItemsSource") == "{Binding DetailsEntries}")
            .Descendants()
            .Single(e => (string?)e.Attribute("Classes") == MainWindow.ExpanderClass);

        // Both halves must carry the numbers, or the equality below would be
        // satisfied by two absent attributes.
        Assert.NotNull((string?)heading.Attribute("Width"));
        Assert.NotNull((string?)heading.Attribute("Margin"));

        Assert.Equal((string?)cell.Attribute("Width"), (string?)heading.Attribute("Width"));
        Assert.Equal((string?)cell.Attribute("Margin"), (string?)heading.Attribute("Margin"));

        // And the slot goes when the listing stops offering the gesture, on
        // both sides — or the heading would keep a slot the rows gave back.
        Assert.Equal("{Binding CanExpandRows}", (string?)heading.Attribute("IsVisible"));

        Assert.Equal(
            "{Binding $parent[ListBox].((vm:PaneViewModel)DataContext).CanExpandRows}",
            (string?)cell.Attribute("IsVisible"));

        // Transparent rather than unpainted: a Panel with no Background is
        // invisible to the pointer in Avalonia, so the triangle would draw and
        // never take a press.
        Assert.Equal("Transparent", (string?)cell.Attribute("Background"));
    }

    /// <summary>
    /// **The shape moves, the element does not.** Decoration that appears and
    /// disappears under the pointer changes what the second click of a
    /// double-click lands on, and Avalonia's gesture needs both clicks on the
    /// same element — the rule MarkupRulesTests states in general. So the
    /// triangle is one Path whose Data is null for a row that is not a folder,
    /// rather than two Paths taking turns.
    /// </summary>
    [Fact]
    public void The_triangle_changes_shape_rather_than_coming_and_going()
    {
        var cell = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ListBox")
            .Single(l => (string?)l.Attribute("ItemsSource") == "{Binding DetailsEntries}")
            .Descendants()
            .Single(e => (string?)e.Attribute("Classes") == MainWindow.ExpanderClass);

        var glyph = Assert.Single(cell.Descendants(Xaml + "Path"));

        Assert.Null((string?)glyph.Attribute("IsVisible"));
        Assert.Equal("False", (string?)glyph.Attribute("IsHitTestVisible"));
        Assert.Single(glyph.Descendants(Xaml + "MultiBinding"));

        // And it is sized by a metric rather than by a literal, so it keeps its
        // proportion to the slot it sits in when the pane is zoomed. The slot
        // is one row icon; the glyph is the smaller number derived beside it.
        Assert.Equal("{DynamicResource IconSize}", (string?)cell.Attribute("Width"));
        Assert.Equal("{DynamicResource TwistSize}", (string?)glyph.Attribute("Width"));
        Assert.Equal("{DynamicResource TwistSize}", (string?)glyph.Attribute("Height"));
    }

    /// <summary>
    /// What the converter behind that Data answers: nothing for a file, and two
    /// different shapes for a folder depending on whether it is open.
    /// </summary>
    [Fact]
    public void A_file_gets_no_triangle_and_a_folder_gets_one_of_two()
    {
        var open = new HashSet<string>(StringComparer.Ordinal) { "/a/docs" };

        Assert.Null(Twisty("/a/note.txt", isDirectory: false, open));

        var shut = Twisty("/a/other", isDirectory: true, open);
        var showing = Twisty("/a/docs", isDirectory: true, open);

        Assert.NotNull(shut);
        Assert.NotNull(showing);
        Assert.NotSame(shut, showing);
    }

    private static object? Twisty(string path, bool isDirectory, IReadOnlySet<string> open)
        => FileConverters.Twisty.Convert(
            [path, isDirectory, open], typeof(object), null,
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The indent the row draws, which is the map read through the
    /// same binding the template uses. The map holds pixels, so the converter's
    /// job is the lookup and the zero for a row that is not in it.</summary>
    [Fact]
    public void The_indent_is_what_the_map_holds_and_nothing_at_the_top()
    {
        var indents = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["/a/docs/kid.txt"] = 16,
            ["/a/docs/inner/deep.txt"] = 32,
        };

        Assert.Equal(16d, Indent("/a/docs/kid.txt", indents));
        Assert.Equal(32d, Indent("/a/docs/inner/deep.txt", indents));
        Assert.Equal(0d, Indent("/a/docs", indents));
    }

    /// <summary>
    /// **And the step is one row icon, which is a metric rather than a
    /// number.** PaneScale scales ThumbSize, IconSize and TileSize with the
    /// pane zoom and its own comment insists a metric keep its ratio to
    /// IconSize through it — a fixed 16 would have left the nesting reading as
    /// noise beside icons and type at twice the size.
    ///
    /// And the map is republished from the stored depths rather than
    /// re-spliced, so a Ctrl+scroll does not Reset every row on screen per
    /// wheel tick.
    /// </summary>
    [AvaloniaFact]
    public async Task The_nesting_step_grows_with_the_pane()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        Assert.Equal(16d, pane.IndentStep);
        Assert.Equal(16d, pane.Indents[In("docs", "kid-a.txt")]);

        var resets = 0;

        ((INotifyCollectionChanged)pane.DetailsEntries).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };

        pane.IconScale = 2.0;

        Assert.Equal(32d, pane.IndentStep);
        Assert.Equal(32d, pane.Indents[In("docs", "kid-a.txt")]);

        Assert.Equal(0, resets);
    }

    private static object? Indent(string path, IReadOnlyDictionary<string, double> indents)
        => FileConverters.Indent.Convert(
            [path, indents], typeof(object), null,
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The press lands on the Path inside the slot, never on the slot itself,
    /// so finding it is a walk — and it stops at the row, so it cannot wander
    /// up into some other list's triangle.
    /// </summary>
    [AvaloniaFact]
    public void The_slot_is_found_from_the_triangle_inside_it()
    {
        var (window, list) = BuildRows();

        try
        {
            var cell = ((Control)list.ContainerFromIndex(0)!)
                .GetVisualDescendants().OfType<Panel>()
                .First(p => p.Classes.Contains("twist"));

            var glyph = cell.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();

            Assert.Same(cell, MainWindow.ExpanderAt(glyph));

            var name = ((Control)list.ContainerFromIndex(0)!)
                .GetVisualDescendants().OfType<TextBlock>().First();

            Assert.Null(MainWindow.ExpanderAt(name));
            Assert.Null(MainWindow.ExpanderAt(list));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A list shaped like the details listing: a row with a triangle
    /// slot in it, the same two layers the template draws.</summary>
    private static (Window Window, ListBox List) BuildRows()
    {
        var list = new ListBox
        {
            Width = 400,
            ItemsSource = new[] { "docs", "a.txt" },
            ItemTemplate = new FuncDataTemplate<string>(
                (_, _) =>
                {
                    var cell = new Panel
                    {
                        Width = 16,
                        Height = 20,
                        Background = Avalonia.Media.Brushes.Transparent,
                    };

                    // The literal, not the constant: the markup and the handler
                    // agree on a string, and a test that reads the constant
                    // would agree with whatever it was changed to.
                    cell.Classes.Add("twist");
                    cell.Children.Add(new Avalonia.Controls.Shapes.Path
                    {
                        Width = 11,
                        Height = 11,
                    });

                    var row = new DockPanel { Background = Avalonia.Media.Brushes.Transparent };
                    DockPanel.SetDock(cell, Dock.Left);
                    row.Children.Add(cell);
                    row.Children.Add(new TextBlock { Text = "name" });

                    return row;
                },
                supportsRecycling: true),
        };

        var window = new Window { Content = list, Width = 400, Height = 300 };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        return (window, list);
    }

    /// <summary>
    /// **The press is claimed on the window's tunnel**, exactly as the
    /// selection box's is and for the same measured reason: by the time the
    /// ListBox has seen it, a five-file selection is already one file. Handled
    /// there also means two quick presses are an open and a close rather than a
    /// double-click that opens the folder for real.
    /// </summary>
    [Fact]
    public void The_press_is_taken_before_the_listing_sees_it()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private void OnPointerPressedAnywhere(");

        var guard = body.IndexOf("ExpanderAt(e.Source)", StringComparison.Ordinal);

        Assert.True(guard > 0, "the expander guard is not in the press handler");

        // Before the band and the drag are armed, which is what the two fields
        // below stand for.
        var band = body.IndexOf("_bandList =", StringComparison.Ordinal);

        Assert.True(guard < band);
        Assert.True(guard < body.IndexOf("_dragSource =", StringComparison.Ordinal));

        // **Returning early is not the same as arming nothing.** The drag
        // fields outlive their press, so a press that returns before the arming
        // block inherits the PREVIOUS press's row and origin — the same trap
        // the selection box's guard is held to.
        Assert.Contains("ArmNothing();", body[guard..band], StringComparison.Ordinal);

        // **And only on a folder.** The slot is the same width on every row so
        // that files and folders keep their icons in one column, so claiming
        // the press over a file's empty slot would put a 16px dead strip down
        // the left of every file in the listing.
        Assert.Contains("IsDirectory: true", body[guard..band], StringComparison.Ordinal);

        // And it claims the pane, for the reason ActivateGroupAt's own summary
        // gives about the box beside it: a guard that returns before the end of
        // the handler still has to make its half of a split active, or the next
        // Delete acts on the other one. Pressed for real in
        // Pressing_a_triangle_in_the_other_half_makes_that_half_active.
        Assert.Contains("ActivateGroupAt(e.Source);", body[guard..band],
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole chain in the real window: the markup parses, the two
    /// MultiBindings resolve through `$parent[ListBox]` to the pane, the
    /// converters run, and a row spliced in from inside really is drawn one
    /// step further right than the folder that holds it.
    ///
    /// **New markup on a path that only runs once somebody opens a folder is
    /// exactly the kind that ships broken.** Nothing above this reaches the
    /// row template at all — a binding that cannot resolve is a logged warning
    /// in Avalonia, not an exception, so a structural read of the file would go
    /// on passing over a triangle that never draws.
    /// </summary>
    [AvaloniaFact]
    public async Task The_real_row_draws_the_triangle_and_the_indent()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-expandwin-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "docs"));
        System.IO.File.WriteAllText(Path.Combine(root, "docs", "kid.txt"), "kid");
        System.IO.File.WriteAllText(Path.Combine(root, "a.txt"), "a");

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        try
        {
            var pane = shell.ActiveTab!;

            await pane.NavigateAsync(root);
            await Layout(window);

            var rows = Cells(window);

            Assert.Equal(2, rows.Count);

            // A folder has one and a file has none, which is the converter's
            // whole job — and the shape, not the visibility, is what moves.
            Assert.NotNull(Glyph(rows[Path.Combine(root, "docs")]));
            Assert.Null(Glyph(rows[Path.Combine(root, "a.txt")]));

            // The slot and the glyph are both sized by metrics, and these are
            // what those metrics resolve to inside the pane at 100%.
            Assert.Equal(16d, rows[Path.Combine(root, "docs")].Bounds.Width);

            Assert.Equal(11d, rows[Path.Combine(root, "docs")]
                                  .GetVisualDescendants()
                                  .OfType<Avalonia.Controls.Shapes.Path>()
                                  .Single().Width);

            var shut = Glyph(rows[Path.Combine(root, "docs")]);
            var top = rows[Path.Combine(root, "docs")].Bounds.X;

            await pane.ToggleExpandAsync(
                pane.DetailsEntries.Single(e => e.Name == "docs"));

            await Layout(window);

            rows = Cells(window);

            Assert.Equal(3, rows.Count);

            // The triangle turned over.
            Assert.NotEqual(shut, Glyph(rows[Path.Combine(root, "docs")]));

            // And the row from inside is drawn one step in.
            Assert.Equal(top + pane.IndentStep,
                         rows[Path.Combine(root, "docs", "kid.txt")].Bounds.X);
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>Runs the dispatcher and lays the window out, so the containers
    /// a listing change asked for are really there to be read.</summary>
    private static async Task Layout(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Every realized triangle slot in the window, by the path of the
    /// row it belongs to.</summary>
    private static Dictionary<string, Panel> Cells(Window window)
        => window.GetVisualDescendants()
                 .OfType<Panel>()
                 .Where(p => p.Classes.Contains(MainWindow.ExpanderClass))
                 .Where(p => p.DataContext is FileEntry)
                 .ToDictionary(p => ((FileEntry)p.DataContext!).FullPath, p => p);

    private static Avalonia.Media.Geometry? Glyph(Panel cell)
        => cell.GetVisualDescendants()
               .OfType<Avalonia.Controls.Shapes.Path>()
               .Single()
               .Data;

    // ---- the keyboard --------------------------------------------------------

    /// <summary>
    /// Right opens and Left closes, in the list view only — and neither is
    /// claimed for a press that would do nothing, because both keys already
    /// move the selection sideways in the two grid layouts.
    /// </summary>
    [AvaloniaFact]
    public async Task Right_opens_left_closes_and_a_key_that_does_nothing_is_given_back()
    {
        var (pane, _) = await Pane();

        pane.SelectedEntry = pane.DetailsEntries.Single(e => e.Name == "docs");

        // Left on a folder that is already shut is not this key's press.
        Assert.False(MainWindow.TurnExpansion(pane, open: false));

        Assert.True(MainWindow.TurnExpansion(pane, open: true));
        await Settle();

        Assert.True(pane.IsExpanded(In("docs")));

        // And Right on a folder that is already open is not either.
        Assert.False(MainWindow.TurnExpansion(pane, open: true));

        Assert.True(MainWindow.TurnExpansion(pane, open: false));
        await Settle();

        Assert.False(pane.IsExpanded(In("docs")));
    }

    /// <summary>
    /// And the switch claims the keystroke from that answer rather than from
    /// the key: `e.Handled = TurnExpansion(...)`, not a bare call. Read from
    /// the handler because nothing a view model can be asked will say whether
    /// the key was given back.
    /// </summary>
    [Fact]
    public void The_arrows_claim_the_keystroke_only_when_they_turned_something()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"), "private void OnWindowKeyDown(");

        Assert.Contains("e.Handled = TurnExpansion(pane, open: true);", body,
                        StringComparison.Ordinal);

        Assert.Contains("e.Handled = TurnExpansion(pane, open: false);", body,
                        StringComparison.Ordinal);
    }

    /// <summary>A file has no triangle, and the tile layouts draw none — so the
    /// key belongs to whatever else wants it in both cases.</summary>
    [AvaloniaFact]
    public async Task The_arrows_are_given_back_where_there_is_no_triangle()
    {
        var (pane, _) = await Pane();

        pane.SelectedEntry = pane.DetailsEntries.Single(e => e.Name == "a.txt");

        Assert.False(MainWindow.TurnExpansion(pane, open: true));

        pane.SelectedEntry = pane.DetailsEntries.Single(e => e.Name == "docs");
        pane.View = ViewMode.Grid;

        Assert.False(MainWindow.TurnExpansion(pane, open: true));
    }

    // ---- the races, and what the gesture costs -------------------------------

    /// <summary>
    /// **Two presses on a folder that has not answered are an open and a
    /// close**, which is what the press handler's own comment promises.
    ///
    /// Measured without the in-flight set: the second press found _open still
    /// empty, started a SECOND enumeration, and both landed and wrote the same
    /// key — so the folder finished OPEN, having read the disk twice to get
    /// there. Held Right did the same thing, because TurnExpansion asks
    /// IsExpanded, which is still false mid-read.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_presses_before_the_read_lands_are_an_open_and_a_close()
    {
        var (pane, fs) = await Pane();

        fs.Hold(In("docs"));
        fs.CountFrom(In("docs"));

        var row = pane.DetailsEntries.Single(e => e.Name == "docs");

        var first = pane.ToggleExpandAsync(row);
        var second = pane.ToggleExpandAsync(row);

        fs.Release(In("docs"));

        await first;
        await second;
        await Settle();

        Assert.False(pane.IsExpanded(In("docs")));
        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));

        // And it read the folder once, not twice.
        Assert.Equal(1, fs.Reads(In("docs")));
    }

    /// <summary>
    /// **The keyboard goes to the folder, not with the rows.** Closing a folder
    /// with one of its children focused left SelectedEntry naming a path the
    /// listing no longer showed — and Reselect cannot repoint it, because it
    /// restores nothing when the paths it kept match nothing.
    ///
    /// It matters beyond the highlight: OpenSelectedAsync, TurnExpansion and
    /// the rename prompt all read that field.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_a_folder_does_not_leave_the_keyboard_on_a_row_that_went()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        var kid = pane.DetailsEntries.Single(e => e.Name == "kid-a.txt");

        pane.SelectedEntry = kid;
        pane.DetailsSelection.Clear();
        pane.DetailsSelection.Add(kid);

        await Open(pane, In("docs"));

        Assert.DoesNotContain(pane.DetailsEntries, e => e.Name == "kid-a.txt");

        Assert.Equal(In("docs"), pane.SelectedEntry?.FullPath);
    }

    /// <summary>
    /// A CHARACTERISATION TEST: it pins what the watcher's batch pass COSTS
    /// while something is open, rather than a property anybody wanted.
    ///
    /// The pass is written to touch each list once and copy neither, and it
    /// still does — but the splice on the end of it is a full rebuild, so one
    /// arriving file produces no Add notification and one Reset over every row
    /// on screen. That is paid deliberately: the alternative is a
    /// top-level-index-to-projected-index walk per arriving PATH, where this is
    /// one pass per BURST. Nothing is paid at all while nothing is open.
    ///
    /// Here so the next person reads the measurement rather than the claim.
    /// </summary>
    [AvaloniaFact]
    public async Task A_watcher_burst_rebuilds_the_listing_once_while_a_folder_is_open()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        var resets = 0;
        var inserts = 0;

        ((INotifyCollectionChanged)pane.DetailsEntries).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (e.Action == NotifyCollectionChangedAction.Add) inserts++;
        };

        fs.Describe(In("new.txt"));
        fs.Raise(new FileSystemChange(ChangeKind.Added, In("new.txt")));

        await Settle();

        Assert.Contains(pane.DetailsEntries, e => e.Name == "new.txt");

        Assert.Equal((1, 0), (resets, inserts));
    }

    /// <summary>
    /// **One reload of the open folders at a time.** Measured without the
    /// guard: three refreshes against a folder that had not answered left three
    /// un-cancellable enumerations in flight, because ReadChildrenAsync takes
    /// CancellationToken.None and a refresh is what a rename, a paste, a delete
    /// and an undo all end in.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_does_not_pile_up_reads_of_a_folder_that_has_not_answered()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        fs.Hold(In("docs"));
        fs.CountFrom(In("docs"));

        await pane.RefreshAsync();
        await Settle();

        await pane.RefreshAsync();
        await Settle();

        await pane.RefreshAsync();
        await Settle();

        Assert.Equal(1, fs.Reads(In("docs")));

        fs.Release(In("docs"));
        await Settle();
    }

    /// <summary>
    /// And the guard lifts, or an open folder would be re-read once and never
    /// again — a refresh after the first would leave a paste into an open
    /// subfolder invisible for the rest of the session.
    ///
    /// A separate test rather than a tail on the one above, because the
    /// held read there answers on a thread pool continuation and the point at
    /// which it lands is not something this harness can pin down: measured,
    /// sixty dispatcher pumps after the release were not enough, and a test
    /// that waits for a race is a test that fails on someone else's machine.
    /// </summary>
    [AvaloniaFact]
    public async Task A_second_refresh_re_reads_the_open_folder_again()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        fs.CountFrom(In("docs"));

        await pane.RefreshAsync();
        await Settle();

        Assert.Equal(1, fs.Reads(In("docs")));

        await pane.RefreshAsync();
        await Settle();

        Assert.Equal(2, fs.Reads(In("docs")));
    }

    /// <summary>
    /// **Closing a folder closes what was opened inside it**, and the listing
    /// goes back to being Entries itself — the identity rule the whole design
    /// rests on.
    ///
    /// Measured with the inner one retained: with nothing visibly open, _open
    /// still held one key, so ExpansionApplies stayed true, DetailsEntries went
    /// on handing back a second full copy of the listing for the life of the
    /// tab, and every later refresh re-read a folder for rows nobody would see.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_the_parent_gives_the_listing_back_to_Entries()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));
        await Open(pane, In("docs", "inner"));

        await Open(pane, In("docs"));

        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));

        Assert.False(pane.IsExpanded(In("docs", "inner")));

        Assert.True(ReferenceEquals(pane.DetailsEntries, pane.Entries),
                    "the listing is still a second copy of itself");
    }

    /// <summary>
    /// And a read still in flight underneath one goes with it. A collapse is
    /// not a navigation, so the generation has not moved and the generation
    /// check cannot see this: measured, the inner read landed after the
    /// collapse and put itself back into the open set, where nothing on screen
    /// could show it and nothing could take it out again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_read_still_in_flight_under_a_folder_you_shut_is_dropped()
    {
        var (pane, fs) = await Pane();

        await Open(pane, In("docs"));

        fs.Hold(In("docs", "inner"));

        var inner = pane.ToggleExpandAsync(
            pane.DetailsEntries.Single(e => e.Name == "inner"));

        await Open(pane, In("docs"));

        fs.Release(In("docs", "inner"));

        await inner;
        await Settle();

        Assert.False(pane.IsExpanded(In("docs", "inner")));

        Assert.True(ReferenceEquals(pane.DetailsEntries, pane.Entries),
                    "the listing is still a second copy of itself");
    }

    /// <summary>
    /// The reload's own generation check, which the expand path's test cannot
    /// stand in for: leave the folder and come back while a re-read is in
    /// flight, and the rows it finally answers with belong to a listing that
    /// has been read again since.
    /// </summary>
    [AvaloniaFact]
    public async Task A_reload_that_lands_in_a_listing_you_left_and_returned_to_is_dropped()
    {
        var (pane, fs) = await Pane();

        fs.Put(In("elsewhere"), Tree.File("other.txt"));

        await Open(pane, In("docs"));

        // The re-read the refresh starts is held open, and while it is held the
        // pane goes away and comes back — which forgets the tree on the way out
        // and reads a fresh listing on the way in.
        fs.Hold(In("docs"));

        await pane.RefreshAsync();
        await Settle();

        await pane.NavigateAsync(In("elsewhere"));
        await Settle();

        await pane.NavigateAsync(Root);
        await Settle();

        fs.Release(In("docs"));
        await Drain();

        Assert.False(pane.IsExpanded(In("docs")));
        Assert.Equal(["docs", "a.txt", "z.txt"], Names(pane));
    }

    // ---- the real window ------------------------------------------------------

    /// <summary>
    /// **The heading gives its slot back where the rows have none.** The two
    /// grids are kept in step by hand, so a heading that kept 16px of nothing
    /// in a listing without triangles would put every column one slot right of
    /// the cells it labels.
    ///
    /// In the real window because the notification is what carries it: the
    /// binding is read once when the row is realized, and only
    /// OnCurrentPathChanged can tell it the answer has changed.
    /// </summary>
    [AvaloniaFact]
    public async Task The_heading_slot_goes_where_the_listing_offers_no_triangles()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-expandslot-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "docs"));

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            await pane.NavigateAsync(root);
            await Layout(window);

            // Found by walking rather than by name: the heading is inside the
            // pane's own template, so the window's namescope does not hold it.
            var slot = window.GetVisualDescendants().OfType<Border>()
                             .SingleOrDefault(b => b.Name == "ExpandHeadingSlot");

            Assert.NotNull(slot);
            Assert.True(slot!.IsVisible, "a real folder reserves the triangle's slot");

            await pane.NavigateAsync(VirtualPaths.Files);
            await Layout(window);

            Assert.False(slot.IsVisible, "a recent listing kept a slot its rows do not draw");
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// **A press on the triangle in the inactive half makes that half active**,
    /// for the reason ActivateGroupAt's summary gives about the selection box
    /// beside it: a guard that returns before the end of the press handler
    /// still has to claim its pane, or the next Delete acts on the other one.
    ///
    /// And it is the only test that presses a real triangle in a real window
    /// with a real pointer, so it is also what says the slot is wide enough to
    /// take a press at all.
    /// </summary>
    [AvaloniaFact]
    public async Task Pressing_a_triangle_in_the_other_half_makes_that_half_active()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-expandsplit-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "docs"));
        System.IO.File.WriteAllText(Path.Combine(root, "docs", "kid.txt"), "kid");

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        string? home = null;
        var split = false;

        try
        {
            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            home = shell.Left.ActiveTab?.CurrentPath;

            // Ensured rather than toggled. A MainWindow restores the real
            // session, and a run in which something else left a split behind
            // would have had this close it — measured: this test passed alone
            // and threw on a null Right in the full project.
            split = shell.IsSplit;

            if (!shell.IsSplit) shell.ToggleSplit();

            await Layout(window);

            var other = shell.Right!;

            await other.ActiveTab!.NavigateAsync(root);

            // Back to the left half, so the press below lands in the one that
            // is NOT active.
            shell.ActivateGroup(shell.Left);

            await Layout(window);

            Assert.NotSame(other, shell.ActiveGroup);

            var cell = window.GetVisualDescendants().OfType<Panel>()
                .Where(p => p.Classes.Contains(MainWindow.ExpanderClass))
                .Single(p => p.DataContext is FileEntry entry
                             && entry.FullPath == Path.Combine(root, "docs"));

            Assert.True(cell.Bounds.Width > 0,
                        "the triangle has no width, so the press below would land on the row");

            var at = cell.TranslatePoint(
                new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window);

            Assert.NotNull(at);

            window.MouseDown(at!.Value, MouseButton.Left);
            window.MouseUp(at.Value, MouseButton.Left);

            await Layout(window);

            Assert.Same(other, shell.ActiveGroup);
            Assert.True(other.ActiveTab!.IsExpanded(Path.Combine(root, "docs")),
                        "the press did not open the folder");
        }
        finally
        {
            // **Put the split away before the window closes.** Closing a real
            // MainWindow flushes the real session store, and a split left in it
            // came back in every window the rest of the run built — which is
            // how this test made two others fail before it was undone here.
            if (window.DataContext is ShellViewModel done)
            {
                if (done.IsSplit != split) done.ToggleSplit();

                if (home is { } back && done.ActiveTab is { } tab)
                    await tab.NavigateAsync(back);

                Dispatcher.UIThread.RunJobs();
            }

            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>
    /// **Right must not reach the listing while the keyboard is in the
    /// sidebar** — the rule SidebarWalk.ActsOnTheListing already states for
    /// Delete, F2, Ctrl+C/X, Ctrl+A and Alt+Enter, and the class of bug its own
    /// comment was written about: the keys that WERE bound went on acting on
    /// the listing, with nothing on screen saying which of the two a keystroke
    /// had gone to.
    /// </summary>
    [AvaloniaFact]
    public async Task Right_in_the_sidebar_does_not_open_a_folder_in_the_listing()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-expandside-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "docs"));
        System.IO.File.WriteAllText(Path.Combine(root, "docs", "kid.txt"), "kid");

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var pane = Assert.IsType<ShellViewModel>(window.DataContext).ActiveTab!;

            await pane.NavigateAsync(root);
            await Layout(window);

            pane.SelectedEntry = pane.DetailsEntries.Single(e => e.Name == "docs");

            var stops = window.FindControl<Border>("SidebarPanel")!
                              .GetVisualDescendants().OfType<Button>()
                              .Where(b => b is not RepeatButton)
                              .Where(b => b.Focusable && b.IsEffectivelyVisible
                                          && b.IsEffectivelyEnabled)
                              .ToList();

            Assert.NotEmpty(stops);

            stops[0].Focus(NavigationMethod.Directional);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(stops[0], window.FocusManager?.GetFocusedElement());

            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            await Layout(window);

            Assert.False(pane.IsExpanded(Path.Combine(root, "docs")),
                         "Right in the sidebar opened a folder in the listing");
        }
        finally
        {
            window.Close();

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    // ---- what else reads the rows ---------------------------------------------

    /// <summary>
    /// **A rename run steps through what is on SCREEN.** Tab moves from the row
    /// just finished to the one beside it, and RenameRun.Next answers null for
    /// a path the list it is given does not hold — so a run that began on a row
    /// from inside an open folder stopped dead against Entries, doing nothing
    /// and saying nothing.
    ///
    /// The window's step is read rather than driven: opening a rename prompt
    /// needs a real listing over a real disk, and the question here is which
    /// collection the step is handed.
    /// </summary>
    [AvaloniaFact]
    public async Task The_rename_run_steps_through_the_rows_on_screen()
    {
        var (pane, _) = await Pane();

        await Open(pane, In("docs"));

        Assert.Equal(Names(pane), pane.Rows.Select(e => e.Name).ToList());

        var next = Input.RenameRun.Next(pane.Rows, In("docs", "kid-a.txt"), 1);

        Assert.Equal("kid-b.txt", next?.Name);

        Assert.Contains(
            "Input.RenameRun.Next(pane.Rows,",
            RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                            "private async Task StepRenameAsync("),
            StringComparison.Ordinal);
    }

    // ---- the fake ------------------------------------------------------------

    /// <summary>
    /// A filesystem of folders and files, so a listing can be opened inside a
    /// listing. Anything not registered enumerates as nothing and stats as
    /// nothing, the way a folder that is not there does.
    /// </summary>
    private sealed class Tree : IFileSystemProvider
    {
        private readonly Dictionary<string, List<(string Name, long Size, EntryFlags Flags)>>
            _folders = new(StringComparer.Ordinal);

        private readonly Dictionary<string, FileEntry> _described = new(StringComparer.Ordinal);
        private readonly List<Action<FileSystemChange>> _watchers = [];
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

        public static (string, long, EntryFlags) Dir(string name)
            => (name, 0, EntryFlags.Directory);

        public static (string, long, EntryFlags) File(string name, long size = 1)
            => (name, size, EntryFlags.None);

        public void Put(string folder, params (string Name, long Size, EntryFlags Flags)[] rows)
            => _folders[folder] = [.. rows];

        /// <summary>Makes a path answerable to the watcher's stat.</summary>
        public void Describe(string path, EntryFlags flags = EntryFlags.None)
            => _described[path] = new(Path.GetFileName(path), path, 1,
                                      DateTimeOffset.UnixEpoch, flags);

        /// <summary>News from the folder currently on screen.</summary>
        public void Raise(FileSystemChange change) => _watchers[^1](change);

        /// <summary>Makes reading this folder throw, the way a folder you have
        /// no rights to does.</summary>
        public void Refuse(string folder) => _refused.Add(folder);

        /// <summary>Holds a read open until <see cref="Release"/>, so a test can
        /// do something else while it is in flight.</summary>
        public void Hold(string folder) => _gates[folder] = new TaskCompletionSource();

        public void Release(string folder) => _gates[folder].SetResult();

        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        /// <summary>Starts counting enumerations of this folder.</summary>
        public void CountFrom(string folder) => _reads[folder] = 0;

        public int Reads(string folder) => _reads[folder];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (_reads.ContainsKey(path)) _reads[path]++;

            if (_gates.TryGetValue(path, out var gate)) await gate.Task.ConfigureAwait(false);

            if (_refused.Contains(path)) throw new UnauthorizedAccessException(path);

            await Task.CompletedTask;

            if (!_folders.TryGetValue(path, out var rows)) yield break;

            yield return [.. rows.Select(r => new FileEntry(
                r.Name, Path.Combine(path, r.Name), r.Size,
                DateTimeOffset.UnixEpoch, r.Flags))];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(
                _described.TryGetValue(path, out var entry) ? entry : (FileEntry?)null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            _watchers.Add(onChange);
            return new Nothing();
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
