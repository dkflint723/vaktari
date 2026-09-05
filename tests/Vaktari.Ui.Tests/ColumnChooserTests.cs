using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Choosing which columns a pane shows.
///
/// **There was no way to turn a column off, and no type column to turn on.**
/// The only thing that ever hid a column was the pane getting too narrow for
/// it, which is not a choice — and sorting by type was implemented from the
/// start with nothing to click, because there was no type heading.
///
/// **The choice is per pane**, the way sort and grouping are. A reference
/// listing beside a working one wants different columns, and ticking one on
/// the left must not move the right. It travels with the tab in the session.
///
/// The tests that matter most are the ones this could quietly get wrong: that
/// a session written before the choice existed restores the old columns, that
/// one pane's choice stays out of the other, and that choosing a column does
/// not override the width rule keeping the name readable.
/// </summary>
public sealed class ColumnChooserTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private PaneViewModel Pane(double width = 1400)
        => new(new Inert(), null, null) { ViewportWidth = width };

    // ---- per pane, which is the point ---------------------------------------

    [AvaloniaFact]
    public void Choosing_on_one_pane_leaves_the_other_alone()
    {
        var left = Pane();
        var right = Pane();

        left.ToggleTypeColumnCommand.Execute(null);
        left.ToggleSizeColumnCommand.Execute(null);

        Assert.True(left.ShowType);
        Assert.False(left.ShowSize);

        Assert.False(right.ShowType, "the right pane grew a column it never asked for");
        Assert.True(right.ShowSize, "the right pane lost a column it never touched");
    }

    // ---- the upgrade, which is the half that would go unnoticed -------------

    /// <summary>
    /// **A session written before this existed must restore the old columns.**
    /// Deserialization here does not run property initializers — an absent
    /// key arrives as default(T), which TabState documents for its own scales —
    /// so all three are phrased to make <c>false</c> mean "what it showed
    /// before". This reads a session with no column keys at all, through the
    /// same source-generated context the real store uses, and restores a pane
    /// from it.
    /// </summary>
    [AvaloniaFact]
    public void A_session_that_never_heard_of_columns_restores_the_old_ones()
    {
        var json = "{\"version\":13,\"windows\":[{\"panes\":[{\"tabs\":[{\"path\":\"" +
                   Path.GetTempPath().Replace("\\", "\\\\") + "\"}]}]}]}";

        var session = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState);

        Assert.NotNull(session);

        var tab = session!.Windows[0].Panes[0].Tabs[0];
        var pane = Pane();

        pane.RestoreFrom(tab);

        Assert.True(pane.ShowSize, "size vanished for everyone who upgraded");
        Assert.True(pane.ShowModified, "modified vanished for everyone who upgraded");
        Assert.False(pane.ShowType, "a new column appeared uninvited");
    }

    [AvaloniaFact]
    public void Out_of_the_box_that_means_size_and_modified_and_no_type()
    {
        var pane = Pane();

        Assert.True(pane.ShowSize);
        Assert.True(pane.ShowModified);
        Assert.False(pane.ShowType);
    }

    // ---- it travels with the tab ---------------------------------------------

    [AvaloniaFact]
    public void The_choice_round_trips_through_the_session()
    {
        var before = Pane();

        before.ToggleTypeColumnCommand.Execute(null);
        before.ToggleModifiedColumnCommand.Execute(null);

        var after = Pane();

        after.RestoreFrom(before.ToTabState());

        Assert.True(after.ShowType);
        Assert.False(after.ShowModified);
        Assert.True(after.ShowSize);
    }

    /// <summary>
    /// **A property ToTabState writes but the shell never marks dirty only
    /// persists when something else changes first.** Grouping had exactly that
    /// gap. So this goes through the shell rather than the pane, and asks the
    /// store whether it heard.
    /// </summary>
    [AvaloniaFact]
    public void Changing_the_choice_is_worth_saving()
    {
        var store = new Listening();
        var shell = Own(new ShellViewModel(new Inert(), store: store));

        shell.Start(null, Path.GetTempPath());

        var heard = store.Heard;

        shell.ActiveTab!.ToggleTypeColumnCommand.Execute(null);

        Assert.True(store.Heard > heard, "the session store was not told");
        Assert.True(store.Last!.Windows[0].Panes[0].Tabs[0].ShowType, "it was told, but not the new value");
    }

    // ---- the width rule, which the choice must not override -----------------

    /// <summary>
    /// **Both questions have to say yes.** The width rule was here first and
    /// keeps the last word: a column crushing the name into an ellipsis is
    /// worse than one that stepped aside, and somebody who ticked Size in a
    /// wide window did not thereby ask for an unreadable narrow one.
    /// </summary>
    [AvaloniaFact]
    public void A_chosen_column_still_gives_way_in_a_narrow_pane()
    {
        var narrow = Pane(width: 300);

        narrow.ToggleTypeColumnCommand.Execute(null);

        Assert.False(narrow.ShowSize, "the width rule stopped applying");
        Assert.False(narrow.ShowModified);
        Assert.False(narrow.ShowType, "a chosen column ignored the width rule");

        // And the tick still says what was chosen, so the menu explains the
        // gap rather than hiding it.
        Assert.True(narrow.IsTypeColumnShown);
    }

    /// <summary>And room is not a request: the type column stays off in a pane
    /// wide enough for it until somebody asks.</summary>
    [AvaloniaFact]
    public void Room_for_a_column_is_not_a_request_for_it()
        => Assert.False(Pane(width: 2400).ShowType);

    // ---- the plumbing that makes the screen follow the tick -----------------

    /// <summary>
    /// The visibility is computed, so nothing raises it on its own. Miss the
    /// fan-out and the tick moves while the column does not.
    /// </summary>
    [AvaloniaFact]
    public void Toggling_tells_the_view()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.ToggleTypeColumnCommand.Execute(null);

        Assert.Contains(nameof(PaneViewModel.ShowType), raised);
        Assert.Contains(nameof(PaneViewModel.IsTypeColumnShown), raised);
    }

    /// <summary>Sorting by type was implemented from the start and had nothing
    /// to click. The heading needs an arrow like the other three, and an arrow
    /// that never moves is worse than none.</summary>
    [AvaloniaFact]
    public void The_type_heading_gets_a_sort_arrow()
    {
        var pane = Pane();
        var raised = new List<string?>();

        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pane.SortByCommand.Execute("kind");

        Assert.NotEqual("", pane.KindSortGlyph);
        Assert.Contains(nameof(PaneViewModel.KindSortGlyph), raised);
    }

    // ---- the two grids nobody was checking ----------------------------------

    /// <summary>
    /// **The header and the rows are two separate grids kept in step by hand.**
    /// Nothing couples them: the headings sit over their columns only because
    /// both declare the same columns, the same widths and the same margin.
    /// Adding a column is precisely the edit that breaks that, and it breaks it
    /// silently — headings sliding one column left is the kind of thing that
    /// ships.
    /// </summary>
    [AvaloniaFact]
    public void The_heading_grid_and_the_row_grid_still_agree()
    {
        var grids = Markup()
            .Descendants(Avalonia + "Grid")
            .Where(g => (string?)g.Attribute("Margin") == "12,0,18,0")
            .Select(g => g.Element(Avalonia + "Grid.ColumnDefinitions")
                          ?.Elements(Avalonia + "ColumnDefinition")
                          .Select(c => (string?)c.Attribute("Width"))
                          .ToList())
            .Where(widths => widths is not null)
            .ToList();

        Assert.Equal(2, grids.Count);
        Assert.Equal(grids[0], grids[1]);
    }

    /// <summary>
    /// The type column really did land in the slot that was standing empty, in
    /// both grids. Renumbering the columns after it is the edit the row
    /// template warns "goes wrong quietly".
    /// </summary>
    [AvaloniaFact]
    public void The_type_column_took_the_empty_slot_in_both_grids()
    {
        var inColumnThree = Markup()
            .Descendants()
            .Count(e => (string?)e.Attribute("Grid.Column") == "3");

        Assert.Equal(2, inColumnThree);
    }

    /// <summary>
    /// The chooser binds to the pane under it, not out through the window. A
    /// binding that reaches the shell would make the choice global again by
    /// accident, and nothing else would notice until both panes moved at once.
    ///
    /// Eight rows, not four: the four on the header's right-click and the four
    /// in the menu the keyboard opens. The listing menu's copy reaches the pane
    /// through the pane GROUP's ActiveTab — the group the menu hangs on — which
    /// in a split is the pane you right-clicked; the shell's ActiveTab is
    /// whichever pane last held focus, and is the way this goes global by
    /// accident.
    /// </summary>
    [AvaloniaFact]
    public void The_chooser_binds_to_its_own_pane()
    {
        var rows = Markup()
            .Descendants(Avalonia + "MenuItem")
            .Where(m => ((string?)m.Attribute("Command") ?? "").Contains("ColumnCommand", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(8, rows.Count);

        foreach (var row in rows)
        {
            Assert.DoesNotContain("$parent[Window]", (string?)row.Attribute("Command"));
            Assert.DoesNotContain("$parent[Window]", (string?)row.Attribute("IsChecked"));
        }
    }

    // ---- the keyboard route, which is the whole of finding 129 --------------

    /// <summary>
    /// **The chooser was on the header's right-click and nowhere else.**
    /// ToggleTypeColumnCommand appeared exactly once in the markup — measured
    /// at the commit before this one — in the menu that opens on the column
    /// headings, and the Arrange submenu had no Columns entry at all. The only
    /// menu a keyboard can open in a listing is the one OpenListingMenu walks
    /// up to, which is the pane group's and never the header's, and the header
    /// band is a plain Border with no Focusable of its own, so no key press
    /// could land on it either. That was the whole of finding 129.
    ///
    /// The real key on the real window, and then the row is driven: a menu
    /// entry proves nothing until the command behind it moves the pane the
    /// menu was opened over.
    ///
    /// The starting state is SET rather than read. This window is built from
    /// the session on disk — the developer's own — so a machine whose last
    /// session had the type column on would have read the assertion below
    /// backwards, and the finally puts the choice back before the close that
    /// writes that file again.
    /// </summary>
    [AvaloniaFact]
    public void Shift_F10_opens_a_menu_that_can_choose_the_columns()
    {
        // Building a MainWindow hands the pane the platform's own search
        // backend; borrowing it here gives it back when this class is done.
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var pane = shell.ActiveTab!;
        var was = pane.ShowTypeColumn;

        try
        {
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            Dispatcher.UIThread.RunJobs();

            var list = Listing(window, pane);
            var menu = ListingMenu(window, pane);

            pane.ShowTypeColumn = false;

            // **The keyboard is sent AWAY first, and that is not ceremony.**
            // Where focus is decides which arm of OnWindowKeyDown answers
            // Shift+F10: from the sidebar it raises ContextRequested on the
            // focused place row and this menu never opens. Measured, the window
            // puts the keyboard in the listing by itself — FocusListingSoon,
            // posted at Background priority while the window starts — so a
            // test that only called Focus() on the listing was green with that
            // call deleted, and said nothing about where the keyboard was.
            // Parked on a place first, the call is load-bearing.
            var sidebar = window.FindControl<Border>("SidebarPanel");

            Assert.NotNull(sidebar);

            // The same rule FirstSidebarRow uses: a section heading is a
            // ToggleButton and is not a row.
            var place = sidebar!.GetVisualDescendants()
                                .OfType<Button>()
                                .First(b => b.IsVisible && b is not ToggleButton);

            place.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.True(sidebar.GetVisualDescendants()
                               .Contains(window.FocusManager?.GetFocusedElement() as Visual),
                        "the keyboard never went to the sidebar, so parking it there proves nothing");

            list.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.True(list.IsFocused, "the listing never took the keyboard back");
            Assert.False(menu.IsOpen);

            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            Dispatcher.UIThread.RunJobs();

            try
            {
                Assert.True(menu.IsOpen, "the key did not open the listing's menu");

                // On the listing rather than at the pointer, which is the other
                // half of "the keyboard opened this": a ContextMenu is born
                // PlacementMode.Pointer, so a menu that came up over the mouse
                // is one this key press did not place. Where exactly it lands —
                // under the focused row, or the middle of an empty listing — is
                // ContextMenuPlacementTests.
                Assert.NotEqual(PlacementMode.Pointer, menu.Placement);
                Assert.True(menu.PlacementTarget is { } anchor
                            && (ReferenceEquals(anchor, list)
                                || anchor.FindAncestorOfType<ListBox>(includeSelf: true) == list),
                            $"the menu anchored on {menu.PlacementTarget?.GetType().Name ?? "nothing"} "
                            + "rather than on the listing");

                var type = Row(Row(Row(menu, "Arrange"), "Columns"), "Type");

                // Non-null only because the menu is open: a shut ContextMenu
                // has no DataContext, so every Command in it reads null.
                Assert.NotNull(type.Command);

                type.Command!.Execute(type.CommandParameter);
                Dispatcher.UIThread.RunJobs();

                // The pane the menu was opened over, and no other.
                Assert.True(pane.IsTypeColumnShown, "the row was there and did nothing");

                // And the tick follows, so the menu says what the pane is
                // showing the next time it is opened.
                Assert.True(type.IsChecked);
            }
            finally
            {
                menu.Close();
            }
        }
        finally
        {
            pane.ShowTypeColumn = was;
            Dispatcher.UIThread.RunJobs();

            window.Close();
        }
    }

    /// <summary>The listing the key press will act on, found the way the
    /// window's own ActiveListing finds it — IsVisible included, because the
    /// pane's three layouts are siblings under one ItemsControl and only one of
    /// them is drawn. A hidden one takes no focus.</summary>
    private static ListBox Listing(Window window, PaneViewModel pane)
        => window.GetVisualDescendants()
                 .OfType<ListBox>()
                 .Single(l => l.IsVisible
                              && ReferenceEquals(l.DataContext, pane)
                              && l.SelectionMode.HasFlag(SelectionMode.Multiple));

    /// <summary>The menu the key is supposed to open, found the way
    /// OpenListingMenu finds it: up from the listing to the first control that
    /// carries one.</summary>
    private static ContextMenu ListingMenu(Window window, PaneViewModel pane)
    {
        for (Visual? visual = Listing(window, pane);
             visual is not null;
             visual = visual.GetVisualParent())
            if (visual is Control { ContextMenu: { } menu })
                return menu;

        throw new InvalidOperationException("nothing above the listing carries a context menu");
    }

    /// <summary>One row of a menu by the words a person reads on it, which is
    /// the header with its access-key marker taken out — see
    /// <see cref="MenuLabels"/>. `as string` rather than a cast: several
    /// headers in this menu are bound and arrive as something else
    /// entirely.</summary>
    private static MenuItem Row(ItemsControl menu, string header)
        => menu.Items.OfType<MenuItem>()
               .Single(i => MenuLabels.Plain(i.Header as string) == header);

    /// <summary>
    /// Beside Sort by and Group by, and last of the three: sort and group were
    /// already one decision about how the listing reads and this is the third.
    ///
    /// Gated on Details for Group by's measured reason — the other two layouts
    /// lay out fixed-size cells in a wrap panel and draw no columns at all, so
    /// every row of the chooser would tick and none of them would do anything.
    ///
    /// Read through <see cref="MenuLabels"/> rather than against the raw
    /// headers: these rows carry access-key markers — "Arran_ge", "_Sort by" —
    /// and which letter each one takes is ContextMenuKeysTests' business, not
    /// this test's. Spelling a marker into the expected list here would redden
    /// this the first time a key moved.
    /// </summary>
    [AvaloniaFact]
    public void Columns_stands_with_sort_and_group_and_only_in_details()
    {
        var arrange = Markup()
            .Descendants(Avalonia + "MenuItem")
            .Single(m => MenuLabels.Plain((string?)m.Attribute("Header")) == "Arrange");

        var inside = arrange.Elements(Avalonia + "MenuItem").ToList();

        Assert.Equal(["Sort by", "Group by", "Columns"],
                     inside.Select(m => MenuLabels.Plain((string?)m.Attribute("Header"))));

        Assert.Equal("{Binding ActiveTab.IsDetailsView}",
                     (string?)inside[2].Attribute("IsVisible"));
    }

    /// <summary>
    /// **Two routes to one setting, so they have to offer the same thing.**
    /// The header menu stays where Explorer and Dolphin put it and the listing
    /// menu is what a keyboard can reach, and a column added to one of them and
    /// forgotten in the other is a chooser that answers differently depending
    /// on how you opened it — including the disabled Name row and the note
    /// about narrow panes, which are what stop the menu reading as a complete
    /// list of the columns there are.
    ///
    /// Two differences are allowed and are normalised away below: the listing
    /// menu stands on the pane group and hops through ActiveTab where the
    /// header menu already stands on the pane, and the words are compared
    /// through <see cref="MenuLabels"/> so that the access key each row takes
    /// stays ContextMenuKeysTests' business — the two menus are separate
    /// namespaces for keys and have no reason to agree on the letters.
    /// </summary>
    [AvaloniaFact]
    public void The_two_chooser_menus_offer_the_same_columns()
        => Assert.Equal(Rows(HeaderChooser()), Rows(MenuChooser()));

    /// <summary>The chooser on the headings: the one whose commands need no hop
    /// to find the pane, because it is already standing on it.</summary>
    private static XElement HeaderChooser()
        => Markup()
            .Descendants(Avalonia + "ContextMenu")
            .Single(m => m.Elements(Avalonia + "MenuItem")
                          .Any(i => (string?)i.Attribute("Command")
                                    == "{Binding ToggleTypeColumnCommand}"));

    private static XElement MenuChooser()
        => Markup()
            .Descendants(Avalonia + "MenuItem")
            .Single(m => MenuLabels.Plain((string?)m.Attribute("Header")) == "Columns");

    private static List<string> Rows(XElement chooser)
        => [.. chooser.Elements().Select(e => string.Join(
               " | ",
               e.Name.LocalName,
               MenuLabels.Plain((string?)e.Attribute("Header")),
               (string?)e.Attribute("ToggleType") ?? "",
               (string?)e.Attribute("IsEnabled") ?? "",
               OnThePane((string?)e.Attribute("IsChecked")),
               OnThePane((string?)e.Attribute("Command"))))];

    private static string OnThePane(string? binding)
        => (binding ?? "").Replace("{Binding ActiveTab.", "{Binding ", StringComparison.Ordinal);

    /// <summary>
    /// **The checkbox that hides Arrange now hides three things and named
    /// two.** Columns joined Sort by and Group by under that one gate, so the
    /// label understated it again — the same defect the label was last widened
    /// to fix — and the note under the list, which exists because the old
    /// wording promised shortcuts that do not exist, would have gone on saying
    /// only the sorting half survives.
    /// </summary>
    [AvaloniaFact]
    public void The_settings_page_counts_columns_as_part_of_arrange()
    {
        var settings = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var box = settings
            .Descendants(Avalonia + "CheckBox")
            .Single(c => ((string?)c.Attribute("IsChecked") ?? "")
                         .Contains("MenuSortBy", StringComparison.Ordinal));

        Assert.Contains("columns", (string?)box.Attribute("Content") ?? "",
                        StringComparison.Ordinal);

        var note = settings
            .Descendants(Avalonia + "TextBlock")
            .Select(t => (string?)t.Attribute("Text") ?? "")
            .Single(t => t.StartsWith("Hiding an entry takes it off the menu",
                                      StringComparison.Ordinal));

        Assert.Contains("still chooses which columns to show", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The escape route that page offers is drawn in one layout of three.**
    /// The settings note says the column headings go on sorting and go on
    /// choosing columns while Arrange is hidden, and both of those live in one
    /// Border gated on IsDetailsView — while Arrange's own Sort by carries no
    /// gate and is offered in all three layouts. So with the box unticked and
    /// the pane in Grid or Compact, sorting has no route at all, and a note
    /// that said "only the grouping half goes" would be false there.
    ///
    /// The two halves are in two files and neither can see the other, which is
    /// what this is for: widen the note, or drop the gate off the band, and the
    /// other half stops being true with nothing to say so.
    /// </summary>
    [AvaloniaFact]
    public void Hiding_arrange_leaves_the_headings_only_in_details()
    {
        // The band the chooser hangs on IS the band the sort headings are in —
        // one Border, declared once — so reaching it through the chooser is
        // reaching both halves of what the note promises.
        var band = HeaderChooser().Ancestors(Avalonia + "Border").First();

        Assert.Equal("{Binding IsDetailsView}", (string?)band.Attribute("IsVisible"));

        var sortBy = Markup()
            .Descendants(Avalonia + "MenuItem")
            .Single(m => MenuLabels.Plain((string?)m.Attribute("Header")) == "Sort by");

        Assert.Null(sortBy.Attribute("IsVisible"));

        // And the note is written to that scope rather than to "the column
        // headers", which is the sentence the gate above makes true.
        var note = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"))
            .Descendants(Avalonia + "TextBlock")
            .Select(t => (string?)t.Attribute("Text") ?? "")
            .Single(t => t.StartsWith("Hiding an entry takes it off the menu",
                                      StringComparison.Ordinal));

        Assert.Contains("the list view's column headers", note, StringComparison.Ordinal);
    }

    private static XDocument Markup() => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

    private sealed class Listening : ISessionStore
    {
        public int Heard { get; private set; }
        public SessionState? Last { get; private set; }

        public SessionState? Load() => null;

        public void NotifyChanged(SessionState state)
        {
            Heard++;
            Last = state;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
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
